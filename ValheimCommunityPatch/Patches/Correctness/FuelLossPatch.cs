using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla removes fuel or ore before the owner credits it by RPC, so a lagging or departed owner destroys the item.
    // Prefixes Smelter.OnAddFuel, Smelter.OnAddOre, Fireplace.Interact and Fireplace.UseItem to claim ownership first.
    // Fuel and ore fed to a smelter, kiln or fireplace are no longer lost to a dropped network message.
    // Provenance: same root cause as Zen.ModLib's FixFuelLeak.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class FuelLossPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(FuelLossPatch),
                ValConfig.SectionCorrectness,
                "Fix Fuel And Ore Loss",
                true,
                "Takes ownership of a smelter, kiln or fireplace before adding fuel or ore, so the item is not " +
                "lost when the owning player lags or disconnects.");
        }

        // Fireplace.Interact claims only an unowned ZDO itself, and the other three entry points never claim.
        private static void ClaimBeforeInteract(ZNetView nview) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (nview == null || !nview.IsValid() || nview.IsOwner()) { return; }

            nview.ClaimOwnership();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        private static void SmelterOnAddFuelPrefix(Smelter __instance) => ClaimBeforeInteract(__instance.m_nview);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        private static void SmelterOnAddOrePrefix(Smelter __instance) => ClaimBeforeInteract(__instance.m_nview);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
        private static void FireplaceInteractPrefix(Fireplace __instance) => ClaimBeforeInteract(__instance.m_nview);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.UseItem))]
        private static void FireplaceUseItemPrefix(Fireplace __instance) => ClaimBeforeInteract(__instance.m_nview);
    }
}
