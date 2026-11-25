using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Reference Position Send Delay: after a teleport the server streams the old area until the next 2 s report.
    // Prefixes ZNet.SendPeriodicData to send this frame when the reference position has jumped two or more zones.
    // After a portal, far respawn or login the server sends the new area at once, shortening the loading screen.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZNet))]
    internal static class RefPosSendDelayPatch {
        private const float VanillaReportInterval = 2f;

        // Ordinary movement cannot cover two zones between reports, so only jumps reach this.
        private const int JumpZones = 2;

        private static ZNet _stateFor;
        private static Vector2s _lastReportedZone;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(RefPosSendDelayPatch));

        [HarmonyPrefix]
        [HarmonyPatch("SendPeriodicData")]
        private static void SendPeriodicDataPrefix(ZNet __instance, float dt) {
            // Only a client reports its position; the host is the server.
            if (ApiSwitch.Off || __instance.IsServer()) { return; }

            // Per session. The server starts from the origin, which is what PeerInfo told it.
            if (!ReferenceEquals(__instance, _stateFor)) {
                _stateFor = __instance;
                _lastReportedZone = ZoneSystem.GetZone(Vector3.zero);
            }

            Vector2s zone = ZoneSystem.GetZone(__instance.GetReferencePosition());
            int moved = Math.Max(Math.Abs(zone.x - _lastReportedZone.x), Math.Abs(zone.y - _lastReportedZone.y));
            if (moved >= JumpZones && __instance.m_periodicSendTimer < VanillaReportInterval) {
                __instance.m_periodicSendTimer = VanillaReportInterval;
                Logger.LogDebug($"Reference position jumped {moved} zones; reporting it now.");
            }

            // The same float comparison vanilla makes right after this prefix.
            if ((float)(__instance.m_periodicSendTimer + dt) >= VanillaReportInterval && AnyReadyPeer(__instance.m_peers)) {
                _lastReportedZone = zone;
            }
        }

        private static bool AnyReadyPeer(List<ZNetPeer> peers) {
            for (int i = 0; i < peers.Count; i++) {
                if (peers[i].IsReady()) { return true; }
            }

            return false;
        }
    }
}
