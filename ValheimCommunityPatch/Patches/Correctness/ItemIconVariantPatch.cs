using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Item Icon Crash: an item whose stored icon variant is out of range draws its first icon
    // instead of throwing from every UI panel.
    //
    // ItemDrop.ItemData.GetIcon indexes m_shared.m_icons with m_variant and no bounds check.
    // m_variant is saved per stack and can outlive the array (a removed item mod, a content update
    // that shortened the icon list, an item whose prefab never got icons), so every inventory,
    // tooltip and crafting panel that draws the item throws IndexOutOfRangeException.
    //
    // A prefix on GetIcon falls back to the first icon, or null when there are none, when the
    // variant is out of range, and logs each offending item once so the broken stack can be found.
    //
    // Client: every caller is UI. Provenance: same defect as ComfyMods/LetMePlay (GPL-3.0,
    // redseiko), which repairs the shared item data instead; this deliberately changes nothing
    // global.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(ItemDrop.ItemData))]
    internal static class ItemIconVariantPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ItemIconVariantPatch),
                ValConfig.SectionCorrectness,
                "Fix Item Icon Crash",
                true,
                "Guards the unchecked array index in ItemDrop.ItemData.GetIcon that throws when an " +
                "item's stored variant no longer matches its icon list - after a game update or a " +
                "removed item mod. Without it the inventory and crafting panels break.");
        }

        private static readonly HashSet<string> Reported = new HashSet<string>();

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
