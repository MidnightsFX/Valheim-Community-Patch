using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Active Area Check Allocation: ChebyshevDistance allocates an array for Mathf.Max on every active-area check.
    // A transpiler removes the array and passes the three distances to Max3, Mathf.Max's own loop unrolled.
    // Wear updates, physics settling and the server's ownership release check the active area without garbage.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(Utils), nameof(Utils.ChebyshevDistance), typeof(Vector3), typeof(Vector3))]
    internal static class ChebyshevDistanceAllocPatch {
        private static readonly MethodInfo MaxArrayMethod =
            AccessTools.Method(typeof(Mathf), nameof(Mathf.Max), new[] { typeof(float[]) });

        private static readonly MethodInfo Max3Method =
            AccessTools.Method(typeof(ChebyshevDistanceAllocPatch), nameof(Max3));

        // A transpiler rather than a prefix, so the hottest callers pay no wrapper and every caller,
        // other mods included, gets it. Priority.Last: see ValheimCommunityPatch.ApplyPatches.
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

        // Mathf.Max(params float[]) for three values, branch for branch, so NaN and signed zeros give
        // the same result.
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
            PatchHelper.WarnOnce(
                "Utils.ChebyshevDistance: the method does not build the three-value array this fix " +
                "removes, so this fix is inactive. Another mod has most likely already rewritten the " +
                "method - if so, nothing is wrong.");
            return instructions;
        }
    }
}
