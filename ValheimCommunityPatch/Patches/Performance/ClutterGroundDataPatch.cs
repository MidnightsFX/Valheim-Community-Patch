using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Grass Ground Raycasts: grass placement reads the ground height, normal and biome from
    // heightmap data instead of casting a physics ray per blade.
    //
    // ClutterSystem.GetGroundInfo answers with a 1 km Physics.Raycast, and GenerateVegPatch calls
    // it for every clutter candidate: up to 80 per clutter type per 8 m patch, one patch per frame
    // while moving and a burst when zones load. The ray mask is exactly the terrain layer and the
    // handler dereferences the hit's Heightmap, so the only thing it can hit is a zone heightmap's
    // collision mesh, and everything it returns is a function of heightmap data.
    //
    // A prefix answers from that data: the heightmap from HeightmapLookupPatch, the height and
    // triangle normal from HeightmapSampling (the same triangulation the collider bakes), and the
    // biome from the same GetBiome call. The ray's +/-500 m vertical window is kept as an explicit
    // check. Clutter is cosmetic, never saved, and regenerated constantly.
    //
    // Client: ClutterSystem needs a camera.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(ClutterSystem))]
    internal static class ClutterGroundDataPatch {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ClutterSystem.GetGroundInfo))]
        private static bool GetGroundInfoPrefix(
            Vector3 p, out Vector3 point, out Vector3 normal, out Heightmap hmap,
            out Heightmap.Biome biome, ref bool __result) {
            Heightmap found;
            Vector3 origin;
            if (!HeightmapLookupPatch.TryGetCached(p, out found, out origin)) {
                found = Heightmap.FindHeightmap(p);
                origin = found != null ? found.transform.position : Vector3.zero;
            }

            float height = 0f;
            Vector3 surfaceNormal = Vector3.up;

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

            // Vanilla's miss result.
            point = p;
            normal = Vector3.up;
            hmap = null;
            biome = Heightmap.Biome.Meadows;
            __result = false;
            return false;
        }
    }
}
