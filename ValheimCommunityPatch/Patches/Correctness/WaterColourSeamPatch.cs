using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Water Colour Seams: shallow and deep water colour blends smoothly across zone borders instead
    // of changing in a hard straight line along the 64 m grid.
    //
    // Each zone's water tile carries the sea depth at its four corners, and the water shader blends
    // them twice: with the mesh UV for wave height, and with TRANSFORM_TEX(uv, _MainTex) for colour.
    // The shader declares no _MainTex and the material sets none, so _MainTex_ST is zero, the colour UV
    // is (0, 0) everywhere, and the whole tile is coloured by its south-west corner. Neighbouring tiles
    // whose south-west corners differ meet in a visible line, which is common along shores, while the
    // waves across the same border stay continuous.
    //
    // A postfix on WaterVolume.SetupMaterial, which runs once when a tile starts, gives that tile's
    // material the identity _MainTex_ST, so colour uses the same blend as the waves. Four corners 64 m
    // apart are coarse, and blended properly one shallow corner tints deep water across the whole tile,
    // so the postfix also renders the depth as Clamp01(depth * scale) to reach the deep-water look sooner.
    // Neighbours share corners, so the result stays seamless. Physics keeps the vanilla depth, so the
    // visible waves in shallow water run up to scale times taller than the ones boats and swimmers ride.
    // Fixed-depth water (caves, hot springs) is left alone, and the postfix stands down if the shader
    // declares a _MainTex, whose own tiling would then be the right one.
    //
    // Client: material state is rendering state.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(WaterVolume))]
    internal static class WaterColourSeamPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> DepthScale;

        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexSt = Shader.PropertyToID("_MainTex_ST");
        private static readonly int Depth = Shader.PropertyToID("_depth");
        private static readonly Vector4 IdentitySt = new Vector4(1f, 1f, 0f, 0f);

        // SetFloatArray copies the values, so one buffer serves every tile.
        private static readonly float[] RenderedDepth = new float[4];

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(WaterColourSeamPatch),
                ValConfig.SectionCorrectness,
                "Fix Water Colour Seams",
                true,
                "Blends shallow and deep water colour across zone borders. Vanilla colours each 64m water tile " +
                "by the depth at one of its corners, so near shores the sea changes colour in a hard straight " +
                "line along the zone grid. Applies to water tiles that load after it is turned on.");

            DepthScale = ValConfig.BindServerConfig(
                ValConfig.SectionCorrectness,
                "Water Colour Depth Scale",
                2f,
                "With Fix Water Colour Seams on, how much sooner water shading reaches its deep-water look. 1 is " +
                "the game's own depth blend, where one shallow corner tints deep water across a whole 64m tile; " +
                "2 reaches the deep-water look at 5m instead of 10m. Values above 1 also make the visible waves " +
                "in shallow water taller than the waves boats and swimmers ride, by up to this factor. Applies " +
                "to water tiles that load afterwards.",
                advanced: true,
                valMin: 1f,
                valMax: 5f);
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

            // Fixed-depth water renders one flat value either way and has no heightmap corners.
            float scale = DepthScale != null ? DepthScale.Value : 1f;
            if (scale <= 1f || __instance.m_heightmap == null || __instance.m_forceDepth >= 0f) { return; }

            float[] depth = __instance.m_normalizedDepth;
            for (int i = 0; i < RenderedDepth.Length; i++) {
                RenderedDepth[i] = Mathf.Clamp01(depth[i] * scale);
            }
            material.SetFloatArray(Depth, RenderedDepth);
        }
    }
}
