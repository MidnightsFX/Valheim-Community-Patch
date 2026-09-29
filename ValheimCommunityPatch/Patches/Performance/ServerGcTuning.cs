using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace ValheimCommunityPatch.Patches.Performance {
    // Server GC Tuning: on a dedicated server, sets how Unity's garbage collector paces itself -
    // its per-frame time slice, how far the heap grows between collections, whether it collects
    // incrementally at all, and how large its mark stack is allowed to get before it needs it.
    //
    // Valheim runs Unity's incremental Boehm collector with a 3 ms slice per frame. On a busy
    // server the main thread has no idle time left over, so 3 ms is the whole budget: each cycle
    // opens with a stop-the-world attempt abandoned at the slice limit, rescans the heap's dirty
    // pages over about a second of frames, and then finishes in one stop-the-world pass with no
    // limit, because the collector allows one bounded attempt per cycle and the opening one used
    // it. On a 5-6 GB server heap that finish measured 150-380 ms every 12 s, growing with the
    // allocation rate, since it rescans every page written while the slow pass ran. The same pass
    // is where the server aborts with "Unexpected mark stack overflow": it fills the mark stack
    // to within 2,048 entries of its end before pushing the roots, and the collector only grows
    // the stack after a pass that came close.
    //
    // Nothing is patched. Each setting writes one runtime value: the slice through Unity's
    // GarbageCollector API, incremental mode through the Mono runtime's export, and on Linux the
    // divisor and the mark stack through the collector's own exports (see MonoGcNative). A longer
    // slice finishes the rescan in fewer frames, leaving less for the final pass to redo; a lower
    // divisor collects less often; the mark stack target sets the collector's own grow flag once
    // per collection until the stack reaches it. The defaults come from Praetoris's pause data,
    // 2026-09-29. 0 or off leaves the game's value, and going back to it restores the value seen
    // before the first change. The 'vcp_gc' console command prints the collector's state and
    // changes each setting live.
    //
    // Server: dedicated servers only. Nothing is applied in a process with a graphics device, so
    // clients and listen hosts ignore the synced values. Divisor and mark stack are Linux only.
    [PatchSide(Side.Server)]
    internal static class ServerGcTuning {
        internal static ConfigEntry<float> TimeSliceMs;
        internal static ConfigEntry<int> FreeSpaceDivisor;
        internal static ConfigEntry<bool> DisableIncremental;
        internal static ConfigEntry<int> MarkStackTarget;

        internal const string Command = "vcp_gc";

        private const float MaxSliceMs = 50f;
        private const int MaxDivisor = 16;
        private const int MaxMarkStack = 16777216;
        private const int BytesPerMarkStackEntry = 16;

        internal static void BindConfig() {
            TimeSliceMs = ValConfig.BindServerConfig(
                ValConfig.SectionServerGc,
                "GC Time Slice Milliseconds",
                10f,
                "Dedicated server only. Milliseconds the incremental garbage collector may run each " +
                "frame; Valheim ships 3. A busy server has no idle frame time to add to that, so each " +
                "collection's rescan drags over about a second and its final stop-the-world pass has " +
                "that much more to redo. 10 cut the modelled final pass on a busy server from about " +
                "350 ms to about 150 ms, at a few milliseconds more frame time for the few frames a " +
                "collection is running. 0 leaves the game's value. Applies immediately.",
                advanced: true,
                valMin: 0f,
                valMax: MaxSliceMs);

            FreeSpaceDivisor = ValConfig.BindServerConfig(
                ValConfig.SectionServerGc,
                "GC Free Space Divisor",
                2,
                "Dedicated server, Linux only. How much the server allocates between collections: " +
                "about the collector's working set divided by this, halved while it collects " +
                "incrementally. The collector's own value is 3; 2 collects about a third less often " +
                "for a few hundred MB more heap on a large world. 0 leaves the collector's value. " +
                "Takes effect after the next collection.",
                advanced: true,
                valMin: 0,
                valMax: MaxDivisor);

            DisableIncremental = ValConfig.BindServerConfig(
                ValConfig.SectionServerGc,
                "Disable Incremental GC",
                false,
                "Dedicated server only. Experimental. Makes every collection one stop-the-world pass " +
                "over the whole heap, less often than now but each as long as marking everything takes. " +
                "Turning it on runs one full collection straight away and logs how long it paused the " +
                "server. Turning it back off takes effect after a restart.",
                advanced: true);

            MarkStackTarget = ValConfig.BindServerConfig(
                ValConfig.SectionServerGc,
                "GC Mark Stack Target Entries",
                4194304,
                "Dedicated server, Linux only. Grows the collector's mark stack to at least this many " +
                "16-byte entries, one doubling per collection, instead of waiting for a pass that " +
                "nearly overflows it; a pass that does overflow it aborts the server with " +
                "'Unexpected mark stack overflow'. 4194304 (64 MiB) is 8x the size one such abort " +
                "happened at. The stack doubles, so it stops at the first power of two at or above " +
                "this, and it never shrinks. 0 leaves it to the collector.",
                advanced: true,
                valMin: 0,
                valMax: MaxMarkStack);

            TimeSliceMs.SettingChanged += (sender, args) => OnChanged();
            FreeSpaceDivisor.SettingChanged += (sender, args) => OnChanged();
            DisableIncremental.SettingChanged += (sender, args) => OnChanged();
            MarkStackTarget.SettingChanged += (sender, args) => OnChanged();
        }

        // Set once Apply has run in a headless process. Everything else is a no-op without it, so
        // a client receiving the server's synced values never acts on them.
        private static bool _active;
        private static int _mainThread;
        private static volatile bool _pending;

        // Captured just before the first write, so 0 can restore them.
        private static ulong _originalSliceNs;
        private static bool _haveOriginalSlice;
        private static long _originalDivisor;
        private static bool _haveOriginalDivisor;

        private static bool _disabledIncremental;
        private static long _switchMs = -1;
        private static long _switchHeapMb;
        private static bool _restartNoticeLogged;
        private static bool _alreadyOffLogged;

        // The mark stack grower. It works a collection at a time: the flag is consumed when a mark
        // finishes, and the collection number only moves after that, so a size read after it
        // moves includes the growth asked for before it.
        private static bool _growing;
        private static long _lastGcNo = -1;
        private static long _gcNoAtLastGrowth;
        private static long _sizeAtLastGrowth;
        private static float _nextPoll;

        private static bool _warnedSlice, _warnedDivisor, _warnedIncremental, _warnedMarkStack;

        internal static void Apply() {
            if (!RunMode.IsHeadless) {
                if (AnyChanged()) {
                    Logger.LogInfo(
                        "The 'Server - Garbage Collector' settings are ignored: this process has a " +
                        "graphics device, so it is not a dedicated server.");
                }
                return;
            }

            _mainThread = Thread.CurrentThread.ManagedThreadId;
            _active = true;
            ApplyAll();
        }

        /// <summary>Puts back the values captured before the first change. Incremental mode stays as it is.</summary>
        internal static void Restore() {
            if (!_active) { return; }
            _active = false;
            _growing = false;

            try {
                if (_haveOriginalSlice) { GarbageCollector.incrementalTimeSliceNanoseconds = _originalSliceNs; }
                if (_haveOriginalDivisor) { MonoGcNative.SetFreeSpaceDivisor(_originalDivisor); }
            } catch (Exception ex) {
                Logger.LogWarning("Server GC: could not restore the collector's settings: " + ex.Message);
            }
        }

        private static bool AnyChanged() {
            return !Equals(TimeSliceMs.BoxedValue, TimeSliceMs.DefaultValue)
                || !Equals(FreeSpaceDivisor.BoxedValue, FreeSpaceDivisor.DefaultValue)
                || !Equals(DisableIncremental.BoxedValue, DisableIncremental.DefaultValue)
                || !Equals(MarkStackTarget.BoxedValue, MarkStackTarget.DefaultValue);
        }

        // SettingChanged fires for the console command, a ConfigurationManager edit relayed by
        // Jotunn and a config reload. Unity's API is main-thread only, so anything else waits for
        // the next frame.
        private static void OnChanged() {
            if (!_active) { return; }

            if (Thread.CurrentThread.ManagedThreadId == _mainThread) {
                ApplyAll();
            } else {
                _pending = true;
            }
        }

        private static void ApplyAll() {
            ApplySlice();
            ApplyDivisor();
            ApplyIncremental();
            ApplyMarkStack();
        }

        private static void ApplySlice() {
            try {
                float ms = TimeSliceMs.Value;
                ulong current = GarbageCollector.incrementalTimeSliceNanoseconds;
                ulong wanted;

                if (ms <= 0f) {
                    if (!_haveOriginalSlice) { return; }
                    wanted = _originalSliceNs;
                } else {
                    if (!_haveOriginalSlice) {
                        _originalSliceNs = current;
                        _haveOriginalSlice = true;
                    }
                    wanted = (ulong)Math.Round(ms * 1e6);
                }

                if (wanted == current) { return; }

                GarbageCollector.incrementalTimeSliceNanoseconds = wanted;
                Logger.LogInfo($"Server GC: incremental time slice {Ms(current)} -> {Ms(wanted)} ms.");
            } catch (Exception ex) {
                WarnOnce(ref _warnedSlice, "'GC Time Slice Milliseconds' could not be applied: " + ex.Message);
            }
        }

        private static void ApplyDivisor() {
            int n = FreeSpaceDivisor.Value;

            // Silent at the default, so a Windows server does not warn about a value nobody chose.
            if (!MonoGcNative.IsLinux) {
                if (n > 0 && !Equals(FreeSpaceDivisor.BoxedValue, FreeSpaceDivisor.DefaultValue)) {
                    WarnOnce(ref _warnedDivisor, "'GC Free Space Divisor' only works on a Linux server; left alone.");
                }
                return;
            }

            if (n <= 0 && !_haveOriginalDivisor) { return; }

            long? current = MonoGcNative.FreeSpaceDivisor();
            if (current == null || !MonoGcNative.HasDivisor) {
                WarnOnce(ref _warnedDivisor, "'GC Free Space Divisor' is not available on this runtime; left alone.");
                return;
            }

            long wanted;
            if (n <= 0) {
                wanted = _originalDivisor;
            } else {
                if (!_haveOriginalDivisor) {
                    _originalDivisor = current.Value;
                    _haveOriginalDivisor = true;
                }
                wanted = n;
            }

            if (wanted == current.Value) { return; }

            if (MonoGcNative.SetFreeSpaceDivisor(wanted)) {
                Logger.LogInfo($"Server GC: free space divisor {current.Value} -> {wanted}, from the next collection on.");
            }
        }

        private static void ApplyIncremental() {
            if (!DisableIncremental.Value) {
                // Methods compiled while it was off may lack the write barriers incremental marking
                // relies on, so it is not switched back on in a running process.
                if (_disabledIncremental && !_restartNoticeLogged) {
                    _restartNoticeLogged = true;
                    Logger.LogInfo("Server GC: incremental collection stays off until the server restarts.");
                }
                return;
            }

            if (_disabledIncremental) { return; }

            bool? incremental = MonoGcNative.IsIncremental();
            if (incremental == null || !MonoGcNative.CanSwitchIncremental) {
                WarnOnce(ref _warnedIncremental, "'Disable Incremental GC' could not reach the Mono runtime; left alone.");
                return;
            }

            if (!incremental.Value) {
                if (!_alreadyOffLogged) {
                    _alreadyOffLogged = true;
                    Logger.LogInfo("Server GC: incremental collection is already off in this runtime.");
                }
                return;
            }

            long heapMb = Profiler.GetMonoUsedSizeLong() / 1048576L;
            Stopwatch watch = Stopwatch.StartNew();
            bool switched = MonoGcNative.SetIncremental(false);
            watch.Stop();
            if (!switched) { return; }

            _disabledIncremental = true;
            _restartNoticeLogged = false;
            _switchMs = watch.ElapsedMilliseconds;
            _switchHeapMb = heapMb;
            Logger.LogInfo(
                $"Server GC: incremental collection off. Switching ran one full collection of a " +
                $"{heapMb:N0} MB heap, which paused the server for {_switchMs:N0} ms; from now on every " +
                "collection is one pause like it.");
        }

        private static void ApplyMarkStack() {
            int target = MarkStackTarget.Value;

            if (!MonoGcNative.IsLinux) {
                if (target > 0 && !Equals(MarkStackTarget.BoxedValue, MarkStackTarget.DefaultValue)) {
                    WarnOnce(ref _warnedMarkStack, "'GC Mark Stack Target Entries' only works on a Linux server; left alone.");
                }
                return;
            }

            if (target <= 0) {
                if (_growing) {
                    _growing = false;
                    Logger.LogInfo("Server GC: stopped growing the mark stack; it keeps its current size.");
                }
                return;
            }

            long? size = MonoGcNative.MarkStackSize();
            if (size == null || !MonoGcNative.HasMarkStack) {
                WarnOnce(ref _warnedMarkStack, "'GC Mark Stack Target Entries' is not available on this runtime; left alone.");
                return;
            }

            if (size.Value >= target) {
                _growing = false;
                return;
            }

            if (!_growing) {
                _growing = true;
                _lastGcNo = MonoGcNative.GcNo() ?? -1;
                _gcNoAtLastGrowth = _lastGcNo;
                _sizeAtLastGrowth = size.Value;
                Logger.LogInfo(
                    $"Server GC: growing the mark stack from {size.Value:N0} to {Reached(size.Value, target):N0} " +
                    "entries, one doubling per collection.");
            }

            MonoGcNative.RequestMarkStackGrowth();
        }

        private static void PollMarkStack() {
            if (!_growing) { return; }

            // GC_get_gc_no takes the collector's lock, so not every frame.
            float now = Time.realtimeSinceStartup;
            if (now < _nextPoll) { return; }
            _nextPoll = now + 1f;

            long? gcNo = MonoGcNative.GcNo();
            if (gcNo == null) {
                _growing = false;
                return;
            }

            if (gcNo.Value == _lastGcNo) { return; }
            _lastGcNo = gcNo.Value;

            int target = MarkStackTarget.Value;
            long size = MonoGcNative.MarkStackSize() ?? 0;

            if (target <= 0) {
                _growing = false;
                return;
            }

            if (size >= target) {
                _growing = false;
                Logger.LogInfo($"Server GC: mark stack reached {size:N0} entries ({size * BytesPerMarkStackEntry / 1048576L:N0} MiB).");
                return;
            }

            if (size != _sizeAtLastGrowth) {
                _sizeAtLastGrowth = size;
                _gcNoAtLastGrowth = gcNo.Value;
            } else if (gcNo.Value - _gcNoAtLastGrowth >= 3) {
                // The collector allocates the new stack itself and keeps the old one if that fails,
                // logging 'Failed to grow mark stack' to its own log.
                _growing = false;
                Logger.LogWarning(
                    $"Server GC: the mark stack stayed at {size:N0} entries for three collections after " +
                    "being asked to grow, so the request was dropped.");
                return;
            }

            MonoGcNative.RequestMarkStackGrowth();
        }

        // Every frame on a server with a world loaded, and nothing can run during the synchronous
        // world load before it anyway. Drains a change made off the main thread, then drives the
        // mark stack grower.
        [HarmonyPatch(typeof(ZDOMan))]
        internal static class UpdateHook {
            [HarmonyPostfix]
            [HarmonyPatch(nameof(ZDOMan.Update))]
            private static void UpdatePostfix() {
                if (!_active) { return; }

                if (_pending) {
                    _pending = false;
                    ApplyAll();
                }

                PollMarkStack();
            }
        }

        [HarmonyPatch(typeof(Terminal))]
        internal static class TerminalHook {
            [HarmonyPostfix]
            [HarmonyPatch("InitTerminal")]
            private static void InitTerminalPostfix() {
                new Terminal.ConsoleCommand(
                    Command,
                    "[slice <ms> | divisor <n> | incremental on|off | markstack <entries> | stats on|off] " +
                    "Prints or changes the server's garbage collector settings (Valheim Community Patch).",
                    (Terminal.ConsoleEvent)(args => {
                        string reply = Run(args);
                        args.Context?.AddString(reply);
                        Logger.LogInfo(reply);
                    }),
                    onlyServer: true,
                    optionsFetcher: () => new List<string> { "slice", "divisor", "incremental", "markstack", "stats" },
                    remoteCommand: true);
            }
        }

        // The settings go through their config entries, so a change made here is saved and
        // applied by the same handler as any other. Values are checked here because the entries'
        // ranges would clamp a typo silently.
        private static string Run(Terminal.ConsoleEventArgs args) {
            if (!_active) { return Command + ": the garbage collector settings only apply on a dedicated server."; }
            if (args.Length < 2) { return BuildStatus(); }

            string verb = args[1].ToLowerInvariant();
            string value = args.Length > 2 ? args[2].ToLowerInvariant() : string.Empty;

            switch (verb) {
                case "slice": {
                    if (!args.TryParameterFloat(2, out float ms) || ms < 0f || ms > MaxSliceMs) {
                        return Command + " slice <milliseconds>: up to " + MaxSliceMs + "; 0 restores the game's value.";
                    }
                    TimeSliceMs.Value = ms;
                    return "Server GC: incremental time slice now " + Ms(GarbageCollector.incrementalTimeSliceNanoseconds) + " ms (saved).";
                }

                case "divisor": {
                    if (!MonoGcNative.IsLinux) { return Command + " divisor: Linux servers only."; }
                    if (!args.TryParameterInt(2, out int n) || n < 0 || n > MaxDivisor) {
                        return Command + " divisor <n>: 1 to " + MaxDivisor + "; 0 restores the collector's value.";
                    }
                    FreeSpaceDivisor.Value = n;
                    return "Server GC: free space divisor now " + (MonoGcNative.FreeSpaceDivisor()?.ToString() ?? "unknown") +
                        ", from the next collection on (saved).";
                }

                case "incremental": {
                    if (value == "off") {
                        DisableIncremental.Value = true;
                        if (!_disabledIncremental) {
                            return MonoGcNative.IsIncremental() == false
                                ? "Server GC: incremental collection was already off in this runtime (saved)."
                                : "Server GC: incremental collection could not be switched off; see the log.";
                        }
                        return $"Server GC: incremental collection off (saved). The switch ran one full collection of a " +
                            $"{_switchHeapMb:N0} MB heap in {_switchMs:N0} ms.";
                    }
                    if (value == "on") {
                        DisableIncremental.Value = false;
                        return _disabledIncremental
                            ? "Server GC: saved; incremental collection comes back when the server restarts."
                            : "Server GC: incremental collection is on.";
                    }
                    return Command + " incremental on|off";
                }

                case "markstack": {
                    if (!MonoGcNative.IsLinux) { return Command + " markstack: Linux servers only."; }
                    string raw = value.Replace(",", string.Empty).Replace("_", string.Empty);
                    if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long entries) ||
                        entries > MaxMarkStack) {
                        return Command + " markstack <entries>: up to " + MaxMarkStack.ToString("N0") + "; 0 leaves it to the collector.";
                    }
                    MarkStackTarget.Value = (int)entries;
                    long size = MonoGcNative.MarkStackSize() ?? 0;
                    if (entries == 0) { return $"Server GC: mark stack left to the collector at {size:N0} entries (saved)."; }
                    return $"Server GC: mark stack {size:N0} entries, target {entries:N0}; it will stop at " +
                        $"{Reached(size, entries):N0} (saved).";
                }

                case "stats": {
                    if (!MonoGcNative.IsLinux || !MonoGcNative.HasPrintStats) { return Command + " stats: Linux servers only."; }
                    if (value != "on" && value != "off") { return Command + " stats on|off"; }
                    MonoGcNative.SetPrintStats(value == "on" ? 1 : 0);
                    return value == "on"
                        ? "Server GC: the collector's own log is on (not saved). It writes a few lines per " +
                          "collection to the server's standard error, or to GC_LOG_FILE if that was set at " +
                          "launch, not to the BepInEx log."
                        : "Server GC: the collector's own log is off.";
                }

                default:
                    return Command + " [slice <ms> | divisor <n> | incremental on|off | markstack <entries> | stats on|off]";
            }
        }

        internal static string BuildStatus() {
            StringBuilder sb = new StringBuilder(384);
            sb.Append("Server GC (").Append(MonoGcNative.Describe()).Append("): ");

            bool? incremental = MonoGcNative.IsIncremental();
            sb.Append(incremental == null ? "incremental unknown" : incremental.Value ? "incremental" : "not incremental");
            if (_disabledIncremental) {
                sb.Append(" (switched off here; full collection took ").Append(_switchMs.ToString("N0")).Append(" ms)");
            }

            sb.Append(" | slice ").Append(Ms(GarbageCollector.incrementalTimeSliceNanoseconds)).Append(" ms");
            long? monoNs = MonoGcNative.MonoTimeSliceNs();
            long? limitNs = MonoGcNative.TimeLimitNs();
            if (monoNs != null || limitNs != null) {
                sb.Append(" (runtime ").Append(monoNs != null ? Ms((ulong)monoNs.Value) : "?");
                if (limitNs != null) { sb.Append(", collector ").Append(Ms((ulong)limitNs.Value)); }
                sb.Append(')');
            }

            if (MonoGcNative.IsLinux) {
                long? divisor = MonoGcNative.FreeSpaceDivisor();
                long? heap = MonoGcNative.HeapSize();
                long? since = MonoGcNative.BytesSinceGc();
                long? gcNo = MonoGcNative.GcNo();
                long? pages = MonoGcNative.RescuingPages();
                long? stack = MonoGcNative.MarkStackSize();
                int? stats = MonoGcNative.PrintStats();

                sb.Append(" | divisor ").Append(divisor?.ToString() ?? "?");
                if (heap != null) { sb.Append(" | heap ").Append((heap.Value / 1048576L).ToString("N0")).Append(" MB"); }
                if (since != null) { sb.Append(", ").Append((since.Value / 1048576L).ToString("N0")).Append(" MB since last collection"); }
                if (gcNo != null) { sb.Append(", collection #").Append(gcNo.Value.ToString("N0")); }
                if (pages != null) { sb.Append(" | last rescue ").Append(pages.Value.ToString("N0")).Append(" dirty pages"); }
                if (stack != null) {
                    sb.Append(" | mark stack ").Append(stack.Value.ToString("N0")).Append(" entries (")
                      .Append((stack.Value * BytesPerMarkStackEntry / 1048576L).ToString("N0")).Append(" MiB)");
                    if (MarkStackTarget.Value > 0) {
                        sb.Append(", target ").Append(MarkStackTarget.Value.ToString("N0"));
                        if (_growing) { sb.Append(", growing"); }
                    }
                }
                if (stats != null) { sb.Append(" | collector log ").Append(stats.Value != 0 ? "on" : "off"); }
            } else {
                long monoHeap = Profiler.GetMonoHeapSizeLong();
                if (monoHeap > 0) { sb.Append(" | heap ").Append((monoHeap / 1048576L).ToString("N0")).Append(" MB"); }
                sb.Append(" | divisor and mark stack: Linux only");
            }

            sb.Append(" | collections ").Append(GC.CollectionCount(0).ToString("N0"));
            return sb.ToString();
        }

        // The stack starts at a power of two and only doubles, so this is where it stops.
        private static long Reached(long size, long target) {
            long reached = Math.Max(size, 1);
            while (reached < target) { reached *= 2; }
            return reached;
        }

        private static string Ms(ulong ns) => (ns / 1e6).ToString("0.###", CultureInfo.InvariantCulture);

        private static void WarnOnce(ref bool warned, string message) {
            if (warned) { return; }
            warned = true;
            Logger.LogWarning("Server GC: " + message);
        }
    }
}
