using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // After a loading screen the player stands outside both reflection probes, flat and orange until one grows in.
    // Prefixes ReflectionUpdate.Update and transpiles UpdateReflection to render a full-size probe on arrival.
    // Lighting is right the moment the loading screen lifts after portals, dungeon doors, logins and respawns.
    // Provenance: Krosshair's Valheim Wire Patches, ported and reworked.
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
                "Keeps the lighting right after portals, dungeon doors, logging in and respawning, instead " +
                "of going flat and orange and sweeping back in.");

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
                // Renders during the load stay vanilla's: they give the probes something to show until
                // the arrival render finishes.
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

            // Fix Lightning Flash Lighting runs from here, after this fix: the one place both touch the
            // probe timer, so neither can undo the other.
            LightningFlashLightingPatch.OnProbeUpdate(__instance, dt);
        }

        // A fresh probe's box normally grows from nothing over 3 s, leaving everything outside it lit by
        // the sky alone; ReflectionProbeRender shows the arrival render at full size once it finishes.
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
