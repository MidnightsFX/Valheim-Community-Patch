using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Collections;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: LiquidVolume (tar pits) allocates its two raycast NativeArrays with
    // Allocator.TempJob, which is contractually a <=4-frame allocator:
    //
    //   this.m_raycastResults  = new NativeArray<RaycastHit>(num * num, Allocator.TempJob);
    //   this.m_raycastCommands = new NativeArray<RaycastCommand>(num * num, Allocator.TempJob);
    //
    // But the arrays live for the whole lifetime of the tar pit GameObject and are reused on every
    // UpdateHeights(), scheduled about once a second from Update(). With m_width = 32 that is a
    // persistent 1089-element pair per pit held for hours. Two consequences:
    //
    //   1. Unity logs "Internal: JobTempAlloc has allocations that are more than 4 frames old" every
    //      time a tar pit streams in.
    //   2. The temp-allocator block is never returned to the pool, so native (non-GC) memory climbs
    //      steadily across a long session.
    //
    // OnDestroy then calls Dispose() unguarded, which throws if Awake never completed or the temp
    // allocator already reclaimed the block.
    //
    // Fix: swap the allocator to Persistent, and guard both Dispose calls with IsCreated. These are
    // interdependent - Persistent memory is reclaimed *only* by an explicit Dispose, so a throwing
    // OnDestroy would turn a temp-pool leak into a permanent one. The guards matter more after the
    // allocator change, not less.
    //
    // Both patches are transpilers anchored on the NativeArray constructor / Dispose call rather than
    // whole-method replacements, so they survive unrelated changes to these methods and coexist with
    // other mods patching them.
    //
    // Provenance: same root cause as Azumatt's MyPitsDontLeak (MIT), which replaces both methods
    // wholesale. Reimplemented here as targeted IL edits.
    // Client: LiquidVolume is a MonoBehaviour, so Awake only runs where ZNetScene actually
    // instantiates the tar pit. A dedicated server only instantiates objects in its own active area,
    // which never leaves world origin, and tar pits are Plains-only - so one can never wake there.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(LiquidVolume))]
    internal static class LiquidVolumeLeakPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(LiquidVolumeLeakPatch),
                ValConfig.SectionPerformance,
                "Fix Tar Pit Memory Leak",
                true,
                "Allocates the tar pit (LiquidVolume) raycast buffers with a persistent allocator instead " +
                "of the 4-frame temporary one, and guards their disposal. Stops the 'JobTempAlloc has " +
                "allocations that are more than 4 frames old' log spam and the native memory growth that " +
                "comes with it. Changing this requires a game restart.");
        }

        private static readonly MethodInfo SafeDisposeMethod =
            AccessTools.Method(typeof(LiquidVolumeLeakPatch), nameof(SafeDispose));

        private static void SafeDispose<T>(ref NativeArray<T> array) where T : struct {
            if (array.IsCreated) { array.Dispose(); }
        }

        private static bool IsNativeArrayOf(Type type) =>
            type != null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(NativeArray<>);

        // Rewrites every `new NativeArray<T>(n, Allocator.TempJob, ...)` in Awake to use
        // Allocator.Persistent. Anchored on the constructor, then walking back for the allocator
        // constant, so the surrounding code is free to change.
        //
        // Priority.Last on both transpilers here, for the reason in ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("Awake")]
        private static IEnumerable<CodeInstruction> AwakeTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);
            if (Enabled == null || !Enabled.Value) { return codes; }

            int patched = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode != OpCodes.Newobj) { continue; }
                if (!(codes[i].operand is ConstructorInfo ctor) || !IsNativeArrayOf(ctor.DeclaringType)) { continue; }

                // The allocator is the second constructor argument, so it is within a few instructions
                // behind the newobj regardless of how the length expression is written.
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
                // Refuse to half-apply: leaving vanilla intact is strictly safer than allocating some
                // buffers persistently and losing track of the rest.
                Logger.LogWarning(
                    $"LiquidVolume.Awake: expected 2 TempJob allocations, rewrote {patched}. " +
                    "Leaving the method unpatched - the tar pit memory leak fix is inactive.");
                return instructions;
            }

            Logger.LogDebug("LiquidVolume.Awake: raycast buffers switched to Allocator.Persistent.");
            return codes;
        }

        // Replaces `nativeArray.Dispose()` with a null-safe equivalent. The managed pointer to the
        // field is already on the stack from the ldflda, so the signatures line up.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("OnDestroy")]
        private static IEnumerable<CodeInstruction> OnDestroyTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);
            if (Enabled == null || !Enabled.Value) { return codes; }

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

            Logger.LogDebug($"LiquidVolume.OnDestroy: guarded {patched} NativeArray disposals.");
            return codes;
        }
    }
}
