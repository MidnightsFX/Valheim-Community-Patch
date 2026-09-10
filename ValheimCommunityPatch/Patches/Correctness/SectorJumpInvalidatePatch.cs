using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Teleport Ghost Players: a player who jumps out of another player's loaded area is
    // removed from that player's game instead of standing frozen where they left.
    //
    // A client only destroys a remote object when its ZDO leaves the client's loaded sectors, and
    // for an object that jumped away the only thing that can move it is the server's
    // sector-invalidation list. Since the sector rewrite, ZDO.InternalSetPosition files the ZDO
    // in its new sector before it stores the new position, and the per-peer check that fills that
    // list reads the position. On a teleport it therefore sees the old spot, still inside the other
    // player's area, and queues nothing. The far client keeps the ZDO in its old sector and the
    // player object with it: frozen, and still answering local chat range checks, until the
    // traveller next crosses a zone line. Walking is unaffected because the previous position is
    // only one step behind.
    //
    // A prefix records which sector the ZDO was filed under, and a postfix, once the position is
    // stored, re-runs vanilla's own peer notification when the sector changed. The vanilla pass is
    // left in place; the second one is idempotent, because a peer already told to invalidate has
    // no entry left for the check to match. A peer that is still streaming the object gets it
    // re-sent in the same packet, ahead of everything else.
    //
    // Server: the invalidation list is only produced where ZNet.IsServer() is true (dedicated
    // server or listen host), gated at runtime because a client process can start hosting later.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(ZDO))]
    internal static class SectorJumpInvalidatePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SectorJumpInvalidatePatch),
                ValConfig.SectionCorrectness,
                "Fix Teleport Ghost Players",
                true,
                "Tells a client to drop a player who teleported out of its loaded area. In vanilla " +
                "the server misses that notification for any single-step jump (teleport, portal, " +
                "respawn), so the other client keeps the player standing frozen where they left, " +
                "still inside local chat range, until the traveller next crosses a zone line.");
        }

        private static bool Active => RunMode.IsServer && Enabled != null && Enabled.Value;

        // The sector ZDOMan holds the ZDO in: sector 0 while it is flagged as outside the grid,
        // which is also where an invalidated ZDO sits.
        private static ZoneSystem.SectorIndex FiledSector(ZDO zdo) =>
            zdo.OutsideZones ? ZoneSystem.SectorZero : zdo.GetSectorIndex();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZDO.InternalSetPosition))]
        private static void InternalSetPositionPrefix(ZDO __instance, out ZoneSystem.SectorIndex __state) {
            __state = Active ? FiledSector(__instance) : default;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZDO.InternalSetPosition))]
        private static void InternalSetPositionPostfix(ZDO __instance, ZoneSystem.SectorIndex __state) {
            if (!Active) { return; }

            ZoneSystem.SectorIndex now = FiledSector(__instance);
            if (now.Sector == __state.Sector) { return; }

            // SetSector leaves portals where they are, so there was no move to announce.
            if (Game.instance.PortalPrefabHash.Contains(__instance.GetPrefab())) { return; }

            if (!Logger.DebugEnabled) {
                ZDOMan.instance.ZDOSectorInvalidated(__instance);
                return;
            }

            int before = QueuedInvalidations();
            ZDOMan.instance.ZDOSectorInvalidated(__instance);
            int added = QueuedInvalidations() - before;
            if (added > 0) {
                Logger.LogDebug(
                    $"Sector jump: {PrefabName(__instance)} moved " +
                    $"{ZoneSystem.IndexToSector(__state.Sector)} -> {ZoneSystem.IndexToSector(now.Sector)}, " +
                    $"invalidated on {added} peer(s) the vanilla pass missed.");
            }
        }

        private static int QueuedInvalidations() {
            List<ZDOMan.ZDOPeer> peers = ZDOMan.instance.m_peers;
            int count = 0;
            for (int i = 0; i < peers.Count; i++) { count += peers[i].m_invalidSector.Count; }
            return count;
        }

        private static string PrefabName(ZDO zdo) {
            int hash = zdo.GetPrefab();
            ZNetScene scene = ZNetScene.instance;
            if (ReferenceEquals(scene, null)) { return hash.ToString(); }
            UnityEngine.GameObject prefab = scene.GetPrefab(hash);
            return ReferenceEquals(prefab, null) ? hash.ToString() : prefab.name;
        }
    }
}
