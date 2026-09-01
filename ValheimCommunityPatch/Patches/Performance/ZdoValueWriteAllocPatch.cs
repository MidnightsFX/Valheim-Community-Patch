using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: every write of a ZDO field allocates, to answer a question that needs no
    // allocation at all. BinarySearchDictionary<TKey, TValue>.SetValue (assembly_utils) opens with:
    //
    //   int keyIndex = this.BinaryFindKeyIndex(key, out bool exactMatch);
    //   if (exactMatch) {
    //     if (this.m_values[keyIndex].Equals((object) value)) { return false; }
    //     ...
    //
    // That cast is a box. TValue is an unconstrained type parameter, so the compiler resolves
    // Equals to object.Equals(object) and emits `box !TValue` for the argument - one heap
    // allocation, 24-32 bytes, every time the method is asked "did this value change?".
    //
    // And that method is the *only* way ZDO data is written. ZDO.Set(hash, value) ->
    // ZDOExtraData.Set(zid, hash, value) (ZDOExtraData.cs:119-156) -> ZDOHelper.InitAndSet
    // (ZDOHelper.cs:72-80) -> BinarySearchDictionary.SetValue. So the box is paid by every
    // ZSyncTransform velocity write, every m_syncBodyVelocity pair (two Vector3 per rigidbody
    // per FixedUpdate, unconditional - ZSyncTransform.cs:185-186), every animation parameter
    // that moves, every health, fuel, growth and state write in the game. The allocation is
    // pure waste: the boxed object is compared and dropped on the same line.
    //
    // Fix: call the value type's own strongly typed Equals overload instead, which every one of
    // these types has. The transpiler drops the `box` and rewrites `constrained. !TValue` +
    // `callvirt object::Equals(object)` into `call instance bool TValue::Equals(TValue)`. The
    // receiver is already a managed pointer from the preceding ldelema, which is exactly the
    // `this` a call on a value type wants, so the whole edit is three instructions and the stack
    // shape does not change.
    //
    // Same answer, not merely a similar one, and that matters for float and Vector3. Single.Equals
    // (Single) is NOT `==`: it reports NaN equal to NaN, exactly as Single.Equals(object) does for
    // a float argument. Using `==` here would flip a NaN-to-NaN write from "unchanged" to
    // "changed", marking the ZDO dirty and re-syncing it forever. Every type below defines
    // Equals(object) as a type test followed by a call to the typed overload, so routing straight
    // to the typed overload is the same comparison with the box removed.
    //
    // Only the five value-type instantiations are patched. string and byte[] do not box, so there
    // is nothing to win there, and patching them would be actively unsafe: Mono shares one
    // compiled body across all reference-type instantiations of a generic, so a patch aimed at
    // <int, string> would land on <int, byte[]> as well. Value-type instantiations each get their
    // own body, which is what makes this targetable at all.
    //
    // Provenance: the boxed comparison was first identified and patched by R4V9N1's Terramizer
    // (BinarySearchDictionarySetValuePatch), which replaces SetValue outright with a hand-written
    // copy driven by reflected field refs. This does the same removal as a three-instruction IL
    // edit instead, so the growth policy, the binary search and the ordering all stay vanilla's
    // and cannot drift from them on a game update.
    //
    // Both: this is the ZDO data layer with no GameObject anywhere near it. A dedicated server
    // writes ZDO values on world load, on ownership changes and for everything it owns.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class ZdoValueWriteAllocPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ZdoValueWriteAllocPatch),
                ValConfig.SectionPerformance,
                "Fix ZDO Value Write Allocation",
                true,
                "Stops every ZDO field write allocating an object just to check whether the value " +
                "changed. Vanilla boxes the new value on the way into that comparison, so a moving " +
                "creature, a burning fire and a growing crop all produce steady garbage for no " +
                "benefit. The comparison itself is unchanged, NaN handling included. Changing this " +
                "requires a game restart.");
        }

        // The value types ZDO data is held in - the five BinarySearchDictionary<int, T>
        // instantiations behind ZDOExtraData's s_floats, s_vec3, s_quats, s_ints and s_longs
        // (ZDOExtraData.cs:18-22). s_strings and s_byteArrays are deliberately absent; see header.
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

        // Priority.Last, for the reason in ValheimCommunityPatch.ApplyPatches.
        //
        // __originalMethod is what makes one transpiler serve all five instantiations: the value
        // type comes off the declaring type's generic arguments, so the typed Equals overload is
        // resolved per target rather than guessed.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> SetValueTranspiler(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);
            if (Enabled == null || !Enabled.Value) { return codes; }

            Type declaring = __originalMethod?.DeclaringType;
            if (declaring == null || !declaring.IsGenericType) { return instructions; }

            Type valueType = declaring.GetGenericArguments()[1];

            // Belt and braces against the shared-generic hazard in the header: a reference type
            // here would mean we are looking at a body Mono may also run for other instantiations.
            if (!valueType.IsValueType) { return instructions; }

            MethodInfo typedEquals = AccessTools.Method(valueType, nameof(object.Equals), new[] { valueType });
            if (typedEquals == null || ObjectEquals == null) {
                Logger.LogWarning(
                    $"{valueType.Name} has no Equals({valueType.Name}) overload to route the ZDO write " +
                    "comparison through, so that type keeps vanilla's boxed comparison.");
                return instructions;
            }

            int rewritten = 0;

            // Anchored on the call rather than the box, and walking back over the prefix, because
            // the prefix is the part that varies. The compiled shape is
            //
            //   readonly. ldelema !TValue / ldarg.2 / box !TValue / constrained. !TValue /
            //   callvirt bool object::Equals(object)
            //
            // but whether Harmony surfaces `constrained.` as an instruction of its own or folds it
            // into the call is its business, not ours, and either layout is handled here.
            for (int i = 1; i < codes.Count; i++) {
                if (!codes[i].Calls(ObjectEquals)) { continue; }

                int prefix = codes[i - 1].opcode == OpCodes.Constrained ? i - 1 : i;
                int box = prefix - 1;
                if (box < 0 || codes[box].opcode != OpCodes.Box) { continue; }

                // Resolved against the instantiation this body is being emitted for, so the operand
                // is normally the concrete type; an unsubstituted type parameter is still this
                // method's TValue and is equally fine to unbox from.
                if (codes[box].operand is Type boxed && boxed != valueType && !boxed.IsGenericParameter) {
                    continue;
                }

                // Nop rather than remove: either instruction may be carrying a branch label or an
                // exception block boundary, and both survive an opcode swap.
                codes[box].opcode = OpCodes.Nop;
                codes[box].operand = null;

                if (prefix != i) {
                    codes[prefix].opcode = OpCodes.Nop;
                    codes[prefix].operand = null;
                }

                // The receiver is already a managed pointer from the ldelema above, which is what a
                // call on a value type wants for `this`; only the boxed argument goes away.
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = typedEquals;

                rewritten++;
            }

            if (rewritten != 1) {
                Logger.LogWarning(
                    $"BinarySearchDictionary<int, {valueType.Name}>.SetValue: expected 1 boxed equality " +
                    $"check, found {rewritten}, so ZDO writes of that type keep vanilla's allocation. " +
                    "Another mod has most likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            Logger.LogDebug($"BinarySearchDictionary<int, {valueType.Name}>.SetValue: boxed equality check removed.");
            return codes;
        }
    }
}
