using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Effect Areas: two defects in the component behind fire warmth, wetness and the
    // no-monster radius around a workbench.
    //
    // 1. EffectArea.m_tempColliders is a fixed 128-element buffer, and IsPointInsideArea and
    //    GetBaseValue feed it to Physics.OverlapSphereNonAlloc, which silently stops writing when
    //    it is full. In a dense base the area that matters can fall outside the first 128 hits,
    //    so warmth and wetness checks miss. Transpilers route both calls through a wrapper that
    //    grows the buffer and retries whenever it came back full.
    //
    // 2. OnTriggerEnter adds a Character to m_collidedWithCharacter and only OnTriggerExit removes
    //    it, but Unity never fires OnTriggerExit for a collider destroyed inside the trigger. A
    //    character that dies in the area stays in the list, and CustomFixedUpdate dereferences it
    //    every physics step from then on. A Character.OnDestroy postfix removes the character from
    //    every area, and a CustomFixedUpdate prefix drops any invalid entries that slipped through.
    //
    // Both: CustomFixedUpdate has no owner gate, so the dangling-reference loop runs on a server
    // too. Provenance: same two defects as ComfyMods/Effectual (GPL-3.0, redseiko).
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class EffectAreaPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(EffectAreaPatch),
                ValConfig.SectionCorrectness,
                "Fix Effect Areas",
                true,
                "Two fixes: grows the fixed 128-collider buffer that made fire warmth and wetness checks " +
                "silently miss in dense builds, and drops destroyed characters from effect areas instead " +
                "of throwing every physics step. Changing this requires a game restart.");
        }

        private const int BufferGrowth = 128;
        private const int MaxBuffer = 4096;

        private static readonly MethodInfo OverlapSphereNonAllocMethod =
            AccessTools.Method(typeof(Physics), nameof(Physics.OverlapSphereNonAlloc),
                new[] { typeof(Vector3), typeof(float), typeof(Collider[]), typeof(int) });
        private static readonly MethodInfo GrowingOverlapMethod =
            AccessTools.Method(typeof(EffectAreaPatch), nameof(GrowingOverlapSphereNonAlloc));

        // A full buffer means the result was truncated. Callers re-read EffectArea.m_tempColliders
        // by field on each iteration, so they see the grown array.
        private static int GrowingOverlapSphereNonAlloc(Vector3 position, float radius, Collider[] results, int layerMask) {
            int count = Physics.OverlapSphereNonAlloc(position, radius, results, layerMask);

            while (count == EffectArea.m_tempColliders.Length && EffectArea.m_tempColliders.Length < MaxBuffer) {
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
