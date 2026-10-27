using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Config Watcher Polling: the file watchers mods use to reload their configs share one
    // directory listing per poll instead of each re-reading the same folders every 750 ms.
    //
    // Unity's Mono has no native file watcher, so every FileSystemWatcher runs on
    // System.IO.DefaultWatcher: one thread that sleeps 750 ms, then for each watcher lists its
    // folders (recursively when subdirectories are included) and reads the timestamps of every file
    // it tracks; one whose filter names a single file asks instead, in every folder it recurses
    // into, whether that file exists. A modded install has a dozen or more watchers over the same
    // BepInEx tree, so the same folders are listed and the same files checked that many times per
    // cycle: a fifth of a core of background work and more garbage than anything else in the
    // process, which the garbage collector's pauses on the main thread pay for.
    //
    // Transpilers on DoFiles and IterateAndModifyFilesData point the listing, existence and
    // timestamp calls at shims that serve the answer taken earlier in the same cycle, so each folder
    // and file is read once per cycle however many watchers cover it. Nothing older than 500 ms is ever served, so a
    // watcher never sees a listing staler than one vanilla poll; a hook on UpdateDataAndDispatch
    // notices the pass over the watcher table starting again and drops the cache, and applies the
    // optional longer poll. The poll thread itself is not touched. Everything here runs on that
    // thread, touches no Unity API, and never throws of its own: an exception there ends the thread
    // and Mono never restarts it.
    //
    // Both: dedicated servers run the same watchers.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class ConfigWatcherPollingPatch {
        private const string FixName = "Fix Config Watcher Polling";
        private const float VanillaPollSeconds = 0.75f;

        // Never serve an answer older than this. Shorter than vanilla's 750 ms poll, so the cache can
        // only ever delay a change by less than one poll, whatever else happens.
        private const int MaxAgeMs = 500;

        // The cache is per cycle, but should the cycle hook be missing it must not grow forever.
        private const int MaxEntries = 50000;

        internal static ConfigEntry<float> PollInterval;

        internal static void BindConfig() {
            PollInterval = ValConfig.BindClientConfig(
                "Config Watcher Poll Interval",
                VanillaPollSeconds,
                "How often, in seconds, the file watchers that mods use to reload their configs re-read " +
                "the folders they watch. 0.75 is the game's own rate. Raising it cuts background work " +
                "and garbage collection pauses further; a config edit then takes up to this long to be " +
                "noticed. Per-machine: this is not synced from the server.",
                VanillaPollSeconds,
                10f);
        }

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ConfigWatcherPollingPatch));

        // Mono's private watcher. Resolved by name, since a Unity update can ship another Mono.
        private static readonly Type WatcherType = AccessTools.TypeByName("System.IO.DefaultWatcher");
        private static readonly Type DataType = AccessTools.TypeByName("System.IO.DefaultWatcherData");
        private static readonly MethodInfo DoFilesMethod = WatcherType == null ? null : AccessTools.DeclaredMethod(WatcherType, "DoFiles");
        private static readonly MethodInfo IterateMethod = WatcherType == null ? null : AccessTools.DeclaredMethod(WatcherType, "IterateAndModifyFilesData");
        private static readonly MethodInfo UpdateMethod = WatcherType == null ? null : AccessTools.DeclaredMethod(WatcherType, "UpdateDataAndDispatch");
        private static readonly FieldInfo EnabledField = DataType == null ? null : AccessTools.DeclaredField(DataType, "Enabled");

        private static readonly bool Supported =
            DoFilesMethod != null && IterateMethod != null && UpdateMethod != null && EnabledField != null
            && UpdateMethod.ReturnType == typeof(bool);

        private static bool _loggedUnsupported;

        // Shared by the three hook classes' Prepare: all or nothing, so the fix is either as described
        // or off. Not itself named Prepare, which Harmony would pick up by name on the outer class.
        private static bool Ready() {
            if (Supported) { return true; }

            if (!_loggedUnsupported) {
                _loggedUnsupported = true;
                FixRegistry.MarkStoodDown(typeof(ConfigWatcherPollingPatch));
                Logger.LogInfo(
                    $"'{FixName}' stands down: this Mono's file watcher does not have the members it expects, so " +
                    "config file watchers keep the game's own polling.");
            }

            return false;
        }

        // ---- the per-thread, per-cycle cache ----------------------------------------------------

        private struct Stamped<T> {
            public int Tick;
            public T Value;
        }

        private sealed class CycleCache {
            public readonly Dictionary<string, Stamped<bool>> Exists = new Dictionary<string, Stamped<bool>>(StringComparer.Ordinal);
            public readonly Dictionary<string, Stamped<bool>> FileExists = new Dictionary<string, Stamped<bool>>(StringComparer.Ordinal);
            public readonly Dictionary<string, Stamped<string[]>> Directories = new Dictionary<string, Stamped<string[]>>(StringComparer.Ordinal);
            public readonly Dictionary<string, Stamped<string[]>> Entries = new Dictionary<string, Stamped<string[]>>(StringComparer.Ordinal);
            public readonly Dictionary<string, Stamped<DateTime>> Creation = new Dictionary<string, Stamped<DateTime>>(StringComparer.Ordinal);
            public readonly Dictionary<string, Stamped<DateTime>> LastWrite = new Dictionary<string, Stamped<DateTime>>(StringComparer.Ordinal);

            // Watchers served since the cache was last dropped. The poll thread walks a copy of the
            // watcher table once per cycle, so one coming round again means a new cycle.
            public readonly HashSet<object> Seen = new HashSet<object>(ReferenceComparer.Instance);

            public int Count => Exists.Count + FileExists.Count + Directories.Count + Entries.Count + Creation.Count + LastWrite.Count;

            public void Clear() {
                Exists.Clear();
                FileExists.Clear();
                Directories.Clear();
                Entries.Clear();
                Creation.Clear();
                LastWrite.Clear();
                Seen.Clear();
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object> {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        // Per thread: the poll thread has one, and the thread that starts a watcher (its seeding
        // scan) its own, so nothing is shared across threads.
        [ThreadStatic] private static CycleCache _cycle;

        private static CycleCache Cycle => _cycle ?? (_cycle = new CycleCache());

        private static bool Fresh(int takenTick) => unchecked(Environment.TickCount - takenTick) < MaxAgeMs;

        private static bool TryGet<T>(Dictionary<string, Stamped<T>> cache, string key, out T value) {
            if (cache.TryGetValue(key, out Stamped<T> entry) && Fresh(entry.Tick)) {
                value = entry.Value;
                return true;
            }

            value = default;
            return false;
        }

        private static void Put<T>(CycleCache cycle, Dictionary<string, Stamped<T>> cache, string key, T value) {
            if (cycle.Count >= MaxEntries) { cycle.Clear(); }
            cache[key] = new Stamped<T> { Tick = Environment.TickCount, Value = value };
        }

        // ---- shims: same signatures as the BCL calls they stand in for ---------------------------
        // The BCL call is made exactly as vanilla would, and its exceptions propagate unchanged.

        private static bool DirectoryExists(string path) {
            if (ApiSwitch.Off) { return Directory.Exists(path); }

            CycleCache cycle = Cycle;
            if (TryGet(cycle.Exists, path, out bool cached)) { return cached; }

            bool result = Directory.Exists(path);
            Put(cycle, cycle.Exists, path, result);
            return result;
        }

        private static bool FileExists(string path) {
            if (ApiSwitch.Off) { return File.Exists(path); }

            CycleCache cycle = Cycle;
            if (TryGet(cycle.FileExists, path, out bool cached)) { return cached; }

            bool result = File.Exists(path);
            Put(cycle, cycle.FileExists, path, result);
            return result;
        }

        private static string[] GetDirectories(string path) {
            if (ApiSwitch.Off) { return Directory.GetDirectories(path); }

            CycleCache cycle = Cycle;
            if (TryGet(cycle.Directories, path, out string[] cached)) { return cached; }

            string[] result = Directory.GetDirectories(path);
            Put(cycle, cycle.Directories, path, result);
            return result;
        }

        private static string[] GetFileSystemEntries(string path, string searchPattern) {
            if (ApiSwitch.Off) { return Directory.GetFileSystemEntries(path, searchPattern); }

            CycleCache cycle = Cycle;
            string key = path + "|" + searchPattern;
            if (TryGet(cycle.Entries, key, out string[] cached)) { return cached; }

            string[] result = Directory.GetFileSystemEntries(path, searchPattern);
            Put(cycle, cycle.Entries, key, result);
            return result;
        }

        private static DateTime GetCreationTime(string path) {
            if (ApiSwitch.Off) { return File.GetCreationTime(path); }

            CycleCache cycle = Cycle;
            if (TryGet(cycle.Creation, path, out DateTime cached)) { return cached; }

            DateTime result = File.GetCreationTime(path);
            Put(cycle, cycle.Creation, path, result);
            return result;
        }

        private static DateTime GetLastWriteTime(string path) {
            if (ApiSwitch.Off) { return File.GetLastWriteTime(path); }

            CycleCache cycle = Cycle;
            if (TryGet(cycle.LastWrite, path, out DateTime cached)) { return cached; }

            DateTime result = File.GetLastWriteTime(path);
            Put(cycle, cycle.LastWrite, path, result);
            return result;
        }

        private static MethodInfo Shim(string name) => AccessTools.Method(typeof(ConfigWatcherPollingPatch), name);

        // ---- hooks ------------------------------------------------------------------------------
        // Priority.Last on the transpilers: see ValheimCommunityPatch.ApplyPatches.

        [HarmonyPatch]
        internal static class DoFilesHook {
            [HarmonyPrepare]
            private static bool Prepare() => Ready();

            [HarmonyTargetMethod]
            private static MethodBase Target() => DoFilesMethod;

            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                const string site = "DefaultWatcher.DoFiles";
                IEnumerable<CodeInstruction> codes = PatchHelper.ReplaceCalls(
                    instructions,
                    AccessTools.Method(typeof(Directory), nameof(Directory.Exists), new[] { typeof(string) }),
                    Shim(nameof(DirectoryExists)), site);
                codes = PatchHelper.ReplaceCalls(
                    codes,
                    AccessTools.Method(typeof(Directory), nameof(Directory.GetDirectories), new[] { typeof(string) }),
                    Shim(nameof(GetDirectories)), site, 1);
                // A filter without wildcards: the one file is looked for in every folder recursed into.
                codes = PatchHelper.ReplaceCalls(
                    codes,
                    AccessTools.Method(typeof(File), nameof(File.Exists), new[] { typeof(string) }),
                    Shim(nameof(FileExists)), site, 1);
                return PatchHelper.ReplaceCalls(
                    codes,
                    AccessTools.Method(typeof(Directory), nameof(Directory.GetFileSystemEntries), new[] { typeof(string), typeof(string) }),
                    Shim(nameof(GetFileSystemEntries)), site, 1);
            }
        }

        [HarmonyPatch]
        internal static class IterateHook {
            [HarmonyPrepare]
            private static bool Prepare() => Ready();

            [HarmonyTargetMethod]
            private static MethodBase Target() => IterateMethod;

            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                const string site = "DefaultWatcher.IterateAndModifyFilesData";
                IEnumerable<CodeInstruction> codes = PatchHelper.ReplaceCalls(
                    instructions,
                    AccessTools.Method(typeof(File), nameof(File.GetCreationTime), new[] { typeof(string) }),
                    Shim(nameof(GetCreationTime)), site, 1);
                return PatchHelper.ReplaceCalls(
                    codes,
                    AccessTools.Method(typeof(File), nameof(File.GetLastWriteTime), new[] { typeof(string) }),
                    Shim(nameof(GetLastWriteTime)), site, 1);
            }
        }

        // Once per watcher per cycle, with dispatch true from the poll thread and false from the
        // seeding scan a watcher's start runs on its own thread.
        [HarmonyPatch]
        internal static class CycleHook {
            private sealed class LastScan { public int Tick; public bool Scanned; }

            private static readonly ConditionalWeakTable<object, LastScan> LastScans = new ConditionalWeakTable<object, LastScan>();

            [HarmonyPrepare]
            private static bool Prepare() => Ready();

            [HarmonyTargetMethod]
            private static MethodBase Target() => UpdateMethod;

            [HarmonyPrefix]
            private static bool Prefix(object data, bool dispatch, ref bool __result) {
                if (ApiSwitch.Off || data == null) { return true; }

                try {
                    CycleCache cycle = Cycle;
                    if (!cycle.Seen.Add(data)) {
                        cycle.Clear();
                        cycle.Seen.Add(data);
                    }

                    // A disabled watcher must reach vanilla, which is what removes it from the table.
                    if (!dispatch || PollInterval == null || !(bool)EnabledField.GetValue(data)) { return true; }

                    int intervalMs = (int)(Math.Max(PollInterval.Value, VanillaPollSeconds) * 1000f);
                    LastScan last = LastScans.GetValue(data, _ => new LastScan());
                    int now = Environment.TickCount;
                    if (last.Scanned && unchecked(now - last.Tick) < intervalMs) {
                        __result = false;
                        return false;
                    }

                    last.Scanned = true;
                    last.Tick = now;
                } catch (Exception) {
                    // Bookkeeping only; never let anything of ours end the poll thread.
                }

                return true;
            }
        }
    }
}
