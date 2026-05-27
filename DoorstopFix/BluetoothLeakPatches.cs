using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Doorstop
{
    internal static class BluetoothLeakPatches
    {
        private static readonly object ConnectLock = new object();
        private static readonly Dictionary<string, DateTime> LastConnectAttempt = new Dictionary<string, DateTime>();
        private static readonly TimeSpan DuplicateConnectWindow = TimeSpan.FromSeconds(60);

        private static FieldInfo _glovesField;
        private static FieldInfo _clientField;
        private static FieldInfo _cachedCharacteristicsField;
        private static FieldInfo _subscriptionsField;
        private static FieldInfo _handleMapField;
        private static FieldInfo _isCharacteristicChangedAttachedField;

        internal static void Apply()
        {
            var harmony = new Harmony("com.qdot.doorstopfix.bluetooth");
            var runtimeAsm = FindAssembly("StretchSense.CompanionApp.Runtime");

            var bleManagerType = runtimeAsm.GetType("StretchSense.BleManager")
                ?? throw new Exception("StretchSense.BleManager not found");

            var pipelineAsm = FindAssembly("StretchSense.Pipeline");
            var bleDeviceInfoType = pipelineAsm.GetType("StretchSense.Pipeline.BleDeviceInfo")
                ?? throw new Exception("StretchSense.Pipeline.BleDeviceInfo not found");

            _glovesField = AccessTools.Field(bleManagerType, "Gloves")
                ?? throw new Exception("BleManager.Gloves field not found");

            var connectMethod = AccessTools.Method(bleManagerType, "Connect", new[] { bleDeviceInfoType })
                ?? throw new Exception("BleManager.Connect(BleDeviceInfo) not found");

            harmony.Patch(connectMethod, prefix: new HarmonyMethod(
                typeof(BluetoothLeakPatches).GetMethod(nameof(BleManagerConnectPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic)));

            var gloveWindowsType = runtimeAsm.GetType("StretchSense.BleRealityGloveWindows");

            if (gloveWindowsType != null)
            {
                CacheWindowsGloveFields(gloveWindowsType);

                var disconnectMethod = AccessTools.Method(gloveWindowsType, "Disconnect")
                    ?? throw new Exception("BleRealityGloveWindows.Disconnect not found");

                harmony.Patch(disconnectMethod, prefix: new HarmonyMethod(
                    typeof(BluetoothLeakPatches).GetMethod(nameof(BleRealityGloveWindowsDisconnectPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic)));

                Entrypoint.Log("  Patched BLE duplicate connect guard and explicit notification cleanup");
            }
            else
            {
                Entrypoint.Log("  Patched BLE duplicate connect guard (Windows glove type not found)");
            }
        }

        private static bool BleManagerConnectPrefix(object __instance, object deviceInfo)
        {
            try
            {
                var handedness = GetFieldValue(deviceInfo, "Handedness");
                var uuid = (long)GetFieldValue(deviceInfo, "Uuid");
                var name = (string)GetFieldValue(deviceInfo, "Name");
                var key = handedness + ":" + uuid;
                var now = DateTime.UtcNow;

                if (TryGetExistingGlove(handedness, out var existingGlove) && existingGlove != null)
                {
                    // The app can issue repeated connects for an already-known glove.
                    // Disconnect stale objects first so subscriptions and cached state do
                    // not accumulate across retries.
                    var existingUuid = GetExistingGloveUuid(existingGlove);
                    var existingConnected = GetExistingGloveIsConnected(existingGlove);

                    if (existingUuid == uuid)
                    {
                        if (existingConnected)
                        {
                            Entrypoint.Log($"  BLE connect skipped; {name} ({key}) is already connected");
                            return false;
                        }

                        lock (ConnectLock)
                        {
                            if (LastConnectAttempt.TryGetValue(key, out var lastAttempt) &&
                                now - lastAttempt < DuplicateConnectWindow)
                            {
                                Entrypoint.Log($"  BLE duplicate connect skipped for {name} ({key}); previous attempt {(now - lastAttempt).TotalSeconds:F1}s ago");
                                return false;
                            }

                            LastConnectAttempt[key] = now;
                        }

                        Entrypoint.Log($"  BLE stale connect state for {name} ({key}); disconnecting old glove before retry");
                        DisconnectGlove(existingGlove);
                        return true;
                    }

                    Entrypoint.Log($"  BLE replacing existing {handedness} glove ({existingUuid}) with {uuid}; disconnecting old glove first");
                    DisconnectGlove(existingGlove);
                }

                lock (ConnectLock)
                {
                    LastConnectAttempt[key] = now;
                }

                return true;
            }
            catch (Exception ex)
            {
                Entrypoint.Log($"  BLE connect guard error; allowing original Connect: {ex.Message}");
                return true;
            }
        }

        private static void BleRealityGloveWindowsDisconnectPrefix(object __instance)
        {
            try
            {
                ExplicitlyUnsubscribeNotifications(__instance);
            }
            catch (Exception ex)
            {
                Entrypoint.Log($"  BLE notification cleanup error; continuing Disconnect: {ex.Message}");
            }
        }

        private static bool TryGetExistingGlove(object handedness, out object glove)
        {
            glove = null;

            var glovesSubject = _glovesField.GetValue(null);
            var valueProp = glovesSubject.GetType().GetProperty("Value");
            var gloves = valueProp?.GetValue(glovesSubject) as IDictionary;
            if (gloves == null || !gloves.Contains(handedness))
            {
                return false;
            }

            glove = gloves[handedness];
            return true;
        }

        private static long GetExistingGloveUuid(object glove)
        {
            var state = GetExistingGloveState(glove);
            return (long)state.GetType().GetField("Uuid").GetValue(state);
        }

        private static bool GetExistingGloveIsConnected(object glove)
        {
            var state = GetExistingGloveState(glove);
            return (bool)state.GetType().GetField("IsConnected").GetValue(state);
        }

        private static object GetExistingGloveState(object glove)
        {
            var gloveStateProp = glove.GetType().GetProperty("GloveState");
            var reactiveProperty = gloveStateProp.GetValue(glove);
            return reactiveProperty.GetType().GetProperty("Value").GetValue(reactiveProperty);
        }

        private static void DisconnectGlove(object glove)
        {
            try
            {
                glove.GetType().GetMethod("Disconnect")?.Invoke(glove, null);
            }
            catch (Exception ex)
            {
                Entrypoint.Log($"  BLE disconnect of previous glove failed: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private static void ExplicitlyUnsubscribeNotifications(object glove)
        {
            var client = _clientField?.GetValue(glove);
            var cachedCharacteristics = _cachedCharacteristicsField?.GetValue(glove) as IDictionary;
            if (client == null || cachedCharacteristics == null || cachedCharacteristics.Count == 0)
            {
                return;
            }

            int attempted = 0;
            foreach (DictionaryEntry entry in cachedCharacteristics)
            {
                // The vendor BLE library keeps notification handlers internally; call
                // its unsubscribe API before clearing our reflected caches.
                var characteristic = entry.Value;
                if (characteristic == null)
                {
                    continue;
                }

                attempted++;
                InvokeUnsubscribeFromNotifications(client, characteristic);
            }

            (_subscriptionsField?.GetValue(glove) as IDictionary)?.Clear();
            (_handleMapField?.GetValue(glove) as IDictionary)?.Clear();
            cachedCharacteristics.Clear();
            _isCharacteristicChangedAttachedField?.SetValue(glove, false);

            if (attempted > 0)
            {
                Entrypoint.Log($"  BLE explicitly unsubscribed {attempted} notification characteristic(s)");
            }
        }

        private static void InvokeUnsubscribeFromNotifications(object client, object characteristic)
        {
            var method = AccessTools.Method(client.GetType(), "UnsubscribeFromNotifications");
            if (method == null)
            {
                Entrypoint.Log("  WARNING: wclGattClient.UnsubscribeFromNotifications not found");
                return;
            }

            var parameters = method.GetParameters();
            var args = new object[parameters.Length];
            args[0] = characteristic;
            for (int i = 1; i < parameters.Length; i++)
            {
                args[i] = parameters[i].ParameterType.IsEnum
                    ? Enum.GetValues(parameters[i].ParameterType).GetValue(0)
                    : Type.Missing;
            }

            var result = method.Invoke(client, args);
            if (result is int code && code != 0)
            {
                Entrypoint.Log($"  BLE unsubscribe returned 0x{code:X8}");
            }
        }

        private static void CacheWindowsGloveFields(Type gloveWindowsType)
        {
            _clientField = AccessTools.Field(gloveWindowsType, "_client");
            _cachedCharacteristicsField = AccessTools.Field(gloveWindowsType, "_cachedCharacteristics");
            _subscriptionsField = AccessTools.Field(gloveWindowsType, "_subscriptions");
            _handleMapField = AccessTools.Field(gloveWindowsType, "_handleMap");
            _isCharacteristicChangedAttachedField = AccessTools.Field(gloveWindowsType, "_isOnCharacteristicChangedAttached");
        }

        private static object GetFieldValue(object instance, string fieldName)
        {
            return instance.GetType().GetField(fieldName).GetValue(instance);
        }

        private static Assembly FindAssembly(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == name)
                {
                    return asm;
                }
            }

            throw new Exception($"Assembly '{name}' not found");
        }

    }
}
