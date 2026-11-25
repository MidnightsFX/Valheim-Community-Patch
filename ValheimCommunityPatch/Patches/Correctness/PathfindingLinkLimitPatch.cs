using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Nav tiles live 60 s and expire one per pass, so fast travel can exhaust the 65,535 NavMesh links and break paths.
    // Prefixes Pathfinding.TimeoutTiles to drop every tile unused for 10 s from 45,000 links until back under 40,000.
    // Creatures keep pathing between tiles, and Unity stops logging a failed link for each new one.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(Pathfinding))]
    internal static class PathfindingLinkLimitPatch {
        private const string FixName = "Fix Pathfinding Link Limit";

        internal static FixToggle Enabled;

        // The gap between the two marks keeps the pass from flipping every second at the line.
        private const int HighLinks = 45000;
        private const int LowLinks = 40000;
        private const float PressureTimeout = 10f;
        private const float CountInterval = 1f;

        private static float _nextCount;
        private static bool _pressure;
        private static bool _loggedPressure;

        private static readonly List<Vector3Int> Expired = new List<Vector3Int>();

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(PathfindingLinkLimitPatch),
            AccessTools.DeclaredMethod(typeof(Pathfinding), "TimeoutTiles"),
            transpilers: true,
            owners => $"Path-finding tile clean-up is changed by {owners}, so '{FixName}' leaves it to that mod.");

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(PathfindingLinkLimitPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "While the engine's 65,535 path-finding links are nearly used up, drops path-finding tiles " +
                "nobody has used for 10 s instead of 60 s, so creatures can still path between tiles.");
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("TimeoutTiles")]
        private static bool TimeoutTilesPrefix(Pathfinding __instance, bool __runOriginal) {
            if (!__runOriginal) { return false; }

            if (Enabled == null || !Enabled.Value) { return true; }

            float time = Time.time;
            if (time >= _nextCount) {
                _nextCount = time + CountInterval;
                UpdatePressure(__instance);
            }

            if (!_pressure || Takeover.TakenOver) { return true; }

            DropUnusedTiles(__instance, time);
            return false;
        }

        private static void UpdatePressure(Pathfinding pathfinding) {
            // Links queued for removal are still allocated in the engine.
            int links = pathfinding.m_linkRemoveQueue.Count;
            foreach (Pathfinding.NavMeshTile tile in pathfinding.m_tiles.Values) {
                links += tile.m_links1.Count + tile.m_links2.Count;
            }

            bool pressure = _pressure ? links >= LowLinks : links >= HighLinks;
            if (pressure == _pressure) { return; }

            _pressure = pressure;
            int tiles = pathfinding.m_tiles.Count;

            if (pressure && !_loggedPressure) {
                _loggedPressure = true;
                Logger.LogInfo(
                    $"Path-finding is using {links} of the engine's 65,535 navigation links across " +
                    $"{tiles} tiles, so '{FixName}' drops tiles unused for {PressureTimeout:0} s until " +
                    $"fewer than {LowLinks} are in use. Logged once per session.");
                return;
            }

            Logger.LogDebug(
                $"'{FixName}': {links} path-finding links in {tiles} tiles, early tile drop " +
                (pressure ? "on." : "off."));
        }

        // TimeoutTiles' per-tile clean-up, for every expired tile instead of the first one found. The
        // engine removals stay queued, spread over frames as in vanilla.
        private static void DropUnusedTiles(Pathfinding pathfinding, float time) {
            float timeout = Mathf.Min(pathfinding.m_tileTimeout, PressureTimeout);
            Dictionary<Vector3Int, Pathfinding.NavMeshTile> tiles = pathfinding.m_tiles;

            // Never the tile being built: its finished build would add links for a tile no longer tracked.
            foreach (KeyValuePair<Vector3Int, Pathfinding.NavMeshTile> entry in tiles) {
                if (time - entry.Value.m_pokeTime > timeout && entry.Value != pathfinding.m_buildTile) {
                    Expired.Add(entry.Key);
                }
            }

            foreach (Vector3Int key in Expired) {
                Pathfinding.NavMeshTile tile = tiles[key];
                pathfinding.ClearLinks(tile);
                if (tile.m_instance.valid) { pathfinding.m_tileRemoveQueue.Enqueue(tile.m_instance); }
                tiles.Remove(key);
            }

            Expired.Clear();
        }
    }
}
