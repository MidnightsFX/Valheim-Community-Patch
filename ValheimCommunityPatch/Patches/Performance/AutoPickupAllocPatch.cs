using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: Player.AutoPickup runs every frame and allocates a fresh Collider[] every time:
    //
    //   foreach (Collider collider in Physics.OverlapSphere(vector3_1, this.m_autoPickupRange, this.m_autoPickupMask))
    //
    // Physics.OverlapSphere is the allocating overload. One array per player per frame is constant
    // garbage for no benefit, and the resulting GC pressure shows up as periodic micro-stutter.
    //
    // Fix: swap it for OverlapSphereNonAlloc against a reused buffer. The subtlety is that the compiled
    // foreach iterates the returned array by its *length*, and the buffer is longer than the hit count,
    // so the Ldlen has to be replaced too or the loop walks past the real results into stale entries
    // from previous frames. Both edits are required; applying one without the other is worse than
    // vanilla, so the transpiler backs out entirely unless it can make both.
    //
    // Provenance: same technique as Zen.ModLib's FixAutoPickupMemAlloc.
    //
    // Client: Player.FixedUpdate only reaches AutoPickup inside the branch where this Player is
    // m_localPlayer, and a dedicated server has none.
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

        // The buffer is longer than the hit count, so the loop bound has to be the hit count. The
        // identity check keeps this correct if another transpiler ever routes a different array here.
        private static int ResultCount(Array array) => ReferenceEquals(array, Buffer) ? _hitCount : array.Length;

        // Priority.Last, for the reason in ValheimCommunityPatch.ApplyPatches.
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
                    // Ldlen pushes a native int; the following Conv.I4 becomes redundant once a method
                    // returning int takes its place, so drop it rather than leave a no-op conversion.
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
                    "likely already rewrote the method.");
                return instructions;
            }

            return codes;
        }
    }
}
