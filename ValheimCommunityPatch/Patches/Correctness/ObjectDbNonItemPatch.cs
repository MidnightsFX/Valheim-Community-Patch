using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Non-Item ObjectDB Entries: removes prefabs that are not items from the game's item list.
    //
    // ObjectDB.m_items is the item list, and mods read an ItemDrop off every entry. The 2026-09-09 update
    // also lists three prefabs with no ItemDrop: PropFeastDeepNorth, SnowRoller and FrozenKing_Summon.
    // Vanilla only needs the component for its by-data lookup, but GetItemPrefab still resolves their
    // names, so a mod that walks the list, or takes a non-null GetItemPrefab to mean "this is an item",
    // throws or logs errors.
    //
    // A prefix on ObjectDB.UpdateRegisters, which Awake and CopyOtherDB both run, removes null entries and
    // entries without an ItemDrop before the lookups are built. It tests the component rather than naming
    // prefabs, so strays from a later update are caught too. Every vanilla GetItemPrefab caller resolves a
    // name or hash taken from an item, and the prefabs stay registered in ZNetScene, so they still spawn.
    //
    // Both: a dedicated server builds an ObjectDB too, and server-side mods read it.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ObjectDB))]
    internal static class ObjectDbNonItemPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ObjectDbNonItemPatch),
                ValConfig.SectionCorrectness,
                "Fix Non-Item ObjectDB Entries",
                true,
                "Removes prefabs that are not items (no ItemDrop component) from the game's item list, ObjectDB, " +
                "when it is built. The 2026-09-09 update lists three - PropFeastDeepNorth, SnowRoller and " +
                "FrozenKing_Summon - and mods that treat every entry as an item throw or log errors on them. " +
                "They can still be spawned. Applies from the next world load.");
        }

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
