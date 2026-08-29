using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: ZNetScene.RemoveObjects DISCOVERS what to unload by elimination - stamp an
    // earmark on every ZDO in the loaded rings, then walk the entire instance dictionary and
    // remove whatever was not stamped (ZNetScene.cs:230-253). The discovery is O(all loaded
    // instances) to find a handful of departures, so its cost scales with the loaded-object count
    // - measured at ~12 ms of every second at a widened zone ring, even after being paced to
    // 10 Hz and stripped to raw field reads.
    //
    // Fix: ask the question directly instead of by elimination. SectorInstanceIndexPatch already
    // maintains every live instance grouped by its ZDO's zone (the same sector value vanilla's
    // own stores hold, updated at the same write sites), so what left the rings is: for each
    // zone that holds instances - a few hundred keys, not tens of thousands of instances - keep
    // everything within the near ring, remove non-distant instances from zones in the distant
    // band, remove everything from zones outside both. That is exactly vanilla's keep-set,
    // because FindSectorObjects builds its near list from all ZDOs in the near square and its
    // distant list from Distant-flagged ZDOs in the band (ZDOMan.cs:693-728), and ring
    // membership is the same Chebyshev square. Sector-invalidated ZDOs (sentinel sector) land in
    // an out-of-ring zone bucket and are removed, as vanilla removes them; ZDO death does not
    // need this pass at all (ZNetScene.OnZDODestroyed removes directly).
    //
    // Cost: O(zones-with-instances) per pass plus O(actual departures) - effectively free at any
    // ring size - and the earmark stamping disappears with the walk. The removal execution is
    // vanilla's exact sequence per instance. Discovery-by-index also removes the need to PACE
    // the sweep: this prefix sits at Priority.First and, while active, replaces the whole stack
    // below it on this method (RemoveSweepPacingPatch's interval, RemoveObjectsNrePatch's fast
    // pass, vanilla) - restoring vanilla's every-pass unload latency at near-zero cost. Toggled
    // off or unhealthy, it returns true and that stack behaves exactly as before.
    //
    // Orphan resilience is inherited, not lost: the execution loop runs inside the same
    // try/catch contract as RemoveObjectsNrePatch, and a throw falls back to that patch's
    // GuardedSweep (earmarks stamped here first, since the guarded sweep is earmark-based).
    //
    // "Verify Unload Discovery" runs vanilla's earmark discovery every pass, compares the two
    // removal sets member-by-member, acts on vanilla's, and reports engagement periodically -
    // the standard proving protocol before trusting the index-driven path.
    //
    // Both: every peer including a dedicated server runs this pass, at whatever ring size.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class ZoneDiffRemovalPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ZoneDiffRemovalPatch),
                ValConfig.SectionPerformance,
                "Fix Unload Discovery Scan",
                true,
                "Finds objects that left the loaded area by asking the per-zone instance index " +
                "directly, instead of stamping and walking every loaded object to see what was " +
                "not stamped. The walk's cost scales with how much is loaded; the index answer " +
                "is effectively free, and unloading returns to vanilla's immediate cadence.");

            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Unload Discovery",
                false,
                "Diagnostic. Computes every unload pass both from the zone index and from " +
                "vanilla's full stamped walk, compares the two removal sets, acts on vanilla's, " +
                "and logs any disagreement. Costs the walk this fix exists to avoid, so leave " +
                "it off unless you are validating the index.",
                advanced: true);
        }

        // Reused between passes; the guarded-sweep fallback and verify sets are rare paths.
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
            if (Enabled == null || !Enabled.Value) { return true; }
            if (!SectorInstanceIndexPatch.MaintenanceHealthy()) { return true; }
            if (ZNet.instance == null || ZoneSystem.instance == null) { return true; }

            // The same reference the ring is built from everywhere else this frame.
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

                // The distant band: vanilla's distant list keeps only Distant-flagged ZDOs here.
                for (int i = 0; i < zone.Count; i++) {
                    ZNetView view = zone[i];
                    if (view.m_zdo == null || !view.m_zdo.Distant) { Removed.Add(view); }
                }
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

        // Vanilla's removal sequence per instance (ZNetScene.cs:243-252), under the same
        // orphan-recovery contract as RemoveObjectsNrePatch: a throw anywhere retries the whole
        // pass through the guarded earmark sweep, which needs the earmarks this path otherwise
        // never writes.
        private static void Execute(
            ZNetScene scene, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects) {
            try {
                for (int i = 0; i < Removed.Count; i++) {
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

                // Earmark the KEEP set from what is actually loaded, not from the caller's
                // lists. The guarded sweep removes everything left unstamped, and those lists
                // are only vanilla's keep-set when vanilla built them - SpawnEventQueuePatch
                // drives this pass with both of them empty, and stamping from them there would
                // hand the sweep an empty keep-set and unload the world. Stamping everything
                // still loaded and then unstamping this pass's own removal set is the same
                // partition, from a source that is correct for either caller.
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

                // Act on vanilla's answer.
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
