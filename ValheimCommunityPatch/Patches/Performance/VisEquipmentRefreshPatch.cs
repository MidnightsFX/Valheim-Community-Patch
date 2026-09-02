using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: every character, dropped armour piece and armour stand in the loaded world
    // re-derives its entire equipment appearance from scratch every frame. VisEquipment implements
    // IMonoUpdater, so MonoUpdaters.Update drives VisEquipment.CustomUpdate (MonoUpdaters.cs:66)
    // -> UpdateVisuals (VisEquipment.cs:333-342) for every registered instance, every frame, and
    // that method re-reads and re-applies values that change when somebody swaps a helmet.
    //
    // Two separable costs, fixed separately below.
    //
    // 1. UpdateColors (VisEquipment.cs:345-365) re-applies skin and hair colour every frame for
    //    every player character:
    //
    //      this.m_bodyModel.materials[0].SetColor("_SkinColor", color1);
    //      this.m_bodyModel.materials[1].SetColor("_SkinColor", color2);
    //      foreach (Renderer r in this.m_beardItemInstance.GetComponentsInChildren<Renderer>()) ...
    //      foreach (Renderer r in this.m_hairItemInstance.GetComponentsInChildren<Renderer>()) ...
    //
    //    Renderer.materials allocates a fresh Material[] on every read, so that is two arrays per
    //    player per frame before anything is written, and the two GetComponentsInChildren calls
    //    allocate an array each and walk the beard and hair hierarchies - then set a colour that
    //    has been the same since the character was created. Renderer.material inside those loops
    //    is another native call per renderer per frame.
    //
    //    Fix: run the method only when one of its inputs has actually changed. The prefix computes
    //    exactly what vanilla would compute - the two colours, from the ZDO when there is one and
    //    from the local fields when there is not - and compares them, plus the beard and hair
    //    instances, the body renderer and the model index, against what was last applied. Nothing
    //    is reimplemented: on a miss vanilla's own method runs untouched. The snapshot is recorded
    //    in a postfix, so a vanilla method that threw is retried next frame rather than being
    //    marked as applied.
    //
    // 2. UpdateEquipmentVisuals (VisEquipment.cs:401-495) opens with fifteen ZDO.GetInt calls, and
    //    each one is dearer than it looks. ZDO.GetInt -> ZDOExtraData.GetInt ->
    //    ZDOHelper.GetValueOrDefault (ZDOHelper.cs:138-145), which is
    //
    //      return !container.ContainsKey(zid) ? defaultValue : container[zid].GetValueOrDefault(...)
    //
    //    - two dictionary lookups, not one, and every one of them hashes a ZDOID, which is itself a
    //    List<long> indexer plus two hashes (ZDOID.cs:97-100). Thirty dictionary lookups per
    //    character per frame to read fifteen fields out of one table.
    //
    //    Fix: fetch that character's int table once in a prefix and answer all fifteen reads from
    //    it, by rewriting the GetInt call operand inside this method only. Values are identical -
    //    the accessor calls the same BinarySearchDictionary.GetValueOrDefault vanilla ends up in,
    //    on the same table, having skipped the two lookups that find it.
    //
    //    The accessor keys off ZDO reference identity, which is what makes the scope safe without a
    //    finalizer to unwind it: a nested call for a different ZDO leaves the scope pointing at
    //    that other ZDO, the identity check fails, and the outer call falls back to vanilla's
    //    lookup. The only case that uses the cache is the one where it is provably the right table.
    //
    // Client: this is the equipment rendering pipeline. A dedicated server instantiates almost
    // nothing and draws none of it, and is not patched here, exactly as with the water material and
    // smoke fixes.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(VisEquipment))]
    internal static class VisEquipmentRefreshPatch {
        // ---- 1. Skin and hair colour ------------------------------------------------------------

        /// <summary>Everything UpdateColors reads, so a match means it would write what it wrote last time.</summary>
        internal struct ColorState {
            internal bool Primed;
            internal Vector3 Skin;
            internal Vector3 Hair;
            internal GameObject Beard;
            internal GameObject HairItem;
            internal SkinnedMeshRenderer Body;
            internal int ModelIndex;

            internal bool Matches(ColorState other) =>
                Primed && other.Primed
                && Skin.Equals(other.Skin)
                && Hair.Equals(other.Hair)
                && ReferenceEquals(Beard, other.Beard)
                && ReferenceEquals(HairItem, other.HairItem)
                && ReferenceEquals(Body, other.Body)
                && ModelIndex == other.ModelIndex;
        }

        private static readonly Dictionary<VisEquipment, ColorState> Applied =
            new Dictionary<VisEquipment, ColorState>();

        [HarmonyPrefix]
        [HarmonyPatch("UpdateColors")]
        private static bool UpdateColorsPrefix(VisEquipment __instance, out ColorState __state) {
            __state = default;

            // Vanilla dereferences both of these unguarded. Bail to it rather than decide anything
            // on a state where it would throw - the exception is vanilla's to report, not ours to
            // silently swallow.
            if (__instance.m_nview == null || __instance.m_bodyModel == null) { return true; }

            Vector3 skin = __instance.m_skinColor;
            Vector3 hair = __instance.m_hairColor;

            ZDO zdo = __instance.m_nview.GetZDO();
            if (zdo != null) {
                skin = zdo.GetVec3(ZDOVars.s_skinColor, Vector3.one);
                hair = zdo.GetVec3(ZDOVars.s_hairColor, Vector3.one);
            }

            __state = new ColorState {
                Primed = true,
                Skin = skin,
                Hair = hair,
                Beard = __instance.m_beardItemInstance,
                HairItem = __instance.m_hairItemInstance,
                Body = __instance.m_bodyModel,
                ModelIndex = __instance.m_currentModelIndex,
            };

            // Not a match, or nothing recorded yet: let vanilla apply the colours.
            return !(Applied.TryGetValue(__instance, out ColorState last) && last.Matches(__state));
        }

        // Postfix rather than the prefix, so a throw inside vanilla's method leaves nothing recorded
        // and the next frame tries again.
        [HarmonyPostfix]
        [HarmonyPatch("UpdateColors")]
        private static void UpdateColorsPostfix(VisEquipment __instance, ColorState __state) {
            if (__state.Primed) { Applied[__instance] = __state; }
        }

        // Vanilla unregisters the instance from MonoUpdaters here (VisEquipment.cs:106); the
        // snapshot goes with it, so nothing holds a destroyed character alive.
        [HarmonyPatch(typeof(VisEquipment), "OnDisable")]
        internal static class DisableHook {
            [HarmonyPostfix]
            private static void Postfix(VisEquipment __instance) => Applied.Remove(__instance);
        }

        // ---- 2. Equipment field reads -----------------------------------------------------------

        // The ZDO whose int table the scope below belongs to. Reference identity, never ZDOID
        // equality: this is only ever asked "is this the same object the prefix looked at", and
        // identity answers that without hashing anything.
        private static ZDO _scopeZdo;
        private static BinarySearchDictionary<int, int> _scopeTable;

        private static readonly MethodInfo ZdoGetInt =
            AccessTools.Method(typeof(ZDO), nameof(ZDO.GetInt), new[] { typeof(int), typeof(int) });
        private static readonly MethodInfo ScopedGetIntMethod =
            AccessTools.Method(typeof(VisEquipmentRefreshPatch), nameof(ScopedGetInt));

        [HarmonyPrefix]
        [HarmonyPatch("UpdateEquipmentVisuals")]
        [HarmonyPriority(Priority.First)]
        private static void EquipmentVisualsPrefix(VisEquipment __instance) {
            _scopeZdo = null;
            _scopeTable = null;

            if (__instance.m_nview == null) { return; }

            ZDO zdo = __instance.m_nview.GetZDO();
            if (zdo == null) { return; }

            _scopeZdo = zdo;

            // A miss leaves the table null, which is the same answer vanilla's ContainsKey miss
            // gives: every read of this ZDO returns its default. TryGetValue also absorbs the
            // transient null entry ZDOHelper.Release leaves behind (ZDOHelper.cs:147-156), which
            // vanilla's indexer would have thrown on.
            ZDOExtraData.s_ints.TryGetValue(zdo.m_uid, out _scopeTable);
        }

        /// <summary>
        /// <see cref="ZDO.GetInt(int, int)"/> answered from the table the prefix already found.
        /// </summary>
        private static int ScopedGetInt(ZDO zdo, int hash, int defaultValue) {
            if (!ReferenceEquals(zdo, _scopeZdo)) {
                // Not the ZDO this scope was opened for - a nested call, or another transpiler
                // routing something else through here. Vanilla answers it.
                return zdo == null ? defaultValue : zdo.GetInt(hash, defaultValue);
            }

            return _scopeTable == null ? defaultValue : _scopeTable.GetValueOrDefault(hash, defaultValue);
        }

        // Priority.Last, for the reason in ValheimCommunityPatch.ApplyPatches.
        //
        // Unlike the paired edits in AutoPickupAllocPatch, each swap here stands alone: the accessor
        // falls back to vanilla whenever the ZDO is not the one in scope, so a partial rewrite is
        // correct, just less complete. That is why this tolerates a changed call count instead of
        // backing out - a game update that adds or removes an equipment slot should not silently
        // switch the fix off.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("UpdateEquipmentVisuals")]
        private static IEnumerable<CodeInstruction> EquipmentVisualsTranspiler(
            IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            if (ZdoGetInt == null || ScopedGetIntMethod == null) { return instructions; }

            int replaced = 0;

            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(ZdoGetInt)) { continue; }

                codes[i].opcode = OpCodes.Call;
                codes[i].operand = ScopedGetIntMethod;
                replaced++;
            }

            if (replaced == 0) {
                Logger.LogWarning(
                    "VisEquipment.UpdateEquipmentVisuals: found no ZDO.GetInt calls to route through " +
                    "the cached table, so it keeps vanilla's per-field lookups. Another mod has most " +
                    "likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            Logger.LogDebug($"VisEquipment.UpdateEquipmentVisuals: {replaced} ZDO int read(s) routed through one table lookup.");
            return codes;
        }
    }
}
