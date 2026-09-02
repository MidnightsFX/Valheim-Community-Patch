using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Unity.Collections;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Tar Pit Memory Leak: tar pit raycast buffers are allocated persistently and disposed
    // safely.
    //
    // LiquidVolume.Awake allocates its two NativeArrays with Allocator.TempJob, a four-frame
    // allocator, then keeps them for the object's whole life. Unity logs "JobTempAlloc has
    // allocations that are more than 4 frames old" every time a tar pit loads, and the block is
    // never returned to the pool. OnDestroy then calls Dispose unguarded, which throws if Awake
    // never completed.
    //
    // Two transpilers: Awake's allocator constants become Allocator.Persistent, and OnDestroy's
    // Dispose calls become IsCreated-guarded ones. They depend on each other, since Persistent
    // memory is reclaimed only by an explicit Dispose. Both are anchored on the constructor and
    // Dispose calls rather than replacing the methods.
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

        // Priority.Last on both: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("Awake")]
        private static IEnumerable<CodeInstruction> AwakeTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int patched = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode != OpCodes.Newobj) { continue; }
                if (!(codes[i].operand is ConstructorInfo ctor) || !IsNativeArrayOf(ctor.DeclaringType)) { continue; }

                // The allocator is the second constructor argument, a few instructions back.
                for (int j = i - 1; j >= 0 && j >= i - 6; j--) {
                    if (codes[j].opcode == OpCodes.Ldc_I4_3) {
                        codes[j].opcode = OpCodes.Ldc_I4_4;
                        patched++;
                        break;
                    }
                    if (codes[j].opcode == OpCodes.Ldc_I4 && codes[j].operand is int v && v == (int)Allocator.TempJob) {
                        codes[j].operand = (int)Allocator.Persistent;
                        patched++;
                        break;
                    }
                }
            }

            if (patched != 2) {
                Logger.LogWarning(
                    $"LiquidVolume.Awake: expected 2 TempJob allocations, rewrote {patched}. " +
                    "Leaving the method unpatched - the tar pit memory leak fix is inactive.");
                return instructions;
            }

            return codes;
        }

        // The managed pointer to the field is already on the stack from the ldflda, so the
        // signatures line up.
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
