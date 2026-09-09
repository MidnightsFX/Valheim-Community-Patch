using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Material Fader Settling: a finished fade stops re-applying the same material property
    // block to every one of its renderers every frame.
    //
    // MaterialFader.m_started is set in Awake from m_triggerOnAwake and again in TriggerFade, and
    // is never cleared. So once a fader has started, its Update evaluates every FadeProperty's
    // animation curve and calls Renderer.SetPropertyBlock on every renderer, every frame, for the
    // component's whole life - long after each FadeProperty has stopped changing the block.
    // FadeProperty.Update itself returns early once m_finished, but the fader has no aggregate
    // check, so the native block push keeps happening with identical contents. fx_Fader_Ragdoll
    // carries one per corpse: twenty lingering ragdolls at three renderers each is sixty native
    // SetPropertyBlock calls a frame writing nothing new.
    //
    // A prefix skips the body when every FadeProperty has settled. A property is settled when
    // vanilla marked it m_finished, or when it has started and its clamped curve input has
    // saturated - Mathf.Clamp01((m_fadeTimer - m_delay) / m_fadeTime) == 1f, which is exactly
    // k >= 1f - after which Evaluate returns the same constant and every write repeats the last.
    // m_finished alone is not enough as a gate: vanilla only sets it when the curve *output* hits
    // exactly 1.0, so a curve that does not end at 1 never sets it. The prefix reads the
    // pre-increment m_fadeTimer, so k >= 1f here means last frame's post-increment timer already
    // saturated, which means vanilla ran last frame, wrote with a clamped input of exactly 1 and
    // pushed the block - the first saturating frame is never skipped. A property that saturated
    // without ever starting its fade (one enormous frame jumping the timer past delay plus
    // duration) defers to vanilla, which still owes it a GetMaterialValues and a write. A NaN
    // curve input fails the >= test and also defers. Re-arming needs no wake hook: TriggerFade
    // calls Reset on every property, zeroing m_fadeTimer and clearing both flags, so the next
    // prefix sees an unsaturated fade and vanilla resumes.
    //
    // Client: material property blocks are rendering state.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(MaterialFader))]
    internal static class MaterialFaderSettlePatch {
        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static bool UpdatePrefix(MaterialFader __instance) {
            if (!__instance.m_started) { return true; }

            List<MaterialFader.FadeProperty> properties = __instance.m_fadeProperties;

            // With no properties the loop below would vacuously report settled, and the block
            // Awake seeded would never reach the renderers.
            if (properties == null || properties.Count == 0) { return true; }

            for (int i = 0; i < properties.Count; i++) {
                MaterialFader.FadeProperty property = properties[i];
                if (property.m_finished) { continue; }

                // Vanilla still owes this one its first write.
                if (!property.m_startedFade) { return true; }

                float progress = (property.m_fadeTimer - property.m_delay) / property.m_fadeTime;
                if (!(progress >= 1f)) { return true; }
            }

            return false;
        }
    }
}
