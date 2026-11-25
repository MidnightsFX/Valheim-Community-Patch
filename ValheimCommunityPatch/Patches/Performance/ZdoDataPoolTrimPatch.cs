using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // ZDO field table pools never shrink, and each pooled table keeps its old strings and byte arrays alive.
    // Transpiles ZDOExtraData's release and remove methods to clear reference-typed tables and cap each pool's depth.
    // Server memory can come back down after a busy period instead of holding the peak since the world loaded.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class ZdoDataPoolTrimPatch {
        internal static FixToggle Enabled;
        internal static ConfigEntry<int> MaxDepth;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ZdoDataPoolTrimPatch),
                ValConfig.SectionServerMemory,
                "Trim ZDO Data Pool",
                false,
                "Caps the pools that recycle objects' field tables and clears the strings and byte arrays a " +
                "recycled table still holds, so server memory can come back down after a busy period.");

            MaxDepth = ValConfig.BindServerConfig(
                ValConfig.SectionServerMemory,
                "ZDO Data Pool Max Depth",
                4096,
                "Field tables of each type kept for reuse; any released beyond this are left to the garbage " +
                "collector, and 0 keeps none.",
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

            // Vanilla's pooling when off, and off a server: the pools only grow large where the whole world is loaded.
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

                // BinarySearchDictionary.Clear only zeroes its length, so the value array still holds the old strings
                // and byte arrays.
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
        // rather than by name, so a renamed accessor is still covered. Patched at these callers
        // rather than on the generic helpers because Mono compiles one body for all reference-type
        // instantiations, so a patch aimed at the string version would land on the byte-array one.
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
                PatchHelper.WarnOnce(
                    $"ZDOExtraData.{__originalMethod?.Name}: expected a ZDOHelper release call, found none, " +
                    "so tables released there keep vanilla's pooling. Another mod has most likely " +
                    "already rewritten it - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
