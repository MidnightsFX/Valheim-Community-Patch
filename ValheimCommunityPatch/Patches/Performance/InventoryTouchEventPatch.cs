using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Inventory Touch Subscription: the inventory screen takes its touch long-press handlers
    // off when it is disabled, so a logout no longer leaves the old inventory screen in memory.
    //
    // InventoryGui.OnEnable adds TouchLongPressStarted and TouchLongPressPerformed to the static
    // TouchLongPress.Started and Performed events, and OnDisable adds them again where it means to
    // remove them; it removes its PvP toggle listener correctly on the next line. OnDestroy
    // removes neither. Each enable and disable leaves two more entries on each event, and after a
    // logout they keep the destroyed InventoryGui, and everything it references, reachable.
    //
    // A postfix on OnDisable removes each handler twice: once for the add OnDisable just made and
    // once for OnEnable's. A removal that finds nothing does nothing, so this stays correct if the
    // game fixes the typo or another mod rewrites the method.
    //
    // Client: InventoryGui is client-only.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(InventoryGui), "OnDisable")]
    internal static class InventoryTouchEventPatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(InventoryTouchEventPatch));

        [HarmonyPostfix]
        private static void Postfix(InventoryGui __instance) {
            if (ApiSwitch.Off) { return; }

            for (int i = 0; i < 2; i++) {
                TouchLongPress.Started -= __instance.TouchLongPressStarted;
                TouchLongPress.Performed -= __instance.TouchLongPressPerformed;
            }
        }
    }
}
