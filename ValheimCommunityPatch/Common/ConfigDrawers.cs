using BepInEx.Configuration;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimCommunityPatch.Common {
    // Shared IMGUI widgets for ConfigurationManager CustomDrawers.
    //
    // The pattern these support: collapse many related ConfigEntries into a single ConfigurationManager
    // row backed by a CustomDrawer. The underlying ConfigEntries are untouched (same keys, same sync) -
    // only their display changes: all but one "anchor" entry get Browsable = false so the manager no
    // longer lays each of them out every frame, which is what caused the lag.
    internal static class ConfigDrawHelpers {
        // Edit buffers so partial typing in text/number fields doesn't immediately overwrite the live value.
        private static readonly Dictionary<object, string> TextBuffer = new Dictionary<object, string>();
        // In-progress slider values; committed to the ConfigEntry only on mouse release to avoid a disk
        // write (SaveOnConfigSet is true) on every frame of a drag.
        private static readonly Dictionary<object, float> SliderPending = new Dictionary<object, float>();

        private static GUIStyle _headerButton;
        private static GUIStyle _groupLabel;
        private static GUIStyle _dim;

        internal static GUIStyle HeaderButton => _headerButton;
        internal static GUIStyle Dim => _dim;

        internal static void EnsureStyles() {
            if (_headerButton != null) { return; }
            _headerButton = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            _groupLabel = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _dim = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic, fontSize = 11 };
        }

        // Fetches the Jotunn ConfigurationManagerAttributes already attached by ValConfig.BindServerConfig.
        internal static ConfigurationManagerAttributes GetAttributes(ConfigEntryBase entry) =>
            entry?.Description?.Tags?.OfType<ConfigurationManagerAttributes>().FirstOrDefault();

        // Hides an entry from the Configuration Manager window without affecting saving or server sync.
        internal static void Hide(ConfigEntryBase entry) {
            ConfigurationManagerAttributes attr = GetAttributes(entry);
            if (attr != null) { attr.Browsable = false; }
        }

        internal static void GroupHeader(string text) {
            GUILayout.Space(4f);
            GUILayout.Label(text, _groupLabel);
        }

        private static bool EnterPressed() =>
            Event.current.type == EventType.KeyDown &&
            (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

        internal static void DrawBool(string label, ConfigEntry<bool> cfg) {
            bool v = GUILayout.Toggle(cfg.Value, " " + label);
            if (v != cfg.Value) { cfg.Value = v; }
        }

        // Free-text string field that commits on Enter or when focus leaves the field (never per keystroke,
        // so handlers like "crafting station changed" don't fire/warn on every character).
        internal static void DrawString(string label, ConfigEntry<string> cfg) {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(230f));
            string ctrl = "str_" + cfg.GetHashCode();
            GUI.SetNextControlName(ctrl);
            bool focused = GUI.GetNameOfFocusedControl() == ctrl;
            string shown = focused && TextBuffer.TryGetValue(cfg, out string buf) ? buf : cfg.Value;
            string typed = GUILayout.TextField(shown, GUILayout.Width(230f));
            if (focused) {
                TextBuffer[cfg] = typed;
                if (EnterPressed() && cfg.Value != typed) { cfg.Value = typed; }
            } else if (TextBuffer.TryGetValue(cfg, out string pending)) {
                if (cfg.Value != pending) { cfg.Value = pending; }
                TextBuffer.Remove(cfg);
            }
            GUILayout.EndHorizontal();
        }

        // Cycler for string entries restricted to an AcceptableValueList (damage modifiers, piece category).
        internal static void DrawChoice(string label, ConfigEntry<string> cfg) {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(230f));
            if (cfg.Description.AcceptableValues is AcceptableValueList<string> list && list.AcceptableValues.Length > 0) {
                string[] vals = list.AcceptableValues;
                int idx = Array.IndexOf(vals, cfg.Value);
                if (idx < 0) { idx = 0; }
                if (GUILayout.Button("◄", GUILayout.Width(26f))) { cfg.Value = vals[(idx - 1 + vals.Length) % vals.Length]; }
                GUILayout.Label(cfg.Value, GUILayout.Width(160f));
                if (GUILayout.Button("►", GUILayout.Width(26f))) { cfg.Value = vals[(idx + 1) % vals.Length]; }
            } else {
                GUILayout.Label(cfg.Value);
            }
            GUILayout.EndHorizontal();
        }

        internal static void DrawFloat(string label, ConfigEntry<float> cfg) {
            float min = 0f, max = Mathf.Max(100f, cfg.Value);
            if (cfg.Description.AcceptableValues is AcceptableValueRange<float> r) { min = r.MinValue; max = r.MaxValue; }
            float v = DrawSliderRow(label, cfg, cfg.Value, min, max, false);
            if (v != cfg.Value) { cfg.Value = Mathf.Clamp(v, min, max); }
        }

        internal static void DrawInt(string label, ConfigEntry<int> cfg) {
            float min = 0f, max = Mathf.Max(100f, cfg.Value);
            if (cfg.Description.AcceptableValues is AcceptableValueRange<int> r) { min = r.MinValue; max = r.MaxValue; }
            float v = DrawSliderRow(label, cfg, cfg.Value, min, max, true);
            int iv = Mathf.Clamp(Mathf.RoundToInt(v), (int)min, (int)max);
            if (iv != cfg.Value) { cfg.Value = iv; }
        }

        // Slider (deferred-commit on mouse release) + a buffered numeric text box (commit on Enter/blur).
        // Returns the value the caller should write; never writes the ConfigEntry itself.
        private static float DrawSliderRow(string label, object key, float current, float min, float max, bool isInt) {
            float result = current;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(230f));

            float shown = SliderPending.TryGetValue(key, out float pend) ? pend : current;
            float slid = GUILayout.HorizontalSlider(shown, min, max, GUILayout.Width(150f));
            if (Mathf.Abs(slid - shown) > Mathf.Epsilon) { SliderPending[key] = slid; }
            if (SliderPending.TryGetValue(key, out float dragging)) {
                result = dragging;
                if (!Input.GetMouseButton(0)) { SliderPending.Remove(key); }
            }

            // Numeric text box for exact entry.
            string ctrl = "num_" + key.GetHashCode();
            GUI.SetNextControlName(ctrl);
            bool focused = GUI.GetNameOfFocusedControl() == ctrl;
            string live = isInt ? Mathf.RoundToInt(result).ToString() : result.ToString("0.###");
            string shownText = focused && TextBuffer.TryGetValue(key, out string buf) ? buf : live;
            string typed = GUILayout.TextField(shownText, GUILayout.Width(70f));
            if (focused) {
                TextBuffer[key] = typed;
                if (EnterPressed() && float.TryParse(typed, out float parsed)) { result = parsed; }
            } else if (TextBuffer.TryGetValue(key, out string pendingText)) {
                if (float.TryParse(pendingText, out float p)) { result = p; }
                TextBuffer.Remove(key);
            }

            GUILayout.Label($"[{(isInt ? min.ToString() : min.ToString("0.#"))} - {(isInt ? max.ToString() : max.ToString("0.#"))}]", _dim, GUILayout.Width(90f));
            GUILayout.EndHorizontal();
            return result;
        }
    }
}
