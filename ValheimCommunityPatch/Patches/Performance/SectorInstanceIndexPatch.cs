using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Zone Occupancy Scan: each zone whose TTL expires asks if it holds an object by walking every loaded one.
    // Prefixes ZNetScene.HaveInstanceInSector to answer from a per-zone tally kept up by three maintenance hooks.
    // Zone unload checks cost a lookup instead of a walk over every loaded object.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class SectorInstanceIndexPatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Zone Occupancy",
                false,
                "Diagnostic. Checks every zone-occupancy answer against vanilla's full walk, uses vanilla's, " +
                "and logs disagreements (one-off ones near a moving creature are harmless); costs the walk " +
                "this fix saves.",
                advanced: true);
        }

        // Zones with a positive count only: Bump removes emptied keys, so presence is the answer.
        private static readonly Dictionary<Vector2s, int> NonDistantCount = new Dictionary<Vector2s, int>();

        internal struct Slot {
            public Vector2s m_zone;
            public int m_index;
        }

        // Slots is keyed on the view's instance id rather than the view because removal paths
        // null the ZDO before OnDestroy runs, and because an int key avoids a native Equals per
        // probe; see TeardownHooks.
        internal static readonly Dictionary<Vector2s, List<ZNetView>> ByZone = new Dictionary<Vector2s, List<ZNetView>>();
        internal static readonly Dictionary<int, Slot> Slots = new Dictionary<int, Slot>();

        // AddInstance is m_instances' only add, ZNetView.OnDestroy sees every removal path and
        // AddToSector every sector change. Without all three the index silently drifts, so every
        // consumer (the occupancy read here and ZoneDiffRemovalPatch) stands down when any is missing.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(SectorInstanceIndexPatch),
            "Sector instance index",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZNetScene), "AddInstance"), typeof(SectorInstanceIndexPatch))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZNetView), "OnDestroy"), typeof(TeardownHooks.ViewHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), "AddToSector"), typeof(SectorMoveHooks)));

        internal static bool MaintenanceHealthy => Hooks.Healthy;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(SectorInstanceIndexPatch));

        private const int VerifyReportInterval = 250;
        private static bool _verifyActive;
        private static long _verifyComparisons;
        private static long _verifyDivergences;
        private static int _comparisonsSinceReport;

        // ---- index maintenance (unconditional) -----------------------------------------------

        // Everything outside the +/-256 zone grid is filed under index 0, which maps back to
        // zone (-256,-256). Both the add and the move go through here so a ZDO that leaves the
        // grid cannot be indexed under one key and removed under another.
        private static Vector2s ZoneOf(ZoneSystem.SectorIndex sectorIndex) {
            return ZoneSystem.IndexToSector(sectorIndex.Sector);
        }

        /// <summary>
        /// The one index key that is lossy: every ZDO the game files under sector index 0 (outside
        /// the +/-256 zone grid, or with its sector invalidated by the server) lands here, whatever
        /// its real zone. Consumers must not reason about this bucket's zone; see
        /// ZoneDiffRemovalPatch and HaveInstanceInSectorPrefix.
        /// </summary>
        internal static Vector2s AliasedZone => ZoneOf(ZoneSystem.SectorZero);

        private static void Bump(Vector2s sector, int delta) {
            NonDistantCount.TryGetValue(sector, out int count);
            count += delta;
            if (count > 0) { NonDistantCount[sector] = count; } else { NonDistantCount.Remove(sector); }
        }

        private static void IndexAdd(ZNetView view, int id, Vector2s zone) {
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

            // Keyed on the sector ZDOMan filed the ZDO under, as the move hook is: an invalidated
            // ZDO sits in sector 0 while its position still reads as the last synced spot. Vanilla
            // reads the live transform; for a mover the two differ by sync latency, which only
            // keeps a zone loaded slightly longer.
            IndexAdd(nview, id, ZoneOf(zdo.OutsideZones ? ZoneSystem.SectorZero : zdo.GetSectorIndex()));
        }

        [HarmonyPatch(typeof(ZDOMan))]
        internal static class SectorMoveHooks {
            [HarmonyPostfix]
            [HarmonyPatch("AddToSector")]
            // The parameter name has to stay 'sectorIndex' for Harmony to bind it. It arrives
            // before ZDO.m_position is updated, so the ZDO cannot be asked for the new zone here.
            private static void AddToSectorPostfix(ZDO zdo, ZoneSystem.SectorIndex sectorIndex) {
                ZNetScene scene = ZNetScene.instance;
                if (ReferenceEquals(scene, null)) { return; }
                if (!scene.m_instances.TryGetValue(zdo, out ZNetView view)) { return; }

                Vector2s sector = ZoneOf(sectorIndex);

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
        private static bool HaveInstanceInSectorPrefix(ZNetScene __instance, Vector2s sector, ref bool __result) {
            // Only the read is turned off: Fix Unload Discovery Scan still reads the index, so the
            // upkeep hooks and MaintenanceHealthy carry on.
            if (ApiSwitch.Off || !Hooks.Healthy) { return true; }

            // A zone the game files under sector index 0 shares its key with every other such
            // zone, so the tally cannot answer for it. Every zone a dedicated server loads around
            // its own far-off reference position is one.
            bool indexed = ZoneSystem.SectorToIndex(sector) == ZoneSystem.SectorZero
                ? AliasedBucketHas(sector)
                : NonDistantCount.ContainsKey(sector);

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

        // Vanilla's test over the aliased bucket alone, which holds every instance that could
        // stand in a zone filed under sector index 0.
        private static bool AliasedBucketHas(Vector2s sector) {
            Vector2s aliased = AliasedZone;
            if (!NonDistantCount.ContainsKey(aliased) || !ByZone.TryGetValue(aliased, out List<ZNetView> bucket)) {
                return false;
            }

            for (int i = 0; i < bucket.Count; i++) {
                ZNetView view = bucket[i];
                if ((bool)(Object)view && !view.m_distant
                    && ZoneSystem.GetZone(view.transform.position) == sector) {
                    return true;
                }
            }

            return false;
        }

        // Vanilla's walk, for the verify path only.
        private static bool WalkedAnswer(ZNetScene scene, Vector2s sector) {
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
