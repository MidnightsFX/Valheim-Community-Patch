using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Portal Idle Updates: portals rewrite their emission color, light and audio every frame, even once settled.
    // Prefixes TeleportWorld and EffectFade Update to skip at the fade's endpoint; Awake postfixes do the first write.
    // Idle portals cost nothing per frame and look the same.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(TeleportWorld))]
    internal static class PortalIdleUpdatePatch {
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(PortalIdleUpdatePatch));

        // A portal is already at its endpoint when it wakes, so the Update prefix would skip the first
        // write too and m_colorUnconnected would never be applied.
        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(TeleportWorld __instance) {
            // Vanilla disables the component in this case, so its Update never runs and neither
            // may ours.
            if (__instance.m_nview == null || __instance.m_nview.GetZDO() == null) { return; }
            if (__instance.m_model == null) { return; }

            // The write vanilla's first Update would have made, with m_colorAlpha still at 0.
            __instance.m_model.material.SetColor(
                EmissionColor,
                Color.Lerp(__instance.m_colorUnconnected, __instance.m_colorTargetfound, __instance.m_colorAlpha));
        }

        // Exact float equality is right: MoveTowards lands on the target exactly, and the test runs
        // before it, so the frame that lands still writes. It reads only vanilla's own inputs, so a
        // flip of m_hadTarget or m_active runs vanilla again the next frame with no wake hook.
        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static bool UpdatePrefix(TeleportWorld __instance) {
            return ApiSwitch.Off || __instance.m_colorAlpha != (__instance.m_hadTarget ? 1f : 0f);
        }

        [HarmonyPatch(typeof(EffectFade))]
        internal static class FadeHooks {
            // EffectFade.Awake zeroes the intensity but leaves the light enabled; vanilla's first
            // Update is what switches it off.
            [HarmonyPostfix]
            [HarmonyPatch("Awake")]
            private static void AwakePostfix(EffectFade __instance) => WriteEndpoint(__instance);

            [HarmonyPrefix]
            [HarmonyPatch("Update")]
            private static bool UpdatePrefix(EffectFade __instance) {
                return ApiSwitch.Off || __instance.m_intensity != (__instance.m_active ? 1f : 0f);
            }

            // Vanilla's Update body minus the MoveTowards, so the light and audio carry the values
            // its first frame would have written.
            private static void WriteEndpoint(EffectFade fade) {
                if (fade.m_light != null) {
                    fade.m_light.intensity = fade.m_intensity * fade.m_lightBaseIntensity;
                    fade.m_light.enabled = fade.m_light.intensity > 0f;
                }

                if (fade.m_audioSource != null) {
                    fade.m_audioSource.volume = fade.m_intensity * fade.m_baseVolume;
                }
            }
        }
    }
}
