using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: ZNetScene.HaveInstanceInSector answers "does any non-distant object stand in
    // this zone" by iterating the ENTIRE instance dictionary - with a native alive-check and a
    // native transform.position read per entry (ZNetScene.cs:329-337) - until it finds one, or
    // has visited all ~90k entries to say no. Its one caller is ZoneSystem.UpdateTTL
    // (ZoneSystem.cs:1779), which asks it for every loaded zone whose TTL has expired before
    // unloading that zone's terrain. Measured while a large base streams: ~29 ms of every second.
    //
    // Fix: a per-zone count of non-distant instances, answered in O(1). Maintained on the
    // complete mutation surface of m_instances, which was verified to be exactly one write site:
    //  - ZNetScene.AddInstance is the only place an entry is added (ZNetScene.cs:65);
    //  - every removal path (RemoveObjects, OnZDODestroyed, Shutdown) destroys the view's
    //    GameObject, so ZNetView.OnDestroy is the removal signal - and because removal paths call
    //    ResetZDO first, the bookkeeping is keyed by the ZNetView, not the ZDO;
    //  - ZDOMan.AddToSector is the single place a ZDO's sector changes land (SetSector's
    //    remove-then-add always ends there), which keeps moving creatures counted in the right
    //    zone. The sector-invalidation bounce (remove to sentinel and back inside one handler)
    //    passes through as two harmless moves that cancel.
    //
    // Equivalence: vanilla keys presence on the zone of the instance's live transform; the index
    // keys it on the ZDO's sector. For the static majority these never differ. For movers they
    // differ by at most the position-sync latency - and the error direction is safe on the side
    // that matters: a remote mover's transform lags its ZDO, so the index reports the NEW zone
    // occupied before the transform arrives there, keeping it loaded early rather than late. An
    // owned mover's ZDO sector trails its transform by under a frame. Against m_zoneTTL, a
    // sub-second disagreement on when a zone became empty is immaterial. A destroyed-this-frame
    // view is counted until its deferred OnDestroy where vanilla's alive-check skips it a few
    // frames sooner - again the safe direction (a zone lives marginally longer).
    //
    // The index is maintained unconditionally (standing rule); the read stands down to
    // vanilla's walk if any maintenance hook failed to attach.
    //
    // Both: a dedicated server runs UpdateTTL over its own loaded zones at the same rate.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class SectorInstanceIndexPatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Zone Occupancy",
                false,
                "Diagnostic. Answers every zone-occupancy check both from the tally and " +
                "vanilla's full walk, acts on vanilla's answer, and logs disagreements. Costs " +
                "the walk this fix exists to avoid, so leave it off unless you are validating " +
                "the tally. Transient one-off disagreements on zones holding a moving creature " +
                "are expected and harmless; persistent ones are not.",
                advanced: true);
        }

        // Zones with a positive count only - Bump removes emptied keys, so presence in the
        // dictionary IS the answer for the occupancy read below.
        private static readonly Dictionary<Vector2i, int> NonDistantCount = new Dictionary<Vector2i, int>();

        // The full index: every live instance, grouped by its ZDO's zone - the same sector value
        // vanilla's own sector stores hold, updated at the same write sites. ZoneDiffRemovalPatch
        // iterates this instead of the whole instance dictionary to find what left the loaded
        // rings. Slots carries each view's zone AND its position in that zone's list, keyed by
        // the view's INSTANCE ID rather than the view because removal paths null the ZDO before
        // OnDestroy runs; the index makes every move/removal an O(1) swap-remove even in
        // five-thousand-piece base zones.
        //
        // The key is an int, not the ZNetView, and that is a measured decision rather than a
        // stylistic one: a Dictionary keyed on a UnityEngine.Object pays a native
        // CompareBaseObjects call on every probe, and this dictionary is probed three times per
        // destroyed object and three more per added one. It was the single largest item on the
        // teardown path. See TeardownHooks for the full rationale and the liveness invariant an
        // int key depends on.
        internal struct Slot {
            public Vector2i m_zone;
            public int m_index;
        }

        internal static readonly Dictionary<Vector2i, List<ZNetView>> ByZone = new Dictionary<Vector2i, List<ZNetView>>();
        internal static readonly Dictionary<int, Slot> Slots = new Dictionary<int, Slot>();

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        // UpdateTTL asks a few times per second at most, so the first session with verify on
        // never reached a 2000-comparison threshold before ending - summaries must land within
        // minutes at this call rate.
        private const int VerifyReportInterval = 250;
        private static bool _verifyActive;
        private static long _verifyComparisons;
        private static long _verifyDivergences;
        private static int _comparisonsSinceReport;

        // ---- index maintenance (unconditional) -----------------------------------------------

        private static void Bump(Vector2i sector, int delta) {
            NonDistantCount.TryGetValue(sector, out int count);
            count += delta;
            if (count > 0) { NonDistantCount[sector] = count; } else { NonDistantCount.Remove(sector); }
        }

        private static void IndexAdd(ZNetView view, int id, Vector2i zone) {
            if (!ByZone.TryGetValue(zone, out List<ZNetView> list)) {
                list = new List<ZNetView>();
                ByZone.Add(zone, list);
            }

            list.Add(view);
            Slots[id] = new Slot { m_zone = zone, m_index = list.Count - 1 };

            if (!view.m_distant) { Bump(zone, 1); }
        }

        private static void IndexRemove(ZNetView view, int id) {
            if (!Slots.TryGetValue(id, out Slot slot)) { return; }
            Slots.Remove(id);

            if (ByZone.TryGetValue(slot.m_zone, out List<ZNetView> list)) {
                int last = list.Count - 1;
                if (slot.m_index < last) {
                    ZNetView moved = list[last];
                    list[slot.m_index] = moved;
                    Slots[moved.GetInstanceID()] = new Slot { m_zone = slot.m_zone, m_index = slot.m_index };
                }

                list.RemoveAt(last);
                if (list.Count == 0) { ByZone.Remove(slot.m_zone); }
            }

            if (!view.m_distant) { Bump(slot.m_zone, -1); }
        }

        /// <summary>
        /// The destroy half of the index, called from this mod's one ZNetView.OnDestroy postfix.
        /// </summary>
        internal static void OnViewDestroyed(ZNetView view) => IndexRemove(view, view.GetInstanceID());

        [HarmonyPostfix]
        [HarmonyPatch("AddInstance")]
        private static void AddInstancePostfix(ZDO zdo, ZNetView nview) {
            if (ReferenceEquals(nview, null)) { return; }

            // Ghost views are never in m_instances (ZNetView.Awake returns before AddInstance when
            // m_ghostInit is set), so they have no business in the index either. Refusing them here
            // is what lets TeardownHooks skip the destroy lookup for the several hundred ghosts a
            // pre-generated zone creates and destroys in one frame - the invariant is enforced at
            // both ends rather than assumed from vanilla's control flow.
            if (nview.m_ghost) { return; }

            int id = nview.GetInstanceID();

            // A re-added view (should not happen, but m_instances[zdo] = nview tolerates it)
            // must not be double-indexed.
            IndexRemove(nview, id);
            IndexAdd(nview, id, zdo.GetSector());
        }

        [HarmonyPatch(typeof(ZDOMan))]
        internal static class SectorMoveHooks {
            [HarmonyPostfix]
            [HarmonyPatch("AddToSector")]
            private static void AddToSectorPostfix(ZDO zdo, Vector2i sector) {
                ZNetScene scene = ZNetScene.instance;
                if (ReferenceEquals(scene, null)) { return; }
                if (!scene.m_instances.TryGetValue(zdo, out ZNetView view)) { return; }

                int id = view.GetInstanceID();
                if (!Slots.TryGetValue(id, out Slot slot) || slot.m_zone == sector) { return; }

                IndexRemove(view, id);
                IndexAdd(view, id, sector);
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                NonDistantCount.Clear();
                ByZone.Clear();
                Slots.Clear();
            }
        }

        // ---- the read ------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch("HaveInstanceInSector")]
        private static bool HaveInstanceInSectorPrefix(ZNetScene __instance, Vector2i sector, ref bool __result) {
            if (!MaintenanceHealthy()) { return true; }

            bool indexed = NonDistantCount.ContainsKey(sector);

            if (Verify != null && Verify.Value) {
                _verifyActive = true;
                _verifyComparisons++;

                bool walked = WalkedAnswer(__instance, sector);
                if (walked != indexed) {
                    _verifyDivergences++;
                    Logger.LogWarning(
                        $"Zone occupancy verify: DIVERGED on zone {sector} (tally: {indexed}, " +
                        $"walk: {walked}). Vanilla's answer was used. One-off disagreements on " +
                        "zones holding a moving creature are expected; report this if it " +
                        "repeats for the same zone.");
                }

                if (++_comparisonsSinceReport >= VerifyReportInterval) {
                    _comparisonsSinceReport = 0;
                    LogVerifySummary("periodic");
                }

                __result = walked;
                return false;
            }

            if (_verifyActive) {
                _verifyActive = false;
                LogVerifySummary("final");
                _verifyComparisons = 0;
                _verifyDivergences = 0;
                _comparisonsSinceReport = 0;
            }

            __result = indexed;
            return false;
        }

        // Vanilla's walk verbatim (ZNetScene.cs:329-337), for the verify path only.
        private static bool WalkedAnswer(ZNetScene scene, Vector2i sector) {
            foreach (KeyValuePair<ZDO, ZNetView> instance in scene.m_instances) {
                if ((bool)(Object)instance.Value && !instance.Value.m_distant
                    && ZoneSystem.GetZone(instance.Value.transform.position) == sector) {
                    return true;
                }
            }

            return false;
        }

        private static void LogVerifySummary(string kind) {
            Logger.LogInfo(
                $"Zone occupancy verify ({kind}): {_verifyComparisons} comparison(s), " +
                $"{_verifyDivergences} divergence(s).");
        }

        // ---- hook health ---------------------------------------------------------------------

        /// Without all three maintenance hooks the index silently drifts, so every consumer -
        /// the occupancy read here, and ZoneDiffRemovalPatch's unload discovery - stands down to
        /// its vanilla path when any is missing.
        internal static bool MaintenanceHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            _hooksHealthy =
                HasOurPostfix(AccessTools.DeclaredMethod(typeof(ZNetScene), "AddInstance"), typeof(SectorInstanceIndexPatch))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(ZNetView), "OnDestroy"), typeof(TeardownHooks.ViewHook))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(ZDOMan), "AddToSector"), typeof(SectorMoveHooks));

            if (!_hooksHealthy) {
                Logger.LogError(
                    "Sector instance index: a maintenance hook is not attached, so the index " +
                    "cannot be trusted; zone occupancy checks and unload discovery have fallen " +
                    "back to vanilla's full walks for this session. This usually means a Valheim " +
                    "update changed those methods - look for the patch failure logged at startup.");
            }

            return _hooksHealthy;
        }

        private static bool HasOurPostfix(MethodBase target, System.Type hookClass) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            foreach (Patch patch in info.Postfixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookClass) { continue; }
                return true;
            }

            return false;
        }
    }
}
