using HarmonyLib;
using Valheim.SettingsGui;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Settings Window Disconnect: a settings window closed by a disconnect, rather than by its
    // own buttons, no longer leaves behind a dead handler that breaks every later graphics change.
    //
    // GraphicsSettings.Initialize adds UpdateUI to the static
    // GraphicsSettingsManager.GraphicsSettingsChanged, and only Terminate removes it, which only
    // Settings.CloseSettings calls. The window is a child of the in-game menu or the main menu, so
    // a disconnect or kick while it is open destroys it along with them, and Settings.OnDestroy
    // leaves the handler. The next graphics change calls UpdateUI on the destroyed window, which
    // throws. Because the event is raised with a plain Invoke, the throw also skips every handler
    // added after it: the next world's terrain, clutter, camera effects and network view distance.
    // That repeats on every graphics change until the game restarts. The other settings tabs pair
    // their static subscriptions in OnEnable/OnDisable or OnDestroy.
    //
    // A prefix on Settings.OnDestroy removes UpdateUI for each graphics tab. It touches only the
    // event, not the half-destroyed UI that Terminate would, and finds nothing after a normal
    // close. A prefix rather than a postfix, so it still runs if the game's OnDestroy throws.
    //
    // Client: the settings window.
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
                "Removes the graphics tab's settings-change handler when the settings window is " +
                "destroyed by a disconnect or kick instead of being closed. Vanilla leaves it behind, so " +
                "every later graphics change throws an error and skips the handlers added after it, " +
                "such as the next world's terrain and clutter.");
        }

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
