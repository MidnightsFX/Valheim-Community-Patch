using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Unload Discovery Scan: objects that left the loaded rings are found through the
    // per-zone instance index instead of by stamping and walking every loaded instance.
    //
    // ZNetScene.RemoveObjects discovers what to unload by elimination: stamp an earmark on every
    // ZDO in the loaded rings, then walk the entire instance dictionary and remove whatever was
    // not stamped. That is O(all loaded instances) to find a handful of departures.
    //
    // A Priority.First prefix asks the question directly. SectorInstanceIndexPatch keeps every
    // live instance grouped by its ZDO's zone, so for each zone holding instances (a few hundred
    // keys) the pass keeps everything within the near ring, removes non-distant instances from the
    // distant band and removes everything outside both, which is exactly vanilla's keep-set. The
    // removal sequence per instance is vanilla's, inside the same orphan-recovery contract as
    // RemoveObjectsNrePatch. An Object Unload Frame Budget caps how many departures are handed to
    // the engine per pass, because Unity's end-of-frame destruction flush is what makes a
    // thousands-at-once unload a visible freeze; the remainder is rediscovered from the index next
    // pass at no cost. While the index is healthy this prefix replaces the pacing and NRE prefixes
    // below it; when it is not, it returns true and that stack runs as before.
    //
    // Both: every peer runs this pass.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class ZoneDiffRemovalPatch {
        internal static ConfigEntry<bool> Verify;
        internal static ConfigEntry<int> FrameBudget;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Unload Discovery",
                false,
                "Diagnostic. Computes every unload pass both from the zone index and from " +
                "vanilla's full stamped walk, compares the two removal sets, acts on vanilla's, " +
                "and logs any disagreement. Costs the walk this fix exists to avoid, so leave " +
                "it off unless you are validating the index.",
                advanced: true);

            FrameBudget = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Object Unload Frame Budget",
                250,
                "How many departed objects may be handed to the engine for destruction in a " +
                "single unload pass. Destroying an object is mostly engine work that happens in " +
                "one burst at the end of the frame, so a pass that unloads thousands at once - " +
                "arriving through a portal, respawning, or a world load settling - is a visible " +
                "freeze no matter how fast the game's own bookkeeping is. Capping the pass turns " +
                "that freeze into a short, shallow dip. The remainder unloads on the following " +
                "passes; until then it lingers at the far edge of the loaded distance, where " +
                "nothing can see it. Raise it to unload faster and hitch harder, lower it for " +
                "the reverse. 0 unloads everything at once, exactly like vanilla.",
                advanced: true,
                valMin: 0,
                valMax: 20000);
        }

        private static readonly List<ZNetView> Removed = new List<ZNetView>();
        private static readonly HashSet<ZNetView> VerifyVanillaSet = new HashSet<ZNetView>();
        private static readonly HashSet<ZNetView> VerifyOurSet = new HashSet<ZNetView>();

        private const int VerifyReportInterval = 900;
        private static bool _verifyActive;
        private static long _verifyPasses;
        private static long _verifyOurRemovals;
        private static long _verifyDivergences;
        private static int _passesSinceReport;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch("RemoveObjects")]
        private static bool RemoveObjectsPrefix(
            ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects) {
            if (!SectorInstanceIndexPatch.MaintenanceHealthy) { return true; }
            if (ZNet.instance == null || ZoneSystem.instance == null) { return true; }

            Vector2i center = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            int near = ZoneSystem.instance.m_activeArea;
            int full = near + ZoneSystem.instance.m_activeDistantArea;

            Removed.Clear();
            foreach (KeyValuePair<Vector2i, List<ZNetView>> pair in SectorInstanceIndexPatch.ByZone) {
                int dx = pair.Key.x - center.x;
                int dy = pair.Key.y - center.y;
                if (dx < 0) { dx = -dx; }
                if (dy < 0) { dy = -dy; }
                int ring = dx > dy ? dx : dy;

                if (ring <= near) { continue; }

                List<ZNetView> zone = pair.Value;
                if (ring > full) {
                    Removed.AddRange(zone);
                    continue;
                }

                // The distant band keeps only Distant-flagged ZDOs.
                for (int i = 0; i < zone.Count; i++) {
                    ZNetView view = zone[i];
                    if (view.m_zdo == null || !view.m_zdo.Distant) { Removed.Add(view); }
                }
            }

            if (TeardownHooks.StatsOn) {
                if (Removed.Count >= StormReportThreshold) {
                    ReportStorm(__instance, center, near, full, Removed.Count);
                }

                _lastCenter = center;
                _haveLastCenter = true;
            }

            if (Verify != null && Verify.Value) {
                VerifyPass(__instance, currentNearObjects, currentDistantObjects);
            } else {
                if (_verifyActive) {
                    _verifyActive = false;
                    LogVerifySummary("final");
                    _verifyPasses = 0;
                    _verifyOurRemovals = 0;
                    _verifyDivergences = 0;
                    _passesSinceReport = 0;
                }

                Execute(__instance, currentNearObjects, currentDistantObjects);
            }

            return false;
        }

        // ZNetScene.InLoadingScreen is private and trivial; replicated because this runs on every
        // pass. Client-side meaning only; see the IsDedicated guard at its call site.
        private static bool InLoadingScreen() =>
            Player.m_localPlayer == null || Player.m_localPlayer.IsTeleporting();

        // ---- storm forensics -------------------------------------------------------------------

        // Reported only while the destroy-storm instrument is on: a pass this large is the
        // loaded set leaving the ring at once, and these fields say what moved it.
        private const int StormReportThreshold = 500;
        private static Vector2i _lastCenter;
        private static bool _haveLastCenter;

        private static void ReportStorm(ZNetScene scene, Vector2i center, int near, int full, int count) {
            int moved = -1;
            if (_haveLastCenter) {
                int dx = center.x - _lastCenter.x;
                int dy = center.y - _lastCenter.y;
                if (dx < 0) { dx = -dx; }
                if (dy < 0) { dy = -dy; }
                moved = dx > dy ? dx : dy;
            }

            Logger.LogInfo(
                $"Destroy storm: unload pass discovered {count} object(s) at once. " +
                $"Ring centre zone ({center.x},{center.y}), moved {moved} zone(s) since the last " +
                $"pass; near ring {near}, full ring {full}; " +
                $"{SectorInstanceIndexPatch.ByZone.Count} zone(s) hold instances, " +
                $"{scene.m_instances.Count} instance(s) loaded; " +
                $"loading screen: {InLoadingScreen()}.");
        }

        // Vanilla's removal sequence per instance. A throw anywhere retries the whole pass through
        // RemoveObjectsNrePatch's guarded earmark sweep.
        private static void Execute(
            ZNetScene scene, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects) {
            bool timed = TeardownHooks.StatsOn;
            long startTicks = timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            int backlog = Removed.Count;

            int budget = FrameBudget != null ? FrameBudget.Value : 0;

            // Vanilla raises its create budget during a loading screen for the same reason this
            // drops its cap: a hitch nobody can see is not worth deferring. Not on a dedicated
            // server, which has no local player and would read as permanently mid-loading-screen.
            if (budget > 0 && !RunMode.IsDedicated && InLoadingScreen()) { budget = 0; }

            int limit = budget > 0 && backlog > budget ? budget : backlog;

            try {
                for (int i = 0; i < limit; i++) {
                    ZNetView view = Removed[i];
                    ZDO zdo = view.m_zdo;

                    view.ResetZDO();
                    UnityEngine.Object.Destroy(view.gameObject);

                    if (!zdo.Persistent && zdo.IsOwner()) { ZDOMan.instance.DestroyZDO(zdo); }

                    scene.m_instances.Remove(zdo);
                }
            } catch (Exception e) {
                Logger.LogDebug(
                    $"Zone-diff unload hit an orphaned instance ({e.GetType().Name}); running guarded sweep.");

                byte earmark = (byte)(Time.frameCount & byte.MaxValue);

                // Earmark the keep-set from what is actually loaded, not from the caller's lists:
                // SpawnEventQueuePatch drives this pass with both lists empty, and stamping from
                // them would hand the sweep an empty keep-set and unload the world.
                foreach (ZNetView loaded in scene.m_instances.Values) {
                    ZDO zdo = ReferenceEquals(loaded, null) ? null : loaded.m_zdo;
                    if (zdo != null) { zdo.m_tempRemoveEarmark = earmark; }
                }

                byte removedMark = (byte)(earmark + 1);
                for (int i = 0; i < Removed.Count; i++) {
                    ZNetView view = Removed[i];
                    ZDO zdo = ReferenceEquals(view, null) ? null : view.m_zdo;
                    if (zdo != null) { zdo.m_tempRemoveEarmark = removedMark; }
                }

                Correctness.RemoveObjectsNrePatch.GuardedSweep(scene, earmark);
            }

            if (timed) {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks)
                            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                TeardownHooks.NoteUnloadPass(limit, backlog, ms);
            }
        }

        // Vanilla's discovery (earmark + full walk) as ground truth: compare, report, act on it.
        private static void VerifyPass(
            ZNetScene scene, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects) {
            _verifyActive = true;
            _verifyPasses++;
            _verifyOurRemovals += Removed.Count;

            byte earmark = (byte)(Time.frameCount & byte.MaxValue);
            for (int i = 0; i < currentNearObjects.Count; i++) { currentNearObjects[i].m_tempRemoveEarmark = earmark; }
            for (int i = 0; i < currentDistantObjects.Count; i++) { currentDistantObjects[i].m_tempRemoveEarmark = earmark; }

            try {
                VerifyVanillaSet.Clear();
                foreach (ZNetView view in scene.m_instances.Values) {
                    if (view.m_zdo.m_tempRemoveEarmark != earmark) { VerifyVanillaSet.Add(view); }
                }

                VerifyOurSet.Clear();
                for (int i = 0; i < Removed.Count; i++) { VerifyOurSet.Add(Removed[i]); }

                int missing = 0;
                foreach (ZNetView view in VerifyVanillaSet) {
                    if (!VerifyOurSet.Contains(view)) { missing++; }
                }

                int extra = Removed.Count - (VerifyVanillaSet.Count - missing);
                if (missing > 0 || extra > 0) {
                    _verifyDivergences++;
                    Logger.LogError(
                        $"Unload discovery verify: DIVERGED - index found {Removed.Count} " +
                        $"removal(s), vanilla found {VerifyVanillaSet.Count} ({missing} missed by " +
                        $"the index, {extra} extra). Vanilla's set was used. Please report this - " +
                        "leave 'Fix Unload Discovery Scan' off until it is understood.");
                }

                Removed.Clear();
                Removed.AddRange(VerifyVanillaSet);
                Execute(scene, currentNearObjects, currentDistantObjects);
            } catch (Exception e) {
                Logger.LogDebug(
                    $"Unload discovery verify hit an orphaned instance ({e.GetType().Name}); running guarded sweep.");
                Correctness.RemoveObjectsNrePatch.GuardedSweep(scene, earmark);
            }

            if (++_passesSinceReport >= VerifyReportInterval) {
                _passesSinceReport = 0;
                LogVerifySummary("periodic");
            }
        }

        private static void LogVerifySummary(string kind) {
            Logger.LogInfo(
                $"Unload discovery verify ({kind}): {_verifyPasses} pass(es), " +
                $"{_verifyOurRemovals} index removal(s), {_verifyDivergences} divergence(s).");
        }
    }
}
