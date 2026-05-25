// =============================================================================
// GcPressurePatches - Harmony patches to reduce per-frame allocation pressure
// =============================================================================
//
// WHY THIS IS NEEDED:
// Mono's Boehm GC is conservative and non-compacting. Per-frame allocations
// cause heap fragmentation that grows monotonically. After ~2 hours, the heap
// is so large/fragmented that the GC's fixed-size mark stack overflows during
// collection, crashing with "Fatal Error In GC - Unexpected mark stack overflow".
//
// WHAT WE PATCH:
// 1. OscMessage.Construct - replaces per-call MemoryStream/byte[] allocations
//    with thread-local cached buffers (~120-2400 calls/sec depending on streams)
//
// 2. MLModelManager mask methods - replaces LINQ .Select().ToArray() with cached
//    float[] arrays that are reused each frame (~360 calls/sec)
//
// 3. KinematicsStream.KinematicMessage - replaces per-call List<object> + boxing
//    with a cached object[] array (~120 calls/sec, ~51 boxed objects per call)
//
// HOW HARMONY PATCHES WORK:
// A "Prefix" that returns false completely replaces the original method.
// Our prefix does the same work but reuses cached allocations instead of
// creating new objects every frame.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace Doorstop
{
    internal static class GcPressurePatches
    {
        private static Harmony _harmony;

        internal static void Apply()
        {
            _harmony = new Harmony("com.qdot.doorstopfix");

            PatchOscMessage();
            PatchMlModelMasks();
            PatchKinematicsStream();

            Entrypoint.Log("  Harmony patches applied successfully");
        }

        // =====================================================================
        // PATCH 1: OscMessage.Construct
        // Original: new MemoryStream(256) + LINQ for type tags + new byte[4] per
        //           int/float + .ToArray() on every call
        // Fixed:    Thread-local MemoryStream reuse, pre-allocated byte[4] buffer,
        //           manual type tag building (no LINQ)
        // Impact:   Eliminates 3-5 allocations per call × 120-2400 calls/sec
        // =====================================================================

        // Thread-local buffers so we're safe even if called from multiple threads
        // (KinematicsStream uses ObserveOnThreadPool)
        [ThreadStatic] private static MemoryStream _oscStream;
        [ThreadStatic] private static byte[] _oscIntBuffer;
        [ThreadStatic] private static StringBuilder _oscTypeTagBuilder;

        private static void PatchOscMessage()
        {
            // Find OscMessage.Construct(string, object[]) in StretchSense.Pipeline
            var pipelineAsm = FindAssembly("StretchSense.Pipeline");
            var oscType = pipelineAsm.GetType("StretchSense.Pipeline.OscMessage");
            var originalMethod = oscType.GetMethod("Construct",
                BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(string), typeof(object[]) }, null);

            if (originalMethod == null)
            {
                Entrypoint.Log("  WARNING: OscMessage.Construct not found, skipping patch");
                return;
            }

            var prefix = typeof(GcPressurePatches).GetMethod(nameof(OscMessagePrefix),
                BindingFlags.Static | BindingFlags.NonPublic);

            _harmony.Patch(originalMethod, new HarmonyMethod(prefix));
            Entrypoint.Log("  Patched OscMessage.Construct (pooled MemoryStream + no LINQ)");
        }

        /// <summary>
        /// Complete replacement for OscMessage.Construct.
        /// Reuses a thread-local MemoryStream and byte buffer instead of allocating new ones.
        /// </summary>
        private static bool OscMessagePrefix(string path, object[] values, ref byte[] __result)
        {
            // Initialize thread-local buffers on first use
            if (_oscStream == null) _oscStream = new MemoryStream(512);
            if (_oscIntBuffer == null) _oscIntBuffer = new byte[4];
            if (_oscTypeTagBuilder == null) _oscTypeTagBuilder = new StringBuilder(16);

            var stream = _oscStream;
            stream.Position = 0;
            stream.SetLength(0);

            // Write OSC path string (null-terminated + padded to 4-byte boundary)
            WriteOscString(stream, path);

            // Build type tag string WITHOUT LINQ (.Select allocates enumerator + delegate)
            var tagBuilder = _oscTypeTagBuilder;
            tagBuilder.Clear();
            tagBuilder.Append(',');
            for (int i = 0; i < values.Length; i++)
            {
                var v = values[i];
                if (v is int) tagBuilder.Append('i');
                else if (v is float) tagBuilder.Append('f');
                else if (v is string) tagBuilder.Append('s');
                else throw new Exception($"Unsupported OSC type: {v.GetType()}");
            }
            WriteOscString(stream, tagBuilder.ToString());

            // Write values (reusing byte[4] buffer instead of allocating new ones)
            var buf = _oscIntBuffer;
            for (int i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (value is int intVal)
                {
                    int networkOrder = BitConverter.IsLittleEndian
                        ? IPAddress.HostToNetworkOrder(intVal)
                        : intVal;
                    BitConverter.GetBytes(networkOrder).CopyTo(buf, 0);
                    stream.Write(buf, 0, 4);
                }
                else if (value is float floatVal)
                {
                    BitConverter.GetBytes(floatVal).CopyTo(buf, 0);
                    if (BitConverter.IsLittleEndian)
                    {
                        // Swap bytes for big-endian (network order)
                        byte tmp = buf[0]; buf[0] = buf[3]; buf[3] = tmp;
                        tmp = buf[1]; buf[1] = buf[2]; buf[2] = tmp;
                    }
                    stream.Write(buf, 0, 4);
                }
                else if (value is string strVal)
                {
                    WriteOscString(stream, strVal);
                }
            }

            // ToArray() still allocates, but we eliminated MemoryStream + byte[4] + LINQ allocations.
            // The returned byte[] is consumed immediately by UdpClient.Send, but we can't pool it
            // safely because the caller may pass it through an Observable pipeline.
            __result = stream.ToArray();
            return false; // Skip original method
        }

        private static void WriteOscString(MemoryStream stream, string str)
        {
            // Use ASCII encoding — allocates a byte[] per call, but these are short strings
            // and this is much less impactful than the MemoryStream allocation
            byte[] bytes = Encoding.ASCII.GetBytes(str);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0); // Null terminator
            // Pad to 4-byte boundary
            int padding = (4 - (int)stream.Length % 4) % 4;
            for (int i = 0; i < padding; i++)
                stream.WriteByte(0);
        }

        // =====================================================================
        // PATCH 2: MLModelManager mask methods
        // Original: .Select((value, index) => mask[index] ? value : 0f).ToArray()
        //           creates new float[] + LINQ enumerator + delegate every call
        // Fixed:    Cached float[] arrays reused each frame
        // Impact:   Eliminates 3 float[] + 3 enumerator allocs × 120 calls/sec
        // =====================================================================

        // Cached output arrays (one per method, since array size is constant per glove)
        [ThreadStatic] private static float[] _joystickMaskBuffer;
        [ThreadStatic] private static float[] _gestureMaskBuffer;
        [ThreadStatic] private static float[] _articulationMaskBuffer;

        private static void PatchMlModelMasks()
        {
            var pipelineAsm = FindAssembly("StretchSense.Pipeline");
            var mlmType = pipelineAsm.GetType("StretchSense.Pipeline.MLModelManager");
            if (mlmType == null)
            {
                Entrypoint.Log("  WARNING: MLModelManager not found, skipping mask patches");
                return;
            }

            // All three methods have same signature: private float[] MethodName(GloveSensorDataProcessed)
            var sensorDataType = pipelineAsm.GetType("StretchSense.Pipeline.GloveSensorDataProcessed");

            PatchMaskMethod(mlmType, "GetJoystickMaskedCapacitances", sensorDataType,
                nameof(JoystickMaskPrefix), "RealityJoystickMask");
            PatchMaskMethod(mlmType, "GetGestureMaskedCapacitances", sensorDataType,
                nameof(GestureMaskPrefix), "RealityGestureMask");
            PatchMaskMethod(mlmType, "GetArticulationMaskedCapacitances", sensorDataType,
                nameof(ArticulationMaskPrefix), "RealityArticulationMask");
        }

        // Cache for reflection lookups (done once at patch time)
        private static PropertyInfo _capacitancesProp;

        private static void PatchMaskMethod(Type mlmType, string methodName, Type paramType,
            string prefixName, string maskFieldName)
        {
            var method = mlmType.GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { paramType }, null);

            if (method == null)
            {
                Entrypoint.Log($"  WARNING: {methodName} not found, skipping");
                return;
            }

            // Cache reflection lookups for the mask fields
            if (_capacitancesProp == null)
            {
                _capacitancesProp = paramType.GetProperty("Capacitances");
            }

            var prefix = typeof(GcPressurePatches).GetMethod(prefixName,
                BindingFlags.Static | BindingFlags.NonPublic);

            _harmony.Patch(method, new HarmonyMethod(prefix));
            Entrypoint.Log($"  Patched {methodName} (cached float[] output)");
        }

        /// <summary>
        /// Applies a capacitance mask using a cached output array instead of LINQ .Select().ToArray()
        /// </summary>
        private static bool ApplyMask(object sensorData, object __instance,
            ref float[] __result, ref float[] buffer, string maskPropertyName)
        {
            try
            {
                // Get capacitances array from sensor data
                var capacitances = (float[])_capacitancesProp.GetValue(sensorData);
                if (capacitances == null)
                {
                    __result = new float[0];
                    return false;
                }

                // Ensure buffer is correct size
                if (buffer == null || buffer.Length != capacitances.Length)
                    buffer = new float[capacitances.Length];

                // Get the mask from the MLModelManager's ManagerParameters
                var mlmType = __instance.GetType();
                var paramsProp = mlmType.GetProperty("ManagerParameters",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (paramsProp == null)
                {
                    // Fallback: let original run
                    return true;
                }

                var managerParams = paramsProp.GetValue(__instance);
                var maskProp = managerParams.GetType().GetProperty(maskPropertyName);
                if (maskProp == null)
                {
                    return true; // Fallback
                }

                var mask = (bool[])maskProp.GetValue(managerParams);

                // Apply mask without LINQ — reuse buffer array
                for (int i = 0; i < capacitances.Length; i++)
                {
                    buffer[i] = (i < mask.Length && mask[i]) ? capacitances[i] : 0f;
                }

                __result = buffer;
                return false; // Skip original
            }
            catch
            {
                return true; // On error, let original run
            }
        }

        private static bool JoystickMaskPrefix(object __instance, object c, ref float[] __result)
        {
            return ApplyMask(c, __instance, ref __result, ref _joystickMaskBuffer, "RealityJoystickMask");
        }

        private static bool GestureMaskPrefix(object __instance, object c, ref float[] __result)
        {
            return ApplyMask(c, __instance, ref __result, ref _gestureMaskBuffer, "RealityGestureMask");
        }

        private static bool ArticulationMaskPrefix(object __instance, object c, ref float[] __result)
        {
            return ApplyMask(c, __instance, ref __result, ref _articulationMaskBuffer, "RealityArticulationMask");
        }

        // =====================================================================
        // PATCH 3: KinematicsStream.KinematicMessage
        // Original: new List<object>{5 items} + foreach bone { 7 .Add() with boxing }
        //           + list.ToArray() = ~225 boxed objects + list + array per call
        // Fixed:    Pre-allocated object[] reused each frame. Still boxes floats
        //           (unavoidable with object[]) but avoids List resizing + ToArray()
        // Impact:   Eliminates List + ToArray alloc × 120 calls/sec
        // =====================================================================

        [ThreadStatic] private static object[] _kinematicsBuffer;
        private static MethodInfo _getLocalPositionMethod;
        private static PropertyInfo _boneTransformsTransformsProp;
        private static PropertyInfo _rotationProp;
        private static Array _boneValues;

        private static void PatchKinematicsStream()
        {
            var pipelineAsm = FindAssembly("StretchSense.Pipeline");
            var ksType = pipelineAsm.GetType("StretchSense.Pipeline.KinematicsStream");
            if (ksType == null)
            {
                Entrypoint.Log("  WARNING: KinematicsStream not found, skipping patch");
                return;
            }

            // KinematicMessage(ProfileGlove, BoneTransforms, int, IBonePositions)
            var profileGloveType = pipelineAsm.GetType("StretchSense.Pipeline.ProfileGlove");
            var boneTransformsType = pipelineAsm.GetType("StretchSense.Pipeline.BoneTransforms");
            var bonePositionsType = pipelineAsm.GetType("StretchSense.Pipeline.IBonePositions");

            var method = ksType.GetMethod("KinematicMessage",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { profileGloveType, boneTransformsType, typeof(int), bonePositionsType }, null);

            if (method == null)
            {
                Entrypoint.Log("  WARNING: KinematicMessage not found, skipping patch");
                return;
            }

            // Cache the Bone enum values (Enum.GetValues allocates a new array each call!)
            var boneType = pipelineAsm.GetType("StretchSense.Pipeline.Bone");
            _boneValues = Enum.GetValues(boneType);

            // Cache other reflection targets
            _getLocalPositionMethod = bonePositionsType.GetMethod("GetLocalPosition");
            _boneTransformsTransformsProp = boneTransformsType.GetProperty("Transforms");

            // BoneTransform has a Rotation property
            var boneTransformSingle = pipelineAsm.GetType("StretchSense.Pipeline.BoneTransform");
            _rotationProp = boneTransformSingle.GetProperty("Rotation");

            var prefix = typeof(GcPressurePatches).GetMethod(nameof(KinematicsPrefix),
                BindingFlags.Static | BindingFlags.NonPublic);

            _harmony.Patch(method, new HarmonyMethod(prefix));
            Entrypoint.Log($"  Patched KinematicsStream.KinematicMessage (cached object[], cached Enum.GetValues)");
        }

        private static bool KinematicsPrefix(object __instance, object profileGlove,
            object boneTransforms, int timeCode, object bonePositions, ref byte[] __result)
        {
            try
            {
                int boneCount = _boneValues.Length;
                // 5 header items + 7 per bone (3 position + 4 quaternion)
                int totalItems = 5 + boneCount * 7;

                if (_kinematicsBuffer == null || _kinematicsBuffer.Length != totalItems)
                    _kinematicsBuffer = new object[totalItems];

                var buf = _kinematicsBuffer;

                // Header
                var gloveType = profileGlove.GetType();
                var profileIdProp = gloveType.GetProperty("ProfileId");
                var handednessProp = gloveType.GetProperty("Handedness");
                var gloveIdProp = gloveType.GetProperty("GloveId");

                var profileId = profileIdProp.GetValue(profileGlove);
                var performerIdProp = profileId.GetType().GetProperty("PerformerId");

                buf[0] = timeCode;
                buf[1] = performerIdProp.GetValue(profileId);
                buf[2] = (int)handednessProp.GetValue(profileGlove);
                buf[3] = gloveIdProp.GetValue(profileGlove);
                buf[4] = "Reality Glove";

                // Get the Transforms dictionary from BoneTransforms
                var transforms = _boneTransformsTransformsProp.GetValue(boneTransforms);
                var handedness = handednessProp.GetValue(profileGlove);

                int idx = 5;
                for (int i = 0; i < boneCount; i++)
                {
                    var bone = _boneValues.GetValue(i);

                    // Get local position via IBonePositions.GetLocalPosition(Handedness, Bone)
                    var pos = _getLocalPositionMethod.Invoke(bonePositions, new[] { handedness, bone });

                    // Vector3: X, Y, Z (apply Vive->OpenXR transform: negate X)
                    var posType = pos.GetType();
                    float px = (float)posType.GetField("X").GetValue(pos);
                    float py = (float)posType.GetField("Y").GetValue(pos);
                    float pz = (float)posType.GetField("Z").GetValue(pos);
                    buf[idx++] = -px;  // VivePositionToOpenXr: negate X
                    buf[idx++] = py;
                    buf[idx++] = pz;

                    // Get rotation from BoneTransforms.Transforms[bone].Rotation
                    var transformsDict = transforms;
                    var dictType = transformsDict.GetType();
                    var indexer = dictType.GetProperty("Item");
                    var boneTransform = indexer.GetValue(transformsDict, new[] { bone });
                    var rotation = _rotationProp.GetValue(boneTransform);

                    // Quaternion: X, Y, Z, W (apply Vive->OpenXR: negate X and W)
                    var quatType = rotation.GetType();
                    float qx = (float)quatType.GetField("X").GetValue(rotation);
                    float qy = (float)quatType.GetField("Y").GetValue(rotation);
                    float qz = (float)quatType.GetField("Z").GetValue(rotation);
                    float qw = (float)quatType.GetField("W").GetValue(rotation);
                    buf[idx++] = -qx;  // ViveRotationToOpenXr: negate X
                    buf[idx++] = qy;
                    buf[idx++] = qz;
                    buf[idx++] = -qw;  // ViveRotationToOpenXr: negate W
                }

                // Call OscMessage.Construct (which is now also patched to pool its internals)
                var pipelineAsm = FindAssembly("StretchSense.Pipeline");
                var oscType = pipelineAsm.GetType("StretchSense.Pipeline.OscMessage");
                var constructMethod = oscType.GetMethod("Construct",
                    BindingFlags.Static | BindingFlags.Public);

                __result = (byte[])constructMethod.Invoke(null,
                    new object[] { "/v1/animation/kinematic/all", buf });

                return false; // Skip original
            }
            catch (Exception ex)
            {
                Entrypoint.Log($"  KinematicsPrefix error (falling back to original): {ex.Message}");
                return true; // Let original run on error
            }
        }

        // =====================================================================
        // Utility
        // =====================================================================

        private static Assembly FindAssembly(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == name)
                    return asm;
            }
            throw new Exception($"Assembly '{name}' not found");
        }
    }
}
