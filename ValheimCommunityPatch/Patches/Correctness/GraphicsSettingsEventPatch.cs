using HarmonyLib;
using Valheim.SettingsGui;

namespace ValheimCommunityPatch.Patches.Correctness {
    // A settings window destroyed by a disconnect leaves its graphics handler subscribed, so later changes throw.
    // Prefixes Settings.OnDestroy to remove each graphics tab's UpdateUI from GraphicsSettingsChanged.
    // Graphics changes after a disconnect or kick apply fully, including the next world's terrain and clutter.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Settings), "OnDestroy")]
    internal static class GraphicsSettingsEventPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(GraphicsSettingsEventPatch),
                ValConfig.SectionCorrectness,
                "Fix Settings Window Disconnect",
                true,
                "Removes the graphics tab's settings handler when a disconnect or kick destroys the settings " +
                "window, so later graphics changes stop throwing errors.");
        }

        // The event is raised with a plain Invoke, so a dead handler's throw also skips every handler
        // added after it. Only the event is touched, not the half-destroyed UI that Terminate would,
        // and a prefix still runs if the game's OnDestroy throws.
        [HarmonyPrefix]
        private static void Prefix(Settings __instance) {
            if (Enabled == null || !Enabled.Value || __instance.SettingsTabs == null) { return; }

            foreach (ISettingsTab tab in __instance.SettingsTabs) {
                if (tab is GraphicsSettings graphics) {
                    GraphicsSettingsManager.GraphicsSettingsChanged -= graphics.UpdateUI;
                }
            }
        }
    }
}
