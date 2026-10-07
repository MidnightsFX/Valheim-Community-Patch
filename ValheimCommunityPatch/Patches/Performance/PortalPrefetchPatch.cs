using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Portal Destination Send Delay: while a player stands at a connected portal, the server
    // starts sending the buildings at the other end, so going through has less left to wait for.
    //
    // A dedicated server streams each player only the area around the position that player last
    // reported. A portal's destination therefore starts arriving only after the player has gone
    // through and reported the new position, and on a big base sending its building pieces is
    // most of the loading screen. Meanwhile the player stood at the portal with a connection that
    // had nothing left to carry once their own surroundings were sent.
    //
    // A TeleportWorld.UpdatePortal postfix on the client sends the portal's id to the server once,
    // when the local player comes within Portal Prefetch Range of a connected portal. The server
    // waits until that player's reported position is near the portal, resolves the portal's
    // connection itself, and queues every Solid and Terrain object in the 3x3 zones around the
    // other end that the player has not been sent: terrain first, then nearest first, capped.
    // These are the objects Fix Loading Screen Wait's confirmation checks. A CreateSyncList
    // postfix appends a batch from the queue only while the player's own sync list is nearly
    // empty, vanilla's own rule for distant objects, and sizes the batch to what the last send
    // carried. The queue is dropped once the player's reported position leaves the portal, which
    // going through it does, after a minute, or when it is drained. Not m_forceSend:
    // AddForceSendZdos puts forced objects at the head of the list, ahead of the player's own
    // surroundings. Capability is a hello over peer RPCs, as in Fix Loading Screen Wait, so
    // peers without this mod never send or receive any of it.
    //
    // Both: the server half answers on the server; the client half needs a local player.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNet))]
    internal static class PortalPrefetchPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> PrefetchRange;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(PortalPrefetchPatch),
                ValConfig.SectionPerformance,
                "Fix Portal Destination Send Delay",
                true,
                "While you stand at a connected portal, the server starts sending the buildings at the " +
                "other end, using only what your connection has spare, so the loading screen behind the " +
                "portal is shorter. Needs this mod on the server and the client; players without it are " +
                "unaffected. Turn it off to stop the server sending destinations a player may not visit.");

            PrefetchRange = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Portal Prefetch Range",
                10f,
                "How close, in meters, you must be to a connected portal for the server to start sending " +
                "its destination. 5 is the game's own portal activation range.",
                advanced: true, valMin: 5f, valMax: 30f);
        }

        private static bool IsOn => Enabled != null && Enabled.Value;

        private static float Range => PrefetchRange != null ? Mathf.Max(PrefetchRange.Value, 5f) : 10f;

        // Bumped whenever a payload changes shape; a mismatch on either side reads as "no support".
        private const int Protocol = 1;
        private const string HelloRpc = "VCP_PortalPrefetchHello";
        private const string RequestRpc = "VCP_PortalPrefetch";

        // Fix Loading Screen Wait's area, IsAreaReady's own: the zone and the ring around it.
        private static readonly SimulationDistance AreaReadyDistance = new SimulationDistance(1, 0);

        // Registered on both sides for every connection; each handler checks its own role. A peer
        // without this mod drops both unread.
        [HarmonyPostfix]
        [HarmonyPatch("OnNewConnection")]
        private static void OnNewConnectionPostfix(ZNetPeer peer) {
            if (peer?.m_rpc == null) { return; }

            peer.m_rpc.Register<ZPackage>(HelloRpc, RPC_Hello);
            peer.m_rpc.Register<ZPackage>(RequestRpc, RPC_Request);
        }

        // A handler that reads past the end of its package would disconnect the peer, so every
        // read is guarded.
        private static void RPC_Hello(ZRpc rpc, ZPackage pkg) {
            try {
                int protocol = pkg.ReadInt();
                ZNet net = ZNet.instance;
                if (net == null) { return; }

                if (net.IsServer()) {
                    // A server with the fix off never claims it, so its clients never ask.
                    if (!IsOn) { return; }

                    ZPackage reply = new ZPackage();
                    reply.Write(Protocol);
                    rpc.Invoke(HelloRpc, reply);
                } else if (protocol == Protocol) {
                    Client.OnHelloReply(net);
                }
            } catch (Exception ex) {
                Logger.LogDebug($"Portal prefetch hello ignored: {ex.Message}");
            }
        }

        private static void RPC_Request(ZRpc rpc, ZPackage pkg) {
            try {
                if (pkg.ReadInt() != Protocol) { return; }

                Server.OnRequest(rpc, pkg.ReadZDOID());
            } catch (Exception ex) {
                Logger.LogDebug($"Portal prefetch request ignored: {ex.Message}");
            }
        }

        // ---- server ------------------------------------------------------------------------------

        [PatchSide(Side.Server)]
        [HarmonyPatch(typeof(ZDOMan))]
        internal static class SyncListHooks {
            // Priority.Last: after every other postfix, Network Performance System's pacing filter
            // among them, so the length tested is what will really be sent and nothing else sees
            // or reorders the appended objects.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            [HarmonyPatch("CreateSyncList")]
            private static void CreateSyncListPostfix(ZDOMan __instance, ZDOMan.ZDOPeer peer, List<ZDO> toSync) =>
                Server.Feed(__instance, peer, toSync);
        }

        private static class Server {
            // Vanilla CreateSyncList adds the distant ring only below this many objects.
            private const int IdleListLength = 10;

            // How far past the range a reported position may be. A player reports every 2 s, and the
            // report that put them at the portal may arrive after the request.
            private const float Slack = 8f;
            private const float ReportGrace = 5f;
            private const float Lifetime = 60f;

            // Large bases keep their nearest objects; the rest arrive after the player does.
            private const int MaxQueued = 4096;

            private const int FirstBatch = 32;
            private const int MinBatch = 8;
            private const int MaxBatch = 512;

            private sealed class Prefetch {
                public ZNetPeer Peer;
                public ZDOID Portal;
                public float ReceivedAt;
                public bool Started;
                public float StartedAt;
                public Vector2s TargetZone;
                public readonly List<ZDOID> Queue = new List<ZDOID>();
                public int Head;
                public readonly List<ZDOID> LastBatch = new List<ZDOID>();
                public int Batch = FirstBatch;
            }

            private struct Candidate {
                public ZDOID Id;
                public bool Terrain;
                public float DistanceSq;
            }

            private static readonly Dictionary<long, Prefetch> Pending = new Dictionary<long, Prefetch>();
            private static readonly List<long> Expired = new List<long>();
            private static readonly List<ZDO> SectorObjects = new List<ZDO>();
            private static readonly List<Candidate> Candidates = new List<Candidate>();
            private static readonly Comparison<Candidate> TerrainThenNearest = (a, b) =>
                a.Terrain != b.Terrain ? (a.Terrain ? -1 : 1) : a.DistanceSq.CompareTo(b.DistanceSq);
            private static ZNet _pendingFor;

            internal static void OnRequest(ZRpc rpc, ZDOID portal) {
                ZNet net = ZNet.instance;
                if (net == null || !net.IsServer() || !IsOn || portal.IsNone()) { return; }

                ZNetPeer peer = net.GetPeer(rpc);
                if (peer == null || !peer.IsReady()) { return; }

                if (!ReferenceEquals(net, _pendingFor)) {
                    Pending.Clear();
                    _pendingFor = net;
                }

                float now = Time.unscaledTime;
                ForgetExpired(now);

                // A repeat for the portal already being served keeps its queue.
                if (Pending.TryGetValue(peer.m_uid, out Prefetch current)
                    && ReferenceEquals(current.Peer, peer) && current.Portal == portal) {
                    return;
                }

                // One destination per player: a portal hub's next portal replaces the last.
                // Validated and queued at the next send to this player, so a burst of requests
                // costs at most one scan per send.
                Pending[peer.m_uid] = new Prefetch { Peer = peer, Portal = portal, ReceivedAt = now };
            }

            // Entries for players who left: their sends, which would drop them, never come again.
            private static void ForgetExpired(float now) {
                Expired.Clear();
                foreach (KeyValuePair<long, Prefetch> entry in Pending) {
                    if (now - entry.Value.ReceivedAt > Lifetime) { Expired.Add(entry.Key); }
                }

                for (int i = 0; i < Expired.Count; i++) { Pending.Remove(Expired[i]); }
                Expired.Clear();
            }

            internal static void Feed(ZDOMan zdoMan, ZDOMan.ZDOPeer zdoPeer, List<ZDO> toSync) {
                if (Pending.Count == 0) { return; }

                ZNetPeer peer = zdoPeer?.m_peer;
                if (peer == null || toSync == null || !Pending.TryGetValue(peer.m_uid, out Prefetch prefetch)) { return; }

                if (!IsOn || !ReferenceEquals(ZNet.instance, _pendingFor) || !RunMode.IsServer
                    || !ReferenceEquals(prefetch.Peer, peer)) {
                    Pending.Remove(peer.m_uid);
                    return;
                }

                float now = Time.unscaledTime;
                ZDO portal = zdoMan.GetZDO(prefetch.Portal);
                if (portal == null) {
                    Stop(zdoPeer, prefetch, "stopped, the portal is gone");
                    return;
                }

                float reach = Range + Slack;
                bool atPortal = (peer.GetRefPos() - portal.GetPosition()).sqrMagnitude <= reach * reach;

                if (!prefetch.Started) {
                    if (!atPortal) {
                        // Never there, or not yet reported there.
                        if (now - prefetch.ReceivedAt > ReportGrace) { Pending.Remove(peer.m_uid); }
                        return;
                    }

                    if (!Start(zdoMan, zdoPeer, prefetch, portal, now)) {
                        Pending.Remove(peer.m_uid);
                        return;
                    }
                } else if (!atPortal) {
                    Vector2s zone = ZoneSystem.GetZone(peer.GetRefPos());
                    bool arrived = Math.Abs(zone.x - prefetch.TargetZone.x) <= 1 && Math.Abs(zone.y - prefetch.TargetZone.y) <= 1;
                    Stop(zdoPeer, prefetch, arrived ? "arrived" : "stopped, the player walked away");
                    return;
                }

                if (now - prefetch.ReceivedAt > Lifetime) {
                    Stop(zdoPeer, prefetch, "timed out");
                    return;
                }

                // Only into room the player's own area leaves. Ahead of this sit the player's
                // surroundings and anything forced, so the queue never delays either.
                if (toSync.Count >= IdleListLength) { return; }

                if (!Append(zdoMan, zdoPeer, prefetch, toSync)) { Stop(zdoPeer, prefetch, "complete"); }
            }

            // The server's own copy of the portal and its connection decide the destination; the
            // client only says which portal it stands at.
            private static bool Start(ZDOMan zdoMan, ZDOMan.ZDOPeer zdoPeer, Prefetch prefetch, ZDO portal, float now) {
                Game game = Game.instance;
                if (game == null || !game.PortalPrefabHash.Contains(portal.GetPrefab())) { return false; }

                ZDOID targetId = portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                ZDO target = targetId.IsNone() ? null : zdoMan.GetZDO(targetId);
                if (target == null) { return false; }

                Vector3 arrival = target.GetPosition();
                Vector2s zone = ZoneSystem.GetZone(arrival);

                // Sector 0 aliases every zone outside the grid, so its objects are not this zone's.
                if (ZoneSystem.SectorToIndex(zone).Sector == 0) { return false; }

                SectorObjects.Clear();
                zdoMan.FindSectorObjects(zone, AreaReadyDistance, SectorObjects);

                Candidates.Clear();
                for (int i = 0; i < SectorObjects.Count; i++) {
                    ZDO zdo = SectorObjects[i];
                    if (zdo.Type < ZDO.ObjectType.Solid || zdoPeer.m_zdos.ContainsKey(zdo.m_uid)) { continue; }

                    Candidates.Add(new Candidate {
                        Id = zdo.m_uid,
                        Terrain = zdo.Type == ZDO.ObjectType.Terrain,
                        DistanceSq = (zdo.GetPosition() - arrival).sqrMagnitude,
                    });
                }

                SectorObjects.Clear();
                Candidates.Sort(TerrainThenNearest);

                int count = Math.Min(Candidates.Count, MaxQueued);
                for (int i = 0; i < count; i++) { prefetch.Queue.Add(Candidates[i].Id); }
                Candidates.Clear();

                prefetch.Started = true;
                prefetch.StartedAt = now;
                prefetch.TargetZone = zone;

                Logger.LogDebug(
                    $"Prefetching portal destination {zone} for {prefetch.Peer.m_playerName}: {prefetch.Queue.Count} " +
                    "building and terrain objects not yet sent.");
                return true;
            }

            // False once nothing is left to send. A batch grows while whole batches get through and
            // shrinks to what got through when one did not, so the send usually carries its whole
            // list and the connection's spare room stays visible to anything that measures it.
            private static bool Append(ZDOMan zdoMan, ZDOMan.ZDOPeer zdoPeer, Prefetch prefetch, List<ZDO> toSync) {
                if (prefetch.LastBatch.Count > 0) {
                    int sent = 0;
                    for (int i = 0; i < prefetch.LastBatch.Count; i++) {
                        if (zdoPeer.m_zdos.ContainsKey(prefetch.LastBatch[i])) { sent++; }
                    }

                    prefetch.Batch = sent == prefetch.LastBatch.Count
                        ? Math.Min(prefetch.Batch * 2, MaxBatch)
                        : Math.Max(sent, MinBatch);
                    prefetch.LastBatch.Clear();
                }

                List<ZDOID> queue = prefetch.Queue;
                while (prefetch.Head < queue.Count && IsDone(zdoMan, zdoPeer, queue[prefetch.Head])) { prefetch.Head++; }
                if (prefetch.Head >= queue.Count) { return false; }

                for (int i = prefetch.Head; i < queue.Count && prefetch.LastBatch.Count < prefetch.Batch; i++) {
                    if (zdoPeer.m_zdos.ContainsKey(queue[i])) { continue; }

                    ZDO zdo = zdoMan.GetZDO(queue[i]);
                    if (zdo == null) { continue; }

                    toSync.Add(zdo);
                    prefetch.LastBatch.Add(queue[i]);
                }

                return true;
            }

            // Sent, by this or by the player's own area, or destroyed since it was queued.
            private static bool IsDone(ZDOMan zdoMan, ZDOMan.ZDOPeer zdoPeer, ZDOID id) =>
                zdoPeer.m_zdos.ContainsKey(id) || zdoMan.GetZDO(id) == null;

            private static void Stop(ZDOMan.ZDOPeer zdoPeer, Prefetch prefetch, string outcome) {
                Pending.Remove(prefetch.Peer.m_uid);
                if (!prefetch.Started || !Logger.DebugEnabled) { return; }

                int sent = 0;
                for (int i = 0; i < prefetch.Queue.Count; i++) {
                    if (zdoPeer.m_zdos.ContainsKey(prefetch.Queue[i])) { sent++; }
                }

                Logger.LogDebug(
                    $"Portal prefetch for {prefetch.Peer.m_playerName} {outcome}: {sent} of {prefetch.Queue.Count} " +
                    $"building and terrain objects sent in {Time.unscaledTime - prefetch.StartedAt:0.0} s.");
            }
        }

        // ---- client ------------------------------------------------------------------------------

        [PatchSide(Side.Client)]
        [HarmonyPatch(typeof(TeleportWorld))]
        internal static class PortalHooks {
            // Twice a second per loaded portal, after vanilla's own proximity check.
            [HarmonyPostfix]
            [HarmonyPatch("UpdatePortal")]
            private static void UpdatePortalPostfix(TeleportWorld __instance) => Client.OnPortalUpdate(__instance);
        }

        private static class Client {
            // Three UpdatePortal periods: a portal that has not seen the player for this long has
            // been left, so coming back to it asks again.
            private const float LeftAfter = 1.5f;

            // How much nearer another portal in range must be to take over, so standing between
            // two does not flip between them.
            private const float SwitchMargin = 1f;

            // Per session, keyed on the ZNet instance like RunMode.
            private static ZNet _sessionFor;
            private static bool _helloSent;
            private static bool _serverPrefetches;

            // The portal the player is at, whether or not it was asked for.
            private static ZDOID _atPortal = ZDOID.None;
            private static float _atPortalSeen;
            private static float _atPortalDistance;

            private static void EnsureSession(ZNet net) {
                if (ReferenceEquals(net, _sessionFor)) { return; }

                _sessionFor = net;
                _helloSent = false;
                _serverPrefetches = false;
                _atPortal = ZDOID.None;
            }

            internal static void OnHelloReply(ZNet net) {
                EnsureSession(net);
                if (!_serverPrefetches) { Logger.LogDebug("The server prefetches portal destinations."); }
                _serverPrefetches = true;
            }

            internal static void OnPortalUpdate(TeleportWorld portal) {
                if (!IsOn) { return; }

                Player player = Player.m_localPlayer;
                ZNet net = ZNet.instance;
                if (player == null || net == null || net.IsServer()) { return; }

                ZNetView nview = portal.m_nview;
                if (nview == null || !nview.IsValid() || portal.m_proximityRoot == null) { return; }

                float range = Range;
                float distance = Vector3.Distance(player.transform.position, portal.m_proximityRoot.position);
                if (distance > range) { return; }

                ZDO zdo = nview.GetZDO();
                if (zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal).IsNone()) { return; }

                EnsureSession(net);

                float now = Time.time;
                bool left = now - _atPortalSeen > LeftAfter;
                if (zdo.m_uid == _atPortal && !left) {
                    _atPortalSeen = now;
                    _atPortalDistance = distance;
                    return;
                }

                if (!left && !_atPortal.IsNone() && distance > _atPortalDistance - SwitchMargin) { return; }

                // Asked again on the next update until the server has answered the hello and the
                // player could use the portal at all.
                if (!_serverPrefetches) {
                    EnsureHello(net);
                    return;
                }

                if (!player.IsTeleportable(portal.m_allowAllItems)) { return; }

                _atPortal = zdo.m_uid;
                _atPortalSeen = now;
                _atPortalDistance = distance;

                // The portal a teleport has just arrived at leads back where the player came from,
                // which the player still holds.
                if (player.IsTeleporting() && Vector3.Distance(portal.transform.position, player.m_teleportTargetPos) <= range) {
                    return;
                }

                ZNetPeer server = net.GetServerPeer();
                if (server == null) { return; }

                ZPackage request = new ZPackage();
                request.Write(Protocol);
                request.Write(zdo.m_uid);
                server.m_rpc.Invoke(RequestRpc, request);
            }

            // Asked the first time the player reaches a connected portal; until the reply arrives
            // nothing is requested.
            private static void EnsureHello(ZNet net) {
                if (_helloSent) { return; }

                ZNetPeer server = net.GetServerPeer();
                if (server == null) { return; }

                _helloSent = true;
                ZPackage hello = new ZPackage();
                hello.Write(Protocol);
                server.m_rpc.Invoke(HelloRpc, hello);
            }
        }
    }
}
