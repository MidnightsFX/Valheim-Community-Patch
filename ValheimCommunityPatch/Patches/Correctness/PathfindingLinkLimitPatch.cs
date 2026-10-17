using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Pathfinding Link Limit: while the engine's navigation links are nearly used up,
    // path-finding tiles nobody has used for 10 s are dropped instead of after the game's 60 s.
    //
    // Pathfinding keeps a navigation tile per 32 m area and creature size and joins neighbouring
    // tiles with NavMesh links, about 90 per tile. A tile goes once no path request has touched it
    // for m_tileTimeout (60 s on the game's object), and TimeoutTiles drops at most one expired tile
    // per pass. Unity allows 65,535 links. Fast travel past areas full of creatures (sailing along a
    // coast, portals) can keep enough tiles loaded to use them all; from then on every new link
    // fails, Unity logs an error for each, and creatures cannot path from one tile to the next.
    //
    // A prefix on TimeoutTiles counts the links once a second, including those queued for removal,
    // which the engine has not freed yet. From 45,000 until back under 40,000 it replaces the pass:
    // every tile unused for 10 s goes through the game's per-tile clean-up, except the one being
    // built, whose finished build would otherwise add links for a tile no longer tracked. The engine
    // removals stay spread over frames as in vanilla. Below the mark the game's pass runs unchanged,
    // and tiles in use are never dropped. Stands down if another mod can skip or rewrite the pass.
    //
    // Both: path-finding runs wherever a creature's owner is, a dedicated server included.
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
                "While the engine's 65,535 path-finding links are nearly used up, drops path-finding " +
                "tiles nobody has used for 10 s instead of 60 s. Fast travel past coasts full of " +
                "creatures could use every link, after which each new one failed with a logged error and " +
                "creatures could not path between tiles. Tiles in use are never dropped; below the mark " +
                "nothing changes.");
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

        // TimeoutTiles' per-tile clean-up, for every expired tile instead of the first one found.
        private static void DropUnusedTiles(Pathfinding pathfinding, float time) {
            float timeout = Mathf.Min(pathfinding.m_tileTimeout, PressureTimeout);
            Dictionary<Vector3Int, Pathfinding.NavMeshTile> tiles = pathfinding.m_tiles;

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
