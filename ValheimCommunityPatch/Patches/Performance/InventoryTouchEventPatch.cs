using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Inventory Touch Subscription: InventoryGui.OnDisable re-adds its touch handlers, keeping old screens alive.
    // Postfixes OnDisable to remove TouchLongPressStarted and TouchLongPressPerformed twice, undoing both adds.
    // A logout no longer leaves the old inventory screen, and everything it references, in memory.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(InventoryGui), "OnDisable")]
    internal static class InventoryTouchEventPatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(InventoryTouchEventPatch));

        [HarmonyPostfix]
        private static void Postfix(InventoryGui __instance) {
            if (ApiSwitch.Off) { return; }

            // Once for OnDisable's add and once for OnEnable's. Removing an absent handler does nothing,
            // so this stays correct if the game turns its add into a removal or another mod rewrites OnDisable.
            for (int i = 0; i < 2; i++) {
                TouchLongPress.Started -= __instance.TouchLongPressStarted;
                TouchLongPress.Performed -= __instance.TouchLongPressPerformed;
            }
        }
    }
}
