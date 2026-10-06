using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Active Area Check Allocation: testing whether a point is inside the loaded area no longer
    // allocates an array per test.
    //
    // Utils.ChebyshevDistance(Vector3, Vector3) passes its three axis distances to
    // Mathf.Max(params float[]), so every call allocates a three-element array. Its one caller is
    // ZNetScene.PointInsideActiveArea, which every owned piece's wear update, every static physics
    // object's settle check and every spawner asks, and which the server asks for each persistent
    // object near every peer when it releases ownership every two seconds.
    //
    // A transpiler removes the array: the three Abs results stay on the stack and go to Max3, which
    // is Mathf.Max(float[])'s own loop unrolled (keep the first, take a later one only when it is
    // strictly greater), so the result is identical for NaN and signed zeros too. A transpiler
    // rather than a prefix so the hottest callers pay no wrapper, and every caller, other mods
    // included, gets it.
    //
    // Both: the server's ownership release is the largest caller.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(Utils), nameof(Utils.ChebyshevDistance), typeof(Vector3), typeof(Vector3))]
    internal static class ChebyshevDistanceAllocPatch {
        private static readonly MethodInfo MaxArrayMethod =
            AccessTools.Method(typeof(Mathf), nameof(Mathf.Max), new[] { typeof(float[]) });

        private static readonly MethodInfo Max3Method =
            AccessTools.Method(typeof(ChebyshevDistanceAllocPatch), nameof(Max3));

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            // ldc.i4.3; newarr float32 / three of dup; ldc.i4.k; <value>; stelem.r4 / call Max(float[]).
            int arrays = 0, slots = 0, stores = 0, calls = 0;
            for (int i = 0; i < codes.Count; i++) {
                CodeInstruction code = codes[i];

                if (code.opcode == OpCodes.Newarr && Equals(code.operand, typeof(float))) {
                    if (i == 0 || !codes[i - 1].LoadsConstant(3)) { return Unexpected(instructions); }

                    Nop(codes[i - 1]);
                    Nop(code);
                    arrays++;
                } else if (code.opcode == OpCodes.Dup) {
                    if (i + 1 >= codes.Count || !codes[i + 1].LoadsConstant(slots)) { return Unexpected(instructions); }

                    Nop(code);
                    Nop(codes[i + 1]);
                    slots++;
                    i++;
                } else if (code.opcode == OpCodes.Stelem_R4) {
                    Nop(code);
                    stores++;
                } else if (code.Calls(MaxArrayMethod)) {
                    code.opcode = OpCodes.Call;
                    code.operand = Max3Method;
                    calls++;
                }
            }

            if (arrays != 1 || slots != 3 || stores != 3 || calls != 1) { return Unexpected(instructions); }

            return codes;
        }

        // Mathf.Max(params float[]) for three values, branch for branch.
        private static float Max3(float a, float b, float c) {
            float max = a;
            if (b > max) { max = b; }
            if (c > max) { max = c; }
            return max;
        }

        // Labels and exception blocks stay on the instruction, so a jump into it still lands.
        private static void Nop(CodeInstruction code) {
            code.opcode = OpCodes.Nop;
            code.operand = null;
        }

        private static IEnumerable<CodeInstruction> Unexpected(IEnumerable<CodeInstruction> instructions) {
            Logger.LogWarning(
                "Utils.ChebyshevDistance: the method does not build the three-value array this fix " +
                "removes, so this fix is inactive. Another mod has most likely already rewritten the " +
                "method - if so, nothing is wrong.");
            return instructions;
        }
    }
}
