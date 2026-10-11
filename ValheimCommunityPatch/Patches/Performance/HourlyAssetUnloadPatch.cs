using System;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Hourly Server Freeze: on a dedicated server, the game's hourly cleanup of unused assets
    // waits until no players are connected, instead of freezing the server, and everyone on it, for
    // most of a second.
    //
    // Game.Start has CollectResourcesCheckPeriodic run every 3600 s. It calls CollectResources,
    // which calls Resources.UnloadUnusedAssets, and Unity does that work on the main thread just
    // after the call returns: a blocking full garbage collection plus its own search for assets
    // nothing references. The collection walks everything still alive, so its length follows the
    // heap. On a dedicated server holding 2.3 million ZDOs in a 1.5 GB heap each one froze the
    // server for 850-990 ms, about 70 times in four days and 18 of them with players connected,
    // while the managed heap came out of each within a megabyte of where it went in: the
    // incremental collector had already freed what was garbage, so the freeze is the cost of the
    // walk, not of a backlog.
    //
    // A prefix on CollectResourcesCheckPeriodic lets the call through when nobody is connected and
    // otherwise records the unload as owed. A Game.Update postfix runs an owed unload, through
    // vanilla's CollectResources, once the server has had no peers for two minutes, so a player
    // relogging is not caught by it, or once it has been owed for 'Asset Unload Max Delay' hours,
    // players or not. Vanilla's one-hour spacing is checked first and left to decide, so the hourly
    // call after a late unload skips as it would after an on-time one. The unloads at world load,
    // at shutdown and from the console are untouched. Each unload this fix lets run is logged with
    // the longest frame that followed it and the memory before and after, which shows what the
    // cleanup actually gives back on a given server.
    //
    // Server: a dedicated server only. A listen host is being played on, so it is never empty, and
    // it keeps the game's timing.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(Game))]
    internal static class HourlyAssetUnloadPatch {
        internal static FixToggle Enabled;
        internal static ConfigEntry<float> MaxDelayHours;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(HourlyAssetUnloadPatch),
                ValConfig.SectionPerformance,
                "Fix Hourly Server Freeze",
                true,
                "Holds the game's hourly cleanup of unused assets until no players are connected. On a " +
                "dedicated server with a large world that cleanup freezes the server, and every player on " +
                "it, for up to a second. On a server that is never empty it still runs within 'Asset Unload " +
                "Max Delay' hours. Turn it off for the game's own hourly timing.");

            MaxDelayHours = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Asset Unload Max Delay",
                12f,
                "With Fix Hourly Server Freeze on, the most hours the cleanup waits for an empty server " +
                "before running anyway, players or not. 0 waits for an empty server however long that takes.",
                advanced: true, valMin: 0f, valMax: 168f);
        }

        // Long enough that a player relogging, or reconnecting after a crash, is back before the
        // server counts as empty.
        private const float EmptySeconds = 120f;

        // How long after an unload the frame times are watched for the freeze it caused.
        private const float MeasureSeconds = 5f;

        private const float SecondsPerHour = 3600f;

        private static bool IsOn => Enabled != null && Enabled.Value;

        private static float MaxDelay => MaxDelayHours != null ? Mathf.Max(MaxDelayHours.Value, 0f) : 12f;

        // The Game the owed state belongs to; a different one is a new world session.
        private static Game _game;

        // Time.realtimeSinceStartup when the unload became owed, and when the server was first seen
        // empty while it was owed. -1 for neither.
        private static float _owedSince = -1f;
        private static float _emptySince = -1f;

        // The unload being watched: what led to it (null when none), when the watch ends, the
        // longest frame seen so far, and memory just before it.
        private static string _measuring;
        private static float _measureUntil;
        private static float _longestFrame;
        private static Memory _before;

        // Priority.Last and __runOriginal: see ValheimCommunityPatch.ApplyPatches. A mod that has
        // already cancelled the hourly unload keeps it cancelled, and nothing is owed.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(Game.CollectResourcesCheckPeriodic))]
        private static bool CollectResourcesCheckPeriodicPrefix(Game __instance, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (!IsOn || !RunMode.IsDedicated) { return true; }

            if (!ReferenceEquals(__instance, _game)) {
                _game = __instance;
                ClearOwed();
            }

            // Vanilla's own spacing: it skips, and logs that it did.
            if (!UnloadDue(__instance)) { return true; }

            // Empty now: vanilla runs it, and that settles anything still owed from an earlier hour.
            int peers = PeerCount();
            if (peers == 0) {
                ClearOwed();
                StartMeasuring("on schedule, with the server empty");
                return true;
            }

            if (_owedSince < 0f) {
                _owedSince = Time.realtimeSinceStartup;
                _emptySince = -1f;
                Logger.LogInfo(
                    $"Hourly asset unload postponed: {peers} player(s) connected. It runs once the server has " +
                    $"been empty for {EmptySeconds / 60f:0} minutes" +
                    (MaxDelay > 0f ? $", or in {MaxDelay:0.#} h at the latest." : "."));
            } else {
                Logger.LogDebug($"Hourly asset unload still postponed: {peers} player(s) connected.");
            }

            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Game.Update))]
        private static void UpdatePostfix(Game __instance) {
            if (_measuring != null) { WatchFrame(); }
            if (_owedSince < 0f) { return; }

            // Turned off, or the session it was owed in is ending: the timing goes back to the game,
            // whose next hourly call finds the unload due. Shutdown runs its own unload anyway.
            if (!IsOn || !ReferenceEquals(__instance, _game) || __instance.m_shuttingDown || ZNet.instance == null) {
                ClearOwed();
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (PeerCount() > 0) {
                _emptySince = -1f;
            } else if (_emptySince < 0f) {
                _emptySince = now;
            }

            bool empty = _emptySince >= 0f && now - _emptySince >= EmptySeconds;
            bool overdue = MaxDelay > 0f && now - _owedSince >= MaxDelay * SecondsPerHour;
            if (!empty && !overdue) { return; }

            float lateHours = (now - _owedSince) / SecondsPerHour;
            ClearOwed();
            StartMeasuring(empty
                ? $"{lateHours:0.0} h late, once the server was empty"
                : $"{lateHours:0.0} h late, with players connected, at 'Asset Unload Max Delay'");
            __instance.CollectResources(displayMessage: true);
        }

        // Vanilla CollectResourcesCheckPeriodic's own test.
        private static bool UnloadDue(Game game) =>
            DateTime.Now - TimeSpan.FromSeconds(3599.0) > game.m_lastCollectResources;

        // Every peer, ready or still joining: a player mid-join is about to be playing.
        private static int PeerCount() {
            ZNet znet = ZNet.instance;
            return znet != null ? znet.m_peers.Count : 0;
        }

        private static void ClearOwed() {
            _owedSince = -1f;
            _emptySince = -1f;
        }

        // ---- the log line each unload gets ----------------------------------------------

        private struct Memory {
            internal long Managed;
            internal long Allocated;
            internal long Reserved;

            // Unity's figures read 0 where the player build does not expose them.
            internal static Memory Read() => new Memory {
                Managed = GC.GetTotalMemory(false),
                Allocated = Profiler.GetTotalAllocatedMemoryLong(),
                Reserved = Profiler.GetTotalReservedMemoryLong(),
            };
        }

        private static void StartMeasuring(string what) {
            _measuring = what;
            _measureUntil = Time.realtimeSinceStartup + MeasureSeconds;
            _longestFrame = 0f;
            _before = Memory.Read();
        }

        // The unload lands in a frame after the call, so the freeze shows up as one long frame
        // somewhere in the watch.
        private static void WatchFrame() {
            _longestFrame = Mathf.Max(_longestFrame, Time.unscaledDeltaTime);
            if (Time.realtimeSinceStartup < _measureUntil) { return; }

            Memory after = Memory.Read();
            StringBuilder sb = new StringBuilder(256);
            sb.Append("Asset unload ran ").Append(_measuring)
              .Append(": longest frame after it ").Append((_longestFrame * 1000f).ToString("N0")).Append(" ms")
              .Append(" | managed heap ").Append(Mb(_before.Managed)).Append(" -> ").Append(Mb(after.Managed)).Append(" MB");
            if (_before.Allocated > 0 && after.Allocated > 0) {
                sb.Append(" | Unity allocated ").Append(Mb(_before.Allocated)).Append(" -> ").Append(Mb(after.Allocated)).Append(" MB");
            }
            if (_before.Reserved > 0 && after.Reserved > 0) {
                sb.Append(", reserved ").Append(Mb(_before.Reserved)).Append(" -> ").Append(Mb(after.Reserved)).Append(" MB");
            }

            Logger.LogInfo(sb.ToString());
            _measuring = null;
        }

        private static string Mb(long bytes) => (bytes / 1048576L).ToString("N0");
    }
}
