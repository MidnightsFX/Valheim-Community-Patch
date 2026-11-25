using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Material Fader Settling: a MaterialFader re-applies its unchanged property block every frame for life.
    // An Update prefix skips the body once every FadeProperty is finished or its curve input has reached 1.
    // Lingering corpses and other finished fades stop costing SetPropertyBlock calls every frame.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(MaterialFader))]
    internal static class MaterialFaderSettlePatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(MaterialFaderSettlePatch));

        // Needs no wake hook: TriggerFade resets every property's timer and flags, so vanilla resumes.
        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static bool UpdatePrefix(MaterialFader __instance) {
            if (ApiSwitch.Off || !__instance.m_started) { return true; }

            List<MaterialFader.FadeProperty> properties = __instance.m_fadeProperties;

            // With no properties the loop below would vacuously report settled, and the block
            // Awake seeded would never reach the renderers.
            if (properties == null || properties.Count == 0) { return true; }

            for (int i = 0; i < properties.Count; i++) {
                MaterialFader.FadeProperty property = properties[i];
                if (property.m_finished) { continue; }

                // Vanilla still owes this one its first write.
                if (!property.m_startedFade) { return true; }

                // m_finished alone misses a curve that does not end at exactly 1. The timer is last
                // frame's, so the first saturating frame still writes; a NaN fails the test and defers.
                float progress = (property.m_fadeTimer - property.m_delay) / property.m_fadeTime;
                if (!(progress >= 1f)) { return true; }
            }

            return false;
        }
    }
}
