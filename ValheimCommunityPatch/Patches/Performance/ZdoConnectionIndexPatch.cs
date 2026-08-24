using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: all three of ZDOMan's world-load connection routines are nested loops that match
    // a "source" list against a "target" list by comparing ZDOConnectionHashData.m_hash one pair at a
    // time. On a mature world with thousands of portals, spawners, ships and their targets, that is
    // O(n*m) work on the critical path of loading the world - a multi-second stall before anyone can
    // join, repeated on every server start.
    //
    // Fix: build a hash-keyed index of the target list once, then resolve each source with a single
    // dictionary lookup. All three rewrites reproduce vanilla's pairing decisions exactly, including
    // its ordering, its self-exclusion check and its "mark as done" fallback. The three differ more
    // than they look, so each prefix follows its own original rather than a shared helper:
    //
    //   * ConnectPortals consumes each target at most once (vanilla re-tests GetConnectionType == None
    //     on every inner iteration, so an already-linked target is skipped). The index therefore pops
    //     candidates as they are used.
    //   * ConnectSpawners does *not* consume - vanilla breaks on the first hash match regardless of
    //     whether an earlier spawner already claimed it - so several spawners may share one target.
    //     The index keeps the first match per hash to match that.
    //   * ConnectSyncTransforms also does not consume, so it indexes the same way as ConnectSpawners.
    //     But it skips every other step the other two take: no GetZDO lookup or null check on either
    //     side, no SetOwner, and no "done" marking for an unmatched source. It works purely through
    //     the static ZDOExtraData maps keyed by ZDOID and never touches a ZDO object, so a connection
    //     entry whose ZDO is gone is still paired. Adding a null check here would change behaviour.
    //
    // Source and target lists are disjoint (GetAllConnectionZDOIDs filters on exact type equality, and
    // Portal != Portal|Target), so vanilla's zid != id guard can never fire; it is kept anyway.
    //
    // Provenance: same approach as the ZDOMan.ConnectSpawners rewrite in ComfyMods/Atlas (GPL-3.0,
    // redseiko), extended here to ConnectPortals and ConnectSyncTransforms.
    //
    // Server: all three targets are private and called only from ZDOMan.Load, which is reached via
    // ZNet.LoadWorld from ZNet.Start's if (m_isServer) branch (ZNet.cs:183). World load only ever
    // happens on the host, so vanilla's call site is already the gate.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class ZdoConnectionIndexPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ZdoConnectionIndexPatch),
                ValConfig.SectionPerformance,
                "Fix World Load Connection Scan",
                true,
                "Replaces the three quadratic scans that pair portals, spawners and sync transforms " +
                "(ships and carts) with their targets during world load with indexed lookups. On a " +
                "long-lived world these are a multi-second stall on every server start.");
        }

        private const ZDOExtraData.ConnectionType PortalType = ZDOExtraData.ConnectionType.Portal;
        private const ZDOExtraData.ConnectionType PortalTargetType = ZDOExtraData.ConnectionType.Portal | ZDOExtraData.ConnectionType.Target;
        private const ZDOExtraData.ConnectionType SpawnedType = ZDOExtraData.ConnectionType.Spawned;
        private const ZDOExtraData.ConnectionType SpawnedTargetType = ZDOExtraData.ConnectionType.Spawned | ZDOExtraData.ConnectionType.Target;
        private const ZDOExtraData.ConnectionType SyncTransformType = ZDOExtraData.ConnectionType.SyncTransform;
        private const ZDOExtraData.ConnectionType SyncTransformTargetType = ZDOExtraData.ConnectionType.SyncTransform | ZDOExtraData.ConnectionType.Target;

        [HarmonyPrefix]
        [HarmonyPatch("ConnectPortals")]
        private static bool ConnectPortalsPrefix(ZDOMan __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }

            List<ZDOID> sources = ZDOExtraData.GetAllConnectionZDOIDs(PortalType);
            List<ZDOID> targets = ZDOExtraData.GetAllConnectionZDOIDs(PortalTargetType);

            // Only targets with no live connection are eligible, which is what vanilla's
            // GetConnectionType(id) == None test means on the first pass.
            Dictionary<int, Queue<ZDOID>> available = new Dictionary<int, Queue<ZDOID>>();
            for (int i = 0; i < targets.Count; i++) {
                ZDOID targetId = targets[i];
                if (ZDOExtraData.GetConnectionType(targetId) != ZDOExtraData.ConnectionType.None) { continue; }

                ZDOConnectionHashData hashData = ZDOExtraData.GetConnectionHashData(targetId, PortalTargetType);
                if (hashData == null) { continue; }

                if (!available.TryGetValue(hashData.m_hash, out Queue<ZDOID> queue)) {
                    queue = new Queue<ZDOID>();
                    available.Add(hashData.m_hash, queue);
                }

                queue.Enqueue(targetId);
            }

            long sessionId = ZDOMan.GetSessionID();
            int connected = 0;

            for (int i = 0; i < sources.Count; i++) {
                ZDOID sourceId = sources[i];

                ZDO source = __instance.GetZDO(sourceId);
                if (source == null) { continue; }

                ZDOConnectionHashData hashData = source.GetConnectionHashData(PortalType);
                if (hashData == null) { continue; }

                if (!available.TryGetValue(hashData.m_hash, out Queue<ZDOID> queue)) { continue; }

                ZDO target = null;
                ZDOID targetId = ZDOID.None;
                while (queue.Count > 0) {
                    ZDOID candidate = queue.Dequeue();
                    if (candidate == sourceId) { continue; }

                    target = __instance.GetZDO(candidate);
                    if (target != null) { targetId = candidate; break; }
                }

                if (target == null) { continue; }

                connected++;
                source.SetOwner(sessionId);
                target.SetOwner(sessionId);
                source.SetConnection(PortalType, targetId);
                target.SetConnection(PortalType, sourceId);
            }

            if (connected > 0) {
                Logger.LogInfo($"ConnectPortals => Connected {connected} portals.");
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch("ConnectSpawners")]
        private static bool ConnectSpawnersPrefix(ZDOMan __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }

            List<ZDOID> sources = ZDOExtraData.GetAllConnectionZDOIDs(SpawnedType);
            List<ZDOID> targets = ZDOExtraData.GetAllConnectionZDOIDs(SpawnedTargetType);

            // First match per hash wins and is never consumed, mirroring vanilla's break-on-first-match
            // with no eligibility re-test.
            Dictionary<int, ZDOID> firstByHash = new Dictionary<int, ZDOID>();
            for (int i = 0; i < targets.Count; i++) {
                ZDOConnectionHashData hashData = ZDOExtraData.GetConnectionHashData(targets[i], SpawnedTargetType);
                if (hashData == null || firstByHash.ContainsKey(hashData.m_hash)) { continue; }

                firstByHash.Add(hashData.m_hash, targets[i]);
            }

            long sessionId = ZDOMan.GetSessionID();
            int connected = 0, done = 0;

            for (int i = 0; i < sources.Count; i++) {
                ZDOID sourceId = sources[i];

                ZDO source = __instance.GetZDO(sourceId);
                if (source == null) { continue; }

                source.SetOwner(sessionId);

                ZDOConnectionHashData hashData = source.GetConnectionHashData(SpawnedType);
                if (hashData != null
                    && firstByHash.TryGetValue(hashData.m_hash, out ZDOID targetId)
                    && targetId != sourceId) {
                    connected++;
                    source.SetConnection(SpawnedType, targetId);
                } else {
                    // Vanilla marks an unmatched spawner as "done" so it is not retried.
                    done++;
                    source.SetConnection(SpawnedType, ZDOID.None);
                }
            }

            if (connected > 0 || done > 0) {
                Logger.LogInfo($"ConnectSpawners => Connected {connected} spawners and {done} 'done' spawners.");
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch("ConnectSyncTransforms")]
        private static bool ConnectSyncTransformsPrefix() {
            if (Enabled == null || !Enabled.Value) { return true; }

            List<ZDOID> sources = ZDOExtraData.GetAllConnectionZDOIDs(SyncTransformType);
            List<ZDOID> targets = ZDOExtraData.GetAllConnectionZDOIDs(SyncTransformTargetType);

            // First match per hash wins and is never consumed, mirroring vanilla's break-on-first-match
            // with no eligibility re-test - so several sources may legitimately share one target.
            Dictionary<int, ZDOID> firstByHash = new Dictionary<int, ZDOID>();
            for (int i = 0; i < targets.Count; i++) {
                ZDOConnectionHashData hashData = ZDOExtraData.GetConnectionHashData(targets[i], SyncTransformTargetType);
                if (hashData == null || firstByHash.ContainsKey(hashData.m_hash)) { continue; }

                firstByHash.Add(hashData.m_hash, targets[i]);
            }

            int connected = 0;

            for (int i = 0; i < sources.Count; i++) {
                ZDOID sourceId = sources[i];

                ZDOConnectionHashData hashData = ZDOExtraData.GetConnectionHashData(sourceId, SyncTransformType);
                if (hashData == null) { continue; }

                if (!firstByHash.TryGetValue(hashData.m_hash, out ZDOID targetId)) { continue; }

                connected++;
                ZDOExtraData.SetConnection(sourceId, SyncTransformType, targetId);
            }

            // Vanilla stays silent when nothing matched; it does not mark unmatched sources 'done'.
            if (connected > 0) {
                Logger.LogInfo($"ConnectSyncTransforms => Connected {connected} SyncTransforms.");
            }

            return false;
        }
    }
}
