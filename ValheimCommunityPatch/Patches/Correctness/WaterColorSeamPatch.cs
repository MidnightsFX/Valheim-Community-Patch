using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Water Color Seams: shallow and deep water color blends smoothly across zone borders instead
    // of changing in a hard straight line along the 64 m grid.
    //
    // Each zone's water tile carries the sea depth at its four corners, and the water shader blends
    // them twice: with the mesh UV for wave height, and with TRANSFORM_TEX(uv, _MainTex) for color.
    // The shader declares no _MainTex and the material sets none, so _MainTex_ST is zero, the color UV
    // is (0, 0) everywhere, and the whole tile is colored by its south-west corner. Neighbouring tiles
    // whose south-west corners differ meet in a visible line, which is common along shores, while the
    // waves across the same border stay continuous.
    //
    // A postfix on WaterVolume.SetupMaterial, which runs once when a tile starts, gives that tile's
    // material the identity _MainTex_ST, so color uses the same blend as the waves. Four corners 64 m
    // apart are coarse, and blended properly one shallow corner gives open sea across the whole tile the
    // sandy shallow-water color, so the postfix also moves the material's shallow color part of the way
    // to its deep color. Every tile gets the same colors, so the result stays seamless. The depth array
    // is left alone: the shader's wave height reads it and never reads a color, so the visible waves
    // still match the ones boats and swimmers ride. Fixed-depth water (caves, hot springs) is left alone,
    // and the postfix stands down if the shader declares a _MainTex, whose own tiling would then be right.
    //
    // Client: material state is rendering state.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(WaterVolume))]
    internal static class WaterColorSeamPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ShoreTint;

        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexSt = Shader.PropertyToID("_MainTex_ST");
        private static readonly int Shallow = Shader.PropertyToID("_ColorBottomShallow");
        private static readonly int Deep = Shader.PropertyToID("_ColorBottom");
        private static readonly int AshlandsShallow = Shader.PropertyToID("_AshlandsColorBottomShallow");
        private static readonly int AshlandsDeep = Shader.PropertyToID("_AshlandsColorBottom");
        private static readonly Vector4 IdentitySt = new Vector4(1f, 1f, 0f, 0f);

        // The game's shallow colors per material instance, so a second SetupMaterial call (another mod,
        // a diagnostic) tints from them again instead of compounding.
        private static readonly ConditionalWeakTable<Material, VanillaShallow> Vanilla =
            new ConditionalWeakTable<Material, VanillaShallow>();

        private sealed class VanillaShallow {
            internal Color Shallow;
            internal Color AshlandsShallow;
        }

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(WaterColorSeamPatch),
                ValConfig.SectionCorrectness,
                "Fix Water Color Seams",
                true,
                "Blends shallow and deep water color across zone borders. Vanilla colors each 64m water tile " +
                "by the depth at one of its corners, so near shores the sea changes color in a hard straight " +
                "line along the zone grid. Applies to water tiles that load after it is turned on.");

            ShoreTint = ValConfig.BindServerConfig(
                ValConfig.SectionCorrectness,
                "Water Color Shore Tint",
                0.75f,
                "With Fix Water Color Seams on, how far the sandy shallow-water color is moved towards the " +
                "deep-sea color, from 0 to 1. 0 is the game's own color, where one shallow corner gives a " +
                "whole 64m tile of open sea a sandy tint; 1 uses the deep color everywhere. Thin water still " +
                "shows the real seabed through it. Changes color only, never waves. Applies to water tiles " +
                "that load afterwards.",
                advanced: true,
                valMin: 0f,
                valMax: 1f);
        }

        [HarmonyPostfix]
        [HarmonyPatch("SetupMaterial")]
        private static void SetupMaterialPostfix(WaterVolume __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            MeshRenderer surface = __instance.m_waterSurface;
            if (surface == null) { return; }

            // The per-renderer instance vanilla has just written _depth to.
            Material material = surface.material;
            if (material.HasProperty(MainTex)) { return; }

            material.SetVector(MainTexSt, IdentitySt);

            // Fixed-depth water renders one flat color either way and has no heightmap corners.
            if (__instance.m_heightmap == null || __instance.m_forceDepth >= 0f) { return; }
            if (!material.HasProperty(Shallow) || !material.HasProperty(Deep)) { return; }

            if (!Vanilla.TryGetValue(material, out VanillaShallow vanilla)) {
                vanilla = new VanillaShallow {
                    Shallow = material.GetColor(Shallow),
                    AshlandsShallow = material.HasProperty(AshlandsShallow) ? material.GetColor(AshlandsShallow) : default
                };
                Vanilla.Add(material, vanilla);
            }

            float tint = ShoreTint != null ? Mathf.Clamp01(ShoreTint.Value) : 0f;
            material.SetColor(Shallow, Color.Lerp(vanilla.Shallow, material.GetColor(Deep), tint));
            if (material.HasProperty(AshlandsShallow) && material.HasProperty(AshlandsDeep)) {
                material.SetColor(AshlandsShallow, Color.Lerp(vanilla.AshlandsShallow, material.GetColor(AshlandsDeep), tint));
            }
        }
    }
}
