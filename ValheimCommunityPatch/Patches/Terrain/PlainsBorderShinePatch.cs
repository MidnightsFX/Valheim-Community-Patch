using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Mistlands' terrain color code is Black Forest's plus Plains', so their blend is drawn with Mistlands' wet gloss.
    // Remaps vertex colors across a dead zone in the per-tile color pass and a GetBiomeColor(ix, iy) postfix.
    // Dirt paths where black forest meets plains no longer look wet in the sun; a narrow meadows strip separates them.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class PlainsBorderShinePatch {
        private const string FixName = "Fix Plains Border Shine";

        internal static FixToggle Enabled;
        internal static ConfigEntry<float> Sharpness;

        private const float DeadZone = BiomeColorAliasPatch.DeadZone;

        // The most one triangle can move the split, with Color32 rounding on three channels. A black
        // forest bridge starts this far from the plains edge of the dead zone, so it never meets plains.
        private const float TriangleStep = 0.15f;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(PlainsBorderShinePatch),
                ValConfig.SectionTerrain,
                FixName,
                true,
                "Stops ground where black forest blends into plains being drawn as glossy as wet Mistlands " +
                "ground, putting a narrow meadows strip between them (see Black Forest Plains Blend Sharpness).");

            Sharpness = ValConfig.BindServerConfig(
                ValConfig.SectionTerrain,
                "Black Forest Plains Blend Sharpness",
                3f,
                "How quickly black forest and plains ground give way to each other with Fix Plains Border Shine " +
                "on: 1 blends across the whole 64 m tile around a wide meadows strip, higher narrows the strip.",
                advanced: true,
                valMin: 1f,
                valMax: 8f);

            Enabled.SettingChanged += (sender, args) => BiomeColorAliasPatch.RebuildLoadedTerrain();
            Sharpness.SettingChanged += (sender, args) => BiomeColorAliasPatch.RebuildLoadedTerrain();
        }

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(PlainsBorderShinePatch),
            AccessTools.DeclaredMethod(typeof(Heightmap), "RebuildRenderMesh"),
            transpilers: false,
            owners => $"Terrain mesh building is changed by {owners}, so '{FixName}' stands down and that " +
                      "mod's terrain colors apply.");

        // True while this fix colors near terrain, Fix Swamp Plains Shore Seams' part included: with that fix
        // on, swamp joins the black forest side here and its own remap stands aside.
        internal static bool Handles => Enabled != null && Enabled.Value && !Takeover.TakenOver;

        internal static float SharpnessValue =>Sharpness != null ? Mathf.Max(1f, Sharpness.Value) : 1f;

        // For callers outside tile rebuilds, which run Remap in BiomeColorAliasPatch's per-tile color pass.
        [HarmonyPostfix]
        [HarmonyPatch("GetBiomeColor", typeof(float), typeof(float))]
        private static void GetBiomeColorPostfix(Heightmap __instance, float ix, float iy, ref Color __result) {
            // No swamp, forest or plains channel: the remap would return it unchanged.
            if (__result.r <= 0f && __result.b <= 0f && __result.a <= 0f) { return; }
            if (__instance.IsDistantLod || !Handles) { return; }

            BiomeColorAliasPatch.ReadCorners(__instance);

            bool swampFix = BiomeColorAliasPatch.Enabled != null && BiomeColorAliasPatch.Enabled.Value;
            Remap(ref __result, BiomeColorAliasPatch.ContributingCorners(ix, iy), SharpnessValue,
                swampFix, BiomeColorAliasPatch.SharpnessValue, BiomeColorAliasPatch.BridgeValue);
        }

        // Mistlands (0,0,1,1) decodes from any Black Forest (0,0,1,0) and Plains (0,0,0,1) blend, and plains
        // ground reaches full Mistlands gloss from a weight of about a quarter. So no triangle may hold both a
        // black forest and a plains vertex; meadows, the only ground with neither channel, fills the gap.
        internal static void Remap(ref Color color, int contributing, float sharpness,
                                   bool swampFix, float swampSharpness, BiomeColorAliasPatch.BridgeGround ground) {
            if ((contributing & BiomeColorAliasPatch.ProtectedCorners) != 0) { return; }
            if ((contributing & BiomeColorAliasPatch.PlainsCorners) == 0) { return; }

            bool withSwamp = swampFix && (contributing & BiomeColorAliasPatch.SwampCorners) != 0;
            if (!withSwamp && (contributing & BiomeColorAliasPatch.ForestCorners) == 0) { return; }

            // The black forest side, with swamp when the swamp fix is on; without it swamp stays as is.
            float swamp = withSwamp ? color.r : 0f;
            float side = swamp + color.b;
            float total = side + color.a;
            if (total <= 0f) { return; }

            // Swamp's part of that side follows the swamp fix's sharpness and bridge, so the remap
            // changes smoothly where a swamp corner starts to contribute.
            float swampShare = side > 0f ? swamp / side : 0f;
            float ramp = Mathf.Lerp((1f - DeadZone) / sharpness, (1f - DeadZone) / swampSharpness, swampShare);

            float d = side - color.a;
            float sideOut = Mathf.Clamp((d - DeadZone) / ramp, 0f, total);
            float plains = Mathf.Clamp((-d - DeadZone) / ramp, 0f, total);
            float bridge = (total - sideOut - plains) * swampShare;

            color.a = plains;
            color.b = sideOut * (1f - swampShare);
            if (!withSwamp) { return; }

            color.r = sideOut * swampShare;

            if (ground == BiomeColorAliasPatch.BridgeGround.Ashlands) {
                // Both channels on purpose, as the swamp fix's own Ashlands bridge. Where a black forest
                // corner also reaches that strip, part of it still reads as Mistlands.
                float ash = Mathf.Min(bridge, BiomeColorAliasPatch.AshlandsCap);
                color.r += ash;
                color.a += ash;
                return;
            }

            // Black forest only where no triangle can also hold plains, fading in across the dead zone.
            color.b += bridge * Mathf.Clamp01((d - (TriangleStep - DeadZone)) / DeadZone);
        }
    }
}
