using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Two independent vanilla defects in EffectArea, the component behind fire warmth, wetness, the
    // no-monster radius around a workbench, and every other area status effect.
    //
    // 1. Truncated collider buffer.
    //    EffectArea.m_tempColliders is a fixed 128-element static array, and both
    //    IsPointInsideArea and GetBaseValue feed it to Physics.OverlapSphereNonAlloc, which silently
    //    stops writing once it is full. In a dense build - a large base, a cluster of fireplaces, a
    //    workbench surrounded by pieces - the area that actually matters can fall outside the first
    //    128 hits, so warmth, wetness and burning checks simply miss. Grow the buffer instead.
    //
    // 2. Dangling character references.
    //    OnTriggerEnter adds a Character to m_collidedWithCharacter, and only OnTriggerExit removes it.
    //    Unity does not fire OnTriggerExit for a collider that is destroyed inside the trigger, so a
    //    character that dies or unloads while standing in the area stays in the list forever.
    //    CustomFixedUpdate then dereferences it with no validity check:
    //
    //      foreach (Character character in this.m_collidedWithCharacter) {
    //        if (this.m_statusEffectHash != 0) character.GetSEMan().AddStatusEffect(...);
    //        if (this.m_isHeatType) character.OnNearFire(this.transform.position);
    //      }
    //
    //    which throws a NullReferenceException every FixedUpdate from then on.
    //
    // Provenance: same two defects as ComfyMods/Effectual (GPL-3.0, redseiko).
    //
    // Both: CustomFixedUpdate has no owner gate and does tick on a dedicated server for areas in its
    // own active area, where the dangling-Character NRE becomes a permanent per-physics-step loop.
    // The two statics are called from both sides too - BaseAI and SpawnSystem as well as Player.
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

        // A full buffer means the result was truncated, so grow the shared array and retry. Callers
        // read EffectArea.m_tempColliders by field on each iteration, so they pick up the new array.
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
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            if (Enabled == null || !Enabled.Value) { return codes; }

            int patched = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(OverlapSphereNonAllocMethod)) { continue; }

                codes[i].operand = GrowingOverlapMethod;
                patched++;
            }

            if (patched == 0) {
                Logger.LogWarning(
                    $"EffectArea.{method}: found no OverlapSphereNonAlloc call, so this fix is inactive here. " +
                    "Another mod has most likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }

        // Priority.Last on both, for the reason in ValheimCommunityPatch.ApplyPatches.
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

        // Strips characters that were destroyed inside the trigger before vanilla iterates the list.
        // The list holds one or two entries in practice, so the sweep is cheaper than the null checks
        // it replaces would be inside the loop.
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

        // Root cause rather than symptom: a character being destroyed leaves every area it was standing
        // in holding a dangling reference, and Unity never fires OnTriggerExit for it.
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
