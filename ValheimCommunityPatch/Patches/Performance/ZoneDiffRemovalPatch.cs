using System;
using System.Collections.Generic;
using System.Reflection;
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
    // A prefix asks the question directly. SectorInstanceIndexPatch keeps every live instance
    // grouped by its ZDO's zone, so for each zone holding instances (a few hundred keys) the pass
    // keeps everything within the near ring, removes non-distant instances from the distant band
    // and removes everything outside both, which is exactly vanilla's keep-set. The removal
    // sequence per instance is vanilla's, inside the same orphan-recovery contract as
    // RemoveObjectsNrePatch. An Object Unload Frame Budget caps how many departures are handed to
    // the engine per pass, because Unity's end-of-frame destruction flush is what makes a
    // thousands-at-once unload a visible freeze; the remainder is rediscovered from the index next
    // pass at no cost.
    //
    // That keep-set is vanilla's only while the near and distant lists are exactly what
    // ZDOMan.FindSectorObjects filled, and other mods keep an object loaded by adding its ZDO to
    // those lists from their own hooks. So the prefix runs at Priority.Last, after every other
    // mod's prefix; a postfix records each list's mutation counter as it is filled; and a pass
    // whose lists changed in between uses vanilla's discovery instead. While engaged this prefix
    // owns every pass and the pacing and NRE prefixes stand down; without the index or the
    // counter it returns true and that stack runs as before.
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
        private static readonly HashSet<ZNetView> VerifyIndexSet = new HashSet<ZNetView>();

        private const int VerifyReportInterval = 900;
        private static bool _verifyActive;
        private static long _verifyPasses;
        private static long _verifyOurRemovals;
        private static long _verifyDivergences;
        private static long _verifyEditedPasses;
        private static int _passesSinceReport;

        // List<T>'s private mutation counter: every Add, Insert, Remove, Clear, Sort or indexer
        // write bumps it, so an unchanged counter means an unchanged list.
        private static readonly AccessTools.FieldRef<List<ZDO>, int> ListVersion = ResolveListVersion();

        private static int _filledFrame = -1;
        private static int _filledNearVersion;
        private static int _filledDistantVersion;
        private static bool _loggedEditedLists;

        private static readonly HookHealth Hooks = new HookHealth(
            "Unload discovery",
            () => ListVersion != null
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), "FindSectorObjects"), typeof(ListFillHook)));

        /// <summary>
        /// Whether this prefix handles every unload pass this session. RemoveSweepPacingPatch and
        /// RemoveObjectsNrePatch stand down on it, so exactly one of the three acts on each pass
        /// whatever order Harmony runs them in.
        /// </summary>
        internal static bool Engaged => SectorInstanceIndexPatch.MaintenanceHealthy && Hooks.Healthy;

        // Probed on a real list rather than trusted: a counter that never moves would pass every
        // edited list off as vanilla's.
        private static AccessTools.FieldRef<List<ZDO>, int> ResolveListVersion() {
            try {
                AccessTools.FieldRef<List<ZDO>, int> version = AccessTools.FieldRefAccess<List<ZDO>, int>("_version");
                List<ZDO> probe = new List<ZDO>();
                int before = version(probe);
                probe.Add(null);
                return version(probe) != before ? version : null;
            } catch (Exception e) {
                Logger.LogDebug($"List mutation counter unavailable ({e.GetType().Name}).");
                return null;
            }
        }

        [HarmonyPatch(typeof(ZDOMan), "FindSectorObjects")]
        internal static class ListFillHook {
            // First among postfixes, so the counters are read before another mod's postfix can
            // edit the lists.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void Postfix(List<ZDO> sectorObjects, List<ZDO> distantSectorObjects) {
                if (ListVersion == null) { return; }

                // Only CreateDestroyObjects fills the scene's pair. The peer sync lists are other
                // lists, and IsAreaReady refills the near list alone.
                ZNetScene scene = ZNetScene.instance;
                if (ReferenceEquals(scene, null)
                    || !ReferenceEquals(distantSectorObjects, scene.m_tempCurrentDistantObjects)
                    || !ReferenceEquals(sectorObjects, scene.m_tempCurrentObjects)) {
                    return;
                }

                _filledFrame = Time.frameCount;
                _filledNearVersion = ListVersion(sectorObjects);
                _filledDistantVersion = ListVersion(distantSectorObjects);
            }
        }

        // True when both lists are the pair FindSectorObjects filled this frame, unchanged since.
        // The record is consumed, so a later call without a fresh fill cannot reuse it.
        private static bool ListsAsFilled(ZNetScene scene, List<ZDO> near, List<ZDO> distant) {
            bool asFilled = _filledFrame == Time.frameCount
                && ReferenceEquals(near, scene.m_tempCurrentObjects)
                && ReferenceEquals(distant, scene.m_tempCurrentDistantObjects)
                && ListVersion(near) == _filledNearVersion
                && ListVersion(distant) == _filledDistantVersion;

            _filledFrame = -1;
            return asFilled;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("RemoveObjects")]
        private static bool RemoveObjectsPrefix(
            ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects,
            bool __runOriginal) {
            // Another mod's prefix already replaced or skipped this pass; its version wins.
            if (!__runOriginal) { return false; }
            if (!Engaged) { return true; }

            bool verify = Verify != null && Verify.Value;
            if (_verifyActive && !verify) {
                _verifyActive = false;
                LogVerifySummary("final");
                _verifyPasses = 0;
                _verifyOurRemovals = 0;
                _verifyDivergences = 0;
                _verifyEditedPasses = 0;
                _passesSinceReport = 0;
            }

            bool asFilled = ListsAsFilled(__instance, currentNearObjects, currentDistantObjects);
            if (!asFilled) { NoteEditedLists(verify); }

            // The pacing and NRE prefixes have stood down, so a pass the index cannot answer is
            // still finished here.
            if (!asFilled || ZNet.instance == null || ZoneSystem.instance == null) {
                if (DiscoverAsVanilla(__instance, currentNearObjects, currentDistantObjects)) {
                    Execute(__instance, currentNearObjects, currentDistantObjects);
                }

                return false;
            }

            Vector2s center = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            SimulationDistance simulationDistance = ZNet.instance.GetSyncedSimulationDistance();
            int near = simulationDistance.NearSimulationDistance;
            int full = simulationDistance.TotalSimulationDistance;
            bool classic = simulationDistance.IsClassic;

            // ZDOMan.FindSectorObjects gates each ring zone on ZoneSystem.ZonesWithinRadius, which
            // is a disc, not a box - except on the classic distance, whose loops take the whole
            // square. That test compares world distance against (radius + 0.5 or 0.8) zone sizes,
            // and ZoneSystem.GetZonePos lays zone centres on a 64 m grid, so it reduces exactly to
            // a comparison of zone deltas against these radii. The scale factor is 1 in vanilla
            // and keeps the reduction exact if anything ever changes the zone size.
            float zoneScale = ZoneSystem.instance.m_zoneSize / 64f;
            float nearRadius = (near + 0.5f) * zoneScale;
            float fullRadius = (full + 0.8f) * zoneScale;
            float nearRadiusSq = nearRadius * nearRadius;
            float fullRadiusSq = fullRadius * fullRadius;

            Removed.Clear();
            foreach (KeyValuePair<Vector2s, List<ZNetView>> pair in SectorInstanceIndexPatch.ByZone) {
                int dx = pair.Key.x - center.x;
                int dy = pair.Key.y - center.y;
                if (dx < 0) { dx = -dx; }
                if (dy < 0) { dy = -dy; }

                int ring = dx > dy ? dx : dy;
                int distanceSq = dx * dx + dy * dy;

                if (classic ? ring <= near : distanceSq < nearRadiusSq) { continue; }

                List<ZNetView> zone = pair.Value;
                if (classic ? ring > full : distanceSq >= fullRadiusSq) {
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

            if (verify) {
                VerifyPass(__instance, currentNearObjects, currentDistantObjects);
            } else {
                Execute(__instance, currentNearObjects, currentDistantObjects);
            }

            return false;
        }

        // Once per session, with the mods hooking the object pass, so the cost of passes that fall
        // back to vanilla's discovery can be traced to whoever edits the lists.
        private static void NoteEditedLists(bool verify) {
            if (verify) {
                _verifyActive = true;
                _verifyEditedPasses++;
            }

            if (_loggedEditedLists) { return; }
            _loggedEditedLists = true;

            Logger.LogInfo(
                "Unload discovery: another mod changed which objects stay loaded " +
                $"(mods hooking the object pass: {OtherOwnersOnObjectPass()}). Passes where that " +
                "happens use the game's own unload check, so whatever that mod keeps loaded stays " +
                "loaded. This is compatibility working, not an error.");
        }

        private static string OtherOwnersOnObjectPass() {
            MethodBase[] pass = {
                AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDestroyObjects"),
                AccessTools.DeclaredMethod(typeof(ZDOMan), "FindSectorObjects"),
                AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjects"),
                AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjectsSorted"),
                AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDistantObjects"),
                AccessTools.DeclaredMethod(typeof(ZNetScene), "RemoveObjects"),
            };

            SortedSet<string> owners = new SortedSet<string>(StringComparer.Ordinal);
            foreach (MethodBase method in pass) {
                // Fully qualified: HarmonyLib.Patches collides with this mod's Patches namespace.
                HarmonyLib.Patches info = method == null ? null : Harmony.GetPatchInfo(method);
                if (info == null) { continue; }

                foreach (string owner in info.Owners) {
                    if (owner != ValheimCommunityPatch.PluginGUID) { owners.Add(owner); }
                }
            }

            return owners.Count > 0 ? string.Join(", ", owners) : "none found";
        }

        // ZNetScene.InLoadingScreen is private and trivial; replicated because this runs on every
        // pass. Client-side meaning only; see the IsDedicated guard at its call site.
        private static bool InLoadingScreen() =>
            Player.m_localPlayer == null || Player.m_localPlayer.IsTeleporting();

        // ---- storm forensics -------------------------------------------------------------------

        // Reported only while the destroy-storm instrument is on: a pass this large is the
        // loaded set leaving the ring at once, and these fields say what moved it.
        private const int StormReportThreshold = 500;
        private static Vector2s _lastCenter;
        private static bool _haveLastCenter;

        private static void ReportStorm(ZNetScene scene, Vector2s center, int near, int full, int count) {
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

                // Earmark the keep-set from what is actually loaded, not from the caller's lists, so
                // the sweep removes exactly the departures chosen above.
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

        // Vanilla's discovery (earmark + full walk) into Removed. An orphaned instance makes the walk
        // throw; the guarded sweep then finishes the pass on vanilla's earmarks and this returns
        // false, leaving nothing for Execute.
        private static bool DiscoverAsVanilla(
            ZNetScene scene, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects) {
            byte earmark = (byte)(Time.frameCount & byte.MaxValue);
            for (int i = 0; i < currentNearObjects.Count; i++) { currentNearObjects[i].m_tempRemoveEarmark = earmark; }
            for (int i = 0; i < currentDistantObjects.Count; i++) { currentDistantObjects[i].m_tempRemoveEarmark = earmark; }

            Removed.Clear();
            try {
                foreach (ZNetView view in scene.m_instances.Values) {
                    if (view.m_zdo.m_tempRemoveEarmark != earmark) { Removed.Add(view); }
                }
            } catch (Exception e) {
                Logger.LogDebug(
                    $"Vanilla unload discovery hit an orphaned instance ({e.GetType().Name}); running guarded sweep.");
                Removed.Clear();
                Correctness.RemoveObjectsNrePatch.GuardedSweep(scene, earmark);
                return false;
            }

            return true;
        }

        // Vanilla's discovery as ground truth: compare, report, act on it.
        private static void VerifyPass(
            ZNetScene scene, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects) {
            _verifyActive = true;
            _verifyPasses++;
            _verifyOurRemovals += Removed.Count;

            VerifyIndexSet.Clear();
            for (int i = 0; i < Removed.Count; i++) { VerifyIndexSet.Add(Removed[i]); }

            if (DiscoverAsVanilla(scene, currentNearObjects, currentDistantObjects)) {
                int missing = 0;
                for (int i = 0; i < Removed.Count; i++) {
                    if (!VerifyIndexSet.Contains(Removed[i])) { missing++; }
                }

                int extra = VerifyIndexSet.Count - (Removed.Count - missing);
                if (missing > 0 || extra > 0) {
                    _verifyDivergences++;
                    Logger.LogError(
                        $"Unload discovery verify: DIVERGED - index found {VerifyIndexSet.Count} " +
                        $"removal(s), vanilla found {Removed.Count} ({missing} missed by " +
                        $"the index, {extra} extra). Vanilla's set was used. Please report this - " +
                        "leave 'Fix Unload Discovery Scan' off until it is understood.");
                }

                Execute(scene, currentNearObjects, currentDistantObjects);
            }

            if (++_passesSinceReport >= VerifyReportInterval) {
                _passesSinceReport = 0;
                LogVerifySummary("periodic");
            }
        }

        private static void LogVerifySummary(string kind) {
            Logger.LogInfo(
                $"Unload discovery verify ({kind}): {_verifyPasses} pass(es), " +
                $"{_verifyOurRemovals} index removal(s), {_verifyDivergences} divergence(s), " +
                $"{_verifyEditedPasses} pass(es) not compared because another mod changed the lists.");
        }
    }
}
