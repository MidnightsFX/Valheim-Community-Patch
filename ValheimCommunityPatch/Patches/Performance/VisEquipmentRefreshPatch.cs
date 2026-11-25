using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Equipment Visual Refresh: every character re-applies unchanged colors and makes forty ZDO lookups each frame.
    // Prefixes UpdateColors to skip unchanged writes; transpiles UpdateEquipmentVisuals to read one fetched int table.
    // Characters, dropped armor and armor stands stop allocating and rehashing every frame for unchanged values.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(VisEquipment))]
    internal static class VisEquipmentRefreshPatch {
        // ---- 1. Skin and hair color ------------------------------------------------------------

        /// <summary>Everything UpdateColors reads; a match means it would write what it wrote last time.</summary>
        internal struct ColorState {
            internal bool Primed;
            internal Vector3 Skin;
            internal Vector3 Hair;
            internal GameObject Beard;
            internal GameObject HairItem;
            internal SkinnedMeshRenderer Body;
            internal int ModelIndex;

            // Where vanilla wrote the hair tint, recorded by the postfix so the prefix can see it
            // overwritten. Null when there was no instance to tint.
            internal Renderer[] BeardRenderers;
            internal Renderer[] HairRenderers;

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

        // Reused for every property block read; GetPropertyBlock fills it in place.
        private static readonly MaterialPropertyBlock Scratch = new MaterialPropertyBlock();

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(VisEquipmentRefreshPatch));

        [HarmonyPrefix]
        [HarmonyPatch("UpdateColors")]
        private static bool UpdateColorsPrefix(VisEquipment __instance, out ColorState __state) {
            __state = default;
            if (ApiSwitch.Off) { return true; }

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

            if (!Applied.TryGetValue(__instance, out ColorState last) || !last.Matches(__state)) { return true; }

            // Nothing vanilla reads has changed, but MaterialMan replaces a character's property blocks whenever an
            // item is attached (RefreshSnowLevel), which vanilla's per-frame rewrite covered; skipping then would leave
            // a beard white from the next equip on. Utils.Vec3ToColor: the three channels, alpha one.
            Color tint = new Color(last.Hair.x, last.Hair.y, last.Hair.z);
            if (StillApplied(last.BeardRenderers, tint) && StillApplied(last.HairRenderers, tint)) { return false; }

            if (Logger.DebugEnabled) {
                Logger.LogDebug($"VisEquipment: hair tint overwritten on {__instance.name}, re-applying.");
            }

            return true;
        }

        // True when every renderer vanilla tinted still carries that tint in its property block.
        // A destroyed renderer under a live instance also defers to vanilla, which re-walks it.
        private static bool StillApplied(Renderer[] renderers, Color expected) {
            if (renderers == null) { return true; }

            for (int i = 0; i < renderers.Length; i++) {
                Renderer renderer = renderers[i];
                if (renderer == null) { return false; }

                renderer.GetPropertyBlock(Scratch);

                // A block without the property reads back clear, never equal to an opaque tint.
                if (Scratch.GetColor(VisEquipment.s_skinColorID) != expected) { return false; }
            }

            return true;
        }

        // Recorded here rather than in the prefix, so a vanilla call that threw is retried rather than marked applied.
        [HarmonyPostfix]
        [HarmonyPatch("UpdateColors")]
        private static void UpdateColorsPostfix(VisEquipment __instance, ColorState __state) {
            if (!__state.Primed) { return; }

            // The walk vanilla just made, on a frame it ran, so these are the renderers it tinted.
            __state.BeardRenderers = __instance.m_beardItemInstance != null
                ? __instance.m_beardItemInstance.GetComponentsInChildren<Renderer>()
                : null;
            __state.HairRenderers = __instance.m_hairItemInstance != null
                ? __instance.m_hairItemInstance.GetComponentsInChildren<Renderer>()
                : null;

            Applied[__instance] = __state;
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

        // Set by the transpiler, which Harmony re-runs each time any mod patches the method. False when another mod
        // already rewrote these reads (ontrigger's ValheimPerformanceOptimizations does the same), so the prefix skips
        // the lookup.
        private static bool _readsRewritten;

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

            // Unscoped, the rewritten reads go straight to the ZDO as vanilla's do.
            if (ApiSwitch.Off || !_readsRewritten) { return; }

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
        private static IEnumerable<CodeInstruction> EquipmentVisualsTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original) {
            IEnumerable<CodeInstruction> codes = PatchHelper.ReplaceCalls(
                instructions, ZdoGetInt, ScopedGetIntMethod, "VisEquipment.UpdateEquipmentVisuals", patched: original);

            // ReplaceCalls hands back its input when it stands down.
            _readsRewritten = !ReferenceEquals(codes, instructions);
            return codes;
        }
    }
}
