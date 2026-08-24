using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: the burning-area lookups test only the cached bounds, never whether the area is
    // actually lit:
    //
    //   public static bool IsPointPlus025InsideBurningArea(Vector3 p) {
    //     foreach (KeyValuePair<Bounds, EffectArea> burningArea in EffectArea.s_BurningAreas)
    //       if (burningArea.Key.Contains(p)) return true;
    //     return false;
    //   }
    //
    // EffectArea.Awake registers the area into s_BurningAreas, and only OnDestroy removes it.
    // Fireplace.UpdateState puts the fire out by calling m_enabledObject.SetActive(false), which
    // deactivates the EffectArea component but leaves its entry in the list forever. Player-visible
    // symptom: a burnt-out or unlit fireplace still counts as a heat source, so cooking stations over
    // a dead fire keep working.
    //
    // Fix: postfix both lookups and require the matched area to be active. The extra walk only runs
    // when vanilla already reported a hit, so the common negative case costs nothing.
    //
    // Client: the callers are CookingStation.IsFireLit and CraftingStation.CheckFire, component
    // behaviours on player-built pieces owned by whichever player is standing near them.
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

            // Vanilla returned the first area whose bounds matched, which may be an unlit one shadowing
            // a lit one at the same spot. Prefer any lit area still covering the point.
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
