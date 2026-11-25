using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Water color reads depth with a zero _MainTex_ST, so each 64 m tile is one corner's color and borders show lines.
    // Postfixes WaterVolume.SetupMaterial to give the tile material an identity _MainTex_ST and tint its shallow color.
    // Sea color blends smoothly across zone borders near shores, as the waves already do.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(WaterVolume))]
    internal static class WaterColorSeamPatch {
        internal static FixToggle Enabled;
        internal static ConfigEntry<float> ShoreTint;

        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexSt = Shader.PropertyToID("_MainTex_ST");
        private static readonly int Shallow = Shader.PropertyToID("_ColorBottomShallow");
        private static readonly int Deep = Shader.PropertyToID("_ColorBottom");
        private static readonly int AshlandsShallow = Shader.PropertyToID("_AshlandsColorBottomShallow");
        private static readonly int AshlandsDeep = Shader.PropertyToID("_AshlandsColorBottom");
        private static readonly Vector4 IdentitySt = new Vector4(1f, 1f, 0f, 0f);

        // The game's values per material instance, recorded before the first change: the shallow
        // colors, so a second SetupMaterial call (another mod, a diagnostic) tints from them again
        // instead of compounding, and the tiling, so switching the fix off can put both back.
        private static readonly ConditionalWeakTable<Material, VanillaValues> Vanilla =
            new ConditionalWeakTable<Material, VanillaValues>();

        private sealed class VanillaValues {
            internal Vector4 MainTexSt;
            internal bool Tinted;
            internal Color Shallow;
            internal Color AshlandsShallow;
        }

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(WaterColorSeamPatch),
                ValConfig.SectionCorrectness,
                "Fix Water Color Seams",
                true,
                "Blends sea color smoothly across zone borders instead of in hard straight lines near shores; " +
                "applies to water that loads after it is turned on.");

            // Switched or turned off, loaded tiles go back to the game's look, so they match the
            // tiles that load afterwards.
            Enabled.SettingChanged += (sender, args) => {
                if (!Enabled.Value) { RestoreLoadedTiles(); }
            };

            ShoreTint = ValConfig.BindServerConfig(
                ValConfig.SectionCorrectness,
                "Water Color Shore Tint",
                0.75f,
                "How far Fix Water Color Seams moves the sandy shallow-water color toward the deep-sea color, from " +
                "0 (the game's color) to 1 (deep color everywhere). Changes color only, never waves, and applies to " +
                "water that loads afterwards.",
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

            // Fixed-depth water has no heightmap corners: all four _depth values are the same, so it
            // renders one flat color either way and its material is not touched.
            if (__instance.m_heightmap == null || __instance.m_forceDepth >= 0f) { return; }

            // The per-renderer instance vanilla has just written _depth to.
            Material material = surface.material;

            // A shader that declares its own _MainTex has tiling that is already right.
            if (material.HasProperty(MainTex)) { return; }

            // The shader uses _MainTex_ST without declaring it, so the material does not list it:
            // reading it would log a Unity error per tile, and the value the shader gets is zero. Writing
            // it still reaches the shader, so the write below must stay unguarded.
            if (!Vanilla.TryGetValue(material, out VanillaValues vanilla)) {
                vanilla = new VanillaValues {
                    MainTexSt = material.HasProperty(MainTexSt) ? material.GetVector(MainTexSt) : Vector4.zero
                };
                Vanilla.Add(material, vanilla);
            }

            material.SetVector(MainTexSt, IdentitySt);

            // Blended properly, one shallow corner tints a whole tile of open sea sandy, so the shallow color moves
            // toward the deep one, the same on every tile. The depth data is left alone, so the waves drawn still
            // match the ones boats and swimmers ride.
            if (!material.HasProperty(Shallow) || !material.HasProperty(Deep)) { return; }

            if (!vanilla.Tinted) {
                vanilla.Tinted = true;
                vanilla.Shallow = material.GetColor(Shallow);
                vanilla.AshlandsShallow = material.HasProperty(AshlandsShallow) ? material.GetColor(AshlandsShallow) : default;
            }

            float tint = ShoreTint != null ? Mathf.Clamp01(ShoreTint.Value) : 0f;
            material.SetColor(Shallow, Color.Lerp(vanilla.Shallow, material.GetColor(Deep), tint));
            if (material.HasProperty(AshlandsShallow) && material.HasProperty(AshlandsDeep)) {
                material.SetColor(AshlandsShallow, Color.Lerp(vanilla.AshlandsShallow, material.GetColor(AshlandsDeep), tint));
            }
        }

        // Inactive tiles too: WaterVolume.Instances lists only enabled ones. The renderer's own
        // instance is what SetupMaterial changed; sharedMaterial returns it without making another.
        private static void RestoreLoadedTiles() {
            foreach (WaterVolume volume in Object.FindObjectsByType<WaterVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                Material material = volume.m_waterSurface != null ? volume.m_waterSurface.sharedMaterial : null;
                if (material == null || !Vanilla.TryGetValue(material, out VanillaValues vanilla)) { continue; }

                material.SetVector(MainTexSt, vanilla.MainTexSt);
                if (!vanilla.Tinted) { continue; }

                material.SetColor(Shallow, vanilla.Shallow);
                if (material.HasProperty(AshlandsShallow)) { material.SetColor(AshlandsShallow, vanilla.AshlandsShallow); }
            }
        }
    }
}
