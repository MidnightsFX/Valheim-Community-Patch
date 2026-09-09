using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Portal Idle Updates: a portal stops re-writing its emission colour, light and audio
    // every frame once the connection fade has reached its endpoint.
    //
    // TeleportWorld.Update runs every frame for every loaded portal and is two lines: a
    // Mathf.MoveTowards on m_colorAlpha, then m_model.material.SetColor("_EmissionColor", ...).
    // Renderer.material is a native accessor and the string overload of SetColor hashes the
    // property name, both paid every frame with no early-out once m_colorAlpha has reached 0 or 1
    // - which is the steady state for every portal, essentially always. EffectFade, which lives
    // only on the three portal prefabs, has the same shape: its Update writes m_light.intensity,
    // m_light.enabled and m_audioSource.volume natively every frame whether or not m_intensity
    // has settled on its target.
    //
    // A prefix on each Update skips the body when the fade value already equals its target.
    // Mathf.MoveTowards returns the target exactly when the remaining distance is within one
    // step, so the endpoint is reached rather than approached and float equality is the right
    // test; because the test runs before the MoveTowards, the frame that lands on the endpoint is
    // never skipped and vanilla writes the final values. The prefix reads only the two inputs
    // vanilla reads, so when UpdatePortal (twice a second) flips m_hadTarget, or SetActive flips
    // m_active, the next frame runs vanilla again - no registry, no wake hook, no eviction.
    // Both components are already converged at birth, though, so a prefix alone would skip
    // forever and never make the first write: TeleportWorld would never apply m_colorUnconnected,
    // and EffectFade never clears m_light.enabled (its Awake zeroes the intensity but not the
    // enabled flag - vanilla's first Update is what turns the light off). An Awake postfix on
    // each performs exactly the write that first Update would have made, after which the skip is
    // exact for the component's life. The teleport postfix mirrors vanilla's own null-ZDO gate,
    // which disables the component so Update never runs.
    //
    // Client: emission colour, light intensity and audio volume are rendering state.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(TeleportWorld))]
    internal static class PortalIdleUpdatePatch {
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

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

        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static bool UpdatePrefix(TeleportWorld __instance) {
            return __instance.m_colorAlpha != (__instance.m_hadTarget ? 1f : 0f);
        }

        [HarmonyPatch(typeof(EffectFade))]
        internal static class FadeHooks {
            [HarmonyPostfix]
            [HarmonyPatch("Awake")]
            private static void AwakePostfix(EffectFade __instance) => WriteEndpoint(__instance);

            [HarmonyPrefix]
            [HarmonyPatch("Update")]
            private static bool UpdatePrefix(EffectFade __instance) {
                return __instance.m_intensity != (__instance.m_active ? 1f : 0f);
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
