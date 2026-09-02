using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Require Lit Fire: an unlit or burnt-out fireplace no longer counts as a heat source.
    //
    // EffectArea.IsPointPlus025InsideBurningArea and GetBurningAreaPointPlus025 test only the
    // cached bounds in s_BurningAreas. Fireplace.UpdateState puts a fire out by deactivating the
    // EffectArea object, which leaves its entry in that list, so cooking stations over a dead fire
    // keep cooking.
    //
    // Postfixes on both lookups require the matching area to be active and enabled. They only do
    // work when vanilla already reported a hit, so the common miss costs nothing.
    //
    // Client: the callers (CookingStation, CraftingStation) run on whichever player owns the piece.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(EffectArea))]
    internal static class UnlitFireCookingPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(UnlitFireCookingPatch),
                ValConfig.SectionCorrectness,
                "Require Lit Fire",
                true,
                "Requires a fire to actually be burning before it counts as a heat source. Vanilla only " +
                "checks the fireplace's bounds, so an unlit or burnt-out fire still cooks.");
        }

        private static bool IsLit(EffectArea area) => area != null && area.isActiveAndEnabled;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(EffectArea.IsPointPlus025InsideBurningArea))]
        private static void IsPointPlus025InsideBurningAreaPostfix(Vector3 p, ref bool __result) {
            if (Enabled == null || !Enabled.Value || !__result) { return; }

            List<KeyValuePair<Bounds, EffectArea>> areas = EffectArea.s_BurningAreas;
            for (int i = 0; i < areas.Count; i++) {
                if (IsLit(areas[i].Value) && areas[i].Key.Contains(p)) { return; }
            }

            __result = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch("GetBurningAreaPointPlus025")]
        private static void GetBurningAreaPointPlus025Postfix(Vector3 p, ref EffectArea __result) {
            if (Enabled == null || !Enabled.Value || __result == null) { return; }
            if (IsLit(__result)) { return; }

            // Vanilla returned the first area whose bounds matched, which may be an unlit one
            // shadowing a lit one at the same spot.
            List<KeyValuePair<Bounds, EffectArea>> areas = EffectArea.s_BurningAreas;
            for (int i = 0; i < areas.Count; i++) {
                if (IsLit(areas[i].Value) && areas[i].Key.Contains(p)) {
                    __result = areas[i].Value;
                    return;
                }
            }

            __result = null;
        }
    }
}
