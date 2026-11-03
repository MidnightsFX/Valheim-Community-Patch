using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Layout Switch Focus: switching the keyboard layout no longer takes keyboard focus away from
    // the text field being typed in.
    //
    // A layout switch (Alt+Shift, Win+Space, Caps Lock or the macOS input source key) makes the Input
    // System report a ConfigurationChanged for the keyboard. ZInput.OnDeviceChanged raises
    // OnInputLayoutChanged for every Added, Reconnected and ConfigurationChanged device, an event meant
    // for switches between keyboard and gamepad, and BuildUi.OnLayoutChanged answers it with
    // EventSystem.SetSelectedGameObject(null). BuildUi stays in the HUD for the whole session, so the
    // deselect reaches any field: chat, the console, map pin names, sign and portal texts, the build
    // menu search and other mods' fields stop taking keys in the middle of a word, and the next keys
    // run game actions instead. The event's other listeners only refresh hints and cursors.
    //
    // A prefix skips the deselect while a text field has keyboard focus and the gamepad is not the
    // active input, and still closes the favorites dropdown as vanilla does. A switch to the gamepad
    // deselects as before.
    //
    // Client: the HUD's build menu. Provenance: contributed by rgeraskin.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class LayoutSwitchFocusPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(LayoutSwitchFocusPatch),
                ValConfig.SectionCorrectness,
                "Fix Layout Switch Focus",
                true,
                "Switching the keyboard layout while typing keeps the text field focused, so the next keys " +
                "go on into chat, the console, a map pin, a sign or a search field. Vanilla drops the focus " +
                "on every layout switch and the keys run game actions instead.");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildUi), "OnLayoutChanged")]
        private static bool OnLayoutChangedPrefix(BuildUi __instance, bool __runOriginal) {
            if (!__runOriginal || Enabled == null || !Enabled.Value || ZInput.IsGamepadActive()) { return true; }
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            TMP_InputField field = selected != null ? selected.GetComponent<TMP_InputField>() : null;
            if (field == null || !field.isFocused) { return true; }

            // What vanilla does here, minus the deselect.
            if (__instance.m_favoritesDropdown != null) { __instance.m_favoritesDropdown.Close(); }
            return false;
        }
    }
}
