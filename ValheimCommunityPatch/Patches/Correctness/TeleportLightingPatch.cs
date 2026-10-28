using System.Collections.Generic;
using System.Reflection;
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
    // then watches the new box grow out from them. Logging in and respawning are the same case. A
    // dungeon door also renders a probe through its interior's environment zone, but at a reference
    // position Player.LateUpdate has not moved yet, so that render lands back outside the door.
    //
    // A prefix on ReflectionUpdate.Update watches for the loading screen ending (no local player, or
    // one that is teleporting; a TeleportTo postfix also catches a teleport that starts and ends
    // between two Updates). A transpiler has UpdateReflection read the position Player.LateUpdate is
    // about to set, so a render on the arrival frame lands where the player is. The first render after
    // the loading screen is the arrival render: the environment zone's when a door makes one, or else
    // one the prefix forces. ReflectionProbeRender shows it at full size as soon as it finishes, and
    // the regular cycle then re-renders the other probe inside that full box, so nothing sweeps.
    // Renders during the load itself are left to vanilla: they give the probes something to show
    // until the arrival one finishes. Without the transpiler the prefix waits one frame for
    // Player.LateUpdate before forcing. This hook is the one place both lighting fixes touch the probe
    // timer: Fix Lightning Flash Lighting runs from it, after this fix, so neither can undo the other.
    //
    // Client: the probes exist only where there is a camera. Provenance: ported from Krosshair's
    // Valheim Wire Patches and reworked.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ReflectionUpdate), "Update")]
    internal static class TeleportLightingPatch {
        internal static FixToggle Enabled;

        private static readonly MethodInfo GetReferencePositionMethod =
            AccessTools.Method(typeof(ZNet), nameof(ZNet.GetReferencePosition));
        private static readonly MethodInfo ReferencePositionMethod =
            AccessTools.Method(typeof(TeleportLightingPatch), nameof(ReferencePosition));

        private static ReflectionUpdate _tracked;
        private static bool _arrivalPending;
        private static int _armedFrame = -1;
        // UpdateReflection reads the player's own position, so the arrival frame can render at once.
        private static bool _anchored;

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
            _arrivalPending = false;
            _armedFrame = -1;
            ReflectionProbeRender.Clear();
        }

        // Where Player.LateUpdate puts the reference position at the end of this frame. Until then it
        // can still hold the spot a teleport moved the player away from.
        private static Vector3 ReferencePosition(ZNet znet) {
            if (Enabled != null && Enabled.Value) {
                Player player = Player.m_localPlayer;
                if (player != null && player.m_nview != null && player.m_nview.IsValid() && player.m_nview.IsOwner()) {
                    return player.transform.position;
                }
            }
            return znet.GetReferencePosition();
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
                    _arrivalPending = true;
                } else if (_arrivalPending) {
                    _arrivalPending = false;
                    if (_anchored) {
                        Arrive(__instance);
                    } else {
                        // One more frame under the black screen: Player.LateUpdate sets the reference
                        // position after the FixedUpdate that ended the teleport, and for a dungeon
                        // door that step is also the one that moved the player.
                        _armedFrame = Time.frameCount;
                        ReflectionProbeRender.Hold(__instance, dt);
                    }
                } else if (_armedFrame >= 0 && Time.frameCount > _armedFrame) {
                    _armedFrame = -1;
                    Arrive(__instance);
                }
            }

            LightningFlashLightingPatch.OnProbeUpdate(__instance, dt);
        }

        private static void Arrive(ReflectionUpdate r) {
            ReflectionProbeRender.Force(r);
            Logger.LogDebug("Fix Lighting After Teleport: rendering the reflection probe on arrival.");
        }

        [HarmonyPostfix]
        private static void Postfix(ReflectionUpdate __instance) {
            ReflectionProbeRender.Tick(__instance);
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("UpdateReflection")]
        private static IEnumerable<CodeInstruction> UpdateReflectionTranspiler(IEnumerable<CodeInstruction> instructions) {
            IEnumerable<CodeInstruction> codes = PatchHelper.ReplaceCalls(
                instructions, GetReferencePositionMethod, ReferencePositionMethod, "ReflectionUpdate.UpdateReflection", 1);

            // ReplaceCalls hands back its input when it stands down.
            _anchored = !ReferenceEquals(codes, instructions);
            return codes;
        }

        // A dungeon's environment zone renders on the frame the door puts the player inside or back
        // out. That render is used as the arrival render: forcing a second would share the probe
        // rendering with it and take twice as long to show.
        [HarmonyPostfix]
        [HarmonyPatch("UpdateReflection")]
        private static void UpdateReflectionPostfix(ReflectionUpdate __instance) {
            if (!_arrivalPending || !_anchored || Enabled == null || !Enabled.Value) { return; }
            if (!ReferenceEquals(__instance, _tracked) || RunMode.InLoadingScreen()) { return; }

            _arrivalPending = false;
            ReflectionProbeRender.Adopt(__instance);
            Logger.LogDebug("Fix Lighting After Teleport: using the environment's reflection probe render on arrival.");
        }

        // A mod that skips the teleport's wait can start and end it between two Updates, where the
        // prefix would never see it; the start is noted where it happens instead.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        private static void TeleportToPostfix(Player __instance, bool __result) {
            if (__result && Enabled != null && Enabled.Value && ReferenceEquals(__instance, Player.m_localPlayer)) {
                _arrivalPending = true;
            }
        }
    }
}
