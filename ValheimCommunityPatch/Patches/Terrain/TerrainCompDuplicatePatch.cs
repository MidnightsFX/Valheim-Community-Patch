using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Fix Terrain Compiler Duplicates: an edit that reaches a zone before its saved terrain compiler
    // loads goes into that compiler, and a spare compiler that still appears is removed from its zone
    // only, which then redraws from the one kept.
    //
    // A zone gains a second compiler when an edit reaches it before its saved compiler has been
    // created: a fire melting snow or a footstep in deep snow right after a portal arrival, or the
    // edits of another player who arrived first. Heightmap.GetAndCreateTerrainCompiler then makes a new,
    // empty one, and when the saved one arrives TerrainComp.Awake puts both in the static
    // s_duplicateInstances. Start's TryCleanInvalidTCs then keeps the single compiler with the most
    // operations in the whole set and destroys every other one, but the set holds the pairs of every
    // zone that doubled since the last cleanup. With two zones doubled at once the real compiler of
    // the less edited zone is destroyed along with both new ones: its height and paint edits are gone
    // (its ZDO is deleted when this peer owns it or nobody does), and the ground drops back to the
    // world's own along that zone's borders. Vanilla also never redraws the zone, so a tile that had drawn from the removed compiler keeps
    // showing it until something else pokes it.
    //
    // Even per zone, the edits that went into the spare are lost there, while the zone across the border
    // kept them: the two sides then disagree on their shared border and the ground cracks along it.
    //
    // A prefix replaces TryCleanInvalidTCs with the same rule applied per zone: within each zone of
    // the set, keep the compiler with the most operations, destroy the others the way vanilla does
    // (claiming an unowned one first, so its ZDO goes too), then poke the kept compiler's heightmap.
    // Compilers of different zones are never compared. The set is cleared as vanilla clears it.
    // A prefix on GetAndCreateTerrainCompiler stops most spares being made: when the zone has a saved
    // compiler that has not spawned yet, it is spawned there and then (or recovered by Fix Terrain
    // Compiler Init Race if it spawned before its heightmap), and claimed if nobody owns it, so the
    // edit goes into it. A client that has not received the zone's saved compiler yet still makes a
    // spare, which the cleanup handles as before.
    //
    // Both: compilers are created and cleaned up on every peer that loads the zone.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(TerrainComp))]
    internal static class TerrainCompDuplicatePatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TerrainCompDuplicatePatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Compiler Duplicates",
                true,
                "An edit that reaches a zone before its saved terrain compiler has loaded (as just after " +
                "arriving) goes into the saved one, which is loaded on the spot. Vanilla starts a second, " +
                "empty compiler that is later deleted, so that zone loses the edit and the ground cracks " +
                "along its border. A second compiler that still appears is removed from that zone only; " +
                "vanilla deletes the less edited compilers of every zone that doubled at the same time, " +
                "wiping a neighbouring zone's terrain edits.");
        }

        private static readonly List<TerrainComp> Spares = new List<TerrainComp>();

        [HarmonyPrefix]
        [HarmonyPatch("TryCleanInvalidTCs")]
        private static bool TryCleanInvalidTCsPrefix(TerrainComp __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }

            HashSet<TerrainComp> duplicates = TerrainComp.s_duplicateInstances;
            // Vanilla acts only from a compiler that is in the set.
            if (duplicates.Count == 0 || !duplicates.Contains(__instance)) { return false; }

            foreach (IGrouping<Vector2s, TerrainComp> zone in duplicates
                         .Where(tc => tc != null)
                         .GroupBy(tc => ZoneSystem.GetZone(tc.transform.position))) {
                List<TerrainComp> compilers = zone.OrderBy(tc => tc.m_operations).ToList();
                if (compilers.Count < 2) { continue; }

                TerrainComp kept = compilers[compilers.Count - 1];
                Spares.Clear();
                Spares.AddRange(compilers.Take(compilers.Count - 1));
                foreach (TerrainComp spare in Spares) {
                    if (spare.m_nview != null && spare.m_nview.IsValid() && !spare.m_nview.HasOwner()) {
                        spare.m_nview.ClaimOwnership();
                    }

                    int operations = spare.m_operations;
                    ZNetScene.instance.Destroy(spare.gameObject);
                    TerrainComp.s_instances.Remove(spare);
                    ZLog.LogWarning(
                        $"Removed duplicate terrain compiler with {operations} operations performed on it " +
                        $"(zone {zone.Key.x},{zone.Key.y}; kept the one with {kept.m_operations}).");
                }

                Spares.Clear();
                if (kept.m_hmap != null) { kept.m_hmap.Poke(); }
            }

            duplicates.Clear();
            return false;
        }

        private static string s_compilerName;
        private static int s_compilerHash;

        /// <summary>
        /// Adds to <paramref name="saved"/> the compiler ZDOs this peer knows for the heightmap's zone, spawned or not;
        /// with <paramref name="saved"/> null, stops at the first. True when there is one.
        /// </summary>
        internal static bool FindSavedCompilers(Heightmap hmap, List<ZDO> saved) {
            if (hmap.m_terrainCompilerPrefab == null || ZDOMan.instance == null) { return false; }

            string name = hmap.m_terrainCompilerPrefab.name;
            if (name != s_compilerName) {
                s_compilerName = name;
                s_compilerHash = name.GetStableHashCode();
            }

            Vector2s zone = ZoneSystem.GetZone(hmap.transform.position);
            uint index = ZoneSystem.SectorToIndex(zone).Sector;
            List<ZDO>[] bySector = ZDOMan.instance.m_objectsBySector;
            if (bySector == null || index >= (uint)bySector.Length || bySector[index] == null) { return false; }

            // Index 0 also holds zones outside the grid, so the zone is checked by position too.
            bool any = false;
            foreach (ZDO zdo in bySector[index]) {
                if (zdo == null || zdo.GetPrefab() != s_compilerHash || ZoneSystem.GetZone(zdo.GetPosition()) != zone) { continue; }
                if (saved == null) { return true; }
                saved.Add(zdo);
                any = true;
            }
            return any;
        }

        // Spawns the zone's saved compiler before vanilla would make an empty spare for the edit.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.GetAndCreateTerrainCompiler))]
        private static class SpawnSavedCompilerHook {
            private static readonly List<ZDO> Saved = new List<ZDO>();

            [HarmonyPrefix]
            private static void Prefix(Heightmap __instance) {
                if (Enabled == null || !Enabled.Value) { return; }
                if (__instance == null || __instance.IsDistantLod || __instance.m_terrainCompilerPrefab == null) { return; }
                if (ZNetScene.instance == null || ZDOMan.instance == null) { return; }

                Vector3 centre = __instance.transform.position;
                if (TerrainComp.FindTerrainCompiler(centre) != null) { return; }

                Saved.Clear();
                if (!FindSavedCompilers(__instance, Saved)) { return; }
                Vector2s zone = ZoneSystem.GetZone(centre);

                foreach (ZDO zdo in Saved) {
                    ZNetView view = ZNetScene.instance.FindInstance(zdo);
                    if (view == null) {
                        ZNetScene.instance.CreateObject(zdo);
                        Logger.LogDebug($"Spawned the saved terrain compiler of zone {zone.x},{zone.y} early for an edit that reached it first.");
                    } else if (view.TryGetComponent(out TerrainComp stuck) && !stuck.m_initialized) {
                        // Spawned before its heightmap; Fix Terrain Compiler Init Race brings it back.
                        TerrainCompNullHmapPatch.Recover(stuck);
                    }
                }
                Saved.Clear();

                // With several saved for the zone, the edit goes to the most edited, the one the cleanup keeps.
                List<TerrainComp> found = TerrainComp.FindAllTerrainCompilers(centre);
                if (found.Count == 0) { return; }
                TerrainComp kept = found.OrderByDescending(tc => tc.m_operations).First();
                if (found.Count > 1) {
                    TerrainComp.s_instances.Remove(kept);
                    TerrainComp.s_instances.Insert(0, kept);
                }

                // An unowned compiler's edits go to nobody (RPC_ApplyOperation runs on the owner only); vanilla's
                // new compiler would have been this peer's.
                if (kept.m_nview != null && kept.m_nview.IsValid() && !kept.m_nview.HasOwner()) { kept.m_nview.ClaimOwnership(); }
            }
        }
    }
}
