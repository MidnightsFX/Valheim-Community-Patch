using System;
using System.Collections.Generic;
using UnityEngine;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// The terrain data grass placement reads from one heightmap, copied on the main thread so a
    /// worker thread can read it while the game keeps changing the live map.
    /// </summary>
    /// <remarks>
    /// Each reader mirrors the vanilla Heightmap method of the same name, with the transform read
    /// replaced by the copied origin and the paint mask by its copied pixels.
    /// </remarks>
    internal sealed class GrassTerrain {
        internal readonly Vector3 Origin;
        internal readonly int Width;
        internal readonly float Scale;
        // HeightmapLookupPatch's containment half-size, computed the same way.
        internal readonly float Half;
        private readonly float[] _heights;
        private readonly int _maskWidth;
        private readonly int _maskHeight;
        private readonly float[] _maskAlpha;
        private readonly bool[] _maskCleared;
        private readonly float[] _ocean;
        private readonly Heightmap.Biome[] _corners;

        private GrassTerrain(Heightmap hmap, Vector3 origin) {
            Origin = origin;
            Width = hmap.m_width;
            Scale = hmap.m_scale;
            Half = hmap.m_width * hmap.m_scale * 0.5f;
            _heights = hmap.m_heights.ToArray();

            // GetPixels decodes exactly as GetPixel does, so the copy answers what vanilla reads.
            Texture2D mask = hmap.m_paintMask;
            _maskWidth = mask.width;
            _maskHeight = mask.height;
            Color[] pixels = mask.GetPixels();
            _maskAlpha = new float[pixels.Length];
            _maskCleared = new bool[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) {
                Color pixel = pixels[i];
                _maskAlpha[i] = pixel.a;
                _maskCleared[i] = pixel.r > 0.5f || pixel.g > 0.5f || pixel.b > 0.5f;
            }

            _ocean = (float[])hmap.m_oceanDepth.Clone();
            _corners = new Heightmap.Biome[4];
            for (int i = 0; i < 4; i++) { _corners[i] = hmap.m_cornerBiomes[i].Biome; }
        }

        /// <summary>A copy of the map, or null when it has no height data or paint mask yet.</summary>
        internal static GrassTerrain Copy(Heightmap hmap, Vector3 origin) {
            if (hmap == null || hmap.m_isDistantLod || hmap.m_paintMask == null || !HeightmapSampling.IsBuilt(hmap)) {
                return null;
            }

            return new GrassTerrain(hmap, origin);
        }

        // Vanilla's IsPointInside with radius 0, as HeightmapLookupPatch's Contains.
        internal bool Contains(Vector3 point) {
            return point.x >= Origin.x - Half && point.x <= Origin.x + Half
                && point.z >= Origin.z - Half && point.z <= Origin.z + Half;
        }

        internal bool TryGetSurface(Vector3 position, out float height, out Vector3 normal) {
            return HeightmapSampling.TryGetSurface(_heights, Width, Scale, Origin, position, out height, out normal);
        }

        // Heightmap.GetBiome for a zone map. The game's version sums into a shared static array,
        // which a worker must not touch, so the weights live in a per-thread one.
        [ThreadStatic] private static float[] _weights;

        internal Heightmap.Biome GetBiome(Vector3 point) {
            if (_corners[0] == _corners[1] && _corners[0] == _corners[2] && _corners[0] == _corners[3]) {
                return _corners[0];
            }

            float[] weights = _weights ?? (_weights = new float[10]);
            WorldToNormalizedHM(point, out float x, out float y);
            for (int i = 1; i < weights.Length; i++) { weights[i] = 0f; }

            weights[_corners[0].ToIndex()] += Distance(x, y, 0f, 0f);
            weights[_corners[1].ToIndex()] += Distance(x, y, 1f, 0f);
            weights[_corners[2].ToIndex()] += Distance(x, y, 0f, 1f);
            weights[_corners[3].ToIndex()] += Distance(x, y, 1f, 1f);

            int best = 0;
            float bestWeight = -99999f;
            for (int j = 1; j < weights.Length; j++) {
                if (weights[j] > bestWeight) {
                    best = j;
                    bestWeight = weights[j];
                }
            }

            return ((Heightmap.BiomeIndex)best).ToBiome();
        }

        internal float GetOceanDepth(Vector3 worldPos) {
            WorldToVertex(worldPos, out int x, out int y);
            float t = (float)((double)x / (double)(float)Width);
            float t2 = (float)y / (float)Width;
            float a = DUtils.Lerp(_ocean[3], _ocean[2], t);
            float b = DUtils.Lerp(_ocean[0], _ocean[1], t);
            return DUtils.Lerp(a, b, t2);
        }

        internal float GetVegetationMask(Vector3 worldPos) {
            worldPos.x -= 0.5f;
            worldPos.z -= 0.5f;
            WorldToVertexMask(worldPos, out int x, out int y);
            return _maskAlpha[MaskIndex(x, y)];
        }

        internal bool IsCleared(Vector3 worldPos) {
            worldPos.x = (float)((double)worldPos.x - 0.5);
            worldPos.z = (float)((double)worldPos.z - 0.5);
            WorldToVertexMask(worldPos, out int x, out int y);
            return _maskCleared[MaskIndex(x, y)];
        }

        // The paint mask's wrap mode is Clamp, so GetPixel clamps out-of-range coordinates.
        private int MaskIndex(int x, int y) {
            x = x < 0 ? 0 : (x >= _maskWidth ? _maskWidth - 1 : x);
            y = y < 0 ? 0 : (y >= _maskHeight ? _maskHeight - 1 : y);
            return y * _maskWidth + x;
        }

        private void WorldToVertex(Vector3 worldPos, out int x, out int y) {
            Vector3 vector = worldPos - Origin;
            int num = Width / 2;
            x = Mathf.FloorToInt(vector.x / Scale + 0.5f) + num;
            y = Mathf.FloorToInt(vector.z / Scale + 0.5f) + num;
        }

        private void WorldToVertexMask(Vector3 worldPos, out int x, out int y) {
            Vector3 vector = worldPos - Origin;
            int num = (Width + 1) / 2;
            x = Mathf.FloorToInt(vector.x / Scale + 0.5f + (float)num);
            y = Mathf.FloorToInt(vector.z / Scale + 0.5f + (float)num);
        }

        private void WorldToNormalizedHM(Vector3 worldPos, out float x, out float y) {
            float num = (float)Width * Scale;
            Vector3 vector = worldPos - Origin;
            x = vector.x / num + 0.5f;
            y = vector.z / num + 0.5f;
        }

        private static float Distance(float x, float y, float rx, float ry) {
            float num = (float)((double)x - (double)rx);
            float num2 = (float)((double)y - (double)ry);
            float num3 = Mathf.Sqrt((float)((double)num * (double)num + (double)num2 * (double)num2));
            float num4 = (float)(1.4140000343322754 - (double)num3);
            return (float)((double)num4 * (double)num4 * (double)num4);
        }
    }

    /// <summary>
    /// UnityEngine.Random's generator (xorshift128) and its Range methods, usable off the main thread.
    /// </summary>
    /// <remarks>
    /// UnityEngine.Random is main-thread only and global. GenerateVegPatch seeds it per clutter type,
    /// so a worker that draws the same numbers in the same order places the same blades.
    /// <see cref="MatchesUnity"/> proves it against the engine before any patch is built this way.
    /// </remarks>
    internal struct UnityRandom {
        private uint _x;
        private uint _y;
        private uint _z;
        private uint _w;

        internal UnityRandom(int seed) {
            _x = (uint)seed;
            _y = _x * 1812433253u + 1u;
            _z = _y * 1812433253u + 1u;
            _w = _z * 1812433253u + 1u;
        }

        private uint Next() {
            uint t = _x ^ (_x << 11);
            _x = _y;
            _y = _z;
            _z = _w;
            _w = _w ^ (_w >> 19) ^ t ^ (t >> 8);
            return _w;
        }

        // The engine computes this in native single precision. Mono keeps float intermediates at double
        // precision unless cast, which lands a unit or two in the last place away, so every step is cast.
        internal float Range(float min, float max) {
            float t = (float)((float)(Next() & 0x7FFFFFu) * (1f / 8388607f));
            return (float)((float)(min * t) + (float)((float)(1f - t) * max));
        }

        internal int Range(int min, int max) {
            if (min < max) { return min + (int)(Next() % (uint)(max - min)); }
            if (min > max) { return min - (int)(Next() % (uint)(min - max)); }
            return min;
        }

        /// <summary>
        /// Draws the same sequences from this and from UnityEngine.Random and compares them bit for
        /// bit. Main thread only; leaves the engine's random state as it found it.
        /// </summary>
        internal static bool MatchesUnity(out string mismatch) {
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            try {
                int[] seeds = { 0, 1, -1, 9321, 1374 * 7 + 9321 * 3, -98765 * 1374 + 9321 * 12, int.MaxValue, int.MinValue };
                foreach (int seed in seeds) {
                    UnityEngine.Random.InitState(seed);
                    UnityRandom mine = new UnityRandom(seed);
                    for (int i = 0; i < 64; i++) {
                        float lo = -5f - i;
                        float hi = 5f + i * 0.37f;
                        float engineF = UnityEngine.Random.Range(lo, hi);
                        float mineF = mine.Range(lo, hi);
                        if (BitConverter.ToInt32(BitConverter.GetBytes(engineF), 0) != BitConverter.ToInt32(BitConverter.GetBytes(mineF), 0)) {
                            mismatch = $"Range({lo}, {hi}) seed {seed} draw {i}: engine {engineF:R}, copy {mineF:R}";
                            return false;
                        }

                        int engineI = UnityEngine.Random.Range(0, 360);
                        int mineI = mine.Range(0, 360);
                        if (engineI != mineI) {
                            mismatch = $"Range(0, 360) seed {seed} draw {i}: engine {engineI}, copy {mineI}";
                            return false;
                        }
                    }
                }

                mismatch = null;
                return true;
            } finally {
                UnityEngine.Random.state = saved;
            }
        }
    }

    /// <summary>One clutter type's placement settings, copied from ClutterSystem.Clutter on the main thread.</summary>
    internal sealed class GrassClutterSpec {
        internal bool Enabled;
        internal Heightmap.Biome Biome;
        internal bool Instanced;
        internal GameObject Prefab;
        internal Vector3 RendererScale;
        internal int Amount;
        internal bool OnUncleared;
        internal bool OnCleared;
        internal float MinVegetation;
        internal float MaxVegetation;
        internal float ScaleMin;
        internal float ScaleMax;
        internal float MaxTilt;
        internal float MinTilt;
        internal float MaxAlt;
        internal float MinAlt;
        internal bool SnapToWater;
        internal bool TerrainTilt;
        internal float RandomOffset;
        internal float MinOceanDepth;
        internal float MaxOceanDepth;
        internal bool InForest;
        internal float ForestThresholdMin;
        internal float ForestThresholdMax;
        internal float FractalScale;
        internal float FractalOffset;
        internal float FractalThresholdMin;
        internal float FractalThresholdMax;

        /// <summary>A copy of the entry, or null when an instanced prefab has no InstanceRenderer.</summary>
        internal static GrassClutterSpec From(ClutterSystem.Clutter c) {
            Vector3 rendererScale = Vector3.one;
            if (c.m_instanced && c.m_enabled) {
                InstanceRenderer renderer = c.m_prefab != null ? c.m_prefab.GetComponent<InstanceRenderer>() : null;
                if (renderer == null) { return null; }
                rendererScale = renderer.m_scale;
            }

            return new GrassClutterSpec {
                Enabled = c.m_enabled,
                Biome = c.m_biome,
                Instanced = c.m_instanced,
                Prefab = c.m_prefab,
                RendererScale = rendererScale,
                Amount = c.m_amount,
                OnUncleared = c.m_onUncleared,
                OnCleared = c.m_onCleared,
                MinVegetation = c.m_minVegetation,
                MaxVegetation = c.m_maxVegetation,
                ScaleMin = c.m_scaleMin,
                ScaleMax = c.m_scaleMax,
                MaxTilt = c.m_maxTilt,
                MinTilt = c.m_minTilt,
                MaxAlt = c.m_maxAlt,
                MinAlt = c.m_minAlt,
                SnapToWater = c.m_snapToWater,
                TerrainTilt = c.m_terrainTilt,
                RandomOffset = c.m_randomOffset,
                MinOceanDepth = c.m_minOceanDepth,
                MaxOceanDepth = c.m_maxOceanDepth,
                InForest = c.m_inForest,
                ForestThresholdMin = c.m_forestTresholdMin,
                ForestThresholdMax = c.m_forestTresholdMax,
                FractalScale = c.m_fractalScale,
                FractalOffset = c.m_fractalOffset,
                FractalThresholdMin = c.m_fractalTresholdMin,
                FractalThresholdMax = c.m_fractalTresholdMax,
            };
        }

        internal bool SameAs(ClutterSystem.Clutter c) {
            return Enabled == c.m_enabled && Biome == c.m_biome && Instanced == c.m_instanced
                && ReferenceEquals(Prefab, c.m_prefab) && Amount == c.m_amount
                && OnUncleared == c.m_onUncleared && OnCleared == c.m_onCleared
                && MinVegetation == c.m_minVegetation && MaxVegetation == c.m_maxVegetation
                && ScaleMin == c.m_scaleMin && ScaleMax == c.m_scaleMax
                && MaxTilt == c.m_maxTilt && MinTilt == c.m_minTilt
                && MaxAlt == c.m_maxAlt && MinAlt == c.m_minAlt
                && SnapToWater == c.m_snapToWater && TerrainTilt == c.m_terrainTilt
                && RandomOffset == c.m_randomOffset
                && MinOceanDepth == c.m_minOceanDepth && MaxOceanDepth == c.m_maxOceanDepth
                && InForest == c.m_inForest
                && ForestThresholdMin == c.m_forestTresholdMin && ForestThresholdMax == c.m_forestTresholdMax
                && FractalScale == c.m_fractalScale && FractalOffset == c.m_fractalOffset
                && FractalThresholdMin == c.m_fractalTresholdMin && FractalThresholdMax == c.m_fractalTresholdMax;
        }
    }

    /// <summary>
    /// One grass patch to place: its inputs, copied on the main thread, and the blades a worker
    /// placed from them.
    /// </summary>
    internal sealed class GrassPatchJob {
        // ---- inputs, read-only once queued ----
        internal Vector2Int PatchId;
        internal Vector3 Center;
        internal float Size;
        internal Heightmap.Biome PatchBiomes;
        internal ClutterSystem.Quality Quality;
        internal float AmountScale;
        internal float WaterLevel;
        internal GrassClutterSpec[] Clutter;
        // The maps of the zones the patch touches, keyed as HeightmapLookupPatch files them, and
        // the same maps in order for its fallback scan.
        internal Vector2s[] Zones;
        internal GrassTerrain[] Terrain;

        // ---- outputs, written by the worker, read once Done ----
        // Per clutter type, its blades as the slice [Start, Start + Count) of Matrices. Only
        // instanced types are placed here: the main thread builds a patch the game's way when it
        // would spawn other clutter, whose objects can draw from UnityEngine.Random as they wake.
        internal int[] Start;
        internal int[] Count;
        internal Matrix4x4[] Matrices = new Matrix4x4[0];
        internal Exception Error;
        internal volatile bool Done;
        internal volatile bool Cancelled;

        // ---- main thread only ----
        internal object Patch;
        // With verification on: the game's own build of the patch, made when the terrain was copied,
        // as each grass object's blades in order.
        internal List<Matrix4x4[]> Reference;
        // Grass the patch held before this rebuild, kept on screen until this one is applied.
        internal readonly List<GameObject> Retired = new List<GameObject>();

        private GrassTerrain TerrainAt(Vector3 point) {
            Vector2s zone = ZoneSystem.GetZone(point);
            for (int i = 0; i < Zones.Length; i++) {
                if (Zones[i] == zone && Terrain[i] != null && Terrain[i].Contains(point)) { return Terrain[i]; }
            }

            for (int i = 0; i < Terrain.Length; i++) {
                if (Terrain[i] != null && Terrain[i].Contains(point)) { return Terrain[i]; }
            }

            return null;
        }

        // ClutterSystem.GetGroundInfo as ClutterGroundDataPatch answers it.
        private bool GetGroundInfo(Vector3 p, out Vector3 point, out Vector3 normal, out GrassTerrain map, out Heightmap.Biome biome) {
            map = TerrainAt(p);
            if (map != null && map.TryGetSurface(p, out float height, out Vector3 surfaceNormal)
                && height <= p.y + 500f && height >= p.y - 500f) {
                point = new Vector3(p.x, height, p.z);
                normal = surfaceNormal;
                biome = map.GetBiome(point);
                return true;
            }

            point = p;
            normal = Vector3.up;
            map = null;
            biome = Heightmap.Biome.Meadows;
            return false;
        }

        /// <summary>True when the patch would spawn clutter other than instanced blades.</summary>
        internal static bool SpawnsObjects(GrassClutterSpec[] clutter, Heightmap.Biome patchBiomes) {
            foreach (GrassClutterSpec spec in clutter) {
                if (spec.Enabled && !spec.Instanced && (patchBiomes & spec.Biome) != 0) { return true; }
            }

            return false;
        }

        /// <summary>
        /// ClutterSystem.GenerateVegPatch's placement loop, in the same order and with the same
        /// random draws, minus the object creation, which the main thread does from the results.
        /// </summary>
        internal void Build() {
            int types = Clutter.Length;
            Start = new int[types];
            Count = new int[types];
            List<Matrix4x4> matrices = new List<Matrix4x4>();

            float num = Size / 2f;
            for (int i = 0; i < types; i++) {
                GrassClutterSpec clutter = Clutter[i];
                Start[i] = matrices.Count;
                if (Cancelled) { return; }
                if (!clutter.Enabled || !clutter.Instanced || (PatchBiomes & clutter.Biome) == 0) { continue; }

                UnityRandom random = new UnityRandom(PatchId.x * (PatchId.y * 1374) + i * 9321);
                Vector3 vector = new Vector3(clutter.FractalOffset, 0f, 0f);
                float num2 = Mathf.Cos(Mathf.PI / 180f * clutter.MaxTilt);
                float num3 = Mathf.Cos(Mathf.PI / 180f * clutter.MinTilt);
                int num4 = (int)((float)(Quality switch {
                    ClutterSystem.Quality.Low => clutter.Amount / 4,
                    ClutterSystem.Quality.Med => clutter.Amount / 2,
                    _ => clutter.Amount,
                }) * AmountScale);

                int placed = 0;
                for (int j = 0; j < num4; j++) {
                    Vector3 vector2 = new Vector3(random.Range(Center.x - num, Center.x + num), 0f, random.Range(Center.z - num, Center.z + num));
                    float num5 = random.Range(0, 360);
                    if (clutter.InForest) {
                        float forestFactor = WorldGenerator.GetForestFactor(vector2);
                        if (forestFactor < clutter.ForestThresholdMin || forestFactor > clutter.ForestThresholdMax) { continue; }
                    }

                    if (clutter.FractalScale > 0f) {
                        float num6 = Utils.Fbm(vector2 * 0.01f * clutter.FractalScale + vector, 3, 1.6f, 0.7f);
                        if (num6 < clutter.FractalThresholdMin || num6 > clutter.FractalThresholdMax) { continue; }
                    }

                    if (!GetGroundInfo(vector2, out Vector3 point, out Vector3 normal, out GrassTerrain hmap, out Heightmap.Biome biome)
                        || (clutter.Biome & biome) == 0) {
                        continue;
                    }

                    float num7 = point.y - WaterLevel;
                    if (num7 < clutter.MinAlt || num7 > clutter.MaxAlt || normal.y < num2 || normal.y > num3) { continue; }

                    if (clutter.MinOceanDepth != clutter.MaxOceanDepth) {
                        float oceanDepth = hmap.GetOceanDepth(vector2);
                        if (oceanDepth < clutter.MinOceanDepth || oceanDepth > clutter.MaxOceanDepth) { continue; }
                    }

                    if (clutter.MinVegetation != clutter.MaxVegetation) {
                        float vegetationMask = hmap.GetVegetationMask(point);
                        if (vegetationMask > clutter.MaxVegetation || vegetationMask < clutter.MinVegetation) { continue; }
                    }

                    if (!clutter.OnCleared || !clutter.OnUncleared) {
                        bool flag = hmap.IsCleared(point);
                        if ((clutter.OnCleared && !flag) || (clutter.OnUncleared & flag)) { continue; }
                    }

                    vector2 = point;
                    if (clutter.SnapToWater) { vector2.y = WaterLevel; }
                    if (clutter.RandomOffset != 0f) { vector2.y += random.Range(0f - clutter.RandomOffset, clutter.RandomOffset); }

                    Quaternion identity = clutter.TerrainTilt ? Quaternion.AngleAxis(num5, normal) : Quaternion.Euler(0f, num5, 0f);

                    // Drawn whether or not it fits: AddInstance drops past 1,023, after the draw.
                    float scale = random.Range(clutter.ScaleMin, clutter.ScaleMax);
                    if (placed < 1023) {
                        matrices.Add(Matrix4x4.TRS(vector2, identity, clutter.RendererScale * scale));
                        placed++;
                    }
                }

                Count[i] = matrices.Count - Start[i];
            }

            Matrices = matrices.ToArray();
        }
    }
}
