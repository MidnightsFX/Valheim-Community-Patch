using System;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Cloth Wind Shelter Errors: stops the "MagicaCloth component not found" error the Root Crown
    // logs each time one is created.
    //
    // PlayerClothWindShelter, new in the Deep North update, turns off the wind on a cape, hair or hat's
    // cloth simulation while its wearer is in shelter. Its Awake looks for the cloth component on its
    // own object; when there is none it logs an error with a stack trace and returns before the line
    // that disables it, so its Update then runs every frame with nothing to do. The Root Crown's hat
    // model carries the component without the cloth, while the 59 other vanilla prefabs that carry it
    // are authored correctly. A crown is created whenever one is dropped, put on a stand, or worn by a
    // player coming into range.
    //
    // A prefix on Awake disables the component and skips vanilla's Awake when its object has no cloth
    // component, writing the object's name to the debug log instead. It tests for the component rather
    // than naming the prefab, so a mod's copy of the crown and any later stray are caught too. Nothing
    // is lost: VisEquipment only gives the wearer to a shelter it found on a cloth component.
    //
    // Both: the error fires wherever the item is created, and a dedicated server creates the objects
    // around its own reference point too.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(PlayerClothWindShelter))]
    internal static class ClothWindShelterPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ClothWindShelterPatch),
                ValConfig.SectionCorrectness,
                "Fix Cloth Wind Shelter Errors",
                true,
                "Stops the 'MagicaCloth component not found' error logged each time a Root Crown is " +
                "created: dropped, put on a stand, or worn by a player coming into range. Its hat model " +
                "carries the wind shelter component without the cloth it controls. The messages are " +
                "still visible with EnableDebugMode on.");
        }

        // The component Awake looks for, read off the field it stores it in, so VCP needs no
        // reference to the cloth library. Null if an update renames the field; the prefix then
        // stands down.
        private static readonly Type ClothType =
            AccessTools.Field(typeof(PlayerClothWindShelter), "m_cloth")?.FieldType;

        [HarmonyPrefix]
        [HarmonyPatch("Awake")]
        private static bool AwakePrefix(PlayerClothWindShelter __instance) {
            if (Enabled == null || !Enabled.Value || ClothType == null) { return true; }
            if (__instance.TryGetComponent(ClothType, out _)) { return true; }

            __instance.enabled = false;

            if (Logger.DebugEnabled) {
                Logger.LogDebug(
                    $"PlayerClothWindShelter on '{__instance.name}' under '{__instance.transform.root.name}' " +
                    "has no cloth component; disabled it.");
            }

            return false;
        }
    }
}
