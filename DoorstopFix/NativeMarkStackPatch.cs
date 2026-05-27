using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Doorstop
{
    internal static class NativeMarkStackPatch
    {
        private const string MonoModuleName = "mono-2.0-bdwgc.dll";

        // Address of Boehm's mark-stack global tuple in Unity 6000.3.2f1's
        // mono-2.0-bdwgc.dll: base, limit, top. This is version-specific.
        private const long MarkStackGlobalsRva = 0x75dfb8;
        private const long MinimumExpectedMarkStackBytes = 0x10000;
        private const uint PageReadWrite = 0x04;

        private static readonly object PatchLock = new object();

        // Do not free successful replacement stacks. A 3.4.6 test freed the previous
        // allocation after swapping globals and crashed in mono-2.0-bdwgc shortly
        // afterward, so Mono can still briefly touch older stack memory.
        private static readonly List<IntPtr> RawAllocations = new List<IntPtr>();
        private static IntPtr _markStack;
        private static int _replacementCount;

        internal static void Apply(long replacementBytes, bool quietWhenAlreadyPatched = false)
        {
            lock (PatchLock)
            {
                ApplyLocked(replacementBytes, quietWhenAlreadyPatched);
            }
        }

        private static void ApplyLocked(long replacementBytes, bool quietWhenAlreadyPatched)
        {
            var mono = GetModuleHandle(MonoModuleName);
            if (mono == IntPtr.Zero)
            {
                Entrypoint.Log($"Native mark stack patch skipped; {MonoModuleName} is not loaded yet");
                return;
            }

            var globals = Add(mono, MarkStackGlobalsRva);
            var oldBase = Marshal.ReadIntPtr(globals, 0);
            var oldLimit = Marshal.ReadIntPtr(globals, IntPtr.Size);
            var oldTop = Marshal.ReadIntPtr(globals, IntPtr.Size * 2);
            var oldSize = ToInt64(oldLimit) - ToInt64(oldBase);

            var oldTopOffset = ToInt64(oldTop) - ToInt64(oldBase);

            // Only patch sane mark-stack shapes. An unexpected value here means the
            // RVA is wrong for this Mono build or Mono is in a state we should not edit.
            if (oldBase == IntPtr.Zero || oldLimit == IntPtr.Zero ||
                oldSize <= 0 ||
                oldTopOffset < -0x1000 || oldTopOffset > oldSize ||
                oldSize < MinimumExpectedMarkStackBytes ||
                !IsPowerOfTwo(oldSize))
            {
                Entrypoint.Log(
                    $"Native mark stack patch skipped; unexpected globals base={Format(oldBase)} limit={Format(oldLimit)} top={Format(oldTop)} size=0x{oldSize:X} ({FormatBytes(oldSize)})");
                return;
            }

            if (oldSize >= replacementBytes)
            {
                if (!quietWhenAlreadyPatched)
                {
                    Entrypoint.Log(
                        $"Native mark stack already large enough: base={Format(oldBase)} limit={Format(oldLimit)} top={Format(oldTop)} size={FormatBytes(oldSize)} target={FormatBytes(replacementBytes)}");
                }
                return;
            }

            var rawAllocation = Marshal.AllocHGlobal(new IntPtr(replacementBytes + 0x1000));
            _markStack = Align(rawAllocation, 0x1000);
            var newLimit = Add(_markStack, replacementBytes);
            var newTop = Add(_markStack, oldTopOffset);

            // The globals live in a writable data page on current builds, but protect
            // explicitly in case the loader mapped this page read-only.
            if (!VirtualProtect(globals, new UIntPtr((uint)(IntPtr.Size * 3)), PageReadWrite, out var oldProtect))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                Entrypoint.Log($"Native mark stack patch failed; VirtualProtect: {error}");
                Marshal.FreeHGlobal(rawAllocation);
                return;
            }

            Marshal.WriteIntPtr(globals, 0, _markStack);
            Marshal.WriteIntPtr(globals, IntPtr.Size, newLimit);
            Marshal.WriteIntPtr(globals, IntPtr.Size * 2, newTop);
            VirtualProtect(globals, new UIntPtr((uint)(IntPtr.Size * 3)), oldProtect, out _);
            RawAllocations.Add(rawAllocation);
            _replacementCount++;

            var verifiedBase = Marshal.ReadIntPtr(globals, 0);
            var verifiedLimit = Marshal.ReadIntPtr(globals, IntPtr.Size);
            var verifiedTop = Marshal.ReadIntPtr(globals, IntPtr.Size * 2);
            Entrypoint.Log(
                $"Native mark stack replaced: old={Format(oldBase)}..{Format(oldLimit)} size={FormatBytes(oldSize)}, " +
                $"new={Format(verifiedBase)}..{Format(verifiedLimit)} top={Format(verifiedTop)} size={FormatBytes(replacementBytes)} replacements={_replacementCount} retainedAllocations={RawAllocations.Count}");
        }

        private static IntPtr Align(IntPtr value, long alignment)
        {
            var address = ToInt64(value);
            var aligned = (address + alignment - 1) & ~(alignment - 1);
            return new IntPtr(aligned);
        }

        private static IntPtr Add(IntPtr value, long offset)
        {
            return new IntPtr(ToInt64(value) + offset);
        }

        private static long ToInt64(IntPtr value)
        {
            return IntPtr.Size == 8 ? value.ToInt64() : value.ToInt32();
        }

        private static string Format(IntPtr value)
        {
            return "0x" + ToInt64(value).ToString("X");
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / 1024.0 / 1024.0:F1}MB";
            return $"{bytes / 1024.0:F1}KB";
        }

        private static bool IsPowerOfTwo(long value)
        {
            return value > 0 && (value & (value - 1)) == 0;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
    }
}
