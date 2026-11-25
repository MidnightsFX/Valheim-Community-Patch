using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla's switch2 glyphs copy the Xbox letters, so every Switch face button hint names the opposite button.
    // Postfixes ZInput.GetBoundKeyString to swap A with B and X with Y in the switch2 face button glyphs.
    // With Switch glyphs, the A, B, X and Y hints name the button the action is really on.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class SwitchFaceButtonGlyphPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SwitchFaceButtonGlyphPatch),
                ValConfig.SectionCorrectness,
                "Fix Switch Button Glyphs",
                true,
                "With Switch button glyphs, makes the A, B, X and Y hints name the button the action is really on.");

            // Localization caches every text it has built, hints with their glyph included, so a hint
            // already on screen would keep the old letter until the cache drops it.
            Enabled.SettingChanged += (sender, args) => Localization.instance?.m_cache.EvictAll();
        }

        private static string Glyph(string name) => "<sprite=\"switch2\" name=\"" + name + "\">";

        // Vanilla's glyph for each position, and the Nintendo label at that position.
        private static readonly Dictionary<string, string> NintendoLabels = new Dictionary<string, string> {
            { Glyph("button_a"), Glyph("button_b") },
            { Glyph("button_b"), Glyph("button_a") },
            { Glyph("button_x"), Glyph("button_y") },
            { Glyph("button_y"), Glyph("button_x") },
        };

        // The only place the game builds a button glyph; ZInput.SwapFaceButtons, which would swap the
        // bindings instead, is false on PC. Switch glyphs show when picked in the gamepad settings, or with
        // Auto for a Switch Pro controller the game reads directly rather than through Steam Input.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetBoundKeyString))]
        private static void GetBoundKeyStringPostfix(ref string __result, bool __runOriginal) {
            if (!__runOriginal || Enabled == null || !Enabled.Value || __result == null) { return; }
            if (NintendoLabels.TryGetValue(__result, out string label)) { __result = label; }
        }
    }
}
