using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Main Menu Popup Subscription: every main menu stays in memory through a static popup event it never leaves.
    // Postfixes FejdStartup.OnDestroy to remove OnUGCPopupShown from CensorShittyWords.UGCPopupShown.
    // Each logout no longer leaks the old menu with every character profile and explored map it loaded.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(FejdStartup), "OnDestroy")]
    internal static class MainMenuPopupEventPatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(MainMenuPopupEventPatch));

        // Start's own removal is bound to the new menu, so it never takes off the old one, and OnDestroy
        // removes the menu's ZInput and Localization handlers but not this one. Nothing raises the event.
        [HarmonyPostfix]
        private static void Postfix(FejdStartup __instance) {
            if (ApiSwitch.Off) { return; }

            CensorShittyWords.UGCPopupShown -= __instance.OnUGCPopupShown;
        }
    }
}
