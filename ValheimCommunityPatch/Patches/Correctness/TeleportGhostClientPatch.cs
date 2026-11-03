using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Teleport Ghost From Client: a player who teleports far away on a server without this mod
    // is removed from other players' games instead of standing frozen where they left.
    //
    // The server drops a player from another client's game only when their ZDO changes sector and
    // the position it held at that moment lies outside that client's area. A single jump is tested
    // against the old spot, so it tells nobody, and the ghost stays until the traveller next crosses
    // a zone line, when the spot tested is at the destination. Fix Teleport Ghost Players corrects
    // this on the server; this fix gets the same result from the traveller's game.
    //
    // A postfix on Player.UpdateTeleport notices the body has moved two or more zones from its ZDO
    // and waits for LateUpdate to bring the ZDO and the reference position along. On a later physics
    // step it sends the ZDO once from just across its nearest zone line, then again from the real
    // position, in the same call. The server sees a sector change tested at the destination and
    // sends its own notification. With no room on the connection it tries again on each step until
    // the teleport ends. Do not move the ZDO ahead of the reference position: the scene destroys any
    // object filed outside the reference's area, the local player included.
    //
    // Client: only the traveller needs this mod. A host runs Fix Teleport Ghost Players instead.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Player))]
    internal static class TeleportGhostClientPatch {
        private const string FixName = "Fix Teleport Ghost From Client";

        // The first NPS with the same fix, as M44.
        private static readonly System.Version NpsFixVersion = new System.Version(1, 18, 3);

        internal static FixToggle Enabled;

        private const int JumpZones = 2;
        private const float HalfZone = 32f;

        // Past the line, so rounding cannot leave the spot on the near side of it.
        private const float LineMargin = 0.5f;

        private static bool _pending;
        private static bool _loggedStandDown;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TeleportGhostClientPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "When you teleport far away on a server without this mod, other players stop seeing " +
                "you standing frozen where you left. Fix Teleport Ghost Players does this on the " +
                "server for every player; this does it from your own game for you.");
        }

        [HarmonyPrepare]
        private static bool Prepare() {
            if (!Chainloader.PluginInfos.TryGetValue(Performance.CreatureSyncDeadbandPatch.NpsGuid, out PluginInfo nps)) { return true; }

            System.Version version = nps?.Metadata?.Version;
            if (version == null || version < NpsFixVersion) { return true; }

            if (!_loggedStandDown) {
                _loggedStandDown = true;
                Logger.LogInfo(
                    $"Network Performance System {version} is loaded and applies the same fix, " +
                    $"so '{FixName}' stands down and leaves it to NPS.");
            }

            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch("UpdateTeleport")]
        private static void UpdateTeleportPostfix(Player __instance) {
            if (Enabled == null || !Enabled.Value || RunMode.IsServer) { return; }

            ZDO zdo = __instance.m_nview.GetZDO();
            if (zdo == null) { return; }

            Vector2s zone = ZoneSystem.GetZone(__instance.transform.position);
            Vector2s filed = zdo.GetSector();
            if (Mathf.Max(Mathf.Abs(zone.x - filed.x), Mathf.Abs(zone.y - filed.y)) >= JumpZones) {
                _pending = true;
                return;
            }
            if (!_pending) { return; }

            // After the teleport the traveller's next zone line clears the ghost, as in vanilla.
            if (SendAcrossZoneLine(zdo) || !__instance.IsTeleporting()) { _pending = false; }
        }

        private static bool SendAcrossZoneLine(ZDO zdo) {
            List<ZDOMan.ZDOPeer> peers = ZDOMan.instance.m_peers;
            if (peers.Count == 0) { return true; }

            // A client's only peer is the server.
            ZDOMan.ZDOPeer server = peers[0];
            Vector3 real = zdo.GetPosition();
            zdo.SetPosition(AcrossNearestZoneLine(real, zdo.GetSector()));
            server.ForceSendZDO(zdo.m_uid);
            ZDOMan.instance.SendZDOs(server, flush: true);
            bool sent = server.m_zdos.TryGetValue(zdo.m_uid, out ZDOMan.ZDOPeer.PeerZDOInfo info)
                && info.m_dataRevision == zdo.DataRevision;
            zdo.SetPosition(real);

            // Right behind it, so the server applies both in one frame and sends players at the
            // destination only the real spot.
            if (sent) {
                server.ForceSendZDO(zdo.m_uid);
                ZDOMan.instance.SendZDOs(server, flush: true);
            }
            return sent;
        }

        // The axis further from the zone's center has the nearer line.
        private static Vector3 AcrossNearestZoneLine(Vector3 point, Vector2s zone) {
            Vector3 center = ZoneSystem.GetZonePos(zone);
            float offsetX = point.x - center.x;
            float offsetZ = point.z - center.z;
            float across = HalfZone + LineMargin;
            if (Mathf.Abs(offsetX) >= Mathf.Abs(offsetZ)) {
                point.x = center.x + (offsetX >= 0f ? across : -across);
            } else {
                point.z = center.z + (offsetZ >= 0f ? across : -across);
            }
            return point;
        }
    }
}
