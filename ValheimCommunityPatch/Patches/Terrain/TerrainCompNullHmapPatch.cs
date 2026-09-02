using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Fix Terrain Compiler Init Race: a terrain compiler that loaded before its zone's heightmap
    // existed recovers once the heightmap appears, instead of throwing every frame.
    //
    // TerrainComp.Awake gives up when Heightmap.FindHeightmap returns null: no Initialize, no
    // ApplyOperation RPC registration, no entry in s_instances. Update then calls CheckLoad every
    // frame regardless, which dereferences the null heightmap. The zone's terrain edits are inert
    // from then on because nothing can find or drive the compiler.
    //
    // A prefix on Update skips the frame while the compiler is unusable and, once a heightmap
    // exists, re-runs the branch Awake skipped in Awake's order: find and destroy any rival
    // compiler for the zone (the deduplication Awake would have done, without which two compilers
    // hold diverging edits for one zone), register in s_instances, register the RPC, Initialize,
    // CheckLoad. A compiler whose recovery throws is abandoned rather than retried every frame.
    //
    // Both: the race is more likely on a loaded server, and the RPC it re-registers is what lets
    // that zone accept edits at all.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(TerrainComp))]
    internal static class TerrainCompNullHmapPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TerrainCompNullHmapPatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Compiler Init Race",
                true,
                "Recovers a terrain compiler that loaded before its zone's heightmap existed. In vanilla " +
                "it throws a NullReferenceException every frame from then on and that zone stops " +
                "accepting terrain edits entirely.");
        }

        // Compilers whose recovery threw. An abandoned compiler stays inert rather than half-live:
        // ApplyToHeightmap returns early on !m_initialized and FindTerrainCompiler cannot match
        // m_size == 0.
        private static readonly HashSet<TerrainComp> RecoveryFailed = new HashSet<TerrainComp>();

        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static bool UpdatePrefix(TerrainComp __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }
            if (__instance.m_hmap != null && __instance.m_initialized) { return true; }

            if (RecoveryFailed.Count > 0 && RecoveryFailed.Contains(__instance)) { return false; }

            // A successful recovery already ran CheckLoad, so vanilla's Update is skipped either way.
            TryRecover(__instance);
            return false;
        }

        private static bool TryRecover(TerrainComp comp) {
            Heightmap hmap = comp.m_hmap != null
                ? comp.m_hmap
                : Heightmap.FindHeightmap(comp.transform.position);

            if (hmap == null) { return false; }
            if (comp.m_nview == null || !comp.m_nview.IsValid()) { return false; }

            comp.m_hmap = hmap;

            try {
                // Only a never-initialised compiler needs the deduplication and RPC registration;
                // one that is initialised is just re-attaching a heightmap after a zone reload.
                if (!comp.m_initialized) {
                    // Before Initialize: FindTerrainCompiler matches on m_size, still 0 here, so
                    // it cannot return this compiler and can only return a real rival.
                    TerrainComp other = TerrainComp.FindTerrainCompiler(comp.transform.position);
                    if (other != null && other != comp && ZNetScene.instance != null) {
                        Logger.LogWarning(
                            $"Found another terrain compiler at {comp.transform.position}, removing it. " +
                            "Two compilers in one zone means one of their saved terrain edits would be " +
                            "discarded at random, so this resolves it the way TerrainComp.Awake does.");
                        ZNetScene.instance.Destroy(other.gameObject);
                    }

                    if (!TerrainComp.s_instances.Contains(comp)) { TerrainComp.s_instances.Add(comp); }

                    comp.m_nview.Register<ZPackage>(
                        "ApplyOperation", new Action<long, ZPackage>(comp.RPC_ApplyOperation));
                    comp.Initialize();
                } else if (!TerrainComp.s_instances.Contains(comp)) {
                    TerrainComp.s_instances.Add(comp);
                }

                comp.CheckLoad();
            } catch (Exception ex) {
                RecoveryFailed.RemoveWhere(failed => failed == null);
                RecoveryFailed.Add(comp);
                Logger.LogError(
                    $"Failed to recover terrain compiler at {comp.transform.position}, and it will not " +
                    $"be retried: {ex}");
                return false;
            }

            Logger.LogInfo($"Recovered terrain compiler at {comp.transform.position} after its heightmap loaded.");
            return true;
        }
    }
}
