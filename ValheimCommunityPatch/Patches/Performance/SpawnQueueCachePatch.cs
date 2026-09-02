using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Spawn Queue Churn: the sorted spawn backlog persists between passes instead of being
    // rebuilt and re-sorted thirty times a second.
    //
    // While any object-creation backlog exists, every CreateDestroyObjects pass rebuilds the
    // pending list (a Created check and a distance per near ZDO) and sorts the entire backlog,
    // only to consume its head. Streaming a large base keeps the backlog in the tens of thousands
    // for minutes.
    //
    // A prefix on CreateObjectsSorted keeps the filtered, sorted list in vanilla's own
    // m_tempCurrentObjects2 and consumes it with a cursor across passes, rebuilding it with
    // vanilla's exact filter and sort only every third pass or when the player's zone changes.
    // Creation itself is unchanged: same per-pass budget, same order. A cached entry is guarded at
    // consume time against having been created meanwhile and against its pooled ZDO having been
    // recycled into a different object (the captured id no longer matches). This is the fallback
    // path when SpawnEventQueuePatch is not driving; that patch's prefix runs first and skips this
    // one while it is. Other mods' prefixes on this method are bypassed; re-check the copied filter
    // against the game source on updates.
    //
    // Both: a dedicated server streams objects for connected players through this path.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class SpawnQueueCachePatch {
        internal static ConfigEntry<int> BurstDivisor;

        internal static void BindConfig() {
            BurstDivisor = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Spawn Burst Divisor",
                100,
                "The per-frame object spawn budget is the spawn backlog divided by this " +
                "(minimum 10 for near objects, exactly like vanilla). 100 is vanilla's " +
                "hardcoded value. Higher spawns fewer objects per frame when entering a " +
                "built-up area - smaller frame hits, slower pop-in.",
                advanced: true,
                valMin: 10,
                valMax: 2000);
        }

        // Rebuild cadence in passes: ~100 ms of maximum staleness at 30 Hz.
        private const int RebuildInterval = 3;

        private static int _passesSinceRebuild = int.MaxValue;
        private static int _cursor;
        private static Vector2i _rebuildZone = new Vector2i(int.MinValue, int.MinValue);

        // Ids captured at rebuild, parallel to the cached list, so a recycled ZDO can be told
        // apart from a live one.
        private static readonly List<ZDOID> CachedIds = new List<ZDOID>();

        [HarmonyPrefix]
        [HarmonyPatch("CreateObjectsSorted")]
        private static bool CreateObjectsSortedPrefix(
            ZNetScene __instance, List<ZDO> currentNearObjects, int maxCreatedPerFrame, ref int created) {
            if (!ZoneSystem.instance.IsActiveAreaLoaded()) { return false; }

            List<ZDO> pending = __instance.m_tempCurrentObjects2;
            Vector3 referencePosition = ZNet.instance.GetReferencePosition();
            Vector2i referenceZone = ZoneSystem.GetZone(referencePosition);

            // The length check re-establishes the parallel-list invariant if anything else
            // rewrote the list.
            if (_passesSinceRebuild >= RebuildInterval - 1 || referenceZone != _rebuildZone
                || pending.Count != CachedIds.Count) {
                _passesSinceRebuild = 0;
                _rebuildZone = referenceZone;
                _cursor = 0;

                // Vanilla's filter and sort.
                pending.Clear();
                for (int i = 0; i < currentNearObjects.Count; i++) {
                    ZDO zdo = currentNearObjects[i];
                    if (!zdo.Created) {
                        zdo.m_tempSortValue = Utils.DistanceSqr(referencePosition, zdo.GetPosition());
                        pending.Add(zdo);
                    }
                }

                pending.Sort(ZNetScene.ZDOCompare);

                CachedIds.Clear();
                for (int i = 0; i < pending.Count; i++) { CachedIds.Add(pending[i].m_uid); }
            } else {
                _passesSinceRebuild++;
            }

            int remaining = pending.Count - _cursor;
            if (remaining <= 0) { return false; }

            int countCap = Mathf.Max(remaining / (BurstDivisor != null ? BurstDivisor.Value : 100), maxCreatedPerFrame);

            while (_cursor < pending.Count) {
                ZDO zdo = pending[_cursor];
                ZDOID cachedId = CachedIds[_cursor];
                _cursor++;

                if (zdo.m_uid != cachedId || zdo.Created) { continue; }

                if (!ZoneSystem.instance.IsZoneReadyForType(zdo.GetSector(), zdo.Type)) { continue; }

                if (__instance.CreateObject(zdo) != null) {
                    ++created;
                    if (created > countCap) { break; }
                } else if (ZNet.instance.IsServer()) {
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    ZLog.Log("Destroyed invalid predab ZDO:" + zdo.m_uid);
                    ZDOMan.instance.DestroyZDO(zdo);
                }
            }

            return false;
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                CachedIds.Clear();
                _cursor = 0;
                _passesSinceRebuild = int.MaxValue;
                _rebuildZone = new Vector2i(int.MinValue, int.MinValue);
            }
        }
    }
}
