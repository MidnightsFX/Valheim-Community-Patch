using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Auto Pickup Allocation: the per-frame auto-pickup check reuses one collider buffer
    // instead of allocating an array every frame.
    //
    // Player.AutoPickup runs every frame and calls the allocating Physics.OverlapSphere overload:
    // one fresh Collider[] per player per frame, feeding GC pauses.
    //
    // A transpiler swaps it for OverlapSphereNonAlloc against a static buffer. The compiled
    // foreach bounds itself on the array's length and the buffer is longer than the hit count, so
    // the ldlen is rewritten to return the hit count too. Both edits are required, so the
    // transpiler backs out unless it can make both.
    //
    // Client: AutoPickup only runs for the local player. Provenance: Zen.ModLib (catalogue).
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Player))]
    internal static class AutoPickupAllocPatch {
        private const int BufferSize = 256;

        private static readonly Collider[] Buffer = new Collider[BufferSize];
        private static int _hitCount;

        private static readonly MethodInfo OverlapSphereMethod =
            AccessTools.Method(typeof(Physics), nameof(Physics.OverlapSphere),
                new[] { typeof(Vector3), typeof(float), typeof(int) });
        private static readonly MethodInfo OverlapSphereReusedMethod =
            AccessTools.Method(typeof(AutoPickupAllocPatch), nameof(OverlapSphereReused));
        private static readonly MethodInfo ResultCountMethod =
            AccessTools.Method(typeof(AutoPickupAllocPatch), nameof(ResultCount));

        private static Collider[] OverlapSphereReused(Vector3 position, float radius, int layerMask) {
            _hitCount = Physics.OverlapSphereNonAlloc(position, radius, Buffer, layerMask);
            return Buffer;
        }

        // The identity check keeps this correct if another transpiler routes a different array here.
        private static int ResultCount(Array array) => ReferenceEquals(array, Buffer) ? _hitCount : array.Length;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("AutoPickup")]
        private static IEnumerable<CodeInstruction> AutoPickupTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replacedCalls = 0, replacedLengths = 0;

            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].Calls(OverlapSphereMethod)) {
                    codes[i].operand = OverlapSphereReusedMethod;
                    replacedCalls++;
                    continue;
                }

                if (codes[i].opcode == OpCodes.Ldlen) {
                    // Ldlen pushes a native int and is followed by Conv.I4; a method returning int
                    // makes that conversion redundant, so it becomes a Nop.
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = ResultCountMethod;
                    replacedLengths++;

                    if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Conv_I4) {
                        codes[i + 1].opcode = OpCodes.Nop;
                        codes[i + 1].operand = null;
                    }
                }
            }

            if (replacedCalls != 1 || replacedLengths != 1) {
                Logger.LogWarning(
                    $"Player.AutoPickup: expected 1 OverlapSphere call and 1 array length read, found " +
                    $"{replacedCalls} and {replacedLengths}, so this fix is inactive. Another mod has most " +
                    "likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
