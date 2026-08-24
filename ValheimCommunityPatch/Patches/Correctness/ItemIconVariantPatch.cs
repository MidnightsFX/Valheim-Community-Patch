using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: ItemDrop.ItemData.GetIcon indexes the shared icon array with no bounds check:
    //
    //   public Sprite GetIcon() => this.m_shared.m_icons[this.m_variant];
    //
    // m_variant is stored per item stack and comes back off the save, so it can outlive the array it
    // indexes: a removed item mod, a content update that shortened m_icons, or a stack written by
    // something that minted its own variant. SharedData.m_icons also defaults to Array.Empty<Sprite>(),
    // so an item whose prefab never got icons throws on variant 0.
    //
    // Player-visible symptom: an IndexOutOfRangeException from any of the twenty call sites, all of
    // them inventory, tooltip or crafting UI - so the panel breaks and the log fills, the same
    // failure mode as Fix Recipe Amount Crash.
    //
    // Fix: fall back to the first icon when the variant is out of range. Each offending item is
    // reported once, by name, so the broken stack can actually be found.
    //
    // Provenance: same defect as ComfyMods/LetMePlay (GPL-3.0, redseiko). Deliberately a smaller fix
    // than that mod's: it repairs the item by resizing m_icons, substituting a hammer sprite and
    // overwriting m_name, m_description, m_itemType and m_crafterName. m_shared is shared by every
    // instance of that item, so that mutates global state and changes what the player sees - a
    // recovery tool rather than a bug fix.
    //
    // Client: every caller of GetIcon is inventory, tooltip or crafting UI.
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

        // Reported once per item so a broken stack is identifiable without the log filling up again
        // with what this fix exists to stop.
        private static readonly HashSet<string> Reported = new HashSet<string>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemDrop.ItemData.GetIcon))]
        private static bool GetIconPrefix(ItemDrop.ItemData __instance, ref Sprite __result) {
            if (Enabled == null || !Enabled.Value) { return true; }

            Sprite[] icons = __instance.m_shared?.m_icons;
            if (icons != null && __instance.m_variant >= 0 && __instance.m_variant < icons.Length) { return true; }

            // Variant 0 is what every item with a single icon uses, so it is the one index that is
            // safe to assume. With no icons at all there is nothing to return but null, which the UI
            // renders as an empty slot rather than throwing.
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
