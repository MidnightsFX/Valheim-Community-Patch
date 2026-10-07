using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Loading Screen Wait: a portal, respawn or login ends its loading screen once the server
    // confirms it has sent the destination, instead of after a fixed 8 seconds.
    //
    // Vanilla holds a distant teleport and a respawn for a fixed 8 s, then asks IsAreaReady, which
    // only checks that the objects the client already knows about have been built. A client of a
    // dedicated server only knows what the server has streamed so far, and the server streams a
    // player's whole area through one queue, every building piece before any item, so a large base
    // can take longer than that: the player lands on bare terrain under floors that have not
    // arrived, and a respawn that finds no bed in a still-empty area wipes the bed spawn point. On
    // a quick server the same 8 s is spent waiting for an area that was ready long before.
    //
    // A client asks the server, over three peer RPCs, to confirm the destination's 3x3 zones. The
    // server answers once its per-peer sent table holds every Solid and Terrain object there, and
    // because it answers on the same connection right after the send pass, the answer always
    // arrives behind those objects. Transpilers swap the 8 s constant in Player.UpdateTeleport and
    // both reads of m_respawnLoadDuration in Game.FindSpawnPoint for a helper, and every
    // IsAreaReady call in the two for one that also waits for the answer, up to a timeout after
    // which vanilla's checks decide alone. Where another mod has already replaced the portal's 8 s,
    // its timing stays and only the IsAreaReady call is swapped. A host holds every object itself,
    // so it drops the fixed wait and lets IsAreaReady decide. A client of a server without this
    // fix, or of one that has it switched off, never gets a hello back and keeps vanilla's timings
    // exactly.
    //
    // Both: the server half answers on a dedicated server; the client half needs a local player.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNet))]
    internal static class LoadingWaitPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> ConfirmationTimeout;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(LoadingWaitPatch),
                ValConfig.SectionCorrectness,
                "Fix Loading Screen Wait",
                true,
                "Portals, respawns and joining end their loading screen once the server has sent the " +
                "buildings at the destination, instead of after a fixed 8 seconds, so you neither land " +
                "under floors that have not arrived nor wait for an area that already has. A client of " +
                "a dedicated server needs this fix on the server too; without it the game's own timings " +
                "apply. A host or single-player game waits only for the area to be built.");

            ConfirmationTimeout = ValConfig.BindServerConfig(
                ValConfig.SectionCorrectness,
                "Loading Confirmation Timeout",
                20,
                "How long, in seconds, a loading screen waits for the server to confirm the destination " +
                "before the game's own checks decide alone. Only reached when the server is struggling " +
                "to send the area.",
                advanced: true, valMin: 8, valMax: 60);
        }

        private static bool IsOn => Enabled != null && Enabled.Value;

        // Bumped whenever a payload changes shape; a mismatch on either side reads as "no support".
        private const int Protocol = 1;
        private const string HelloRpc = "VCP_LoadingHello";
        private const string QueryRpc = "VCP_LoadingQuery";
        private const string ConfirmedRpc = "VCP_LoadingConfirmed";

        // Vanilla's own constants: the teleport's fade-and-move delay and its fixed arrival wait.
        private const float VanillaMoveDelay = 2f;
        private const float VanillaArrivalWait = 8f;

        // IsAreaReady's own neighbourhood: the zone and the ring around it.
        private static readonly SimulationDistance AreaReadyDistance = new SimulationDistance(1, 0);

        // Registered on both sides for every connection; each handler checks its own role.
        // ZRpc.Register replaces an existing handler, and a peer without this mod drops all three
        // unread.
        [HarmonyPostfix]
        [HarmonyPatch("OnNewConnection")]
        private static void OnNewConnectionPostfix(ZNetPeer peer) {
            if (peer?.m_rpc == null) { return; }

            peer.m_rpc.Register<ZPackage>(HelloRpc, RPC_Hello);
            peer.m_rpc.Register<ZPackage>(QueryRpc, RPC_Query);
            peer.m_rpc.Register<ZPackage>(ConfirmedRpc, RPC_Confirmed);
        }

        // A handler that reads past the end of its package would disconnect the peer, so every
        // read is guarded.
        private static void RPC_Hello(ZRpc rpc, ZPackage pkg) {
            try {
                int protocol = pkg.ReadInt();
                ZNet net = ZNet.instance;
                if (net == null) { return; }

                if (net.IsServer()) {
                    // A server with the fix off never claims it, so its clients keep vanilla timings.
                    if (!IsOn) { return; }

                    ZPackage reply = new ZPackage();
                    reply.Write(Protocol);
                    rpc.Invoke(HelloRpc, reply);
                } else if (protocol == Protocol) {
                    Client.OnHelloReply(net);
                }
            } catch (Exception ex) {
                Logger.LogDebug($"Loading confirmation hello ignored: {ex.Message}");
            }
        }

        private static void RPC_Query(ZRpc rpc, ZPackage pkg) {
            try {
                if (pkg.ReadInt() != Protocol) { return; }

                int id = pkg.ReadInt();
                Vector3 point = pkg.ReadVector3();
                Server.OnQuery(rpc, id, point);
            } catch (Exception ex) {
                Logger.LogDebug($"Loading confirmation query ignored: {ex.Message}");
            }
        }

        private static void RPC_Confirmed(ZRpc rpc, ZPackage pkg) {
            try {
                if (pkg.ReadInt() != Protocol) { return; }

                Client.OnConfirmed(pkg.ReadInt());
            } catch (Exception ex) {
                Logger.LogDebug($"Loading confirmation answer ignored: {ex.Message}");
            }
        }

        // After the frame's send pass, which records every object it hands a peer in that peer's
        // sent table before invoking ZDOData, so an answer sent here trails the objects it vouches for.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Update))]
        private static void ZdoManUpdatePostfix(ZDOMan __instance) => Server.Evaluate(__instance);

        // ---- server ------------------------------------------------------------------------------

        private static class Server {
            // A query nobody is waiting on any more: longer than the longest client timeout (60 s).
            private const float QueryLifetime = 65f;
            private const float EvaluationInterval = 0.1f;

            private sealed class Query {
                public int Id;
                public Vector2s Zone;
                public float ReceivedAt;
            }

            private static readonly Dictionary<long, Query> Pending = new Dictionary<long, Query>();
            private static readonly List<long> Finished = new List<long>();
            private static readonly List<ZDO> SectorObjects = new List<ZDO>();
            private static ZNet _pendingFor;
            private static float _nextEvaluation;

            internal static void OnQuery(ZRpc rpc, int id, Vector3 point) {
                ZNet net = ZNet.instance;
                if (net == null || !net.IsServer() || !IsOn) { return; }
                if (!IsFinite(point)) { return; }

                ZNetPeer peer = net.GetPeer(rpc);
                if (peer == null || !peer.IsReady()) { return; }

                Vector2s zone = ZoneSystem.GetZone(point);

                // Sector 0 aliases every zone outside the grid, so its contents say nothing about
                // this zone. No answer means the client falls back to vanilla after its timeout.
                if (ZoneSystem.SectorToIndex(zone).Sector == 0) { return; }

                if (!ReferenceEquals(net, _pendingFor)) {
                    Pending.Clear();
                    _pendingFor = net;
                }

                // One query per peer: a new destination replaces the old one.
                Pending[peer.m_uid] = new Query { Id = id, Zone = zone, ReceivedAt = Time.unscaledTime };
            }

            internal static void Evaluate(ZDOMan zdoMan) {
                if (Pending.Count == 0) { return; }

                if (!ReferenceEquals(ZNet.instance, _pendingFor) || !RunMode.IsServer || !IsOn) {
                    Pending.Clear();
                    return;
                }

                float now = Time.unscaledTime;
                if (now < _nextEvaluation) { return; }
                _nextEvaluation = now + EvaluationInterval;

                Finished.Clear();
                foreach (KeyValuePair<long, Query> entry in Pending) {
                    ZDOMan.ZDOPeer zdoPeer = FindPeer(zdoMan, entry.Key);
                    if (zdoPeer == null || now - entry.Value.ReceivedAt > QueryLifetime) {
                        Finished.Add(entry.Key);
                        continue;
                    }

                    if (!AllSent(zdoMan, zdoPeer, entry.Value.Zone, out int vouched)) { continue; }

                    ZPackage answer = new ZPackage();
                    answer.Write(Protocol);
                    answer.Write(entry.Value.Id);
                    zdoPeer.m_peer.m_rpc.Invoke(ConfirmedRpc, answer);
                    Finished.Add(entry.Key);

                    Logger.LogDebug(
                        $"Confirmed loading destination {entry.Value.Zone} to {zdoPeer.m_peer.m_playerName} after " +
                        $"{(now - entry.Value.ReceivedAt) * 1000f:0} ms ({vouched} building and terrain objects).");
                }

                for (int i = 0; i < Finished.Count; i++) { Pending.Remove(Finished[i]); }
                Finished.Clear();
            }

            // Presence in the sent table, not ShouldSend: the client needs a copy, not the latest
            // revision, and network mods park updates by faking the revision but never hold back a
            // first copy. Default and Prioritized objects are left out on purpose: the server sends
            // them only after every Solid object in the player's whole area, and items or creatures
            // appearing after arrival are ordinary pop-in.
            private static bool AllSent(ZDOMan zdoMan, ZDOMan.ZDOPeer zdoPeer, Vector2s zone, out int vouched) {
                SectorObjects.Clear();
                zdoMan.FindSectorObjects(zone, AreaReadyDistance, SectorObjects);

                bool allSent = true;
                vouched = 0;
                for (int i = 0; i < SectorObjects.Count; i++) {
                    ZDO zdo = SectorObjects[i];
                    if (zdo.Type < ZDO.ObjectType.Solid) { continue; }
                    if (!zdoPeer.m_zdos.ContainsKey(zdo.m_uid)) {
                        allSent = false;
                        break;
                    }

                    vouched++;
                }

                SectorObjects.Clear();
                return allSent;
            }

            private static ZDOMan.ZDOPeer FindPeer(ZDOMan zdoMan, long uid) {
                List<ZDOMan.ZDOPeer> peers = zdoMan.m_peers;
                for (int i = 0; i < peers.Count; i++) {
                    if (peers[i].m_peer != null && peers[i].m_peer.m_uid == uid) { return peers[i]; }
                }

                return null;
            }

            private static bool IsFinite(Vector3 p) =>
                !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z)
                && !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z);
        }

        // ---- client ------------------------------------------------------------------------------

        internal static class Client {
            // Per session, keyed on the ZNet instance like RunMode.
            private static ZNet _sessionFor;
            private static bool _helloSent;
            private static bool _serverConfirms;

            // The one destination currently being waited on.
            private static bool _haveQuery;
            private static Vector2s _queryZone;
            private static int _queryId;
            private static int _nextQueryId = 1;
            private static float _querySentAt;
            private static bool _queryConfirmed;
            private static bool _timeoutLogged;

            private static void EnsureSession(ZNet net) {
                if (ReferenceEquals(net, _sessionFor)) { return; }

                _sessionFor = net;
                _helloSent = false;
                _serverConfirms = false;
                _haveQuery = false;
            }

            internal static void OnHelloReply(ZNet net) {
                EnsureSession(net);
                if (!_serverConfirms) { Logger.LogDebug("The server confirms loading destinations."); }
                _serverConfirms = true;
            }

            internal static void OnConfirmed(int id) {
                ZNet net = ZNet.instance;
                if (net == null || net.IsServer()) { return; }

                EnsureSession(net);
                if (!_haveQuery || id != _queryId || _queryConfirmed) { return; }

                _queryConfirmed = true;
                Logger.LogDebug(
                    $"Loading destination confirmed by the server after {(Time.unscaledTime - _querySentAt) * 1000f:0} ms.");
            }

            // A host holds every object itself, so IsAreaReady is the whole answer there.
            internal static bool HostDecides() {
                if (!IsOn) { return false; }

                ZNet net = ZNet.instance;
                return net != null && net.IsServer();
            }

            // Whether the wait for this destination is decided by the server's confirmation.
            internal static bool Confirms(Vector3 point) {
                if (!IsOn) { return false; }

                ZNet net = ZNet.instance;
                if (net == null || net.IsServer()) { return false; }

                EnsureSession(net);
                EnsureHello(net);
                if (!_serverConfirms) { return false; }

                return ZoneSystem.SectorToIndex(ZoneSystem.GetZone(point)).Sector != 0;
            }

            // Asked the first time anything loads, which is after the connection is up; until the
            // reply arrives every wait stays vanilla.
            private static void EnsureHello(ZNet net) {
                if (_helloSent) { return; }

                ZNetPeer server = net.GetServerPeer();
                if (server == null) { return; }

                _helloSent = true;
                ZPackage hello = new ZPackage();
                hello.Write(Protocol);
                server.m_rpc.Invoke(HelloRpc, hello);
            }

            internal static void ResetQuery() => _haveQuery = false;

            internal static void EnsureQuery(Vector3 point) {
                Vector2s zone = ZoneSystem.GetZone(point);
                if (_haveQuery && zone == _queryZone) { return; }

                _haveQuery = true;
                _queryZone = zone;
                _queryId = _nextQueryId++;
                _querySentAt = Time.unscaledTime;
                _queryConfirmed = false;
                _timeoutLogged = false;

                // No server peer: no answer, so the timeout hands the decision back to vanilla.
                ZNetPeer server = ZNet.instance.GetServerPeer();
                if (server == null) { return; }

                ZPackage query = new ZPackage();
                query.Write(Protocol);
                query.Write(_queryId);
                query.Write(point);
                server.m_rpc.Invoke(QueryRpc, query);
            }

            // True while the server has not yet vouched for this destination and the timeout has
            // not run out. Read by Fix Lost Bed Spawn Point as well.
            internal static bool AwaitingConfirmation(Vector3 point) {
                if (!Confirms(point)) { return false; }

                if (!_haveQuery || ZoneSystem.GetZone(point) != _queryZone) { return true; }
                if (_queryConfirmed) { return false; }

                return Time.unscaledTime - _querySentAt <= ConfirmationTimeout.Value;
            }

            // Replaces ZNetScene.IsAreaReady in the teleport and respawn waits. Vanilla's own check
            // still runs, so its other hooks (Fix Loading Screen Hang among them) still see it.
            internal static bool ConfirmedAreaReady(ZNetScene scene, Vector3 point) {
                bool ready = scene.IsAreaReady(point);
                if (!Confirms(point)) { return ready; }

                EnsureQuery(point);
                if (!AwaitingConfirmation(point)) {
                    if (!_queryConfirmed && !_timeoutLogged) {
                        _timeoutLogged = true;
                        Logger.LogInfo(
                            $"The server did not confirm the loading destination within {ConfirmationTimeout.Value} s, " +
                            "so the game's own checks decide.");
                    }

                    return ready;
                }

                return false;
            }

            // Replaces the 8 s constant in UpdateTeleport. 2 s is vanilla's own fade-and-move
            // delay, so returning it leaves IsAreaReady and the floor check as the only gate.
            internal static float ArrivalMinimum(Player player) {
                if (HostDecides() || Confirms(player.m_teleportTargetPos)) { return VanillaMoveDelay; }

                return VanillaArrivalWait;
            }

            // Replaces both reads of m_respawnLoadDuration in FindSpawnPoint. Otherwise the field's
            // own value, so another mod's change to it still applies.
            internal static float RespawnMinimum(Game game) {
                if (HostDecides()) { return 0f; }
                if (TryGetRespawnPoint(game, out Vector3 point) && Confirms(point)) { return 0f; }

                return game.m_respawnLoadDuration;
            }

            // The point FindSpawnPoint's waiting branches use: the logout point on a login, else the bed.
            private static bool TryGetRespawnPoint(Game game, out Vector3 point) {
                PlayerProfile profile = game.m_playerProfile;
                if (profile != null && !game.m_respawnAfterDeath && profile.HaveLogoutPoint()) {
                    point = profile.GetLogoutPoint();
                    return true;
                }

                if (profile != null && profile.HaveCustomSpawnPoint()) {
                    point = profile.GetCustomSpawnPoint();
                    return true;
                }

                point = Vector3.zero;
                return false;
            }
        }

        // ---- client hooks ------------------------------------------------------------------------

        private static readonly MethodInfo IsAreaReadyMethod =
            AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady));
        private static readonly MethodInfo ConfirmedAreaReadyMethod =
            AccessTools.Method(typeof(Client), nameof(Client.ConfirmedAreaReady));

        [PatchSide(Side.Client)]
        [HarmonyPatch(typeof(Player))]
        internal static class TeleportHooks {
            private static readonly MethodInfo ArrivalMinimumMethod =
                AccessTools.Method(typeof(Client), nameof(Client.ArrivalMinimum));

            // Asked as the fade starts, so a destination the client already holds is confirmed
            // before the move.
            [HarmonyPostfix]
            [HarmonyPatch(nameof(Player.TeleportTo))]
            private static void TeleportToPostfix(Player __instance, Vector3 pos, bool __result) {
                if (!__result || __instance != Player.m_localPlayer) { return; }

                Client.ResetQuery();
                if (Client.Confirms(pos)) { Client.EnsureQuery(pos); }
            }

            // Harmony re-runs every transpiler whenever any mod patches the method.
            private static bool _waitOwnerLogged;

            // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            [HarmonyPatch("UpdateTeleport")]
            private static IEnumerable<CodeInstruction> UpdateTeleportTranspiler(IEnumerable<CodeInstruction> instructions) {
                IEnumerable<CodeInstruction> swapped = PatchHelper.ReplaceCalls(
                    instructions, IsAreaReadyMethod, ConfirmedAreaReadyMethod, "Player.UpdateTeleport", expected: 1);
                if (ReferenceEquals(swapped, instructions)) { return instructions; }

                // The arrival wait is the only 8 in the method.
                List<CodeInstruction> codes = PatchHelper.Copy(swapped);
                int found = 0;
                for (int i = 0; i < codes.Count; i++) {
                    if (IsArrivalWait(codes[i])) { found++; }
                }

                // A mod that replaced the 8 owns the wait (SteadyFrame does). The swap stands on its
                // own, so that mod's timing still waits for the server's confirmation.
                if (found == 0) {
                    if (!_waitOwnerLogged) {
                        _waitOwnerLogged = true;
                        Logger.LogInfo(
                            "Player.UpdateTeleport: another mod has replaced the fixed portal arrival wait, so " +
                            "its timing applies; 'Fix Loading Screen Wait' still holds the arrival until the " +
                            "server has sent the destination.");
                    }

                    return codes;
                }

                if (found > 1) {
                    Logger.LogWarning(
                        $"Player.UpdateTeleport: expected 1 arrival wait constant, found {found}, so 'Fix Loading " +
                        "Screen Wait' leaves portals alone. Another mod has most likely already rewritten the " +
                        "method - if so, nothing is wrong.");
                    return instructions;
                }

                // The constant becomes "ldarg.0; call ArrivalMinimum", keeping any label on the first
                // instruction.
                for (int i = 0; i < codes.Count; i++) {
                    if (!IsArrivalWait(codes[i])) { continue; }

                    codes[i].opcode = OpCodes.Ldarg_0;
                    codes[i].operand = null;
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, ArrivalMinimumMethod));
                    break;
                }

                return codes;
            }

            private static bool IsArrivalWait(CodeInstruction code) =>
                code.opcode == OpCodes.Ldc_R4 && code.operand is float value && value == VanillaArrivalWait;
        }

        [PatchSide(Side.Client)]
        [HarmonyPatch(typeof(Game))]
        internal static class RespawnHooks {
            private static readonly FieldInfo RespawnLoadDurationField =
                AccessTools.Field(typeof(Game), nameof(Game.m_respawnLoadDuration));
            private static readonly MethodInfo RespawnMinimumMethod =
                AccessTools.Method(typeof(Client), nameof(Client.RespawnMinimum));

            // Each respawn asks afresh, so an answer for an earlier visit is never reused.
            [HarmonyPostfix]
            [HarmonyPatch("_RequestRespawn")]
            private static void RequestRespawnPostfix() => Client.ResetQuery();

            // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            [HarmonyPatch("FindSpawnPoint")]
            private static IEnumerable<CodeInstruction> FindSpawnPointTranspiler(IEnumerable<CodeInstruction> instructions) {
                // Logout point, bed and start location each ask IsAreaReady once.
                IEnumerable<CodeInstruction> swapped = PatchHelper.ReplaceCalls(
                    instructions, IsAreaReadyMethod, ConfirmedAreaReadyMethod, "Game.FindSpawnPoint", expected: 3);
                if (ReferenceEquals(swapped, instructions)) { return instructions; }

                // "ldarg.0; ldfld m_respawnLoadDuration" becomes "ldarg.0; call RespawnMinimum(Game)":
                // the same stack in and out.
                List<CodeInstruction> codes = PatchHelper.Copy(swapped);
                int found = 0;
                for (int i = 0; i < codes.Count; i++) {
                    if (!codes[i].LoadsField(RespawnLoadDurationField)) { continue; }

                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = RespawnMinimumMethod;
                    found++;
                }

                if (found == 2) { return codes; }

                Logger.LogWarning(
                    $"Game.FindSpawnPoint: expected 2 reads of m_respawnLoadDuration, found {found}, so 'Fix " +
                    "Loading Screen Wait' leaves respawns alone. Another mod has most likely already rewritten " +
                    "the method - if so, nothing is wrong.");
                return instructions;
            }
        }
    }
}
