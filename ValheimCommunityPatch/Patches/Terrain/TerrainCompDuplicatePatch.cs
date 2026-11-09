using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Fix Terrain Compiler Duplicates: when a zone ends up with two terrain compilers, the spare is
    // removed from that zone only, and the zone redraws from the one kept.
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
    // world's own along that zone's borders. A reporter's Deep North base lost the raised ground of
    // the zone beside its portal this way on arriving back ("Removed duplicate terrain compiler with
    // 143152 operations"), which showed as a gap from the portal hall's door to the bonfire.
    // Vanilla also never redraws the zone, so a tile that had drawn from the removed compiler keeps
    // showing it until something else pokes it.
    //
    // A prefix replaces TryCleanInvalidTCs with the same rule applied per zone: within each zone of
    // the set, keep the compiler with the most operations, destroy the others the way vanilla does
    // (claiming an unowned one first, so its ZDO goes too), then poke the kept compiler's heightmap.
    // Compilers of different zones are never compared. The set is cleared as vanilla clears it.
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
                "When a zone gets a second terrain compiler (an edit arriving before the zone's saved one " +
                "loads, as after a portal trip), removes the spare from that zone only. Vanilla keeps the " +
                "single most edited compiler of every zone that doubled at the same time and deletes the " +
                "rest, wiping a neighbouring zone's terrain edits so its ground drops back to the original.");
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
    }
}
