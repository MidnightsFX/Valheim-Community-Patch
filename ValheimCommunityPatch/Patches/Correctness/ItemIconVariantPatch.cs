using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // ItemData.GetIcon indexes the icons with the saved variant unchecked, so a stale stack throws from every UI panel.
    // Prefixes GetIcon to return the first icon (or null) for an out-of-range variant, logging each item once.
    // Inventory, tooltip and crafting panels keep working after an item mod is removed or an update shortens icons.
    // Provenance: same defect as ComfyMods/LetMePlay (GPL-3.0, redseiko), which repairs the shared item data instead.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ItemDrop.ItemData))]
    internal static class ItemIconVariantPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ItemIconVariantPatch),
                ValConfig.SectionCorrectness,
                "Fix Item Icon Crash",
                true,
                "Draws the first icon for an item whose saved icon variant no longer exists, so inventory " +
                "and crafting panels keep working after a game update or a removed item mod.");
        }

        private static readonly HashSet<string> Reported = new HashSet<string>();

        // Answers per call rather than repairing the shared item data, so nothing global changes.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemDrop.ItemData.GetIcon))]
        private static bool GetIconPrefix(ItemDrop.ItemData __instance, ref Sprite __result) {
            if (Enabled == null || !Enabled.Value) { return true; }

            Sprite[] icons = __instance.m_shared?.m_icons;
            if (icons != null && __instance.m_variant >= 0 && __instance.m_variant < icons.Length) { return true; }

            __result = icons != null && icons.Length > 0 ? icons[0] : null;

            string name = __instance.m_shared?.m_name ?? "<unknown item>";
            if (Reported.Add(name)) {
                Logger.LogWarning(
                    $"Item '{name}' has variant {__instance.m_variant} but only {icons?.Length ?? 0} icon(s); " +
                    "falling back to the first. This item was probably saved by a mod or game version that is no longer installed.");
            }

            return false;
        }
    }
}
