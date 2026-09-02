using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Portal Connection Scan: the server pairs portals by tag in one linear pass instead of
    // rescanning every portal for every unconnected one.
    //
    // Game.ConnectPortals runs every 5 s and is quadratic: for each unconnected portal,
    // FindRandomUnconnectedPortal allocates a list and rescans all portals with a string lookup
    // per element. An unpaired portal never resolves, so the full scan repeats forever, and
    // Game.SetConnection defers the write to an RPC round-trip when a client owns the ZDO.
    //
    // A prefix replaces it with three O(n) passes over a tag-keyed dictionary with no per-call
    // allocation: validate existing connections and bucket the rest by tag, pair each bucket two
    // at a time after seizing ownership, then one force-send union per peer. Pairing is in list
    // order rather than random and completes in one tick.
    //
    // Server: vanilla only starts the coroutine inside Game.Start's IsServer branch.
    // Provenance: ComfyMods/BetterServerPortals (GPL-3.0, redseiko), without its random-portal
    // gameplay feature.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(Game))]
    internal static class PortalConnectionPatch {
        private static readonly Dictionary<string, List<ZDO>> UnconnectedByTag = new Dictionary<string, List<ZDO>>();
        private static readonly Stack<List<ZDO>> ListPool = new Stack<List<ZDO>>();
        private static readonly HashSet<ZDOID> ToForceSend = new HashSet<ZDOID>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Game.ConnectPortals))]
        private static bool ConnectPortalsPrefix() {
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) { return true; }

            ConnectPortals(zdoMan);
            return false;
        }

        private static void ConnectPortals(ZDOMan zdoMan) {
            long sessionId = ZDOMan.GetSessionID();
            ClearCaches();

            CollectUnconnected(zdoMan, sessionId);
            int connected = PairByTag(sessionId);
            FlushForceSend(zdoMan);

            ClearCaches();

            if (connected > 0) {
                Logger.LogInfo($"Connected {connected} portal(s).");
            }
        }

        // Pass 1: validate every existing connection and bucket everything still unconnected.
        private static void CollectUnconnected(ZDOMan zdoMan, long sessionId) {
            List<ZDO> portals = zdoMan.m_portalObjects;

            for (int i = 0; i < portals.Count; i++) {
                ZDO portal = portals[i];
                if (portal == null) { continue; }

                string tag = portal.GetString(ZDOVars.s_tag);
                ZDOID targetId = portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);

                if (!targetId.IsNone()) {
                    zdoMan.m_objectsByID.TryGetValue(targetId, out ZDO target);
                    if (target != null && target.GetString(ZDOVars.s_tag) == tag) { continue; }

                    Disconnect(portal, sessionId);
                }

                Bucket(tag).Add(portal);
            }
        }

        // Pass 2: pair each tag's portals two at a time. An odd one out stays unconnected.
        private static int PairByTag(long sessionId) {
            int connected = 0;

            foreach (KeyValuePair<string, List<ZDO>> group in UnconnectedByTag) {
                List<ZDO> candidates = group.Value;
                for (int i = 0; i + 1 < candidates.Count; i += 2) {
                    Connect(candidates[i], candidates[i + 1], sessionId);
                    connected++;
                }
            }

            return connected;
        }

        private static List<ZDO> Bucket(string tag) {
            if (UnconnectedByTag.TryGetValue(tag, out List<ZDO> list)) { return list; }

            list = ListPool.Count > 0 ? ListPool.Pop() : new List<ZDO>();
            UnconnectedByTag.Add(tag, list);
            return list;
        }

        // UpdateConnection rather than SetConnection: it only rewrites an existing record, so
        // clearing a portal that never had one does not create an empty entry.
        private static void Disconnect(ZDO portal, long sessionId) {
            portal.SetOwner(sessionId);
            portal.UpdateConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None);
            ToForceSend.Add(portal.m_uid);
        }

        private static void Connect(ZDO a, ZDO b, long sessionId) {
            a.SetOwner(sessionId);
            b.SetOwner(sessionId);
            a.SetConnection(ZDOExtraData.ConnectionType.Portal, b.m_uid);
            b.SetConnection(ZDOExtraData.ConnectionType.Portal, a.m_uid);
            ToForceSend.Add(a.m_uid);
            ToForceSend.Add(b.m_uid);
        }

        // Pass 3: one set union per peer instead of vanilla's per-ZDO loop over every peer.
        private static void FlushForceSend(ZDOMan zdoMan) {
            if (ToForceSend.Count == 0) { return; }

            List<ZDOMan.ZDOPeer> peers = zdoMan.m_peers;
            for (int i = 0; i < peers.Count; i++) {
                peers[i].m_forceSend.UnionWith(ToForceSend);
            }
        }

        private static void ClearCaches() {
            foreach (KeyValuePair<string, List<ZDO>> group in UnconnectedByTag) {
                group.Value.Clear();
                ListPool.Push(group.Value);
            }

            UnconnectedByTag.Clear();
            ToForceSend.Clear();
        }
    }
}
