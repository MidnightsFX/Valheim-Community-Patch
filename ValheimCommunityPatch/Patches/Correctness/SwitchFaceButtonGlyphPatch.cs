using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Switch Button Glyphs: with Switch glyphs, a face button hint names the button the action is
    // really on.
    //
    // Valheim binds gamepad actions to button positions, the Input System's buttonSouth, buttonEast,
    // buttonWest and buttonNorth: Use is the left button, the inventory the top one. Each binding's hint
    // comes from ZInput.GetBoundKeyString, which looks the position up in s_gamepadSpriteMap, and that
    // table's switch2 column copies the Xbox letters: south A, east B, west X, north Y. A Nintendo
    // controller has them the other way round, south B, east A, west Y and north X, so with Switch
    // glyphs every face button hint names the opposite button. "X Use" sits on the left button, while
    // the button labelled X opens the inventory; "A" for jump points at the button labelled B. The
    // switch2 glyphs are plain letters, so the sprite name is exactly the label shown.
    //
    // A postfix swaps A with B and X with Y in the switch2 face button glyph the method returns, the only
    // place the game builds a button glyph. Shoulders, triggers, sticks, the d-pad, plus and minus are
    // positioned the same on both controllers and are left as they are, as are the Xbox and PlayStation
    // glyphs. ZInput.SwapFaceButtons, which would swap the bindings themselves, is false on PC, so the
    // positions above are the ones the game uses.
    //
    // Switch glyphs show when they are picked in the gamepad settings, or with Auto when the game reads a
    // Switch Pro controller directly (ZInput.ConnectedGamepadType). A controller that reaches the game as
    // an Xbox controller, as Steam Input presents one, gets Xbox glyphs with Auto.
    //
    // Client: button hints are only drawn on a client.
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
                "With Switch button glyphs, the A, B, X and Y hints name the button the action is really on. " +
                "Vanilla gives the Switch glyphs the Xbox letters, so every face button hint names the " +
                "opposite button: the X hint for Use is on the button labelled Y.");
        }

        private static string Glyph(string name) => "<sprite=\"switch2\" name=\"" + name + "\">";

        // Vanilla's glyph for each position, and the Nintendo label at that position.
        private static readonly Dictionary<string, string> NintendoLabels = new Dictionary<string, string> {
            { Glyph("button_a"), Glyph("button_b") },
            { Glyph("button_b"), Glyph("button_a") },
            { Glyph("button_x"), Glyph("button_y") },
            { Glyph("button_y"), Glyph("button_x") },
        };

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetBoundKeyString))]
        private static void GetBoundKeyStringPostfix(ref string __result, bool __runOriginal) {
            if (!__runOriginal || Enabled == null || !Enabled.Value || __result == null) { return; }
            if (NintendoLabels.TryGetValue(__result, out string label)) { __result = label; }
        }
    }
}
