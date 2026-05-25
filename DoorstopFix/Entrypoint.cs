// =============================================================================
// DoorstopFix - Memory Leak Patch for StretchSense XR Game
// =============================================================================
//
// This mod fixes a memory leak in the StretchSense XR Game caused by unbounded
// ReplaySubject<BoneTransforms> instances in ArticulationManager.
//
// HOW IT WORKS:
// Unity Doorstop (winhttp.dll proxy) loads this DLL before any game code runs.
// We subscribe to AppDomain.AssemblyLoad to detect when the game's pipeline
// assembly loads, then use reflection to replace the leaking objects.
//
// WHY REFLECTION:
// By using pure reflection with no compile-time references to game assemblies,
// this DLL has zero dependencies beyond the .NET runtime. This means:
//   - It builds without needing game DLLs on the build machine
//   - It's resilient to minor game updates (field names/types rarely change)
//   - CI/CD can build it without proprietary assets
//
// THE BUG:
// ArticulationManager declares:
//   static readonly Dictionary<Handedness, ReplaySubject<BoneTransforms>> AnimatorOutput = new() {
//       { LEFT,  new ReplaySubject<BoneTransforms>() },   // <-- NO BUFFER SIZE
//       { RIGHT, new ReplaySubject<BoneTransforms>() }    // <-- NO BUFFER SIZE
//   };
//
// R3's ReplaySubject() without a buffer size defaults to int.MaxValue, meaning it
// stores EVERY value ever emitted and never trims. Since AnimatorOutput receives
// BoneTransforms every frame during calibration (PRECAPTURE state), this grows
// unboundedly: ~120 objects/sec with two gloves, never freed.
//
// THE FIX:
// Replace each ReplaySubject<BoneTransforms>() with ReplaySubject<BoneTransforms>(1),
// which keeps only the most recent value. This is semantically correct because
// subscribers only ever need the latest bone transforms.
// =============================================================================

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Doorstop
{
    /// <summary>
    /// Unity Doorstop entrypoint. Doorstop calls Start() before any game code runs.
    /// The method signature must be exactly: static void Doorstop.Entrypoint.Start()
    /// </summary>
    public class Entrypoint
    {
        /// <summary>
        /// Log file path — placed next to the DLL (which is in the game's root directory).
        /// </summary>
        private static readonly string LogPath = Path.Combine(
            Path.GetDirectoryName(typeof(Entrypoint).Assembly.Location) ?? ".",
            "doorstop_fix.log");

        /// <summary>
        /// Doorstop calls this before Unity loads any game assemblies.
        /// We can't patch yet because the target types don't exist, so we register
        /// an event handler to detect when the target assembly loads.
        /// </summary>
        public static void Start()
        {
            Log("DoorstopFix loaded - waiting for StretchSense.Pipeline assembly...");

            // AssemblyLoad fires after an assembly is loaded but before its types are used.
            // This is the perfect interception point: types are available for reflection,
            // but no game code has called into them yet.
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        /// <summary>
        /// Fired every time a new assembly is loaded into the AppDomain.
        /// We watch for StretchSense.Pipeline.dll specifically.
        /// </summary>
        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetName().Name != "StretchSense.Pipeline")
                return;

            Log("StretchSense.Pipeline loaded - patching ArticulationManager...");

            try
            {
                PatchAnimatorOutput(args.LoadedAssembly);
            }
            catch (Exception ex)
            {
                Log($"ERROR during patching: {ex}");
            }

            // Unsubscribe regardless of success — we only need to run once
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        }

        /// <summary>
        /// Core patching logic. Replaces unbounded ReplaySubjects with bounded ones.
        ///
        /// Steps:
        /// 1. Force ArticulationManager's static constructor to run (initializes the field)
        /// 2. Read the AnimatorOutput dictionary via reflection
        /// 3. For each hand (LEFT, RIGHT), create a new ReplaySubject(bufferSize: 1)
        /// 4. Swap it into the dictionary, dispose the old one
        /// </summary>
        private static void PatchAnimatorOutput(Assembly pipelineAssembly)
        {
            // --- Step 1: Get the ArticulationManager type ---
            var artMgrType = pipelineAssembly.GetType("StretchSense.Pipeline.ArticulationManager");
            if (artMgrType == null)
            {
                Log("ERROR: Could not find ArticulationManager type");
                return;
            }

            // Force the static constructor (.cctor) to run NOW. This ensures the
            // AnimatorOutput field initializer has executed, populating the dictionary.
            // Without this, the field might still be null (beforefieldinit semantics
            // allow the CLR to defer initialization until first access).
            RuntimeHelpers.RunClassConstructor(artMgrType.TypeHandle);
            Log("Forced ArticulationManager static constructor");

            // --- Step 2: Get the AnimatorOutput field value ---
            var field = artMgrType.GetField("AnimatorOutput",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null)
            {
                Log("ERROR: Could not find AnimatorOutput field (was it renamed in an update?)");
                return;
            }

            // The field is: Dictionary<Handedness, ReplaySubject<BoneTransforms>>
            var dict = field.GetValue(null);
            if (dict == null)
            {
                Log("ERROR: AnimatorOutput is null after forcing static constructor");
                return;
            }

            // --- Step 3: Resolve types we need via reflection ---
            var handednessType = pipelineAssembly.GetType("StretchSense.Pipeline.Handedness");
            var boneTransformsType = pipelineAssembly.GetType("StretchSense.Pipeline.BoneTransforms");

            if (handednessType == null || boneTransformsType == null)
            {
                Log("ERROR: Could not find Handedness or BoneTransforms types");
                return;
            }

            // R3 should already be loaded as a dependency of StretchSense.Pipeline.
            // Find it in the loaded assemblies.
            Assembly r3Assembly = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "R3")
                {
                    r3Assembly = asm;
                    break;
                }
            }
            if (r3Assembly == null)
            {
                Log("ERROR: R3 assembly not loaded (expected as dependency of Pipeline)");
                return;
            }

            // Build the closed generic type: ReplaySubject<BoneTransforms>
            var replaySubjectOpen = r3Assembly.GetType("R3.ReplaySubject`1");
            if (replaySubjectOpen == null)
            {
                Log("ERROR: Could not find R3.ReplaySubject`1 type");
                return;
            }
            var replaySubjectType = replaySubjectOpen.MakeGenericType(boneTransformsType);

            // Get the constructor: ReplaySubject(int bufferSize)
            var ctor = replaySubjectType.GetConstructor(new[] { typeof(int) });
            if (ctor == null)
            {
                Log("ERROR: Could not find ReplaySubject(int bufferSize) constructor");
                return;
            }

            // Get Dispose(bool callOnCompleted) for cleanup of old subjects.
            // R3's ReplaySubject.Dispose(false) frees the buffer without notifying subscribers.
            var disposeMethod = replaySubjectType.GetMethod("Dispose", new[] { typeof(bool) });

            // --- Step 4: Swap the subjects for each hand ---
            var dictType = dict.GetType();
            var indexerProp = dictType.GetProperty("Item");             // dict[key] accessor
            var containsKeyMethod = dictType.GetMethod("ContainsKey");

            var leftValue = Enum.Parse(handednessType, "LEFT");
            var rightValue = Enum.Parse(handednessType, "RIGHT");

            int patched = 0;
            foreach (var hand in new[] { leftValue, rightValue })
            {
                // Verify the key exists (defensive — it should always be there)
                bool exists = (bool)containsKeyMethod.Invoke(dict, new[] { hand });
                if (!exists)
                {
                    Log($"WARNING: No entry for {hand} in AnimatorOutput dictionary");
                    continue;
                }

                // Get the old unbounded subject
                var oldSubject = indexerProp.GetValue(dict, new[] { hand });

                // Create replacement: ReplaySubject<BoneTransforms>(bufferSize: 1)
                // bufferSize=1 means only the latest value is retained for replay
                var newSubject = ctor.Invoke(new object[] { 1 });

                // Swap it in
                indexerProp.SetValue(dict, newSubject, new[] { hand });

                // Dispose old subject to free accumulated buffer memory.
                // Pass false = don't call OnCompleted on subscribers (they should keep working).
                if (oldSubject != null && disposeMethod != null)
                {
                    try { disposeMethod.Invoke(oldSubject, new object[] { false }); }
                    catch (Exception) { /* Disposal failure is non-fatal */ }
                }

                Log($"Replaced unbounded ReplaySubject for {hand} with bufferSize=1");
                patched++;
            }

            Log($"Patch complete - {patched}/2 ReplaySubjects replaced. Memory leak fixed!");
        }

        /// <summary>
        /// Simple file logger. We can't use Unity's Debug.Log this early in the boot process,
        /// and we don't have BepInEx's logging infrastructure, so we write to a plain text file.
        /// </summary>
        private static void Log(string message)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
            catch
            {
                // If we can't write logs, there's nothing we can do — fail silently
            }
        }
    }
}
