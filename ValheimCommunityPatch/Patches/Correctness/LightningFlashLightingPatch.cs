using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Lightning Flash Lighting: a lightning flash no longer stays in the lighting for seconds
    // after it is over.
    //
    // Thunder.DoFlash spawns the flash prefab, whose lights last about two seconds. The lighting
    // comes from realtime reflection probes that ReflectionUpdate re-renders every 3 s, one face per
    // frame. A render that starts during a flash, or is half-way through when one starts, captures
    // the lit-up scene, and that capture lights everything until the probe is rendered again, up to
    // 3 s later.
    //
    // A postfix on DoFlash notes when the flash's lights will be gone, from the longest LightFlicker
    // lifetime in the prefab, and whether a capture was in flight. From the Update hook in
    // TeleportLightingPatch, a render that would start before then is held until the flash is over,
    // by keeping the timer one frame short of the interval; a render not due yet is left alone. Once
    // the flash is over, a capture that caught it is replaced by an immediate render shown at full
    // size through ReflectionProbeRender. A render forced by Fix Lighting After Teleport during a
    // flash goes ahead, since the baked light matches the real one while it lasts, and is replaced
    // the same way. Either order of Thunder.Update and ReflectionUpdate.Update in a frame is covered.
    // The accessibility flash setting still lights the flash, so nothing changes there.
    //
    // Client: the probes exist only where there is a camera. Provenance: ported from Krosshair's
    // Valheim Wire Patches and reworked.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Thunder), "DoFlash")]
    internal static class LightningFlashLightingPatch {
        internal static FixToggle Enabled;

        // A flash prefab with no lifetime on its lights, and the longest a flash is ever waited out.
        private const float DefaultLength = 1f;
        private const float MaxLength = 4f;
        // The lights are destroyed when their lifetime ends; one frame's slack past that.
        private const float Margin = 0.2f;

        // When the lights of the latest flash are gone, in Time.time, which never resets.
        private static float _flashEndsAt = -1f;
        private static bool _rerenderAfterFlash;
        private static readonly Dictionary<GameObject, float> FlashLength = new Dictionary<GameObject, float>();

        // The per-frame part runs from TeleportLightingPatch's hook; without it a noted flash could
        // never be acted on.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(LightningFlashLightingPatch),
            "Fix Lightning Flash Lighting",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ReflectionUpdate), "Update"), typeof(TeleportLightingPatch)));

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(LightningFlashLightingPatch),
                ValConfig.SectionCorrectness,
                "Fix Lightning Flash Lighting",
                true,
                "Keeps a lightning flash from staying in the lighting for seconds after it is over. Vanilla " +
                "can render its lighting probes during the flash and shows that capture until the next one.");

            Enabled.SettingChanged += (sender, args) => { if (!Enabled.Value) { _rerenderAfterFlash = false; } };
        }

        // A new game session: a flash noted at logout must not force a render at the next login.
        internal static void Reset() {
            _flashEndsAt = -1f;
            _rerenderAfterFlash = false;
        }

        [HarmonyPostfix]
        private static void Postfix(Thunder __instance) {
            if (Enabled == null || !Enabled.Value || !Hooks.Healthy) { return; }

            // Several Thunder components can be live while environments cross-fade: the latest end wins.
            float length = Mathf.Min(LengthOf(__instance.m_flashEffect), MaxLength);
            _flashEndsAt = Mathf.Max(_flashEndsAt, Time.time + length + Margin);

            ReflectionUpdate r = ReflectionUpdate.instance;
            if (r != null && r.m_current != null && !r.m_current.IsFinishedRendering(r.m_renderID)) {
                _rerenderAfterFlash = true;
            }
        }

        // Called from TeleportLightingPatch's prefix on every Update that vanilla is about to run.
        internal static void OnProbeUpdate(ReflectionUpdate r, float dt) {
            if (Enabled == null || !Enabled.Value) { return; }

            if (Time.time < _flashEndsAt) {
                if (ReflectionProbeRender.PendingForce) {
                    _rerenderAfterFlash = true;
                } else {
                    ReflectionProbeRender.Hold(r, dt);
                }
                return;
            }

            if (_rerenderAfterFlash) {
                _rerenderAfterFlash = false;
                ReflectionProbeRender.Force(r);
                Logger.LogDebug("Fix Lightning Flash Lighting: rendering the reflection probe again after a flash.");
            }
        }

        private static float LengthOf(EffectList effects) {
            float length = 0f;
            EffectList.EffectData[] entries = effects?.m_effectPrefabs;
            if (entries != null) {
                for (int i = 0; i < entries.Length; i++) {
                    EffectList.EffectData entry = entries[i];
                    if (entry == null || !entry.m_enabled || entry.m_prefab == null) { continue; }
                    length = Mathf.Max(length, LengthOf(entry.m_prefab));
                }
            }
            return length > 0f ? length : DefaultLength;
        }

        // One component scan per flash prefab for the session.
        private static float LengthOf(GameObject prefab) {
            if (FlashLength.TryGetValue(prefab, out float length)) { return length; }

            length = 0f;
            foreach (LightFlicker light in prefab.GetComponentsInChildren<LightFlicker>(true)) {
                if (light.m_ttl > 0f) { length = Mathf.Max(length, light.m_ttl); }
            }
            if (length <= 0f) {
                foreach (TimedDestruction timed in prefab.GetComponentsInChildren<TimedDestruction>(true)) {
                    length = Mathf.Max(length, timed.m_timeout);
                }
            }
            FlashLength[prefab] = length;
            return length;
        }
    }
}
