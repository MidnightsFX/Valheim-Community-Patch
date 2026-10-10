using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Reference Position Send Delay: a client reports its position at once when it jumps two
    // or more zones (portal, respawn, login) instead of at the next 2 s report.
    //
    // A client tells the server where it is only from ZNet.SendPeriodicData, every 2 s. The server
    // decides from that position which objects to stream to the player and which zones to generate
    // around them, so after a portal, a respawn at a far bed or a login the server keeps streaming
    // the old area for up to 2 s while the player waits on a loading screen for the new one.
    //
    // A prefix on SendPeriodicData compares the reference position's zone with the zone last
    // reported. When it has moved two or more zones it sets the timer so vanilla sends this frame;
    // the report itself is vanilla's. Ordinary movement cannot cover two zones between reports, so
    // only jumps trigger it.
    //
    // Client: only a client reports its position; the host is the server.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZNet))]
    internal static class RefPosSendDelayPatch {
        private const float VanillaReportInterval = 2f;
        private const int JumpZones = 2;

        private static ZNet _stateFor;
        private static Vector2s _lastReportedZone;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(RefPosSendDelayPatch));

        [HarmonyPrefix]
        [HarmonyPatch("SendPeriodicData")]
        private static void SendPeriodicDataPrefix(ZNet __instance, float dt) {
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
