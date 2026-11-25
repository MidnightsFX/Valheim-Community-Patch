using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Spawn Queue Churn: every spawn pass rebuilds and re-sorts the whole pending backlog just to take its head.
    // Prefixes CreateObjectsSorted to keep the sorted list across passes, rebuilt every third pass or on a new zone.
    // Loading into a large base no longer re-sorts tens of thousands of pending objects thirty times a second.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class SpawnQueueCachePatch {
        internal static ConfigEntry<int> BurstDivisor;

        internal static void BindConfig() {
            BurstDivisor = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Spawn Burst Divisor",
                100,
                "The per-frame object spawn budget is the spawn backlog divided by this; 100 is vanilla's " +
                "value. Higher spawns fewer objects per frame in a built-up area: smaller hitches, slower pop-in.",
                advanced: true,
                valMin: 10,
                valMax: 2000);
        }

        // Rebuild cadence in passes: ~100 ms of maximum staleness at 30 Hz.
        private const int RebuildInterval = 3;

        // A zone id no world position maps to: the grid runs to +/-256, and Vector2s holds shorts.
        private static readonly Vector2s NoZone = new Vector2s(short.MinValue, short.MinValue);

        private static int _passesSinceRebuild = int.MaxValue;
        private static int _cursor;
        private static Vector2s _rebuildZone = NoZone;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(SpawnQueueCachePatch));

        // Ids captured at rebuild, parallel to the cached list, so a recycled ZDO can be told
        // apart from a live one.
        private static readonly List<ZDOID> CachedIds = new List<ZDOID>();

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("CreateObjectsSorted")]
        private static bool CreateObjectsSortedPrefix(
            ZNetScene __instance, List<ZDO> currentNearObjects, int maxCreatedPerFrame, ref int created,
            bool __runOriginal) {
            // Another mod has replaced the pass (ValheimOptimized outside loading screens, Fast
            // Loading behind them): a second batch would double it, and the cached list is stale.
            if (!__runOriginal) {
                Invalidate();
                return false;
            }

            // Vanilla rebuilds its own list from scratch every pass.
            if (ApiSwitch.Off) {
                Invalidate();
                return true;
            }

            if (!ZoneSystem.instance.IsActiveAreaLoaded()) { return false; }

            // Between rebuilds this is not the current pass's candidates, so nothing may read it as
            // that: SceneIdleSkipPatch decides from the near list instead.
            List<ZDO> pending = __instance.m_tempCurrentObjects2;
            Vector3 referencePosition = ZNet.instance.GetReferencePosition();
            Vector2s referenceZone = ZoneSystem.GetZone(referencePosition);

            // The length check re-establishes the parallel-list invariant if anything else
            // rewrote the list.
            if (_passesSinceRebuild >= RebuildInterval - 1 || referenceZone != _rebuildZone
                || pending.Count != CachedIds.Count) {
                _passesSinceRebuild = 0;
                _rebuildZone = referenceZone;
                _cursor = 0;

                // Vanilla's filter and sort; re-check the copy against the game source on updates.
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
                    if (created >= countCap) { break; }
                } else if (ZNet.instance.IsServer()) {
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    Correctness.MissingPrefabLogSpamPatch.DestroyedSink("Destroyed invalid prefab ZDO:" + zdo.m_uid);
                    ZDOMan.instance.DestroyZDO(zdo);
                }
            }

            return false;
        }

        private static void Invalidate() {
            CachedIds.Clear();
            _cursor = 0;
            _passesSinceRebuild = int.MaxValue;
            _rebuildZone = NoZone;
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() => Invalidate();
        }
    }
}
