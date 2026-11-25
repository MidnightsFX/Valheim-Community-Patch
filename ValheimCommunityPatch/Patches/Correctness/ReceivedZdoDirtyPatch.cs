using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Remote client changes do not trigger chunk dirtying on the server, leaving them unsaved across restarts.
    // Postfixes ZDO.Deserialize to call SetDirtySector (or SetDirtyPortals) whenever remote data is received.
    // Ensures remote player building and storage modifications are reliably written to disk on world save.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZDO))]
    internal static class ReceivedZdoDirtyPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ReceivedZdoDirtyPatch),
                ValConfig.SectionCorrectness,
                "Fix Unsaved Client Changes",
                true,
                "Forces remote player changes (building, chest storage) to be marked dirty for the next world save.");
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZDO.Deserialize))]
        private static void DeserializePostfix(ZDO __instance) {
            if (Enabled == null || !Enabled.Value) { return; }
            // Checked per call, not by patch side: a client process can start hosting later.
            if (!RunMode.IsServer) { return; }
            if (!__instance.Persistent) { return; }

            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) { return; }

            // Portals are saved from their own store, so a portal's chunk is never rewritten for it.
            Game game = Game.instance;
            if (!ReferenceEquals(game, null) && game.PortalPrefabHash.Contains(__instance.GetPrefab())) {
                zdoMan.SetDirtyPortals();
                return;
            }

            // The position was stored before Deserialize ran, so this is the sector it was filed in.
            zdoMan.SetDirtySector(__instance);
        }
    }
}
