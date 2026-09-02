using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Fuel And Ore Loss: takes ownership of a smelter, kiln or fireplace before feeding it, so
    // the item cannot be lost in flight.
    //
    // Vanilla removes the fuel or ore from the inventory first and then sends an RPC to the ZDO's
    // owner to credit it. The handler drops the call unless it is the owner, so if the owning peer
    // is lagging, mid-handoff or gone, the item is simply destroyed.
    //
    // Prefixes on the four feeding entry points claim ownership when another peer holds it, so the
    // RPC resolves locally. Fireplace.Interact already claims an unowned ZDO; this covers the
    // owned-by-someone-else case and the three methods that never claimed at all.
    //
    // Client: these are interaction entry points reached from Player.Interact, so the fix protects
    // whoever does the feeding. Provenance: same root cause as Zen.ModLib's FixFuelLeak.
    [PatchSide(Side.Client)]
    [HarmonyPatch]
    internal static class FuelLossPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(FuelLossPatch),
                ValConfig.SectionCorrectness,
                "Fix Fuel And Ore Loss",
                true,
                "Takes ownership of a smelter, kiln or fireplace before adding fuel or ore. Vanilla removes " +
                "the item from your inventory and then sends a network message that is silently dropped if " +
                "the owning player is lagging or has disconnected, destroying the item.");
        }

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
