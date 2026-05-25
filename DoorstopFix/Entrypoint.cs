// =============================================================================
// DoorstopFix - Memory Leak & GC Pressure Fix for StretchSense XR Game
// =============================================================================
//
// This mod fixes two categories of issues:
//
// 1. MEMORY LEAK: Unbounded ReplaySubject<BoneTransforms> in ArticulationManager
//    (fixed via reflection - no dependencies needed)
//
// 2. GC PRESSURE: Per-frame allocations that overflow Mono's Boehm GC mark stack
//    (fixed via HarmonyX method patches - loaded AFTER game assemblies are ready)
//
// IMPORTANT ARCHITECTURE NOTE:
// MonoMod (which Harmony uses) hooks into Mono's JIT when it loads. This
// interferes with AppDomain.AssemblyLoad events if loaded too early. Therefore:
//   - Phase 1 (early): ReplaySubject fix via pure reflection (no Harmony)
//   - Phase 2 (deferred): Harmony patches loaded ONLY after game assemblies exist
// =============================================================================

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

// ReSharper disable InconsistentNaming (Harmony conventions use __instance, __result)
namespace Doorstop
{
    public class Entrypoint
    {
        private static readonly string LogPath = Path.Combine(
            Path.GetDirectoryName(typeof(Entrypoint).Assembly.Location) ?? ".",
            "doorstop_fix.log");

        internal static readonly string OurDirectory =
            Path.GetDirectoryName(typeof(Entrypoint).Assembly.Location) ?? ".";

        public static void Start()
        {
            Log("DoorstopFix v2.0 loaded - waiting for game assemblies...");
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private static bool _applied = false;
        private static bool _replaySubjectFixed = false;

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            var name = args.LoadedAssembly.GetName().Name;

            // Wait for CompanionApp.Runtime — it depends on Pipeline, so when it loads
            // we know all game assemblies are available. Pipeline's AssemblyLoad event
            // sometimes doesn't fire separately on Mono (loaded as transitive dep).
            if (name == "StretchSense.CompanionApp.Runtime" && !_applied)
            {
                _applied = true;
                Log("StretchSense.CompanionApp.Runtime loaded");

                // Phase 1: Apply ReplaySubject fix immediately (pure reflection)
                try
                {
                    // Find Pipeline assembly (already loaded as dependency)
                    Assembly pipelineAsm = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "StretchSense.Pipeline")
                        {
                            pipelineAsm = asm;
                            break;
                        }
                    }

                    if (pipelineAsm != null)
                    {
                        ReplaySubjectFix.Apply(pipelineAsm);
                        _replaySubjectFixed = true;
                    }
                    else
                    {
                        Log("  Pipeline not yet enumerable - will retry in deferred phase");
                    }
                }
                catch (Exception ex)
                {
                    Log($"ERROR in ReplaySubject fix: {ex}");
                }

                // Unsubscribe before loading Harmony
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;

                // Phase 2: Defer all remaining patches to a thread pool thread.
                // This ensures the current assembly load chain completes first,
                // and prevents Harmony's runtime patching from interfering with Mono's
                // assembly loader.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    // Wait for Unity to finish loading all assemblies
                    Thread.Sleep(2000);

                    // Retry ReplaySubject fix if it wasn't applied yet
                    // (Pipeline may not have been enumerable during the synchronous phase)
                    if (!_replaySubjectFixed)
                    {
                        try
                        {
                            Assembly pipelineAsm2 = null;
                            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                if (asm.GetName().Name == "StretchSense.Pipeline")
                                {
                                    pipelineAsm2 = asm;
                                    break;
                                }
                            }
                            if (pipelineAsm2 != null)
                            {
                                ReplaySubjectFix.Apply(pipelineAsm2);
                                _replaySubjectFixed = true;
                            }
                            else
                            {
                                Log("  ERROR: Pipeline assembly STILL not found");
                            }
                        }
                        catch (Exception ex2)
                        {
                            Log($"ERROR in deferred ReplaySubject fix: {ex2}");
                        }
                    }

                    ApplyHarmonyPatches();
                });
            }
        }

        /// <summary>
        /// Loads Harmony and applies GC pressure patches.
        /// Called on a background thread AFTER game assemblies are loaded.
        /// Separated into its own method with NoInlining to prevent the JIT from
        /// eagerly resolving Harmony types during earlier code paths.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyHarmonyPatches()
        {
            Log("Applying Harmony patches (deferred)...");

            // NOW it's safe to register the assembly resolver for Harmony's deps
            AppDomain.CurrentDomain.AssemblyResolve += ResolveOurDependencies;

            try
            {
                GcPressurePatches.Apply();
            }
            catch (Exception ex)
            {
                Log($"ERROR in Harmony patches: {ex}");
            }
        }

        /// <summary>
        /// Resolves Harmony and its dependencies (MonoMod, Cecil) from our directory.
        /// Only active AFTER game assemblies are loaded.
        /// </summary>
        private static Assembly ResolveOurDependencies(object sender, ResolveEventArgs args)
        {
            var assemblyName = new AssemblyName(args.Name).Name;
            var path = Path.Combine(OurDirectory, assemblyName + ".dll");
            if (File.Exists(path))
            {
                return Assembly.LoadFrom(path);
            }
            return null;
        }

        public static void Log(string message)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
            catch { }
        }
    }
}
