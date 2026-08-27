using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: ClutterSystem.GetGroundInfo answers "where is the ground here, and which way
    // does it face" with a 1 km Physics.Raycast (ClutterSystem.cs:216-237) - and grass placement
    // asks it for every clutter candidate: up to 80 candidates per clutter type per 8 m patch,
    // one patch generated per frame while moving, and a multi-patch burst when zones load. That
    // is hundreds of raycasts per frame in the steady state - profiling attributed ~4.2 seconds
    // of a 5-minute window to patch generation, a large share of it these queries.
    //
    // The ray mask is exactly the "terrain" layer (ClutterSystem.cs:42), and the handler
    // dereferences the hit collider's Heightmap unconditionally - so the only thing that ray can
    // ever hit is a zone heightmap's collision mesh. Everything it returns is therefore a pure
    // function of heightmap data: the surface height and triangle normal come from
    // HeightmapSampling (the same triangulation the collider bakes), the heightmap from
    // FindHeightmap (indexed by HeightmapLookupPatch), and the biome from the same GetBiome call.
    //
    // Faithfulness notes:
    //  - The vertical ray only reaches surfaces within +/-500 m of the query's y (origin +500,
    //    length 1000). Replicated as an explicit window check; candidates are generated at y=0
    //    and world terrain spans roughly -50..+450, so the window never cuts in practice.
    //  - A raycast sees the last *baked* collider; this reads current data, which is never
    //    staler. With the zone-collider bake deferred (Fix Zone Collider Stall) it is strictly
    //    fresher.
    //  - A borderline float difference in the normal can flip a single blade's tilt test.
    //    Clutter is cosmetic, client-local, never saved, and regenerated on a 2-second timeout,
    //    so there is no persistence or multiplayer surface.
    //
    // GetGroundInfo's only caller is GenerateVegPatch (verified across the assembly), but it is
    // public, so the replacement keeps its exact contract for any mod that calls it.
    //
    // Client: ClutterSystem requires a main camera; nothing headless ever generates grass.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(ClutterSystem))]
    internal static class ClutterGroundDataPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ClutterGroundDataPatch),
                ValConfig.SectionPerformance,
                "Fix Grass Ground Raycasts",
                true,
                "Answers grass placement's ground queries from terrain data instead of casting " +
                "hundreds of physics rays per frame. The rays could only ever hit the terrain " +
                "surface, whose shape is already known; the same surface and slope come out, " +
                "without the physics engine.");
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ClutterSystem.GetGroundInfo))]
        private static bool GetGroundInfoPrefix(
            Vector3 p, out Vector3 point, out Vector3 normal, out Heightmap hmap,
            out Heightmap.Biome biome, ref bool __result) {
            if (Enabled == null || !Enabled.Value) {
                // Vanilla runs and overwrites these; out params just have to be assigned.
                point = p;
                normal = Vector3.up;
                hmap = null;
                biome = Heightmap.Biome.Meadows;
                return true;
            }

            // The registry path answers with the cached transform origin - no native reads at all
            // on the hot path. Falls back to the plain lookup when the registry cannot serve.
            Heightmap found;
            Vector3 origin;
            if (!HeightmapLookupPatch.TryGetCached(p, out found, out origin)) {
                found = Heightmap.FindHeightmap(p);
                origin = found != null ? found.transform.position : Vector3.zero;
            }

            float height = 0f;
            Vector3 surfaceNormal = Vector3.up;

            // The vanilla ray starts 500 above the query and travels 1000 down; a surface outside
            // that window is a miss there too.
            if (found != null
                && HeightmapSampling.TryGetSurface(found, origin, p, out height, out surfaceNormal)
                && height <= p.y + 500f && height >= p.y - 500f) {
                point = new Vector3(p.x, height, p.z);
                normal = surfaceNormal;
                hmap = found;
                biome = found.GetBiome(point);
                __result = true;
                return false;
            }

            // Vanilla's miss result, verbatim (ClutterSystem.cs:232-236).
            point = p;
            normal = Vector3.up;
            hmap = null;
            biome = Heightmap.Biome.Meadows;
            __result = false;
            return false;
        }
    }
}
