using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimCommunityPatch.Patches.Correctness {
    // A keyboard layout switch makes the build menu deselect the UI, so a text field stops taking keys mid-word.
    // Prefixes BuildUi.OnLayoutChanged to skip the deselect while a text field has focus and the gamepad is not active.
    // Switching layout while typing keeps chat, the console, signs and search fields focused.
    // Provenance: contributed by rgeraskin.
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
                "Keeps the text field focused when you switch keyboard layout while typing, so the next keys " +
                "go on into chat, the console, a sign or a search field instead of running game actions.");
        }

        // ZInput raises the layout event for a keyboard's ConfigurationChanged too, not only for a switch
        // to or from the gamepad. BuildUi stays in the HUD all session, so its deselect reaches any field;
        // the event's other listeners only refresh hints and cursors.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildUi), "OnLayoutChanged")]
        private static bool OnLayoutChangedPrefix(BuildUi __instance, bool __runOriginal) {
            if (!__runOriginal || Enabled == null || !Enabled.Value || ZInput.IsGamepadActive()) { return true; }
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (selected == null || !IsTyping(selected)) { return true; }

            // What vanilla does here, minus the deselect.
            if (__instance.m_favoritesDropdown != null) { __instance.m_favoritesDropdown.Close(); }
            return false;
        }

        // The game's fields are TMP_InputField. Jotunn's GUIManager.CreateInputField builds the legacy kind,
        // so mods' fields can be either.
        private static bool IsTyping(GameObject selected) {
            TMP_InputField field = selected.GetComponent<TMP_InputField>();
            if (field != null) { return field.isFocused; }
            InputField legacyField = selected.GetComponent<InputField>();
            return legacyField != null && legacyField.isFocused;
        }
    }
}
