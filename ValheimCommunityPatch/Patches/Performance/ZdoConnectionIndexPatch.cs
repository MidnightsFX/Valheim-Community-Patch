using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix World Load Connection Scan: the three world-load routines that pair portals, spawners
    // and sync transforms with their targets use a hash index instead of nested loops.
    //
    // ZDOMan.ConnectPortals, ConnectSpawners and ConnectSyncTransforms each match a source list
    // against a target list by comparing ZDOConnectionHashData.m_hash one pair at a time. On a
    // mature world that is O(n*m) on the critical path of every server start.
    //
    // Prefixes index the target list by hash once and resolve each source with one lookup,
    // reproducing vanilla's pairing decisions exactly. The three differ, so each follows its own
    // original: ConnectPortals consumes each target at most once (vanilla re-tests eligibility
    // on every inner iteration), ConnectSpawners and ConnectSyncTransforms keep the first match
    // per hash and never consume (vanilla breaks on the first match), and ConnectSyncTransforms
    // works purely through the static ZDOExtraData maps with no ZDO lookup or null check, so a
    // connection whose ZDO is gone is still paired, as in vanilla.
    //
    // Server: all three are private and only reached from ZDOMan.Load on the host.
    // Provenance: ComfyMods/Atlas's ConnectSpawners rewrite (GPL-3.0, redseiko), extended here
    // to the other two.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class ZdoConnectionIndexPatch {
        private const ZDOExtraData.ConnectionType PortalType = ZDOExtraData.ConnectionType.Portal;
        private const ZDOExtraData.ConnectionType PortalTargetType = ZDOExtraData.ConnectionType.Portal | ZDOExtraData.ConnectionType.Target;
        private const ZDOExtraData.ConnectionType SpawnedType = ZDOExtraData.ConnectionType.Spawned;
        private const ZDOExtraData.ConnectionType SpawnedTargetType = ZDOExtraData.ConnectionType.Spawned | ZDOExtraData.ConnectionType.Target;
        private const ZDOExtraData.ConnectionType SyncTransformType = ZDOExtraData.ConnectionType.SyncTransform;
        private const ZDOExtraData.ConnectionType SyncTransformTargetType = ZDOExtraData.ConnectionType.SyncTransform | ZDOExtraData.ConnectionType.Target;

        [HarmonyPrefix]
        [HarmonyPatch("ConnectPortals")]
        private static bool ConnectPortalsPrefix(ZDOMan __instance) {
            List<ZDOID> sources = ZDOExtraData.GetAllConnectionZDOIDs(PortalType);
            List<ZDOID> targets = ZDOExtraData.GetAllConnectionZDOIDs(PortalTargetType);

            // Only targets with no live connection are eligible, which is vanilla's
            // GetConnectionType(id) == None test on the first pass.
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
            List<ZDOID> sources = ZDOExtraData.GetAllConnectionZDOIDs(SpawnedType);
            List<ZDOID> targets = ZDOExtraData.GetAllConnectionZDOIDs(SpawnedTargetType);

            // First match per hash wins and is never consumed, so several spawners may share one
            // target, as in vanilla.
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
            List<ZDOID> sources = ZDOExtraData.GetAllConnectionZDOIDs(SyncTransformType);
            List<ZDOID> targets = ZDOExtraData.GetAllConnectionZDOIDs(SyncTransformTargetType);

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

            if (connected > 0) {
                Logger.LogInfo($"ConnectSyncTransforms => Connected {connected} SyncTransforms.");
            }

            return false;
        }
    }
}
