using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Trim ZDO Data Pool: released ZDO field tables stop pinning their old contents, and the
    // pools they return to get a ceiling.
    //
    // A ZDO's fields of one type live in a BinarySearchDictionary, and when the ZDO is destroyed
    // or its last field of that type removed, ZDOHelper hands the table to Pool<T>, a static
    // stack with no limit and no trim. BinarySearchDictionary.Clear only zeroes its length: the
    // key and value arrays stay allocated, and for strings and byte arrays the value array keeps
    // the old payloads alive. So each pool holds the high-water mark of tables ever live at once,
    // plus whatever strings and blobs those tables last held. A plateau rather than a climb, but
    // one the process never comes down from.
    //
    // A transpiler on ZDOExtraData's release and remove entry points swaps ZDOHelper.Release and
    // ZDOHelper.Remove(zid, hash) for versions that do the same work and then, when the fix is on,
    // clear a reference-typed table's value array before pooling it, and drop the table instead
    // of pooling it once that type's pool holds 'ZDO Data Pool Max Depth'. Patched at the callers
    // rather than on the generic helpers because Mono compiles one body for all reference-type
    // instantiations, so a patch aimed at the string version would land on the byte-array one.
    // With the fix off the replacements do exactly what vanilla does.
    //
    // Server: the tables and their pools are largest where the whole world is loaded. Off by default.
    [PatchSide(Side.Server)]
    [HarmonyPatch]
    internal static class ZdoDataPoolTrimPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> MaxDepth;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ZdoDataPoolTrimPatch),
                ValConfig.SectionServerMemory,
                "Trim ZDO Data Pool",
                false,
                "Caps the pools that recycle objects' field tables, and clears the strings and byte " +
                "arrays a recycled table still points at. Without it the pools hold as many tables as " +
                "were ever in use at once, each keeping its last contents alive, so memory never " +
                "comes back down from the busiest moment since the world loaded. Off by default.");

            MaxDepth = ValConfig.BindServerConfig(
                ValConfig.SectionServerMemory,
                "ZDO Data Pool Max Depth",
                4096,
                "Tables of each field type kept for reuse. Tables released beyond this are left to " +
                "the garbage collector; new ones are created again when needed.",
                advanced: true,
                valMin: 0,
                valMax: 100000);
        }

        private static long _dropped;

        /// <summary>Tables left to the garbage collector instead of pooled, for the stats line.</summary>
        internal static long Dropped => _dropped;

        /// <summary>Depth of one type's pool, or -1 when it could not be reached.</summary>
        internal static int PoolDepth<T>() => PoolOf<T>.Stack?.Count ?? -1;

        // ---- the pool behind each type -----------------------------------------------------

        // Resolved once per closed type. A generic static class keeps its own statics per
        // instantiation even where Mono shares the code, so the string and byte[] pools are told
        // apart here even though the helpers below run one body for both.
        internal static class PoolOf<T> {
            internal static readonly Stack<BinarySearchDictionary<int, T>> Stack = ResolveStack();
            internal static readonly AccessTools.FieldRef<BinarySearchDictionary<int, T>, T[]> Values = ResolveValues();
            internal static readonly bool IsReference = !typeof(T).IsValueType;

            private static Stack<BinarySearchDictionary<int, T>> ResolveStack() {
                try {
                    FieldInfo field = AccessTools.DeclaredField(typeof(Pool<BinarySearchDictionary<int, T>>), "s_available");
                    return field?.GetValue(null) as Stack<BinarySearchDictionary<int, T>>;
                } catch (Exception) {
                    return null;
                }
            }

            private static AccessTools.FieldRef<BinarySearchDictionary<int, T>, T[]> ResolveValues() {
                try {
                    return AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, T[]>("m_values");
                } catch (Exception) {
                    return null;
                }
            }
        }

        // ---- the replacements. Signatures match ZDOHelper's exactly. -------------------------

        private static void Release<T>(Dictionary<ZDOID, BinarySearchDictionary<int, T>> container, ZDOID zid) {
            if (!container.TryGetValue(zid, out BinarySearchDictionary<int, T> table)) { return; }

            table.Clear();
            container.Remove(zid);
            Recycle(table);
        }

        private static bool Remove<T>(Dictionary<ZDOID, BinarySearchDictionary<int, T>> container, ZDOID id, int hash) {
            if (!container.TryGetValue(id, out BinarySearchDictionary<int, T> table)) { return false; }
            if (!table.Remove(hash)) { return false; }

            if (table.Count == 0) {
                container.Remove(id);
                Recycle(table);
            }

            return true;
        }

        private static void Recycle<T>(BinarySearchDictionary<int, T> table) {
            Stack<BinarySearchDictionary<int, T>> pool = PoolOf<T>.Stack;

            if (pool == null || Enabled == null || !Enabled.Value || !RunMode.IsServer) {
                Pool<BinarySearchDictionary<int, T>>.Release(table);
                return;
            }

            int cap = MaxDepth != null ? MaxDepth.Value : 4096;

            // The same lock Pool<T> takes, since this pushes onto its stack directly.
            lock (pool) {
                if (pool.Count >= cap) {
                    _dropped++;
                    return;
                }

                if (PoolOf<T>.IsReference && PoolOf<T>.Values != null) {
                    T[] values = PoolOf<T>.Values(table);
                    if (values != null) { Array.Clear(values, 0, values.Length); }
                }

                pool.Push(table);
            }
        }

        // ---- the swap -----------------------------------------------------------------------

        private static readonly MethodInfo VanillaRelease = FindHelper("Release", 2);
        private static readonly MethodInfo VanillaRemove = FindHelper("Remove", 3);
        private static readonly MethodInfo OurRelease = AccessTools.DeclaredMethod(typeof(ZdoDataPoolTrimPatch), nameof(Release));
        private static readonly MethodInfo OurRemove = AccessTools.DeclaredMethod(typeof(ZdoDataPoolTrimPatch), nameof(Remove));

        // The (container, zid) and (container, id, hash) overloads; ZDOHelper also has Remove
        // overloads taking a hash list, which never release a table.
        private static MethodInfo FindHelper(string name, int parameters) {
            foreach (MethodInfo method in typeof(ZDOHelper).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if (method.Name != name || !method.IsGenericMethodDefinition) { continue; }

                ParameterInfo[] p = method.GetParameters();
                if (p.Length != parameters || p[1].ParameterType != typeof(ZDOID)) { continue; }

                return method;
            }

            return null;
        }

        // Every ZDOExtraData method whose body calls one of the two helpers: the seven ReleaseX
        // methods, the seven RemoveX methods and the untyped Remove. Found by reading their IL
        // rather than by name, so a renamed accessor is still covered.
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods() {
            if (VanillaRelease == null || VanillaRemove == null || OurRelease == null || OurRemove == null) {
                Logger.LogWarning(
                    "ZDOHelper.Release or Remove could not be resolved, so Trim ZDO Data Pool is inactive here.");
                yield break;
            }

            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(ZDOExtraData))) {
                if (!method.IsStatic) { continue; }

                bool calls = false;
                try {
                    foreach (KeyValuePair<OpCode, object> instruction in PatchProcessor.ReadMethodBody(method)) {
                        if (Targets(instruction.Value as MethodInfo)) {
                            calls = true;
                            break;
                        }
                    }
                } catch (Exception ex) {
                    Logger.LogDebug($"Could not read the body of ZDOExtraData.{method.Name}: {ex.Message}");
                }

                if (calls) { yield return method; }
            }
        }

        private static bool Targets(MethodInfo called) {
            if (called == null || !called.IsGenericMethod) { return false; }

            MethodInfo definition = called.GetGenericMethodDefinition();
            return definition == VanillaRelease || definition == VanillaRemove;
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> ReleaseTranspiler(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replaced = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode != OpCodes.Call && codes[i].opcode != OpCodes.Callvirt) { continue; }
                if (!(codes[i].operand is MethodInfo called) || !Targets(called)) { continue; }

                MethodInfo ours = called.GetGenericMethodDefinition() == VanillaRelease ? OurRelease : OurRemove;
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = ours.MakeGenericMethod(called.GetGenericArguments());
                replaced++;
            }

            if (replaced == 0) {
                Logger.LogWarning(
                    $"ZDOExtraData.{__originalMethod?.Name}: expected a ZDOHelper release call, found none, " +
                    "so tables released there keep vanilla's pooling. Another mod has most likely " +
                    "already rewritten it - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
