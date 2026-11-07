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
    // check. Clutter is cosmetic, never saved, and regenerated constantly. Stands down while
    // HearthBelow is loaded, because ground dug out with it exists only in colliders and the
    // heightmap data still describes the surface before digging.
    //
    // Also stands down, to the game's raycast, when another mod can skip or rewrite GetGroundInfo,
    // and keeps the answer of a prefix that ran first. The results are taken by ref so that a call
    // this fix does not answer leaves them untouched. Valheim AVX's ground sampling writes them
    // before it checks whether to run, so next to this fix every blade lost its heightmap and no
    // grass grew.
    //
    // Client: ClutterSystem needs a camera.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ClutterSystem))]
    internal static class ClutterGroundDataPatch {
        private const string FixName = "Fix Grass Ground Raycasts";

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ClutterGroundDataPatch));

        private static bool _loggedStandDown;

        // A transpiler counts too: this prefix replaces the whole method, which would hide its edit.
        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(ClutterGroundDataPatch),
            AccessTools.DeclaredMethod(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo)),
            transpilers: true,
            owners => $"Grass ground placement is changed by {owners}, so '{FixName}' stands down and " +
                      "the game's ground raycast applies.");

        [HarmonyPrepare]
        private static bool Prepare() {
            if (!HearthBelowCompat.Loaded) { return true; }

            if (!_loggedStandDown) {
                _loggedStandDown = true;
                Logger.LogInfo(
                    $"HearthBelow is loaded, so '{FixName}' stands down: ground dug out with HearthBelow " +
                    "is not in the terrain data this fix reads, and grass would grow at the surface from " +
                    "before digging. Grass placement uses the game's ground raycast.");
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ClutterSystem.GetGroundInfo))]
        private static bool GetGroundInfoPrefix(
            Vector3 p, ref Vector3 point, ref Vector3 normal, ref Heightmap hmap,
            ref Heightmap.Biome biome, ref bool __result, bool __runOriginal) {
            // Another mod's prefix already answered this call; its results stand.
            if (!__runOriginal) { return false; }
            if (ApiSwitch.Off || Takeover.TakenOver) { return true; }

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
