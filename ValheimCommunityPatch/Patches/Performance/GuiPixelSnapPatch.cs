using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix GUI Pixel Snap Writes: the HUD's pixel-snap component only writes its offset when the
    // parent's size parity changed, instead of every frame.
    //
    // GuiPixelFix.LateUpdate nudges a full-stretch panel by one pixel whenever its parent has an odd
    // width or height, so the HUD lands on whole pixels. Every frame it takes the parity with a float
    // remainder, which Mono runs in software, then writes offsetMax, which Unity turns into four
    // native position and size calls, although the value only changes when the window does.
    //
    // A prefix reads the parent rect as vanilla does, takes the parity with integer arithmetic and
    // compares it with the live offsetMax: it skips the method when they agree and lets vanilla's own
    // body run when they do not. No state is kept, so a layout rebuild that moves the offset is put
    // right on the next frame, exactly as before.
    //
    // Client: the HUD, plus one instance each on the main menu and the loading screen.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(GuiPixelFix), "LateUpdate")]
    internal static class GuiPixelSnapPatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(GuiPixelSnapPatch));

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(GuiPixelFix __instance, bool __runOriginal) {
            if (!__runOriginal || ApiSwitch.Off) { return true; }

            RectTransform rectTransform = __instance.transform as RectTransform;
            if (rectTransform == null) { return true; }

            RectTransform parent = rectTransform.parent as RectTransform;
            if (parent == null) { return false; }

            Rect rect = parent.rect;
            float x = Odd(rect.width) ? 1f : 0f;
            float y = Odd(rect.height) ? 1f : 0f;

            // The getter composes anchoredPosition, sizeDelta and pivot in floats.
            Vector2 current = rectTransform.offsetMax;
            return !Mathf.Approximately(current.x, x) || !Mathf.Approximately(current.y, y);
        }

        // Vanilla's `v % 2f != 0f` without the float remainder: a value is even exactly when it
        // equals twice its truncated half, which holds for fractional and negative sizes too.
        private static bool Odd(float v) => v != 2f * (int)(v / 2f);
    }
}
