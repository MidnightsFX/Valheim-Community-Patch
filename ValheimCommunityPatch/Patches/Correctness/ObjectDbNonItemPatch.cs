using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Since the 2026-09-09 update ObjectDB.m_items holds three non-items, which break mods that read each entry as one.
    // Prefixes ObjectDB.UpdateRegisters to drop null entries and entries without an ItemDrop before lookups are built.
    // Mods can walk the item list safely, and the prefabs stay registered in ZNetScene, so they still spawn.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ObjectDB))]
    internal static class ObjectDbNonItemPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ObjectDbNonItemPatch),
                ValConfig.SectionCorrectness,
                "Fix Non-Item ObjectDB Entries",
                true,
                "From the next world load, removes prefabs that are not items from the game's item list, " +
                "where they break mods that treat every entry as an item.");
        }

        // Awake and CopyOtherDB both run UpdateRegisters. Tests the component rather than naming the three
        // (PropFeastDeepNorth, SnowRoller, FrozenKing_Summon), so strays from later updates go too. Safe for
        // vanilla: every GetItemPrefab caller resolves a name or hash taken from an item.
        [HarmonyPrefix]
        [HarmonyPatch("UpdateRegisters")]
        private static void UpdateRegistersPrefix(ObjectDB __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            List<GameObject> items = __instance.m_items;
            if (items == null) { return; }

            List<string> removed = null;
            for (int i = items.Count - 1; i >= 0; i--) {
                GameObject prefab = items[i];
                if (prefab != null && prefab.GetComponent<ItemDrop>() != null) { continue; }

                // A destroyed prefab compares equal to null, and reading its name would throw.
                (removed ??= new List<string>()).Add(prefab != null ? prefab.name : "null");
                items.RemoveAt(i);
            }

            if (removed != null) {
                Logger.LogInfo($"Removed {removed.Count} non-item entries from ObjectDB: {string.Join(", ", removed)}.");
            }
        }
    }
}
