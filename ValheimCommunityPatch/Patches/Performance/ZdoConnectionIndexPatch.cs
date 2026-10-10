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
    // original:
    // - ConnectPortals consumes each target at most once (vanilla re-tests the target's live
    //   connection on every inner iteration, and only a pairing in this loop changes it).
    // - ConnectSpawners consumes each target at most once (vanilla removes the matched target
    //   from its list), marks a spawner with no match "done", then strips the hash data from
    //   every target left unclaimed, in list order, and logs the same three counts.
    // - ConnectSyncTransforms keeps the first match per hash and never consumes (vanilla breaks
    //   on the first match), and works purely through the static ZDOExtraData maps with no ZDO
    //   lookup or null check, so a connection whose ZDO is gone is still paired, as in vanilla.
    // Nothing in these loops writes ZDOExtraData's hash data until ConnectSpawners' orphan pass,
    // so hashes read up front are the ones vanilla reads live. A ZDOID holds one hash entry, so
    // no id is in both a source and a target list; vanilla's skip of the source's own id never
    // fires, and the prefixes keep it only as a guard. Priority.Last and __runOriginal, so a mod
    // that replaces the pairing (XPortalNetworks restores portals from its own targets) keeps its
    // say instead of gaining vanilla's two-way links on top.
    //
    // Server: all three are private and only reached from ZDOMan.Load on the host.
    // Provenance: ComfyMods/Atlas's ConnectSpawners rewrite (GPL-3.0, redseiko), extended here
    // to the other two.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class ZdoConnectionIndexPatch {
        private const ZDOExtraData.ConnectionType PortalType = ZDOExtraData.ConnectionType.Portal;
        private const ZDOExtraData.ConnectionType PortalTargetType = ZDOExtraData.ConnectionType.Portal | ZDOExtraData.ConnectionType.Target;
        private const ZDOExtraData.ConnectionType SpawnedType = ZDOExtraData.ConnectionType.Spawned;
        private const ZDOExtraData.ConnectionType SpawnedTargetType = ZDOExtraData.ConnectionType.Spawned | ZDOExtraData.ConnectionType.Target;
        private const ZDOExtraData.ConnectionType SyncTransformType = ZDOExtraData.ConnectionType.SyncTransform;
        private const ZDOExtraData.ConnectionType SyncTransformTargetType = ZDOExtraData.ConnectionType.SyncTransform | ZDOExtraData.ConnectionType.Target;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ZdoConnectionIndexPatch));

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("ConnectPortals")]
        private static bool ConnectPortalsPrefix(ZDOMan __instance, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (ApiSwitch.Off) { return true; }

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
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("ConnectSpawners")]
        private static bool ConnectSpawnersPrefix(ZDOMan __instance, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (ApiSwitch.Off) { return true; }

            List<ZDOID> sources = ZDOExtraData.GetAllConnectionZDOIDs(SpawnedType);
            List<ZDOID> targets = ZDOExtraData.GetAllConnectionZDOIDs(SpawnedTargetType);

            // Indices into targets, grouped by hash in list order. Vanilla removes a matched
            // target from its list, so each target pairs with one spawner at most.
            Dictionary<int, LinkedList<int>> available = new Dictionary<int, LinkedList<int>>();
            for (int i = 0; i < targets.Count; i++) {
                ZDOConnectionHashData hashData = ZDOExtraData.GetConnectionHashData(targets[i], SpawnedTargetType);
                if (hashData == null) { continue; }

                if (!available.TryGetValue(hashData.m_hash, out LinkedList<int> candidates)) {
                    candidates = new LinkedList<int>();
                    available.Add(hashData.m_hash, candidates);
                }

                candidates.AddLast(i);
            }

            bool[] removed = new bool[targets.Count];
            int orphans = targets.Count;
            long sessionId = ZDOMan.GetSessionID();
            int connected = 0, done = 0;

            for (int i = 0; i < sources.Count; i++) {
                ZDOID sourceId = sources[i];

                ZDO source = __instance.GetZDO(sourceId);
                if (source == null) { continue; }

                source.SetOwner(sessionId);

                // First remaining target with the same hash, passing over the spawner's own id
                // (which stays in the list) as vanilla does. Ids are unique, so one skip suffices.
                LinkedListNode<int> match = null;
                ZDOConnectionHashData hashData = source.GetConnectionHashData(SpawnedType);
                if (hashData != null && available.TryGetValue(hashData.m_hash, out LinkedList<int> candidates)) {
                    match = candidates.First;
                    if (match != null && targets[match.Value] == sourceId) { match = match.Next; }
                }

                if (match != null) {
                    ZDOID targetId = targets[match.Value];
                    connected++;
                    source.SetConnection(SpawnedType, targetId);

                    // Vanilla's removal is guarded by targetId != ZDOID.None.
                    if (targetId != ZDOID.None) {
                        removed[match.Value] = true;
                        orphans--;
                        match.List.Remove(match);
                    }
                } else {
                    // Vanilla marks an unmatched spawner as "done" so it is not retried.
                    done++;
                    source.SetConnection(SpawnedType, ZDOID.None);
                }
            }

            // Targets no spawner claimed lose their hash data, in list order as in vanilla.
            for (int i = 0; i < targets.Count; i++) {
                if (removed[i]) { continue; }

                ZDOExtraData.RemoveConnectionHashData(targets[i], SpawnedTargetType);
            }

            if (connected > 0 || done > 0 || orphans > 0) {
                Logger.LogInfo($"ConnectSpawners => Connected {connected} spawners and {done} 'done' spawners. Removed connection from {orphans} orphan spawn:s.");
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("ConnectSyncTransforms")]
        private static bool ConnectSyncTransformsPrefix(bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (ApiSwitch.Off) { return true; }

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
