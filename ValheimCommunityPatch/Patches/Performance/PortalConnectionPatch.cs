using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: Game.ConnectPortals runs every 5 seconds on the server main thread and is
    // quadratic. Its second pass calls FindRandomUnconnectedPortal for every unconnected portal, and
    // that method allocates a fresh List<ZDO> and rescans *all* portals, doing a ZDO string lookup and
    // comparison per element, plus a linear IsCurrentlyConnectingPortal scan per element.
    //
    // The pathological part is that an unpaired portal never resolves, so it re-runs the full scan
    // forever - every five seconds, for the life of the server. On a mature world with hundreds of
    // portal ZDOs (including orphans left behind by destroyed portals) that is the recurring stall.
    //
    // Game.SetConnection compounds it: it does a linear ZNet.GetPeer scan and, when the ZDO's owner is
    // an online client, does not write the connection at all - it fires an RPC and defers to another
    // tick via m_currentlyConnectingPortals.
    //
    // Fix: three O(n) passes over the portal list with a tag-keyed dictionary, no per-call allocation,
    // and direct writes after seizing ownership so no RPC round-trip is needed. Force-sends are
    // batched into one UnionWith per peer instead of vanilla's per-ZDO loop over all peers.
    //
    // Behavioural note: vanilla picks a *random* partner among same-tag candidates. This pairs them in
    // list order instead, so pairing is deterministic. It also pairs every same-tag portal in one tick
    // rather than one pair per tick.
    //
    // Provenance: algorithm follows ComfyMods/BetterServerPortals (GPL-3.0, redseiko), with its
    // "random portal" gameplay feature deliberately omitted - this project ships fixes, not features.
    [HarmonyPatch(typeof(Game))]
    internal static class PortalConnectionPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Fix Portal Connection Scan",
                true,
                "Replaces the quadratic portal-pairing scan the server runs every five seconds with an " +
                "indexed one. Vanilla rescans every portal for every unconnected portal, forever, so the " +
                "cost grows with the square of how many portals a world has ever had.");
        }

        // Reused across ticks and cleared rather than reallocated, so a steady state allocates nothing.
        private static readonly Dictionary<string, List<ZDO>> UnconnectedByTag = new Dictionary<string, List<ZDO>>();
        private static readonly Stack<List<ZDO>> ListPool = new Stack<List<ZDO>>();
        private static readonly HashSet<ZDOID> ToForceSend = new HashSet<ZDOID>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Game.ConnectPortals))]
        private static bool ConnectPortalsPrefix() {
            if (Enabled == null || !Enabled.Value) { return true; }

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

        // Pass 1: validate every existing connection and bucket everything still unconnected by tag.
        // Vanilla needs two separate sweeps for this; the validation result feeds straight into the
        // bucket, so one sweep does both.
        private static void CollectUnconnected(ZDOMan zdoMan, long sessionId) {
            List<ZDO> portals = zdoMan.m_portalObjects;

            for (int i = 0; i < portals.Count; i++) {
                ZDO portal = portals[i];
                if (portal == null) { continue; }

                string tag = portal.GetString(ZDOVars.s_tag);
                ZDOID targetId = portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);

                if (!targetId.IsNone()) {
                    // Direct dictionary hit rather than vanilla's GetZDO wrapper path.
                    zdoMan.m_objectsByID.TryGetValue(targetId, out ZDO target);
                    if (target != null && target.GetString(ZDOVars.s_tag) == tag) { continue; }

                    Disconnect(portal, sessionId);
                }

                Bucket(tag).Add(portal);
            }
        }

        // Pass 2: pair up each tag's unconnected portals two at a time. An odd one out simply stays
        // unconnected, exactly as in vanilla.
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

        // UpdateConnection rather than SetConnection: it only rewrites an existing connection record,
        // so clearing a portal that never had one does not create an empty entry.
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

        // One set union per peer, instead of vanilla ForceSendZDO's loop over every peer per ZDO.
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
