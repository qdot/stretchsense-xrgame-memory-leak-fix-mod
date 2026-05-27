# XR Game Memory Leak Fix

[![Ko-fi donate button](https://img.shields.io/badge/kofi-donate-green.svg)](https://www.ko-fi.com/qdot76367)
[![Patreon donate button](https://img.shields.io/badge/patreon-donate-yellow.svg)](https://www.patreon.com/qdot)
[![Github donate button](https://img.shields.io/badge/github-donate-ff69b4.svg)](https://www.github.com/sponsors/qdot)
[![bluesky](https://img.shields.io/bluesky/followers/buttplug.engineer)](https://bsky.app/profile/buttplug.engineer)

A runtime patch for the StretchSense XR Game that reduces hand-tracking pipeline memory growth and prevents the long-run Mono/Boehm GC mark-stack overflow crash.

## Current Status

The current build identifies as:

```text
DoorstopFix v3.4.7 monitored native mark stack + BLE guard
```

This is a field-tested build. It has survived multi-hour local soak tests that previously crashed with `Fatal Error In GC - Unexpected mark stack overflow`, including staged Mono mark-stack growth through 8 MB. It still keeps diagnostic heap logging enabled, and the managed heap/process memory can continue to grow during long sessions.

## Problems

### Unbounded ReplaySubjects

`ArticulationManager` in `StretchSense.Pipeline.dll` creates two unbounded `ReplaySubject<BoneTransforms>` instances. R3's default constructor retains every emitted value. During calibration and animation updates this can retain frame data indefinitely.

### Mono GC Mark-Stack Overflow

XR Game uses Unity Mono with Boehm GC. During long sessions the GC mark stack grows through small staged sizes and can eventually overflow while scanning a large fragmented heap. This presents as a Mono fatal error or an access violation in `mono-2.0-bdwgc.dll`.

### Per-Frame Allocation Pressure

Several hot paths allocate objects every frame:

| Hotspot | Issue |
| --- | --- |
| `OscMessage.Construct` | `MemoryStream`, LINQ, temp byte arrays |
| `MLModelManager` mask methods | LINQ and new `float[]` outputs |
| `KinematicsStream.KinematicMessage` | `List<object>`, `ToArray`, boxed values |
| `ControllerStream.ControllerMessage` | new `object[17]` per frame |
| `OrientationStream.OrientationMessage` | new `object[12]` per frame |

### BLE Reconnect Churn

Repeated Bluetooth connect/disconnect paths can leave stale glove state and notification subscriptions around. The current build guards duplicate connect attempts and explicitly clears notification state during disconnect.

## Fixes

### ReplaySubject Swap

The patch replaces the unbounded `ReplaySubject<BoneTransforms>` instances with `ReplaySubject<BoneTransforms>(1)` via reflection once the game assemblies are available.

### Native Mark-Stack Patch

The effective crash fix is a direct native patch of Mono's Boehm mark-stack globals. At startup, and again whenever Mono later resets its own mark stack, DoorstopFix replaces the active mark stack with a 32 MB native allocation.

This Unity/Mono build grows the stack in stages from small sizes like 64 KB, 256 KB, 1 MB, 2 MB, 4 MB, and 8 MB. A background monitor checks every 5 seconds and re-expands the mark stack when Mono resets it to a smaller size.

DoorstopFix also sets `MONO_GC_PARAMS=mark-stack-size=33554432` as a best-effort hint, but local testing showed that this environment variable is not sufficient by itself for this Unity/Mono build. The native replacement log line is the important verification signal.

Successful replacement allocations are intentionally retained. Testing showed that freeing an older replacement immediately after swapping globals can crash in `mono-2.0-bdwgc.dll`, which means Mono can still briefly touch older stack memory.

### Harmony Allocation Patches

Harmony patches reduce allocations in the hot methods listed above by reusing thread-local buffers and caching reflection lookups where practical.

### BLE Guard

The Bluetooth patch skips duplicate connect attempts for the same glove, disconnects stale glove objects before replacing them, and clears cached notification/subscription state on disconnect.

## Installation

1. Download `DoorstopFix-v*.zip` from the Releases page.
2. Close XR Game.
3. Extract the zip into the game directory, next to `XR Game.exe`.

Expected layout:

```text
C:\Program Files\StretchSense\XRGame\
|-- DoorstopFix\
|   |-- DoorstopFix.dll
|   `-- 0Harmony.dll
|-- winhttp.dll
`-- doorstop_config.ini
```

4. Launch the game normally.

## Verification

Check `DoorstopFix\doorstop_fix.log` after launch. A healthy v3.4.7 startup looks like:

```text
Set MONO_GC_PARAMS=mark-stack-size=33554432
Native mark stack replaced: old=... size=64.0KB, new=... size=32.0MB replacements=1 retainedAllocations=1
DoorstopFix v3.4.7 monitored native mark stack + BLE guard loaded - waiting for game assemblies...
ReplaySubject fix complete: 2/2 replaced
Patched BLE duplicate connect guard and explicit notification cleanup
Patched OscMessage.Construct (pooled MemoryStream + no LINQ)
Patched KinematicsStream.KinematicMessage (cached object[], cached Enum.GetValues)
Patched ControllerStream.ControllerMessage (cached object[17])
Patched OrientationStream.OrientationMessage (cached object[12])
Harmony patches applied successfully
Native mark stack monitor started (checking every 5s)
Heap monitor started (logging every 60s)
```

The `MONO_GC_PARAMS` line is expected, but it is not enough to prove the fix is active. Verify that at least one `Native mark stack replaced` line appears and that the startup banner says `v3.4.7`.

During long sessions, additional lines like this are expected:

```text
Native mark stack replaced: old=... size=4.0MB, new=... size=32.0MB replacements=6 retainedAllocations=6
```

Unexpected repeated `Native mark stack patch skipped` messages or a crash in `mono-2.0-bdwgc.dll` should be reported with the full `doorstop_fix.log`.

## Uninstalling

Set `enabled=false` in `doorstop_config.ini`, or remove:

```text
DoorstopFix\
winhttp.dll
doorstop_config.ini
```

## Building

```bash
cd DoorstopFix
dotnet build -c Release
```

Output:

```text
DoorstopFix/bin/Release/net48/DoorstopFix.dll
DoorstopFix/bin/Release/net48/0Harmony.dll
```

Requirements: .NET SDK 6.0+; the project targets `net48` for Unity Mono compatibility.

## Technical Details

| | |
| --- | --- |
| Game | StretchSense XR Game |
| Engine | Unity 6 / Mono runtime with Boehm GC |
| Target assemblies | `StretchSense.Pipeline.dll`, `StretchSense.CompanionApp.Runtime.dll` |
| Patching | Reflection, Harmony 2.3, native Mono data patch |
| Injection | Unity Doorstop 4.5 |
| Build target | `net48` |

## License

MIT
