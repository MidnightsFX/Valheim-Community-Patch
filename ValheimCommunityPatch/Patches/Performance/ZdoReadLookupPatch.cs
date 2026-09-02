using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Doubled ZDO Lookups: every read of ZDO data searches the per-type dictionary once
    // instead of twice.
    //
    // The four ZDOHelper helpers behind the whole read path ask the dictionary whether a key is
    // present and then ask it again for the value, e.g.
    //
    //   => !container.ContainsKey(zid) ? defaultValue : container[zid].GetValueOrDefault(hash, defaultValue);
    //
    // Both are a full Dictionary.FindEntry over the largest dictionaries in the process, keyed on
    // a ZDOID whose hash is a bounds-checked list read. This sits under every ZDO.GetInt, GetFloat,
    // GetVec3 and GetString in the game, under ZSyncAnimation for every remote character every
    // fixed step, and under ZDO.Serialize, which does sixteen of them per ZDO on every send tick
    // and every save.
    //
    // Four signature-identical replacements answer both questions with one TryGetValue, and a
    // transpiler swaps the call operand inside each of ZDOExtraData's 34 accessors. Equivalence is
    // exact, including a present-but-null table throwing the same NullReferenceException. Patched
    // on the accessors rather than on the generic helpers themselves because Mono compiles one
    // shared body for all reference-type instantiations of a generic, so a patch aimed at
    // GetValueOrDefault<string> would land on GetValueOrDefault<byte[]> too.
    //
    // Both: hardest on the server, under every ZDO send and every world save.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class ZdoReadLookupPatch {
        // Every accessor on ZDOExtraData that reaches one of the four helpers. The seven scalar
        // getters each have an out-parameter and a default-value overload, so 27 names cover 34
        // methods.
        private static readonly HashSet<string> AccessorNames = new HashSet<string> {
            "GetFloat", "GetVec3", "GetQuaternion", "GetInt", "GetLong", "GetString", "GetByteArray",
            "GetBool",
            "GetConnection", "GetConnectionZDOID", "GetConnectionType", "GetConnectionHashData",
            "GetFloats", "GetVec3s", "GetQuaternions", "GetInts", "GetLongs", "GetStrings", "GetByteArrays",
            "GetSaveFloats", "GetSaveVec3s", "GetSaveQuaternions", "GetSaveInts", "GetSaveLongs",
            "GetSaveStrings", "GetSaveByteArrays", "GetSaveConnections",
        };

        private const int ExpectedAccessors = 34;

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods() {
            int found = 0;

            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(ZDOExtraData))) {
                if (!method.IsStatic || !AccessorNames.Contains(method.Name)) { continue; }
                found++;
                yield return method;
            }

            if (found != ExpectedAccessors) {
                Logger.LogWarning(
                    $"ZDOExtraData: expected {ExpectedAccessors} data accessors, found {found}. The ones " +
                    "that were found are still fixed; a Valheim update has most likely added or removed " +
                    "an accessor, and this fix now covers a different share of the read path.");
            }
        }

        // ---- Single-lookup replacements. Signatures match ZDOHelper's exactly. -------------------

        private static TType GetValueOrDefault<TType>(
            Dictionary<ZDOID, BinarySearchDictionary<int, TType>> container,
            ZDOID zid, int hash, TType defaultValue) {
            return container.TryGetValue(zid, out BinarySearchDictionary<int, TType> table)
                ? table.GetValueOrDefault(hash, defaultValue)
                : defaultValue;
        }

        private static bool GetValue<TType>(
            Dictionary<ZDOID, BinarySearchDictionary<int, TType>> container,
            ZDOID zid, int hash, out TType value) {
            if (container.TryGetValue(zid, out BinarySearchDictionary<int, TType> table)) {
                return table.TryGetValue(hash, out value);
            }

            value = default;
            return false;
        }

        // Same Enumerable.ToList vanilla calls, and on a miss the same fresh zero-capacity list.
        private static List<KeyValuePair<int, TType>> GetValuesOrEmpty<TType>(
            Dictionary<ZDOID, BinarySearchDictionary<int, TType>> container, ZDOID zid) {
            return container.TryGetValue(zid, out BinarySearchDictionary<int, TType> table)
                ? table.ToList()
                : new List<KeyValuePair<int, TType>>();
        }

        // Named after vanilla's helper so Pair() can match it by name. Keeps the IDictionary
        // parameter so the swap is a pure operand replacement.
        private static TValue GetValueOrDefaultPiktiv<TKey, TValue>(
            IDictionary<TKey, TValue> container, TKey zid, TValue defaultValue) {
            return container.TryGetValue(zid, out TValue value) ? value : defaultValue;
        }

        // ---- The swap ---------------------------------------------------------------------------

        private static readonly Dictionary<MethodInfo, MethodInfo> Replacements = BuildReplacements();

        private static Dictionary<MethodInfo, MethodInfo> BuildReplacements() {
            var map = new Dictionary<MethodInfo, MethodInfo>();

            Pair(map, nameof(ZDOHelper.GetValueOrDefault));
            Pair(map, nameof(ZDOHelper.GetValue));
            Pair(map, nameof(ZDOHelper.GetValuesOrEmpty));
            Pair(map, nameof(ZDOHelper.GetValueOrDefaultPiktiv));

            return map;
        }

        // Plain reflection rather than AccessTools: these are open generic definitions, which
        // GetMethod returns directly and which the transpiler compares against.
        private static void Pair(Dictionary<MethodInfo, MethodInfo> map, string name) {
            MethodInfo vanilla = typeof(ZDOHelper).GetMethod(
                name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            MethodInfo ours = typeof(ZdoReadLookupPatch).GetMethod(
                name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);

            if (vanilla == null || ours == null) {
                Logger.LogWarning(
                    $"ZDOHelper.{name} could not be paired with a single-lookup replacement, so reads " +
                    "through it keep vanilla's doubled lookup. The other helpers are unaffected.");
                return;
            }

            map[vanilla] = ours;
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> AccessorTranspiler(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);
            if (Replacements.Count == 0) { return instructions; }

            int replaced = 0;

            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode != OpCodes.Call && codes[i].opcode != OpCodes.Callvirt) { continue; }
                if (!(codes[i].operand is MethodInfo called) || !called.IsGenericMethod) { continue; }
                if (!Replacements.TryGetValue(called.GetGenericMethodDefinition(), out MethodInfo ours)) { continue; }

                // Instantiated to match the call being replaced, so the operand swap is all there is.
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = ours.MakeGenericMethod(called.GetGenericArguments());
                replaced++;
            }

            if (replaced != 1) {
                Logger.LogWarning(
                    $"ZDOExtraData.{__originalMethod?.Name}: expected 1 ZDO lookup helper call, found " +
                    $"{replaced}, so that accessor keeps vanilla's doubled lookup. Another mod has most " +
                    "likely already rewritten it - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
