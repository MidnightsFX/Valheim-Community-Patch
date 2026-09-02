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
    /// </remarks>
    internal static class HeightmapSampling {
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

        private static bool Sample(Heightmap hmap, Vector3 origin, Vector3 position, out float height, out Vector3 normal, bool wantNormal) {
            normal = Vector3.up;

            int width = hmap.m_width;
            float scale = hmap.m_scale;

            float fx = (position.x - origin.x) / scale + width * 0.5f;
            float fz = (position.z - origin.z) / scale + width * 0.5f;
            if (fx < 0f || fx > width || fz < 0f || fz > width) {
                height = 0f;
                return false;
            }

            int x0 = Mathf.Min((int)fx, width - 1);
            int z0 = Mathf.Min((int)fz, width - 1);
            float rx = fx - x0;
            float rz = fz - z0;

            float h00 = hmap.GetHeight(x0, z0);
            float h10 = hmap.GetHeight(x0 + 1, z0);
            float h01 = hmap.GetHeight(x0, z0 + 1);
            float h11 = hmap.GetHeight(x0 + 1, z0 + 1);

            if (rx + rz <= 1f) {
                height = h00 + (h10 - h00) * rx + (h01 - h00) * rz + origin.y;
                if (wantNormal) { normal = new Vector3(h00 - h10, scale, h00 - h01).normalized; }
            } else {
                height = h11 + (h01 - h11) * (1f - rx) + (h10 - h11) * (1f - rz) + origin.y;
                if (wantNormal) { normal = new Vector3(h01 - h11, scale, h10 - h11).normalized; }
            }

            return true;
        }
    }
}
