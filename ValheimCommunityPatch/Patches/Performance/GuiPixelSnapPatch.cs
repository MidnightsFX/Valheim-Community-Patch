using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix GUI Pixel Snap Writes: GuiPixelFix writes the HUD's pixel-snap offset every frame, costing four native calls.
    // Prefixes GuiPixelFix.LateUpdate to compute the parity in integers and run vanilla only when offsetMax differs.
    // The HUD still lands on whole pixels, without a native write every frame.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(GuiPixelFix), "LateUpdate")]
    internal static class GuiPixelSnapPatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(GuiPixelSnapPatch));

        // No state is kept: comparing with the live offsetMax puts a layout rebuild that moves it
        // right on the next frame.
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

        // Vanilla's `v % 2f != 0f` without the float remainder, which Mono runs in software: a value is even exactly
        // when it equals twice its truncated half, which holds for fractional and negative sizes too.
        private static bool Odd(float v) => v != 2f * (int)(v / 2f);
    }
}
