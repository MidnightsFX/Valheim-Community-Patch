using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Unity.Collections;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Tar Pit Buffer Disposal: tar pit raycast buffers are disposed safely.
    //
    // LiquidVolume.OnDestroy calls Dispose on its two NativeArrays unguarded, and Awake allocates
    // them in its last few lines, after the mesh build and the save load. A tar pit whose Awake
    // did not reach them - because something above threw - throws again out of OnDestroy, during
    // scene teardown, where it takes the rest of the destroy chain with it.
    //
    // A transpiler rewrites each Dispose call to an IsCreated-guarded one, anchored on the calls
    // rather than replacing the method.
    //
    // This fix used to have a second half, rewriting Awake's Allocator.TempJob constants to
    // Allocator.Persistent: vanilla kept a four-frame allocation for the object's whole life,
    // leaking the block and logging "JobTempAlloc has allocations that are more than 4 frames old"
    // on every tar pit load. Valheim now allocates both arrays Persistent itself, so that half is
    // gone. Persistent memory is reclaimed only by an explicit Dispose, which is what makes the
    // guard below matter more than it did, not less.
    //
    // Client: tar pits are Plains-only and never inside a dedicated server's active area.
    // Provenance: Azumatt's MyPitsDontLeak (MIT), which replaces both methods wholesale.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(LiquidVolume))]
    internal static class LiquidVolumeLeakPatch {
        private static readonly MethodInfo SafeDisposeMethod =
            AccessTools.Method(typeof(LiquidVolumeLeakPatch), nameof(SafeDispose));

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
                Logger.LogWarning(
                    "LiquidVolume.OnDestroy: found no NativeArray.Dispose calls to guard, so this fix is " +
                    "inactive. Another mod has most likely already rewritten the method - if so, nothing " +
                    "is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
