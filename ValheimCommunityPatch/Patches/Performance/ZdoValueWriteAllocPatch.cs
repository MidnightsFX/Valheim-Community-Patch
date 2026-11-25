using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix ZDO Value Write Allocation: BinarySearchDictionary.SetValue boxes every ZDO value it writes to compare it.
    // Transpiles SetValue for float, Vector3, Quaternion, int and long to call the type's own Equals without boxing.
    // Every ZDO field write in the world (velocity, animation, health, fuel, growth) stops allocating garbage.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class ZdoValueWriteAllocPatch {
        // string and byte[] never boxed, and Mono shares one compiled body across reference-type
        // instantiations, so a patch aimed at one would land on the other.
        private static readonly Type[] BoxedValueTypes = {
            typeof(float), typeof(Vector3), typeof(Quaternion), typeof(int), typeof(long),
        };

        private static readonly MethodInfo ObjectEquals =
            AccessTools.Method(typeof(object), nameof(object.Equals), new[] { typeof(object) });

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods() {
            foreach (Type valueType in BoxedValueTypes) {
                MethodInfo setValue = AccessTools.Method(
                    typeof(BinarySearchDictionary<,>).MakeGenericType(typeof(int), valueType),
                    "SetValue",
                    new[] { typeof(int), valueType });

                if (setValue == null) {
                    Logger.LogWarning(
                        $"BinarySearchDictionary<int, {valueType.Name}>.SetValue could not be resolved, so " +
                        "ZDO writes of that type keep vanilla's boxed comparison. The other types are " +
                        "unaffected.");
                    continue;
                }

                yield return setValue;
            }
        }

        // SetValue (assembly_utils) compares m_values[keyIndex].Equals((object)value), a box since TValue is
        // unconstrained. Rewritten to `call TValue.Equals(TValue)`: the receiver is already a managed pointer
        // from ldelema, and each type's Equals(object) is a type test then the typed overload, so the answer
        // is identical, NaN included. One transpiler serves all five targets: the value type comes off
        // __originalMethod's declaring type. Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> SetValueTranspiler(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            Type declaring = __originalMethod?.DeclaringType;
            if (declaring == null || !declaring.IsGenericType) { return instructions; }

            Type valueType = declaring.GetGenericArguments()[1];

            // A reference type here would be a body Mono may share with other instantiations.
            if (!valueType.IsValueType) { return instructions; }

            MethodInfo typedEquals = AccessTools.Method(valueType, nameof(object.Equals), new[] { valueType });
            if (typedEquals == null || ObjectEquals == null) {
                PatchHelper.WarnOnce(
                    $"{valueType.Name} has no Equals({valueType.Name}) overload to route the ZDO write " +
                    "comparison through, so that type keeps vanilla's boxed comparison.");
                return instructions;
            }

            int rewritten = 0;

            // Anchored on the Equals call and walking back over `constrained.` (which Harmony may
            // surface as its own instruction or fold into the call) to the `box`.
            for (int i = 1; i < codes.Count; i++) {
                if (!codes[i].Calls(ObjectEquals)) { continue; }

                int prefix = codes[i - 1].opcode == OpCodes.Constrained ? i - 1 : i;
                int box = prefix - 1;
                if (box < 0 || codes[box].opcode != OpCodes.Box) { continue; }

                // The operand is normally the concrete type; an unsubstituted generic parameter is
                // still this method's TValue.
                if (codes[box].operand is Type boxed && boxed != valueType && !boxed.IsGenericParameter) {
                    continue;
                }

                // Nop rather than remove, so any label or exception block boundary survives.
                codes[box].opcode = OpCodes.Nop;
                codes[box].operand = null;

                if (prefix != i) {
                    codes[prefix].opcode = OpCodes.Nop;
                    codes[prefix].operand = null;
                }

                codes[i].opcode = OpCodes.Call;
                codes[i].operand = typedEquals;

                rewritten++;
            }

            if (rewritten != 1) {
                PatchHelper.WarnOnce(
                    $"BinarySearchDictionary<int, {valueType.Name}>.SetValue: expected 1 boxed equality " +
                    $"check, found {rewritten}, so ZDO writes of that type keep vanilla's allocation. " +
                    "Another mod has most likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
