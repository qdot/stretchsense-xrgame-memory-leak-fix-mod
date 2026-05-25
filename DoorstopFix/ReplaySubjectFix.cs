// =============================================================================
// ReplaySubjectFix - Patches unbounded ReplaySubject<BoneTransforms> instances
// =============================================================================
//
// THE BUG:
// ArticulationManager.AnimatorOutput contains two ReplaySubject<BoneTransforms>()
// with no buffer size (defaults to int.MaxValue in R3). Every frame during
// calibration, OnNext() is called, storing values forever.
//
// THE FIX:
// Replace with ReplaySubject<BoneTransforms>(1) which only keeps the latest value.
// This is semantically correct — subscribers only need the most recent frame.
//
// TIMING:
// Applied when StretchSense.Pipeline.dll loads, before any game code uses the type.
// We force the static constructor to initialize the field, then swap the values.
// =============================================================================

using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Doorstop
{
    internal static class ReplaySubjectFix
    {
        internal static void Apply(Assembly pipelineAssembly)
        {
            // --- Resolve types ---
            var artMgrType = pipelineAssembly.GetType("StretchSense.Pipeline.ArticulationManager")
                ?? throw new Exception("ArticulationManager type not found");

            var handednessType = pipelineAssembly.GetType("StretchSense.Pipeline.Handedness")
                ?? throw new Exception("Handedness type not found");

            var boneTransformsType = pipelineAssembly.GetType("StretchSense.Pipeline.BoneTransforms")
                ?? throw new Exception("BoneTransforms type not found");

            // Force static constructor — ensures AnimatorOutput field is initialized
            RuntimeHelpers.RunClassConstructor(artMgrType.TypeHandle);
            Entrypoint.Log("  Forced ArticulationManager static constructor");

            // --- Get the AnimatorOutput field ---
            var field = artMgrType.GetField("AnimatorOutput",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new Exception("AnimatorOutput field not found");

            var dict = field.GetValue(null)
                ?? throw new Exception("AnimatorOutput is null");

            // --- Find R3's ReplaySubject<BoneTransforms> ---
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
                throw new Exception("R3 assembly not loaded");

            var replaySubjectOpen = r3Assembly.GetType("R3.ReplaySubject`1")
                ?? throw new Exception("R3.ReplaySubject`1 not found");

            var replaySubjectType = replaySubjectOpen.MakeGenericType(boneTransformsType);
            var ctor = replaySubjectType.GetConstructor(new[] { typeof(int) })
                ?? throw new Exception("ReplaySubject(int) constructor not found");
            var disposeMethod = replaySubjectType.GetMethod("Dispose", new[] { typeof(bool) });

            // --- Swap subjects for each hand ---
            var dictType = dict.GetType();
            var indexerProp = dictType.GetProperty("Item");
            var containsKeyMethod = dictType.GetMethod("ContainsKey");

            var leftValue = Enum.Parse(handednessType, "LEFT");
            var rightValue = Enum.Parse(handednessType, "RIGHT");

            int patched = 0;
            foreach (var hand in new[] { leftValue, rightValue })
            {
                if (!(bool)containsKeyMethod.Invoke(dict, new[] { hand }))
                {
                    Entrypoint.Log($"  WARNING: No entry for {hand}");
                    continue;
                }

                var oldSubject = indexerProp.GetValue(dict, new[] { hand });

                // Create bounded replacement: ReplaySubject<BoneTransforms>(bufferSize: 1)
                var newSubject = ctor.Invoke(new object[] { 1 });
                indexerProp.SetValue(dict, newSubject, new[] { hand });

                // Dispose old subject (false = don't notify subscribers with OnCompleted)
                if (oldSubject != null && disposeMethod != null)
                {
                    try { disposeMethod.Invoke(oldSubject, new object[] { false }); }
                    catch { }
                }

                Entrypoint.Log($"  Replaced ReplaySubject for {hand} with bufferSize=1");
                patched++;
            }

            Entrypoint.Log($"  ReplaySubject fix complete: {patched}/2 replaced");
        }
    }
}
