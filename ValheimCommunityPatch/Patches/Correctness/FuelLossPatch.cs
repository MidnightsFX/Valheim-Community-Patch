using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: adding fuel or ore removes the item from your inventory *first*, then asks the
    // ZDO's owner to credit it over the network:
    //
    //   user.GetInventory().RemoveItem(this.m_fuelItem.m_itemData.m_shared.m_name, 1);
    //   this.m_nview.InvokeRPC("RPC_AddFuel");
    //
    // and the handler on the other end drops the call unless it is the owner:
    //
    //   private void RPC_AddFuel(long sender) { if (!this.m_nview.IsOwner()) return; ... }
    //
    // If the owning peer is mid-handoff, lagging, or has just disconnected, the RPC goes nowhere and
    // the coal or ore is simply gone. This is the "I fed 40 coal into the smelter and it only took 12"
    // report, and it gets worse the more players are near the same base.
    //
    // Fix: take ownership before the interaction runs, so the RPC resolves locally and cannot be lost
    // in flight. Fireplace.Interact already does this (it calls ClaimOwnership when the ZDO has no
    // owner at all) - it just does not cover the case where the owner is another peer, and Smelter and
    // Fireplace.UseItem do not do it at all.
    //
    // Provenance: same root cause as Zen.ModLib's FixFuelLeak; reimplemented as prefixes rather than
    // that mod's backwards IL match, which is fragile and harder to verify.
    //
    // Client: all four targets are interaction entry points - the Smelter pair are Switch.m_onUse
    // callbacks, the Fireplace pair are Interactable/ItemUse - reached from Player.Interact. A
    // dedicated server never interacts with anything, so this protects whoever does the feeding.
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

        // Claiming is a no-op when we already own it, and ZNetView.ClaimOwnership is the same call
        // Fireplace.Interact already makes for the unowned case.
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
