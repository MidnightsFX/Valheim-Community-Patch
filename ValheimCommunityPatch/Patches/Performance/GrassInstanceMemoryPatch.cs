using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Grass Instance Memory: every grass renderer allocates room for 1,024 blades (64 KB) however few it holds.
    // Transpiles the InstanceRenderer constructor to allocate 16; AddInstance, SetInstance and Resize prefixes grow it.
    // Grass around the player holds a fraction of the memory, and building grass patches leaves far less garbage.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(InstanceRenderer))]
    internal static class GrassInstanceMemoryPatch {
        private const string FixName = "Fix Grass Instance Memory";

        // InstanceRenderer's own array size. AddInstance stops at 1,023 entries of it.
        private const int VanillaCapacity = 1024;
        private const int SmallCapacity = 16;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(GrassInstanceMemoryPatch));

        private static readonly FieldInfo InstancesField = AccessTools.Field(typeof(InstanceRenderer), nameof(InstanceRenderer.m_instances));
        private static readonly MethodInfo InitialCapacityMethod = AccessTools.Method(typeof(GrassInstanceMemoryPatch), nameof(InitialCapacity));

        private static readonly MethodInfo AddInstanceMethod =
            AccessTools.DeclaredMethod(typeof(InstanceRenderer), nameof(InstanceRenderer.AddInstance), new[] { typeof(Matrix4x4) });
        private static readonly MethodInfo SetInstanceMethod = AccessTools.DeclaredMethod(typeof(InstanceRenderer), nameof(InstanceRenderer.SetInstance));
        private static readonly MethodInfo ResizeMethod = AccessTools.DeclaredMethod(typeof(InstanceRenderer), "Resize");

        // A small array is only safe while every write goes through a growth hook.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(GrassInstanceMemoryPatch),
            FixName,
            () => PatchHelper.HasHook(AddInstanceMethod, typeof(GrowthHooks))
                  && PatchHelper.HasHook(SetInstanceMethod, typeof(GrowthHooks))
                  && PatchHelper.HasHook(ResizeMethod, typeof(GrowthHooks)));

        // Another mod could write the array without going through a growth hook: a rewrite of any
        // writer, or a prefix that replaces one of the two that size themselves through Resize.
        private static readonly TakeoverCheck[] Takeovers = {
            Writer(AddInstanceMethod, HookKinds.Transpilers),
            Writer(SetInstanceMethod, HookKinds.Transpilers),
            Writer(ResizeMethod, HookKinds.Transpilers),
            Writer(AccessTools.DeclaredMethod(typeof(InstanceRenderer), nameof(InstanceRenderer.SetInstances)), HookKinds.BoolPrefixes | HookKinds.Transpilers),
            Writer(AccessTools.DeclaredMethod(typeof(InstanceRenderer), nameof(InstanceRenderer.SetInstancesBillboard)), HookKinds.BoolPrefixes | HookKinds.Transpilers),
        };

        private static TakeoverCheck Writer(MethodBase target, HookKinds kinds) => new TakeoverCheck(
            typeof(GrassInstanceMemoryPatch),
            target,
            kinds,
            owners => $"Grass instance arrays are written by {owners}, so '{FixName}' stands down and " +
                      "grass renderers keep the game's full-size array.");

        // Read by the constructor, which Unity can run on a loading thread.
        private static volatile bool _small;

        private static int InitialCapacity() => _small && !ApiSwitch.Off ? SmallCapacity : VanillaCapacity;

        // Replaces `ldc.i4 1024` in `m_instances = new Matrix4x4[1024]` with `InitialCapacity()`.
        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(MethodType.Constructor)]
        private static IEnumerable<CodeInstruction> ConstructorTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (InstancesField == null || InitialCapacityMethod == null) {
                PatchHelper.WarnOnce("InstanceRenderer constructor: a member this fix needs could not be resolved, so it is inactive here.");
                return instructions;
            }

            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replaced = 0;
            for (int i = 0; i + 2 < codes.Count; i++) {
                if (!codes[i].LoadsConstant(VanillaCapacity)) { continue; }
                if (codes[i + 1].opcode != OpCodes.Newarr || !Equals(codes[i + 1].operand, typeof(Matrix4x4))) { continue; }
                if (!codes[i + 2].StoresField(InstancesField)) { continue; }

                // In place, so any label on the constant stays put.
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = InitialCapacityMethod;
                replaced++;
            }

            if (replaced != 1) {
                PatchHelper.WarnOnce(
                    $"InstanceRenderer constructor: expected 1 instance array allocation, found {replaced}, so " +
                    "this fix is inactive. Another mod has most likely already rewritten the constructor - if so, " +
                    "nothing is wrong.");
                return instructions;
            }

            return codes;
        }

        // Decided once every mod has patched, on the main thread, before the first grass is built.
        [HarmonyPatch(typeof(ClutterSystem), "Awake")]
        internal static class ReadyHook {
            [HarmonyPostfix]
            private static void Postfix() {
                bool takenOver = false;
                foreach (TakeoverCheck check in Takeovers) { takenOver |= check.TakenOver; }

                _small = !takenOver && Hooks.Healthy;
            }
        }

        // These run even while the fix is switched off, so arrays already small stay writable.
        [HarmonyPatch(typeof(InstanceRenderer))]
        internal static class GrowthHooks {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            [HarmonyPatch(nameof(InstanceRenderer.AddInstance), typeof(Matrix4x4))]
            private static void AddInstancePrefix(InstanceRenderer __instance) {
                int count = __instance.m_instanceCount;
                Matrix4x4[] instances = __instance.m_instances;
                if (instances != null && count >= instances.Length && count < VanillaCapacity - 1) { Grow(__instance, count + 1); }
            }

            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            [HarmonyPatch(nameof(InstanceRenderer.SetInstance))]
            private static void SetInstancePrefix(InstanceRenderer __instance, int index) {
                if (index >= 0 && index < VanillaCapacity) { Grow(__instance, index + 1); }
            }

            // Past 1,024 vanilla throws while filling, and so does this at the same index.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            [HarmonyPatch("Resize")]
            private static void ResizePrefix(InstanceRenderer __instance, int instances) {
                Grow(__instance, Math.Min(instances, VanillaCapacity));
            }
        }

        // Doubles until the array holds `needed` entries, never past vanilla's size. Copies the whole
        // array, since SetInstance can have written past the count.
        internal static void Grow(InstanceRenderer renderer, int needed) {
            Matrix4x4[] current = renderer.m_instances;
            if (current == null || needed <= current.Length) { return; }

            int size = Math.Max(current.Length, SmallCapacity);
            while (size < needed) { size *= 2; }

            size = Math.Min(size, VanillaCapacity);
            if (size <= current.Length) { return; }

            Matrix4x4[] grown = new Matrix4x4[size];
            Array.Copy(current, grown, current.Length);
            renderer.m_instances = grown;
        }
    }
}
