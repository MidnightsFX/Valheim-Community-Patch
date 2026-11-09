using UnityEngine;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// Evaluates a heightmap's collision surface from its height data, without the physics engine.
    /// </summary>
    /// <remarks>
    /// Heightmap.RebuildCollisionMesh builds each grid cell as two triangles split along the
    /// B-D anti-diagonal: (A, D, B) then (B, D, C), where A=(x,y), B=(x+1,y), C=(x+1,y+1),
    /// D=(x,y+1). These helpers interpolate height and take the triangle normal using that same
    /// split, so they answer what a terrain-layer raycast would hit. One difference: a raycast
    /// sees the last baked collider, while this reads the current data, which is never staler.
    ///
    /// Used by the ground-check and grass-placement fixes, so the triangulation lives in one place.
    ///
    /// A map with no height data yet answers false, as a point outside it does. Vanilla builds a map
    /// inside its OnEnable, but a mod can put that off to a later frame.
    /// </remarks>
    internal static class HeightmapSampling {
        /// <summary>
        /// True when the map has its height data. Heightmap.GetHeight checks the grid coordinates
        /// but not the list behind them, so reading a map without it throws.
        /// </summary>
        internal static bool IsBuilt(Heightmap hmap) {
            int stride = hmap.m_width + 1;
            return hmap.m_heights.Count == stride * stride;
        }

        /// <summary>Height of the collision surface at (position.x, position.z), world space.</summary>
        internal static bool TryGetHeight(Heightmap hmap, Vector3 position, out float height) {
            return Sample(hmap, hmap.transform.position, position, out height, out _, false);
        }

        /// <summary>Same, with the heightmap origin already in hand, saving the transform read.</summary>
        internal static bool TryGetHeight(Heightmap hmap, Vector3 origin, Vector3 position, out float height) {
            return Sample(hmap, origin, position, out height, out _, false);
        }

        /// <summary>Height and triangle normal of the collision surface.</summary>
        internal static bool TryGetSurface(Heightmap hmap, Vector3 position, out float height, out Vector3 normal) {
            return Sample(hmap, hmap.transform.position, position, out height, out normal, true);
        }

        /// <summary>Same, with the heightmap origin already in hand.</summary>
        internal static bool TryGetSurface(Heightmap hmap, Vector3 origin, Vector3 position, out float height, out Vector3 normal) {
            return Sample(hmap, origin, position, out height, out normal, true);
        }

        /// <summary>
        /// Height and triangle normal from a copy of a map's height grid, so a worker thread can ask:
        /// <paramref name="heights"/> is m_heights as an array, (width + 1) squared, row by row.
        /// </summary>
        internal static bool TryGetSurface(float[] heights, int width, float scale, Vector3 origin, Vector3 position,
            out float height, out Vector3 normal) {
            normal = Vector3.up;
            if (!Locate(width, scale, origin, position, out int x0, out int z0, out float rx, out float rz)) {
                height = 0f;
                return false;
            }

            int stride = width + 1;
            Surface(
                heights[z0 * stride + x0], heights[z0 * stride + x0 + 1],
                heights[(z0 + 1) * stride + x0], heights[(z0 + 1) * stride + x0 + 1],
                rx, rz, scale, origin.y, true, out height, ref normal);
            return true;
        }

        private static bool Sample(Heightmap hmap, Vector3 origin, Vector3 position, out float height, out Vector3 normal, bool wantNormal) {
            normal = Vector3.up;

            if (!Locate(hmap.m_width, hmap.m_scale, origin, position, out int x0, out int z0, out float rx, out float rz)
                || !IsBuilt(hmap)) {
                height = 0f;
                return false;
            }

            Surface(
                hmap.GetHeight(x0, z0), hmap.GetHeight(x0 + 1, z0),
                hmap.GetHeight(x0, z0 + 1), hmap.GetHeight(x0 + 1, z0 + 1),
                rx, rz, hmap.m_scale, origin.y, wantNormal, out height, ref normal);
            return true;
        }

        // The grid cell under the point and the point's place within it; false outside the map.
        private static bool Locate(int width, float scale, Vector3 origin, Vector3 position,
            out int x0, out int z0, out float rx, out float rz) {
            float fx = (position.x - origin.x) / scale + width * 0.5f;
            float fz = (position.z - origin.z) / scale + width * 0.5f;
            if (fx < 0f || fx > width || fz < 0f || fz > width) {
                x0 = z0 = 0;
                rx = rz = 0f;
                return false;
            }

            x0 = Mathf.Min((int)fx, width - 1);
            z0 = Mathf.Min((int)fz, width - 1);
            rx = fx - x0;
            rz = fz - z0;
            return true;
        }

        private static void Surface(float h00, float h10, float h01, float h11, float rx, float rz, float scale,
            float originY, bool wantNormal, out float height, ref Vector3 normal) {
            if (rx + rz <= 1f) {
                height = h00 + (h10 - h00) * rx + (h01 - h00) * rz + originY;
                if (wantNormal) { normal = new Vector3(h00 - h10, scale, h00 - h01).normalized; }
            } else {
                height = h11 + (h01 - h11) * (1f - rx) + (h10 - h11) * (1f - rz) + originY;
                if (wantNormal) { normal = new Vector3(h01 - h11, scale, h10 - h11).normalized; }
            }
        }
    }
}
