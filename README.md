# XR Game Memory Leak Fix

[![Patreon donate button](https://img.shields.io/badge/patreon-donate-yellow.svg)](https://www.patreon.com/qdot)
[![Github donate button](https://img.shields.io/badge/github-donate-ff69b4.svg)](https://www.github.com/sponsors/qdot)
[![bluesky](https://img.shields.io/bluesky/followers/buttplug.engineer)](https://bsky.app/profile/buttplug.engineer)

A runtime patch for the [StretchSense XR Game](https://stretchsense.com/) that fixes a memory leak caused by unbounded reactive buffers in the hand-tracking pipeline.

## Support

If you have any issues with the mod, I'm qdot on discord, am around the Stretchsense server. As I've just gotten back to using my gloves, I'm not sure if this is the fix to the leak everyone has been seeing, but it does seem to have reduced memory footprint growth for me.

## The Problem

`ArticulationManager` in `StretchSense.Pipeline.dll` declares two `ReplaySubject<BoneTransforms>` instances without specifying a buffer size:

```csharp
private static readonly Dictionary<Handedness, ReplaySubject<BoneTransforms>> AnimatorOutput = new()
{
    { Handedness.LEFT,  new ReplaySubject<BoneTransforms>() },   // unbounded!
    { Handedness.RIGHT, new ReplaySubject<BoneTransforms>() }    // unbounded!
};
```

The R3 reactive library's `ReplaySubject<T>()` constructor (no arguments) defaults to `bufferSize = int.MaxValue` — it stores **every value ever emitted** and never trims. Every other `ReplaySubject` in the codebase correctly uses `new ReplaySubject<T>(1)`.

During glove calibration (PRECAPTURE state), `AnimatorOutput` receives a `BoneTransforms` object every frame. At 60fps with two gloves, that's ~120 objects/second accumulating forever — roughly 430,000 objects per hour, never freed.

### Symptoms

- Memory usage climbs steadily over time (10-50+ MB/min during calibration)
- Never returns to baseline, even after calibration finishes
- Eventually causes performance degradation or OOM crashes in long sessions

## The Fix

Replace each unbounded `ReplaySubject<BoneTransforms>()` with `ReplaySubject<BoneTransforms>(1)`, which retains only the most recent value. This is semantically correct — subscribers only ever need the latest bone transforms for rendering.

## How It Works

This mod uses [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop) to inject code before the game starts:

1. **Doorstop** (a `winhttp.dll` proxy already used by BepInEx-style mods) loads `DoorstopFix.dll` at process startup
2. `Doorstop.Entrypoint.Start()` registers an `AppDomain.AssemblyLoad` event handler
3. When `StretchSense.Pipeline.dll` loads, the handler fires
4. We force `ArticulationManager`'s static constructor via `RuntimeHelpers.RunClassConstructor` to ensure the field is initialized
5. We replace both `ReplaySubject` instances in the dictionary via reflection
6. Old subjects are disposed to free any accumulated buffer

The entire fix is **pure reflection** — no compile-time dependencies on game assemblies. This makes it resilient to minor game updates and buildable without proprietary DLLs.

## Why Not BepInEx?

We originally built this as a BepInEx 5 plugin with Harmony patches. However:

- **BepInEx 5 is incompatible with Unity 6** (which XR Game uses — version 6000.3.2f1)
- Doorstop 4.5 loads fine, but BepInEx's Preloader crashes with "Undefined ManagedTempMemScopePolicy"
- BepInEx 6 bleeding-edge might work but is unstable and harder to distribute

Since we only need to swap a static field value at startup (no method patching needed), using Doorstop directly is simpler, more reliable, and has zero framework overhead.

## Installation

### Steps

1. **Download** `DoorstopFix-v*.zip` from the [Releases](../../releases) page

2. **Extract all files** into the game directory:
   ```
   C:\Program Files\StretchSense\XRGame\
   ├── DoorstopFix.dll          (the patch)
   ├── winhttp.dll              (Unity Doorstop 4.5 loader)
   └── doorstop_config.ini      (configuration)
   ```

3. **Launch the game normally** — the fix applies automatically

### Verifying It Works

Check `doorstop_fix.log` in the game directory after launching:
```
[2026-05-24 19:57:49.921] DoorstopFix loaded - waiting for StretchSense.Pipeline assembly...
[2026-05-24 19:57:50.420] StretchSense.Pipeline loaded - patching ArticulationManager...
[2026-05-24 19:57:50.425] Forced ArticulationManager static constructor
[2026-05-24 19:57:50.435] Replaced unbounded ReplaySubject for LEFT with bufferSize=1
[2026-05-24 19:57:50.435] Replaced unbounded ReplaySubject for RIGHT with bufferSize=1
[2026-05-24 19:57:50.436] Patch complete - 2/2 ReplaySubjects replaced. Memory leak fixed!
```

### Uninstalling

Set `enabled=false` in `doorstop_config.ini`, or delete `DoorstopFix.dll` and `winhttp.dll`.

## Building from Source

```bash
cd DoorstopFix
dotnet build -c Release
```

Output: `DoorstopFix/bin/Release/netstandard2.1/DoorstopFix.dll`

Requirements: .NET SDK 6.0+ (targets netstandard2.1)

## Technical Details

| | |
|---|---|
| **Game** | StretchSense XR Game |
| **Engine** | Unity 6 (6000.3.2f1), Mono runtime |
| **Target assembly** | StretchSense.Pipeline.dll |
| **Target type** | `StretchSense.Pipeline.ArticulationManager` |
| **Target field** | `AnimatorOutput` (private static) |
| **Reactive library** | R3 (Cysharp) |
| **Injection method** | Unity Doorstop 4.x |

## License

MIT
