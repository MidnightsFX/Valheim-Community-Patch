using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: while any object-creation backlog exists, every CreateDestroyObjects pass
    // (30 Hz) rebuilds the pending list from scratch - a Created check and a distance computation
    // over every near ZDO - and then sorts the ENTIRE backlog, only to consume its head
    // (ZNetScene.CreateObjectsSorted, ZNetScene.cs:152-189). Streaming a large base keeps the
    // backlog in the tens of thousands for minutes; measured there, the re-sort alone was
    // ~56 ms of every second and the refilter another ~18, against ~227 ms/s of actual object
    // creation.
    //
    // Fix: keep the filtered, sorted backlog and consume it with a cursor across passes,
    // rebuilding it from scratch - vanilla's exact filter and sort - only every third pass, or
    // immediately when the player's zone changes (the sort order is distance-from-player, so a
    // zone hop is the one event that meaningfully reorders it). Between rebuilds a pass is just
    // the consume loop vanilla already ran. Creation itself is untouched: the same per-pass
    // budget, computed the same way, consumes the same entries in the same order. This throttles
    // bookkeeping, not work - the lesson of the withdrawn spawn-burst budget - so nothing
    // accumulates; the worst case (a backlog too small to outlive one pass) simply rebuilds each
    // pass, which IS vanilla.
    //
    // What a stale pass can see, and why each is safe (the cache is at most 3 passes = ~100 ms
    // old):
    //  - An entry created meanwhile (by us, a previous pass, or the distant path): its Created
    //    flag is live state, checked at consume time and skipped - vanilla's refilter, one entry
    //    later.
    //  - An entry whose ZDO was destroyed and possibly recycled by the ZDO pool into a different
    //    object: the id captured at rebuild no longer matches the live m_uid and the slot is
    //    skipped. This guard is load-bearing - without it a recycled entry could be instantiated
    //    off-ring, or worse, fed to the server-side invalid-prefab destroy branch.
    //  - An entry whose zone is not ready: skipped past, retried after the next rebuild. Vanilla
    //    retries every pass; ~100 ms later is invisible at zone-generation granularity, and this
    //    is exactly what keeps the everything-gated regime from degenerating into per-pass
    //    rebuilds.
    //  - A ZDO newly entering the ring waits for the next rebuild to join the queue - the same
    //    ~100 ms.
    //
    // The cache lives in vanilla's own m_tempCurrentObjects2 (this method is its only writer),
    // so SceneIdleSkipPatch's pending-count diagnostic reads data at most ~100 ms old - it only
    // runs on quiet passes, where the list is empty anyway. IsActiveAreaLoaded gating, the
    // per-pass budget formula, the shared created counter, and the server-side invalid-prefab
    // branch are replicated verbatim; CreateDistantObjects is left vanilla on purpose (no sort,
    // and the shared budget's early-out already keeps it cheap). Replicated wholesale like
    // RemoveObjectsNrePatch's sibling: re-check against the game source on updates; other mods'
    // prefixes on this method are bypassed while it is on (postfixes still run).
    //
    // Both: a dedicated server streams objects for connected players through this same path.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class SpawnQueueCachePatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> BurstDivisor;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SpawnQueueCachePatch),
                ValConfig.SectionPerformance,
                "Fix Spawn Queue Churn",
                true,
                "Keeps the sorted queue of objects waiting to spawn across frames instead of " +
                "rebuilding and re-sorting the whole thing thirty times a second while an area " +
                "streams in. Objects spawn at the same rate in the same order; only the " +
                "repeated bookkeeping goes away.");

            BurstDivisor = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Spawn Burst Divisor",
                100,
                "The per-frame object spawn budget is the spawn backlog divided by this " +
                "(minimum 10 for near objects, exactly like vanilla). 100 is vanilla's " +
                "hardcoded value. Higher spawns fewer objects per frame when entering a " +
                "built-up area - smaller frame hits, slower pop-in. Only applies while 'Fix " +
                "Spawn Queue Churn' is on.",
                advanced: true,
                valMin: 10,
                valMax: 2000);
        }

        // Rebuild cadence in passes. At 30 Hz this is ~100 ms of maximum staleness - the safety
        // analysis in the header is written against it.
        private const int RebuildInterval = 3;

        private static int _passesSinceRebuild = int.MaxValue;
        private static int _cursor;
        private static Vector2i _rebuildZone = new Vector2i(int.MinValue, int.MinValue);

        // Ids captured at rebuild, parallel to the cached list: a pooled ZDO recycled into a new
        // object keeps the reference alive but changes m_uid, which is how stale slots are told
        // apart from live ones.
        private static readonly List<ZDOID> CachedIds = new List<ZDOID>();

        [HarmonyPrefix]
        [HarmonyPatch("CreateObjectsSorted")]
        private static bool CreateObjectsSortedPrefix(
            ZNetScene __instance, List<ZDO> currentNearObjects, int maxCreatedPerFrame, ref int created) {
            if (Enabled == null || !Enabled.Value) { return true; }

            if (!ZoneSystem.instance.IsActiveAreaLoaded()) { return false; }

            List<ZDO> pending = __instance.m_tempCurrentObjects2;
            Vector3 referencePosition = ZNet.instance.GetReferencePosition();
            Vector2i referenceZone = ZoneSystem.GetZone(referencePosition);

            // The length check re-establishes the parallel-array invariant if vanilla passes
            // rewrote the list while the toggle was off; a coincidental same-length rewrite is
            // caught slot-by-slot by the id guard below.
            if (_passesSinceRebuild >= RebuildInterval - 1 || referenceZone != _rebuildZone
                || pending.Count != CachedIds.Count) {
                _passesSinceRebuild = 0;
                _rebuildZone = referenceZone;
                _cursor = 0;

                // Vanilla's filter and sort verbatim (ZNetScene.cs:159-170).
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

                // Stale-slot guards, in place of the refilter a rebuild pass would have done.
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

        // The cache indexes into scene state; a session boundary invalidates both.
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
