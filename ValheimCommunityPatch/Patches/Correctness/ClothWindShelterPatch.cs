using System;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // The Root Crown's hat has a cloth wind shelter but no cloth, so it logs an error each time a crown is created.
    // Prefixes PlayerClothWindShelter.Awake to disable the component and skip Awake when no cloth component is present.
    // Root Crowns no longer log "MagicaCloth component not found"; the object's name goes to the debug log instead.
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
                "Stops the 'MagicaCloth component not found' error a Root Crown logs each time one is created; " +
                "the messages still show with EnableDebugMode on.");
        }

        // The component Awake looks for, read off the field it stores it in, so VCP needs no
        // reference to the cloth library. Null if an update renames the field; the prefix then
        // stands down.
        private static readonly Type ClothType =
            AccessTools.Field(typeof(PlayerClothWindShelter), "m_cloth")?.FieldType;

        // Tests for the component rather than naming the crown, so a mod's copy and any later stray are caught too.
        // Vanilla logs and leaves the shelter on, updating every frame with nothing to do (1.0.21 only named the object
        // in the error). Disabling loses nothing: VisEquipment only hands the wearer to a shelter on a cloth component.
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
