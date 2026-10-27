using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Lighting After Teleport: the lighting is right as soon as the loading screen lifts after a
    // portal, a dungeon door, logging in or respawning, instead of going flat and sweeping back in.
    //
    // ReflectionUpdate re-renders one of two realtime reflection probes every 3 s at the network
    // reference position, and the fresh probe's 2000 x 1000 x 2000 m box grows from nothing over 3 s
    // on a power curve that keeps it near nothing for the first two. Anything outside both boxes is
    // lit by the skybox alone: flat and orange. After a far portal or a dungeon door (interiors sit
    // 5000 m up, far outside the box) the player stands outside both probes until the next render,
    // then watches the new box grow out from them, and a render that happened to land during the
    // load shows the half-loaded destination. Logging in and respawning are the same case.
    //
    // A prefix on ReflectionUpdate.Update watches for the loading screen ending (no local player, or
    // one that is teleporting). On the first frame after it, the prefix holds vanilla's timer one
    // frame, so Player.LateUpdate has moved the reference position (for a dungeon door the jump and
    // the arrival are the same FixedUpdate), then has vanilla render through ReflectionProbeRender,
    // which also shows the capture at full size as soon as it finishes. The regular cycle then
    // re-renders the other probe inside that full box, so nothing sweeps. Renders during the load
    // itself are left to vanilla: they give the probes something to show until the forced one
    // finishes. EnvMan and EnvZone still call UpdateReflection directly; those renders are left
    // alone too, and the forced one wins at importance 1. This hook is the one place both lighting
    // fixes touch the probe timer: Fix Lightning Flash Lighting runs from it, after this fix, so
    // neither can undo the other.
    //
    // Client: the probes exist only where there is a camera. Provenance: ported from Krosshair's
    // Valheim Wire Patches and reworked.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ReflectionUpdate), "Update")]
    internal static class TeleportLightingPatch {
        internal static FixToggle Enabled;

        private static ReflectionUpdate _tracked;
        private static bool _wasLoading;
        private static int _armedFrame = -1;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TeleportLightingPatch),
                ValConfig.SectionCorrectness,
                "Fix Lighting After Teleport",
                true,
                "Keeps the lighting right after portals, dungeon doors, logging in and respawning. Vanilla " +
                "grows a freshly rendered lighting probe out from the player over a few seconds, so after " +
                "a loading screen the lighting goes flat and orange and sweeps back in.");

            Enabled.SettingChanged += (sender, args) => { if (!Enabled.Value) { Reset(); } };
        }

        private static void Reset() {
            _wasLoading = false;
            _armedFrame = -1;
            ReflectionProbeRender.Clear();
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(ReflectionUpdate __instance, bool __runOriginal) {
            // Another mod skipped this Update: nothing to time.
            if (!__runOriginal) { return; }

            float dt = Time.deltaTime;

            // A new game session has a new updater; the old one's window is gone with it.
            if (!ReferenceEquals(__instance, _tracked)) {
                _tracked = __instance;
                Reset();
                LightningFlashLightingPatch.Reset();
            }

            if (Enabled != null && Enabled.Value) {
                if (RunMode.InLoadingScreen()) {
                    _wasLoading = true;
                } else if (_wasLoading) {
                    // One more frame under the black screen: Player.LateUpdate sets the reference
                    // position after the FixedUpdate that ended the teleport, and for a dungeon
                    // door that step is also the one that moved the player.
                    _wasLoading = false;
                    _armedFrame = Time.frameCount;
                    ReflectionProbeRender.Hold(__instance, dt);
                } else if (_armedFrame >= 0 && Time.frameCount > _armedFrame) {
                    _armedFrame = -1;
                    ReflectionProbeRender.Force(__instance);
                    Logger.LogDebug("Fix Lighting After Teleport: rendering the reflection probe on arrival.");
                }
            }

            LightningFlashLightingPatch.OnProbeUpdate(__instance, dt);
        }

        [HarmonyPostfix]
        private static void Postfix(ReflectionUpdate __instance) {
            ReflectionProbeRender.Tick(__instance);
        }
    }
}
