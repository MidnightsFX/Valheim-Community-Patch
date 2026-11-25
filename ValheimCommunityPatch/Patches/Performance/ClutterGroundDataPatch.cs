using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Grass Ground Raycasts: grass placement casts a 1 km physics ray for every candidate blade.
    // Prefixes ClutterSystem.GetGroundInfo to answer height, normal and biome from the zone's heightmap data.
    // Building grass patches no longer pays a physics raycast per blade, and the grass lands where it did.
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

        private static bool? _attached;

        /// <summary>
        /// True while this fix answers ground queries: patched, not turned off and not taken over.
        /// Fix Grass Build Stall reproduces its answer off the main thread, so it asks. Main thread only.
        /// </summary>
        internal static bool Answering {
            get {
                if (_attached == null) {
                    _attached = PatchHelper.HasHook(
                        AccessTools.DeclaredMethod(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo)),
                        typeof(ClutterGroundDataPatch));
                }

                return _attached.Value && !ApiSwitch.Off && !Takeover.TakenOver;
            }
        }

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

        // The ray's mask is only the terrain layer and vanilla dereferences the hit's Heightmap, so it can only
        // hit a heightmap's collider, whose triangles HeightmapSampling reproduces; its +/-500 m reach is kept.
        // Results are by ref so a call passed on keeps what an earlier prefix wrote: Valheim AVX writes them
        // before deciding whether to run, and otherwise every blade lost its heightmap and no grass grew.
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
