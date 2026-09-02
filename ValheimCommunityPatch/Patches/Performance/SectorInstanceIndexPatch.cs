using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Zone Occupancy Scan: "does any object stand in this zone" is answered from a per-zone
    // tally instead of by walking every loaded instance.
    //
    // ZNetScene.HaveInstanceInSector iterates the entire instance dictionary, with a native
    // alive-check and transform read per entry, until it finds one in the zone or has visited all
    // of them to say no. ZoneSystem.UpdateTTL asks it for every loaded zone whose TTL expired.
    //
    // A per-zone count of non-distant instances answers in O(1), and a full index of every live
    // instance grouped by its ZDO's zone (ByZone, with Slots giving each view's position for O(1)
    // removal) is kept alongside it for ZoneDiffRemovalPatch. Both are maintained at the complete
    // mutation surface of m_instances: ZNetScene.AddInstance is the only add, ZNetView.OnDestroy
    // (via TeardownHooks) is the removal signal for every removal path, and ZDOMan.AddToSector is
    // where every sector change lands. The index keys on the ZDO's sector where vanilla reads the
    // live transform; for movers they differ by at most position-sync latency, in the direction
    // that keeps a zone loaded marginally longer. Maintenance is unconditional; the read stands
    // down to vanilla's walk if any hook failed to attach.
    //
    // Both: a dedicated server runs UpdateTTL over its own loaded zones.
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

        // Zones with a positive count only: Bump removes emptied keys, so presence is the answer.
        private static readonly Dictionary<Vector2i, int> NonDistantCount = new Dictionary<Vector2i, int>();

        internal struct Slot {
            public Vector2i m_zone;
            public int m_index;
        }

        // Slots is keyed on the view's instance id rather than the view because removal paths
        // null the ZDO before OnDestroy runs, and because an int key avoids a native Equals per
        // probe; see TeardownHooks.
        internal static readonly Dictionary<Vector2i, List<ZNetView>> ByZone = new Dictionary<Vector2i, List<ZNetView>>();
        internal static readonly Dictionary<int, Slot> Slots = new Dictionary<int, Slot>();

        // Without all three maintenance hooks the index silently drifts, so every consumer (the
        // occupancy read here and ZoneDiffRemovalPatch) stands down when any is missing.
        private static readonly HookHealth Hooks = new HookHealth(
            "Sector instance index",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZNetScene), "AddInstance"), typeof(SectorInstanceIndexPatch))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZNetView), "OnDestroy"), typeof(TeardownHooks.ViewHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), "AddToSector"), typeof(SectorMoveHooks)));

        internal static bool MaintenanceHealthy => Hooks.Healthy;

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

        /// <summary>The destroy half, called from TeardownHooks' one ZNetView.OnDestroy postfix.</summary>
        internal static void OnViewDestroyed(ZNetView view) => IndexRemove(view, view.GetInstanceID());

        [HarmonyPostfix]
        [HarmonyPatch("AddInstance")]
        private static void AddInstancePostfix(ZDO zdo, ZNetView nview) {
            if (ReferenceEquals(nview, null)) { return; }

            // Ghost views never enter m_instances (ZNetView.Awake returns before AddInstance), so
            // they have no business in the index; TeardownHooks relies on this to skip them.
            if (nview.m_ghost) { return; }

            int id = nview.GetInstanceID();

            // A re-added view must not be double-indexed.
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
            if (!Hooks.Healthy) { return true; }

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

        // Vanilla's walk, for the verify path only.
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
    }
}
