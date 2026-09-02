using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Equipment Visual Refresh: characters stop re-applying unchanged skin and hair colours
    // every frame, and read their equipment fields with one table lookup instead of thirty.
    //
    // VisEquipment.UpdateVisuals runs every frame for every character, dropped armour piece and
    // armour stand. Two separable costs:
    //
    // 1. UpdateColors re-applies skin and hair colour every frame. Renderer.materials allocates a
    //    Material[] on each of its two reads, and the beard and hair GetComponentsInChildren calls
    //    allocate and walk a hierarchy, all to write a colour that has not changed since the
    //    character was created. A prefix computes what vanilla would compute (the two colours,
    //    the beard, hair and body renderers, the model index), compares it with what was last
    //    applied, and only lets vanilla run on a difference. The snapshot is recorded in a
    //    postfix so a vanilla method that threw is retried rather than marked applied.
    //
    // 2. UpdateEquipmentVisuals opens with fifteen ZDO.GetInt calls, and each is two dictionary
    //    lookups (ZDOHelper.GetValueOrDefault does ContainsKey then indexes) hashing a ZDOID
    //    twice. A prefix fetches the character's int table once, and a transpiler routes the
    //    fifteen reads inside this method through it. The accessor keys on ZDO reference
    //    identity, so a nested call for a different ZDO falls back to vanilla's lookup.
    //
    // Client: this is the equipment rendering pipeline.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(VisEquipment))]
    internal static class VisEquipmentRefreshPatch {
        // ---- 1. Skin and hair colour ------------------------------------------------------------

        /// <summary>Everything UpdateColors reads; a match means it would write what it wrote last time.</summary>
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

            // Vanilla dereferences both unguarded; let it throw rather than decide on that state.
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

            return !(Applied.TryGetValue(__instance, out ColorState last) && last.Matches(__state));
        }

        [HarmonyPostfix]
        [HarmonyPatch("UpdateColors")]
        private static void UpdateColorsPostfix(VisEquipment __instance, ColorState __state) {
            if (__state.Primed) { Applied[__instance] = __state; }
        }

        // Vanilla unregisters the instance from MonoUpdaters here; the snapshot goes with it.
        [HarmonyPatch(typeof(VisEquipment), "OnDisable")]
        internal static class DisableHook {
            [HarmonyPostfix]
            private static void Postfix(VisEquipment __instance) => Applied.Remove(__instance);
        }

        // ---- 2. Equipment field reads -----------------------------------------------------------

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

            // A miss leaves the table null, which answers every read with its default exactly as
            // vanilla's ContainsKey miss does.
            ZDOExtraData.s_ints.TryGetValue(zdo.m_uid, out _scopeTable);
        }

        // ZDO.GetInt(int, int) answered from the table the prefix found, or by vanilla for any
        // other ZDO.
        private static int ScopedGetInt(ZDO zdo, int hash, int defaultValue) {
            if (!ReferenceEquals(zdo, _scopeZdo)) {
                return zdo == null ? defaultValue : zdo.GetInt(hash, defaultValue);
            }

            return _scopeTable == null ? defaultValue : _scopeTable.GetValueOrDefault(hash, defaultValue);
        }

        // Any count is accepted: each swap stands alone because the accessor falls back to
        // vanilla, so a game update that adds or removes an equipment slot does not switch the
        // fix off. Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("UpdateEquipmentVisuals")]
        private static IEnumerable<CodeInstruction> EquipmentVisualsTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(instructions, ZdoGetInt, ScopedGetIntMethod, "VisEquipment.UpdateEquipmentVisuals");
    }
}
