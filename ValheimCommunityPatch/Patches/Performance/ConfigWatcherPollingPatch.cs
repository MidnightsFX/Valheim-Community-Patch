using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Config Watcher Polling: each mod's config file watcher re-lists the same BepInEx folders every 750 ms.
    // Transpiles Mono's DefaultWatcher.DoFiles and IterateAndModifyFilesData to share each listing within a poll cycle.
    // Config watchers cost a fraction of the background work and garbage, so the main thread pauses for GC less.
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
                "Seconds between scans by the file watchers mods use to reload their configs; 0.75 is the " +
                "game's own rate. Higher cuts background work further but a config edit takes up to this " +
                "long to be noticed; per-machine, not synced from the server.",
                VanillaPollSeconds,
                10f);
        }

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ConfigWatcherPollingPatch));

        // Unity's Mono has no native file watcher, so every FileSystemWatcher polls through this private
        // one, on a single thread. Resolved by name, since a Unity update can ship another Mono.
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
        // The BCL call is made exactly as vanilla would, and its exceptions propagate unchanged. These run on
        // the poll thread, so they touch no Unity API.

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
                    // Bookkeeping only. An exception would end the poll thread, and Mono never restarts it.
                }

                return true;
            }
        }
    }
}
