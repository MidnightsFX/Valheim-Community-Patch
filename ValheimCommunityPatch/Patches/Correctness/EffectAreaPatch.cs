using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // EffectArea's fixed 128-collider buffer truncates in dense bases, and a character dying in an area stays listed.
    // Transpilers regrow a full overlap buffer; Character.OnDestroy and CustomFixedUpdate hooks drop dead characters.
    // Warmth and wetness checks no longer miss in dense builds, and dead characters stop throwing every physics step.
    // Provenance: same two defects as ComfyMods/Effectual (GPL-3.0, redseiko).
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class EffectAreaPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(EffectAreaPatch),
                ValConfig.SectionCorrectness,
                "Fix Effect Areas",
                true,
                "Stops fire warmth and wetness checks missing in dense builds, and stops characters that died " +
                "inside an effect area throwing errors every physics step; turning it on needs a game restart.");

            // Switched or turned off, the buffer goes back to the game's size.
            Enabled.SettingChanged += (sender, args) => {
                if (!Enabled.Value && _originalLength > 0 && EffectArea.m_tempColliders.Length > _originalLength) {
                    EffectArea.m_tempColliders = new Collider[_originalLength];
                }
            };
        }

        private const int BufferGrowth = 128;
        private const int MaxBuffer = 4096;

        // The buffer's size before it was first grown.
        private static int _originalLength;

        private static readonly MethodInfo OverlapSphereNonAllocMethod =
            AccessTools.Method(typeof(Physics), nameof(Physics.OverlapSphereNonAlloc),
                new[] { typeof(Vector3), typeof(float), typeof(Collider[]), typeof(int) });
        private static readonly MethodInfo GrowingOverlapMethod =
            AccessTools.Method(typeof(EffectAreaPatch), nameof(GrowingOverlapSphereNonAlloc));

        // A full buffer means the result was truncated. Callers re-read EffectArea.m_tempColliders
        // by field on each iteration, so they see the grown array.
        private static int GrowingOverlapSphereNonAlloc(Vector3 position, float radius, Collider[] results, int layerMask) {
            int count = Physics.OverlapSphereNonAlloc(position, radius, results, layerMask);
            if (!Enabled.Value) { return count; }

            while (count == EffectArea.m_tempColliders.Length && EffectArea.m_tempColliders.Length < MaxBuffer) {
                if (_originalLength == 0) { _originalLength = EffectArea.m_tempColliders.Length; }

                int grown = EffectArea.m_tempColliders.Length + BufferGrowth;
                Array.Resize(ref EffectArea.m_tempColliders, grown);
                Logger.LogDebug($"Grew the effect area collider buffer to {grown}.");

                count = Physics.OverlapSphereNonAlloc(position, radius, EffectArea.m_tempColliders, layerMask);
            }

            return count;
        }

        private static IEnumerable<CodeInstruction> ReplaceOverlapCall(IEnumerable<CodeInstruction> instructions, string method) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, OverlapSphereNonAllocMethod, GrowingOverlapMethod, "EffectArea." + method);
        }

        // Priority.Last on both: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(EffectArea), nameof(EffectArea.IsPointInsideArea))]
        private static IEnumerable<CodeInstruction> IsPointInsideAreaTranspiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceOverlapCall(instructions, nameof(EffectArea.IsPointInsideArea));

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(EffectArea), nameof(EffectArea.GetBaseValue))]
        private static IEnumerable<CodeInstruction> GetBaseValueTranspiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceOverlapCall(instructions, nameof(EffectArea.GetBaseValue));

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EffectArea), nameof(EffectArea.CustomFixedUpdate))]
        private static void CustomFixedUpdatePrefix(EffectArea __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            List<Character> collided = __instance.m_collidedWithCharacter;
            if (collided == null || collided.Count == 0) { return; }

            for (int i = collided.Count - 1; i >= 0; i--) {
                Character character = collided[i];
                if (character == null || !character.m_nview || !character.m_nview.IsValid()) { collided.RemoveAt(i); }
            }
        }

        // Unity never fires OnTriggerExit for a collider destroyed inside the trigger, so vanilla keeps it listed.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Character), "OnDestroy")]
        private static void CharacterOnDestroyPostfix(Character __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            List<EffectArea> areas = EffectArea.GetAllAreas();
            for (int i = 0; i < areas.Count; i++) {
                areas[i]?.m_collidedWithCharacter?.Remove(__instance);
            }
        }
    }
}
