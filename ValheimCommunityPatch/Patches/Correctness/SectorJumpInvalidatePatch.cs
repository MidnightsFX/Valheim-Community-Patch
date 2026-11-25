using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // ZDO.InternalSetPosition re-files a ZDO before storing its position, so peers are never told a teleporter left.
    // A prefix records the ZDO's sector, and a postfix re-runs ZDOMan.ZDOSectorInvalidated when the sector changed.
    // A player who teleports away vanishes from other clients instead of standing frozen in local chat range.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZDO))]
    internal static class SectorJumpInvalidatePatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SectorJumpInvalidatePatch),
                ValConfig.SectionCorrectness,
                "Fix Teleport Ghost Players",
                true,
                "Tells a client to drop a player who teleported out of its loaded area, instead of keeping " +
                "them frozen where they left until they next cross a zone line.");
        }

        // Checked per call, not by patch side: a client process can start hosting later.
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

            // Vanilla's pass read the old position: fine for a walk, one step behind, but a jump is missed.
            // Running it again is idempotent: a peer already told has no entry left for the check to match.
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
