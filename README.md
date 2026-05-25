# XR Game Memory Leak Fix

[![Patreon donate button](https://img.shields.io/badge/kofi-donate-green.svg)](https://www.ko-fi.com/qdot76367)
[![Patreon donate button](https://img.shields.io/badge/patreon-donate-yellow.svg)](https://www.patreon.com/qdot)
[![Github donate button](https://img.shields.io/badge/github-donate-ff69b4.svg)](https://www.github.com/sponsors/qdot)
[![bluesky](https://img.shields.io/bluesky/followers/buttplug.engineer)](https://bsky.app/profile/buttplug.engineer)

A runtime patch for the [StretchSense XR Game](https://stretchsense.com/) that fixes a memory leak and GC crash caused by issues in the hand-tracking pipeline.

## Support

If you have any issues with the mod, I'm qdot on discord, am around the Stretchsense server. As I've just gotten back to using my gloves, I'm not sure if this is the fix to the leak everyone has been seeing, but it does seem to have reduced memory footprint growth for me.

## The Problems

### 1. Memory Leak: Unbounded ReplaySubjects

`ArticulationManager` in `StretchSense.Pipeline.dll` declares two `ReplaySubject<BoneTransforms>` instances without specifying a buffer size:

```csharp
private static readonly Dictionary<Handedness, ReplaySubject<BoneTransforms>> AnimatorOutput = new()
{
    { Handedness.LEFT,  new ReplaySubject<BoneTransforms>() },   // unbounded!
    { Handedness.RIGHT, new ReplaySubject<BoneTransforms>() }    // unbounded!
};
```

R3's `ReplaySubject<T>()` constructor defaults to `bufferSize = int.MaxValue` — it stores **every value ever emitted** and never trims. During glove calibration (PRECAPTURE state), `AnimatorOutput` receives a `BoneTransforms` object every frame — ~120 objects/second accumulating forever.

### 2. GC Crash: Per-Frame Allocation Pressure

Multiple hot paths allocate objects every frame that, over ~2 hours of use, cause Mono's Boehm GC to crash with **"Fatal Error In GC - Unexpected mark stack overflow"**:

| Hotspot | Allocations/sec | Issue |
|---------|----------------|-------|
| `OscMessage.Construct` | 120-2400 | `new MemoryStream` + LINQ + `byte[4]` per call |
| `KinematicsStream.KinematicMessage` | 120 | `new List<object>` + 51 boxed floats + `.ToArray()` |
| `MLModelManager` mask methods | 360 | `.Select().ToArray()` creating new `float[]` each call |

Mono's Boehm GC is conservative and non-compacting. These allocations cause heap fragmentation that grows monotonically until the GC's internal mark stack overflows during collection.

### Symptoms

- Memory climbs steadily during calibration (never freed)
- After ~2 hours of normal use: **"Fatal Error In GC - Unexpected mark stack overflow"** crash
- Affects machines regardless of available RAM (it's a GC infrastructure limit, not OOM)

## The Fixes

### Fix 1: ReplaySubject Swap (via reflection)

Replace each unbounded `ReplaySubject<BoneTransforms>()` with `ReplaySubject<BoneTransforms>(1)` which retains only the most recent value. Applied early at startup via pure reflection.

### Fix 2: Harmony Method Patches (reduces allocation rate)

Using [Harmony](https://github.com/pardeike/Harmony), we patch the hot methods to reuse cached buffers:

- **OscMessage.Construct**: Thread-local `MemoryStream` reuse, manual type-tag building (no LINQ), reused `byte[4]` buffer
- **MLModelManager mask methods**: Cached `float[]` output arrays instead of `.Select().ToArray()` per frame
- **KinematicsStream.KinematicMessage**: Pre-allocated `object[]` array, cached `Enum.GetValues` result

## How It Works

1. [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop) (`winhttp.dll` proxy) loads `DoorstopFix.dll` at process startup
2. We register an `AppDomain.AssemblyLoad` handler to detect when game assemblies load
3. When `StretchSense.CompanionApp.Runtime` loads, we apply the ReplaySubject fix via reflection
4. After a short delay (letting Unity finish its load sequence), we apply Harmony patches on a background thread

The delay before Harmony patches is intentional — Harmony's runtime detours must be applied after Mono's assembly loader is idle, or they interfere with the JIT.

## Why Not BepInEx?

- **BepInEx 5 is incompatible with Unity 6** (crashes with "Undefined ManagedTempMemScopePolicy")
- BepInEx 6 bleeding-edge is unstable
- Doorstop + standalone Harmony is simpler and more reliable

## Installation

### Steps

1. **Download** `DoorstopFix-v*.zip` from the [Releases](../../releases) page

2. **Extract all files** into the game directory:
   ```
   C:\Program Files\StretchSense\XRGame\
   ├── DoorstopFix\
   │   ├── DoorstopFix.dll      (the patch)
   │   └── 0Harmony.dll         (Harmony runtime patching library)
   ├── winhttp.dll              (Unity Doorstop 4.5 loader)
   └── doorstop_config.ini      (configuration)
   ```

3. **Launch the game normally** — the fix applies automatically

### Verifying It Works

Check `DoorstopFix\doorstop_fix.log` in the game directory after launching:
```
[...] DoorstopFix v2.0 loaded - waiting for game assemblies...
[...] StretchSense.CompanionApp.Runtime loaded
[...]   Forced ArticulationManager static constructor
[...]   Replaced ReplaySubject for LEFT with bufferSize=1
[...]   Replaced ReplaySubject for RIGHT with bufferSize=1
[...]   ReplaySubject fix complete: 2/2 replaced
[...] Applying Harmony patches (deferred)...
[...]   Patched OscMessage.Construct (pooled MemoryStream + no LINQ)
[...]   Patched GetJoystickMaskedCapacitances (cached float[] output)
[...]   Patched GetGestureMaskedCapacitances (cached float[] output)
[...]   Patched GetArticulationMaskedCapacitances (cached float[] output)
[...]   Patched KinematicsStream.KinematicMessage (cached object[], cached Enum.GetValues)
[...]   Harmony patches applied successfully
```

### Uninstalling

Set `enabled=false` in `doorstop_config.ini`, or delete the `DoorstopFix\` folder and `winhttp.dll`.

## Building from Source

```bash
cd DoorstopFix
dotnet build -c Release
```

Output: `DoorstopFix/bin/Release/net48/DoorstopFix.dll` + `0Harmony.dll`

Requirements: .NET SDK 6.0+ (targets net48 for Unity Mono compatibility)

## Technical Details

| | |
|---|---|
| **Game** | StretchSense XR Game |
| **Engine** | Unity 6 (6000.3.2f1), Mono runtime (Boehm GC) |
| **Target assemblies** | StretchSense.Pipeline.dll, StretchSense.CompanionApp.Runtime.dll |
| **Patching** | Reflection (ReplaySubject swap) + Harmony 2.3 (method patches) |
| **Injection** | Unity Doorstop 4.5 |
| **Build target** | net48 (Unity Mono compatible) |

## License

MIT
