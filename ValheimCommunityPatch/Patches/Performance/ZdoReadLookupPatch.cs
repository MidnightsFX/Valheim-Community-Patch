using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: every read of ZDO data looks the ZDO up twice. The four helpers that back the
    // entire read path all ask the dictionary whether a key is present and then ask it again for the
    // value (ZDOHelper.cs:64-70, :123-145):
    //
    //   public static TType GetValueOrDefault<TType>(this Dictionary<ZDOID, BinarySearchDictionary<int, TType>> container, ...)
    //     => !container.ContainsKey(zid) ? defaultValue : container[zid].GetValueOrDefault(hash, defaultValue);
    //
    //   public static bool GetValue<TType>(...) {
    //     if (container.ContainsKey(zid)) { return container[zid].TryGetValue(hash, out value); }
    //     value = default; return false;
    //   }
    //
    // ContainsKey and the indexer are both a full Dictionary.FindEntry: hash the key, probe the
    // bucket chain, compare. The second one buys nothing - it re-derives an answer the first one
    // already had and threw away.
    //
    // Two things make that worse than it sounds. The key is a ZDOID, whose GetHashCode is
    // `ZDOID.GetUserID(this.UserKey).GetHashCode() ^ this.ID.GetHashCode()` (ZDOID.cs:97-100), and
    // GetUserID is a List<long> indexer (ZDOID.cs:46) - so hashing is a bounds-checked heap read
    // before any probing starts, and it happens twice. And these are the largest dictionaries in
    // the process: one entry per ZDO that holds a field of that type, so on a long-lived world the
    // buckets and entries arrays are megabytes and the probe is a cold read.
    //
    // This sits under everything. ZDO.GetInt/GetFloat/GetVec3/GetString/GetBool and their
    // out-parameter forms all land here, so it is paid by every WearNTear health read, every
    // Fireplace fuel read, every Pickable and Plant and Container check, and by
    // ZSyncAnimation.SyncParameters, which reads every animation parameter of every character this
    // machine does NOT own on every fixed step (ZSyncAnimation.cs:99-121). ZDO.Serialize
    // (ZDO.cs:461-470) does seven of the list forms plus a connection read - sixteen lookups per
    // ZDO - and runs for every ZDO sent to every peer on every send tick, and again for every ZDO
    // in the world on every save.
    //
    // Fix: one lookup. TryGetValue answers both questions in a single FindEntry. The four
    // replacements below are signature-identical drop-ins for the vanilla helpers, so the edit at
    // each call site is a call-operand swap with no change to the stack - which also means no
    // Harmony prefix dispatch is added to methods this small. Nothing is cached and no state is
    // kept, so there is nothing to invalidate and no Verify toggle to justify: this is the same
    // question asked once instead of twice.
    //
    // Equivalence is exact, including the two edge cases worth naming. An entry that is present but
    // whose table is null - the transient ZDOHelper.Release leaves behind at ZDOHelper.cs:151-155 -
    // throws a NullReferenceException in vanilla off `container[zid].GetValueOrDefault(...)` and
    // throws the identical one here off the fetched reference, rather than being quietly absorbed.
    // And GetValuesOrEmpty's miss path returns `Array.Empty<...>().ToList()`, a fresh empty List
    // with zero capacity, which is what `new List<...>()` is; the hit path calls the same
    // Enumerable.ToList on the same table.
    //
    // Patched on ZDOExtraData's accessors rather than on the four ZDOHelper helpers themselves, and
    // that is a correctness requirement, not a preference. The helpers are generic methods, and Mono
    // compiles one shared body for all reference-type instantiations of a generic - so a patch aimed
    // at GetValueOrDefault<string> would land on GetValueOrDefault<byte[]> too. Emitting a *call* to
    // our own instantiation is free of that problem; patching vanilla's is not. Same reasoning as
    // ZdoValueWriteAllocPatch, and it is why this covers string and byte[] reads where that fix
    // could not. The 34 accessors are selected by name off ZDOExtraData and every one of them was
    // checked to contain exactly one helper call; a name that stops matching is logged and skipped,
    // and a method that turns out to contain no helper call keeps vanilla's IL.
    //
    // Composition: VisEquipmentRefreshPatch routes UpdateEquipmentVisuals' fifteen reads around
    // ZDOExtraData.GetInt entirely, at one table lookup for all fifteen, so that path stays ahead of
    // this one and the two do not interact.
    //
    // Deliberately not included, both the same defect and both worth their own round: the write path
    // (ZDOHelper.InitAndSet calls Init, which is a ContainsKey, and then indexes - two lookups on an
    // existing field, three when adding one), and ZDOExtraData.GetOwner (ZDOExtraData.cs:204-207),
    // which spells the double lookup out inline rather than going through a helper and so needs a
    // different edit. ZDO.IsOwner does not go near it - that is a flag test on the ZDO itself
    // (ZDO.cs:984, :1036-1038) - which is what makes GetOwner cold enough to leave for now.
    //
    // Both: this is the ZDO data layer with no GameObject in sight. The server pays it hardest, on
    // the serialize path under every ZDO send and every world save.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class ZdoReadLookupPatch {
        // Every accessor on ZDOExtraData that reaches one of the four helpers, by name. 27 names,
        // 34 methods - GetFloat, GetVec3, GetQuaternion, GetInt, GetLong, GetString and GetByteArray
        // each have both an out-parameter and a default-value overload, and both are wanted.
        private static readonly HashSet<string> AccessorNames = new HashSet<string> {
            "GetFloat", "GetVec3", "GetQuaternion", "GetInt", "GetLong", "GetString", "GetByteArray",
            "GetBool",
            "GetConnection", "GetConnectionZDOID", "GetConnectionType", "GetConnectionHashData",
            "GetFloats", "GetVec3s", "GetQuaternions", "GetInts", "GetLongs", "GetStrings", "GetByteArrays",
            "GetSaveFloats", "GetSaveVec3s", "GetSaveQuaternions", "GetSaveInts", "GetSaveLongs",
            "GetSaveStrings", "GetSaveByteArrays", "GetSaveConnections",
        };

        // What the count below is checked against, so a game update that adds or removes an accessor
        // says so in the log instead of silently changing how much of the read path is covered.
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

        private static List<KeyValuePair<int, TType>> GetValuesOrEmpty<TType>(
            Dictionary<ZDOID, BinarySearchDictionary<int, TType>> container, ZDOID zid) {
            // Enumerable.ToList, as vanilla calls it, on the same table; and on a miss the same
            // fresh zero-capacity List its Array.Empty<...>().ToList() produces. The allocation
            // vanilla makes here is not this fix's business and is left exactly as it is.
            return container.TryGetValue(zid, out BinarySearchDictionary<int, TType> table)
                ? table.ToList()
                : new List<KeyValuePair<int, TType>>();
        }

        // Keeps vanilla's IDictionary parameter rather than narrowing to Dictionary: the call sites
        // pass a Dictionary either way, and matching the declared signature keeps this a pure
        // operand swap. One interface call replaces two.
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

        // Plain reflection rather than AccessTools: these are generic method *definitions*, and
        // asking AccessTools for one without supplying type arguments is a case its overloads
        // handle inconsistently across Harmony versions. GetMethod returns the open definition,
        // which is exactly what the transpiler compares GetGenericMethodDefinition() against.
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

        // Priority.Last, for the reason in ValheimCommunityPatch.ApplyPatches.
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

                // Instantiated to match the call being replaced - <TType> for three of them,
                // <TKey, TValue> for the connection helper - so the operand swap is all there is.
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
