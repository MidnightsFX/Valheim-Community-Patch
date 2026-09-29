using System;
using System.IO;
using System.Runtime.InteropServices;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// A bridge to the garbage collector inside the Mono runtime this process has already loaded:
    /// Unity's fork of the Boehm collector (bdwgc 7.7).
    /// </summary>
    /// <remarks>
    /// It never loads a library by name, so it cannot map a second copy of the runtime. On Windows
    /// it asks for the module handle of mono-2.0-bdwgc.dll, which only returns a module already in
    /// the process. On Linux the file is libmonobdwgc-2.0.so but its soname is
    /// libmonoboehm-2.0.so.1, so a DllImport by either name is unreliable; the path is read from
    /// /proc/self/maps, opened with RTLD_NOLOAD, and every symbol found is checked to lie inside
    /// that library's own mappings.
    ///
    /// The Windows runtime exports only Mono's mono_gc_* functions. The Linux one also exports the
    /// collector itself, including globals such as GC_mark_stack_too_small that have no setter.
    /// Every symbol is optional and a missing one reads as null; the first native call that throws
    /// disables the bridge for the rest of the process. Resolved on first use, so a server that
    /// changes nothing native never touches it. Main thread only.
    /// </remarks>
    internal static class MonoGcNative {
        internal static readonly bool IsWindows =
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        internal static readonly bool IsLinux =
            Environment.OSVersion.Platform == PlatformID.Unix && File.Exists("/proc/self/maps");

        private const string WindowsModule = "mono-2.0-bdwgc.dll";

        private static bool _resolved;
        private static string _failure;
        private static string _library;

        // Linux: the handle from dlopen and the address range of the library's mappings.
        private static IntPtr _handle;
        private static bool _dlsymFromLibC;
        private static long _lo, _hi;

        // Windows: the module handle.
        private static IntPtr _module;

        // MonoBoolean is a guint8, so these are bytes: bool would marshal as a 4-byte BOOL, and a
        // one-byte return leaves the upper bits of the register undefined.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetByteFn(byte value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte GetByteFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long GetInt64Fn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong GetUInt64Fn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate UIntPtr GetWordFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetWordFn(UIntPtr value);

        // Both platforms.
        private static SetByteFn _monoSetIncremental;
        private static GetByteFn _monoIsIncremental;
        private static GetInt64Fn _monoGetTimeSliceNs;

        // Linux only.
        private static GetWordFn _getFreeSpaceDivisor;
        private static SetWordFn _setFreeSpaceDivisor;
        private static GetWordFn _getGcNo;
        private static GetWordFn _getHeapSize;
        private static GetWordFn _getBytesSinceGc;
        private static GetUInt64Fn _getTimeLimitNs;
        private static IntPtr _markStackSize;       // word
        private static IntPtr _markStackTooSmall;   // GC_bool, an int
        private static IntPtr _printStats;          // int
        private static IntPtr _rescuingPages;       // word

        /// <summary>Where the collector was found, or why it was not.</summary>
        internal static string Describe() {
            if (!EnsureResolved()) { return "unavailable (" + _failure + ")"; }
            return (IsLinux ? "Linux, " : "Windows, ") + _library;
        }

        internal static bool CanSwitchIncremental => EnsureResolved() && _monoSetIncremental != null && _monoIsIncremental != null;
        internal static bool HasDivisor => EnsureResolved() && _getFreeSpaceDivisor != null && _setFreeSpaceDivisor != null;
        internal static bool HasMarkStack => EnsureResolved() && _markStackSize != IntPtr.Zero && _markStackTooSmall != IntPtr.Zero && _getGcNo != null;
        internal static bool HasPrintStats => EnsureResolved() && _printStats != IntPtr.Zero;

        internal static bool? IsIncremental() {
            if (!EnsureResolved() || _monoIsIncremental == null) { return null; }
            try { return _monoIsIncremental() != 0; } catch (Exception ex) { Fail(ex); return null; }
        }

        /// <summary>
        /// Turns incremental collection on or off. Either direction runs one full, stop-the-world
        /// collection inside the call.
        /// </summary>
        internal static bool SetIncremental(bool on) {
            if (!EnsureResolved() || _monoSetIncremental == null) { return false; }
            try { _monoSetIncremental(on ? (byte)1 : (byte)0); return true; } catch (Exception ex) { Fail(ex); return false; }
        }

        /// <summary>The runtime's own copy of the time slice, to check Unity's setter reached it.</summary>
        internal static long? MonoTimeSliceNs() {
            if (!EnsureResolved() || _monoGetTimeSliceNs == null) { return null; }
            try { return _monoGetTimeSliceNs(); } catch (Exception ex) { Fail(ex); return null; }
        }

        internal static long? FreeSpaceDivisor() => ReadWord(_getFreeSpaceDivisor);

        /// <summary>Sets GC_free_space_divisor. Refuses anything below 1: the collector divides by it.</summary>
        internal static bool SetFreeSpaceDivisor(long value) {
            if (value < 1) { return false; }
            if (!EnsureResolved() || _setFreeSpaceDivisor == null) { return false; }
            try { _setFreeSpaceDivisor(new UIntPtr((ulong)value)); return true; } catch (Exception ex) { Fail(ex); return false; }
        }

        /// <summary>The collector's collection number. Takes the allocation lock.</summary>
        internal static long? GcNo() => ReadWord(_getGcNo);

        internal static long? HeapSize() => ReadWord(_getHeapSize);

        internal static long? BytesSinceGc() => ReadWord(_getBytesSinceGc);

        /// <summary>The collector's stop-the-world time limit, which Unity's time slice sets.</summary>
        internal static long? TimeLimitNs() {
            if (!EnsureResolved() || _getTimeLimitNs == null) { return null; }
            try { return (long)_getTimeLimitNs(); } catch (Exception ex) { Fail(ex); return null; }
        }

        internal static long? MarkStackSize() => ReadInt64(_markStackSize);

        /// <summary>Dirty pages the latest rescue pass marked from.</summary>
        internal static long? RescuingPages() => ReadInt64(_rescuingPages);

        /// <summary>
        /// Sets the collector's own "grow the mark stack" flag, which it consumes when the mark in
        /// progress, or the next one, finishes with an empty stack, and doubles the stack then.
        /// </summary>
        internal static bool RequestMarkStackGrowth() {
            if (!EnsureResolved() || _markStackTooSmall == IntPtr.Zero) { return false; }
            try {
                if (Marshal.ReadInt32(_markStackTooSmall) == 0) { Marshal.WriteInt32(_markStackTooSmall, 1); }
                return true;
            } catch (Exception ex) { Fail(ex); return false; }
        }

        internal static int? PrintStats() {
            if (!EnsureResolved() || _printStats == IntPtr.Zero) { return null; }
            try { return Marshal.ReadInt32(_printStats); } catch (Exception ex) { Fail(ex); return null; }
        }

        /// <summary>0 off, 1 the collector's per-collection lines, 2 verbose. Written to its log fd.</summary>
        internal static bool SetPrintStats(int level) {
            if (!EnsureResolved() || _printStats == IntPtr.Zero) { return false; }
            try { Marshal.WriteInt32(_printStats, level); return true; } catch (Exception ex) { Fail(ex); return false; }
        }

        private static long? ReadWord(GetWordFn fn) {
            if (!EnsureResolved() || fn == null) { return null; }
            try { return (long)fn().ToUInt64(); } catch (Exception ex) { Fail(ex); return null; }
        }

        private static long? ReadInt64(IntPtr address) {
            if (!EnsureResolved() || address == IntPtr.Zero) { return null; }
            try { return Marshal.ReadInt64(address); } catch (Exception ex) { Fail(ex); return null; }
        }

        private static void Fail(Exception ex) {
            if (_failure != null) { return; }
            _failure = ex.GetType().Name + ": " + ex.Message;
            Logger.LogWarning("Server GC: a call into the Mono runtime's collector failed (" + _failure +
                "), so the settings that need it are off for the rest of this run.");
        }

        private static bool EnsureResolved() {
            if (_resolved) { return _failure == null; }
            _resolved = true;

            try {
                if (IsWindows) {
                    ResolveWindows();
                } else if (IsLinux) {
                    ResolveLinux();
                } else {
                    _failure = "unsupported platform " + Environment.OSVersion.Platform;
                }
            } catch (Exception ex) {
                _failure = ex.GetType().Name + ": " + ex.Message;
            }

            if (_failure != null) {
                Logger.LogWarning("Server GC: the Mono runtime's collector could not be reached (" +
                    _failure + "), so the garbage collector settings that need it are unavailable.");
                return false;
            }

            _monoSetIncremental = Fn<SetByteFn>("mono_gc_set_incremental");
            _monoIsIncremental = Fn<GetByteFn>("mono_gc_is_incremental");
            _monoGetTimeSliceNs = Fn<GetInt64Fn>("mono_gc_get_max_time_slice_ns");

            if (IsLinux) {
                _getFreeSpaceDivisor = Fn<GetWordFn>("GC_get_free_space_divisor");
                _setFreeSpaceDivisor = Fn<SetWordFn>("GC_set_free_space_divisor");
                _getGcNo = Fn<GetWordFn>("GC_get_gc_no");
                _getHeapSize = Fn<GetWordFn>("GC_get_heap_size");
                _getBytesSinceGc = Fn<GetWordFn>("GC_get_bytes_since_gc");
                _getTimeLimitNs = Fn<GetUInt64Fn>("GC_get_time_limit_ns");
                _markStackSize = Symbol("GC_mark_stack_size");
                _markStackTooSmall = Symbol("GC_mark_stack_too_small");
                _printStats = Symbol("GC_print_stats");
                _rescuingPages = Symbol("GC_n_rescuing_pages");
            }

            Logger.LogDebug("Server GC: collector found: " + Describe() + ".");
            return true;
        }

        private static void ResolveWindows() {
            _module = Kernel32.GetModuleHandleW(WindowsModule);
            if (_module == IntPtr.Zero) { _failure = WindowsModule + " is not loaded"; return; }
            _library = WindowsModule;
        }

        private static void ResolveLinux() {
            string path = null;
            long lo = long.MaxValue, hi = 0;

            // Each line: "start-end perms offset dev inode path", in address order. The path is
            // everything from the first '/', so an install path with spaces survives.
            foreach (string line in File.ReadAllLines("/proc/self/maps")) {
                int dash = line.IndexOf('-');
                int space = line.IndexOf(' ');
                if (dash <= 0 || space <= dash) { continue; }

                long start = Convert.ToInt64(line.Substring(0, dash), 16);
                long end = Convert.ToInt64(line.Substring(dash + 1, space - dash - 1), 16);

                int slash = line.IndexOf('/');
                if (slash < 0) {
                    // The library's .bss runs on past its last file-backed page into an anonymous
                    // mapping directly after it, and the collector's zero-initialised globals,
                    // GC_mark_stack_too_small among them, live there.
                    if (path != null && start == hi && line.IndexOf('[') < 0) { hi = end; }
                    continue;
                }

                string file = line.Substring(slash).TrimEnd();
                string name = Path.GetFileName(file);
                if (!name.StartsWith("libmonobdwgc", StringComparison.Ordinal) &&
                    !name.StartsWith("libmonoboehm", StringComparison.Ordinal)) { continue; }

                if (path == null) { path = file; } else if (file != path) { continue; }

                if (start < lo) { lo = start; }
                if (end > hi) { hi = end; }
            }

            if (path == null) { _failure = "no Mono runtime library in /proc/self/maps"; return; }

            const int RTLD_LAZY = 0x1;
            const int RTLD_NOLOAD = 0x4;

            // libdl.so.2 still exists on current glibc as a stub; libc.so.6 carries dlopen itself
            // from glibc 2.34.
            try {
                _handle = LibDl.dlopen(path, RTLD_LAZY | RTLD_NOLOAD);
            } catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) {
                _dlsymFromLibC = true;
                _handle = LibC.dlopen(path, RTLD_LAZY | RTLD_NOLOAD);
            }

            if (_handle == IntPtr.Zero) { _failure = "dlopen found no loaded copy of " + path; return; }

            _lo = lo;
            _hi = hi;
            _library = path;
        }

        private static IntPtr Symbol(string name) {
            IntPtr p;
            if (IsWindows) {
                p = Kernel32.GetProcAddress(_module, name);
            } else {
                p = _dlsymFromLibC ? LibC.dlsym(_handle, name) : LibDl.dlsym(_handle, name);

                // A symbol resolved from any other library is not the one this bridge is for.
                long address = p.ToInt64();
                if (address < _lo || address >= _hi) { p = IntPtr.Zero; }
            }

            if (p == IntPtr.Zero) { Logger.LogDebug("Server GC: " + name + " not found in " + _library + "."); }
            return p;
        }

        private static T Fn<T>(string name) where T : class {
            IntPtr p = Symbol(name);
            return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        private static class Kernel32 {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
            internal static extern IntPtr GetModuleHandleW(string moduleName);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
            internal static extern IntPtr GetProcAddress(IntPtr module, string procName);
        }

        private static class LibDl {
            [DllImport("libdl.so.2")]
            internal static extern IntPtr dlopen(string file, int mode);

            [DllImport("libdl.so.2")]
            internal static extern IntPtr dlsym(IntPtr handle, string name);
        }

        private static class LibC {
            [DllImport("libc.so.6")]
            internal static extern IntPtr dlopen(string file, int mode);

            [DllImport("libc.so.6")]
            internal static extern IntPtr dlsym(IntPtr handle, string name);
        }
    }
}
