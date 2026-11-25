using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Unity.Collections;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Tar Pit Buffer Disposal: a tar pit whose Awake failed throws from OnDestroy, cutting short scene teardown.
    // Transpiles LiquidVolume.OnDestroy so each NativeArray.Dispose call runs only when the array was created.
    // A tar pit that failed to load unloads cleanly instead of throwing during scene teardown.
    // Provenance: Azumatt's MyPitsDontLeak (MIT), which replaces both methods wholesale.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(LiquidVolume))]
    internal static class LiquidVolumeLeakPatch {
        private static readonly MethodInfo SafeDisposeMethod =
            AccessTools.Method(typeof(LiquidVolumeLeakPatch), nameof(SafeDispose));

        // Awake allocates the arrays in its last lines, so one that threw earlier leaves them uncreated.
        private static void SafeDispose<T>(ref NativeArray<T> array) where T : struct {
            if (array.IsCreated) { array.Dispose(); }
        }

        private static bool IsNativeArrayOf(Type type) =>
            type != null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(NativeArray<>);

        // The managed pointer to the field is already on the stack from the ldflda, so the
        // signatures line up. Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("OnDestroy")]
        private static IEnumerable<CodeInstruction> OnDestroyTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int patched = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode != OpCodes.Call && codes[i].opcode != OpCodes.Callvirt) { continue; }
                if (!(codes[i].operand is MethodInfo method)) { continue; }
                if (method.Name != nameof(NativeArray<int>.Dispose) || !IsNativeArrayOf(method.DeclaringType)) { continue; }

                Type elementType = method.DeclaringType.GetGenericArguments()[0];
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = SafeDisposeMethod.MakeGenericMethod(elementType);
                patched++;
            }

            if (patched == 0) {
                PatchHelper.WarnOnce(
                    "LiquidVolume.OnDestroy: found no NativeArray.Dispose calls to guard, so this fix is " +
                    "inactive. Another mod has most likely already rewritten the method - if so, nothing " +
                    "is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
