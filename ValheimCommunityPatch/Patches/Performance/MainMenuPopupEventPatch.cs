using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Main Menu Popup Subscription: leaving the main menu no longer keeps it in memory for the
    // rest of the session, along with every character profile it loaded.
    //
    // FejdStartup.Start removes OnUGCPopupShown from the static CensorShittyWords.UGCPopupShown
    // and adds it again. The removal is bound to the new menu, so it never takes off the previous
    // one, and OnDestroy, which removes the menu's ZInput and Localization handlers, leaves this
    // one. Nothing ever invokes the delegate, so its only effect is that every main menu the game
    // has shown, at start and after each logout, stays reachable with its m_profiles: every
    // character's PlayerProfile with each world's explored map data.
    //
    // A postfix on OnDestroy removes the menu's handler, as the game does with its other two.
    //
    // Client: the main menu. A dedicated server shows it once and never returns to it.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(FejdStartup), "OnDestroy")]
    internal static class MainMenuPopupEventPatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(MainMenuPopupEventPatch));

        [HarmonyPostfix]
        private static void Postfix(FejdStartup __instance) {
            if (ApiSwitch.Off) { return; }

            CensorShittyWords.UGCPopupShown -= __instance.OnUGCPopupShown;
        }
    }
}
