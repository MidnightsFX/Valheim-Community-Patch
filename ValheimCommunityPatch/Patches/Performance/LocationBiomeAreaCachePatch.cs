using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: location placement asks the same question tens of millions of times to get
    // about eighty thousand distinct answers. ZoneSystem.GenerateLocationsTimeSliced
    // (ZoneSystem.cs:957-1141) runs 100,000 attempts per location type - 200,000 for a
    // prioritized one - and every attempt picks a fresh random zone and gates it on
    // (ZoneSystem.cs:1005):
    //
    //   if ((location.m_biomeArea & WorldGenerator.instance.GetBiomeArea(ZoneSystem.GetZonePos(zoneID))) == 0)
    //
    // GetBiomeArea (WorldGenerator.cs:576-588) is nine GetBiome calls - the sample point and its
    // eight neighbours at +/-64m - and each GetBiome runs the multi-octave Perlin chain in
    // GetBaseHeight (WorldGenerator.cs:620-651). It is a pure function of the position, and the
    // positions are zone centres: a vanilla 10km world has ~78,000 of them. Across the ~150
    // enabled location types that is on the order of 10^8 noise evaluations spent re-deriving
    // ~78,000 values, and it is paid again on every genloc and whenever a mod adds locations.
    //
    // Fix: memoise GetBiomeArea by exact (x, z). A prefix answers repeats, a postfix records what
    // vanilla computed on a miss. Output is identical by construction rather than by argument -
    // the value handed back is the one vanilla itself produced for that exact input, no branch is
    // taken that vanilla would not take, and the UnityEngine.Random stream is never touched. Same
    // seed, same world, and safe to turn on for an existing save.
    //
    // Two properties of the target make this cheap to be sure about, both checked against the
    // decompiled source. WorldGenerator.GetBiomeArea(Vector3) has exactly one call site in the
    // whole game - the line above (Heightmap.GetBiomeArea() at Heightmap.cs:345 is an unrelated
    // instance method) - so there is no second caller to keep correct and no unbounded key space.
    // And that call site is a coroutine, so it is main thread only. GetBiome by contrast IS
    // called off-thread from HeightmapBuilder.BuildThread (HeightmapBuilder.cs:103-143), which is
    // exactly why the cache sits on GetBiomeArea and not one level down on GetBiome.
    //
    // This does not make location generation an order of magnitude faster. Per attempt vanilla
    // spends ~9 GetBiome on this gate and, when the gate passes, up to ~20 more on the per-point
    // biome checks inside the zone. Removing the 9 is most of what can be removed without
    // changing which points get sampled - and changing that is what moves every dungeon in the
    // world, which is not a trade a patch mod should make silently.
    //
    // Server: only the machine that owns the world generates locations. GenerateLocations is
    // reached from ZNet.ServerLoadWorld (ZNet.cs:193) and from the genloc console command, which
    // is declared onlyServer.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(WorldGenerator))]
    internal static class LocationBiomeAreaCachePatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Location Biome Area Cache",
                false,
                "Diagnostic. Recomputes every remembered biome-area answer the vanilla way, " +
                "acts on vanilla's answer, and logs any disagreement. Costs exactly the work " +
                "this fix exists to avoid, so leave it off unless you are validating the cache. " +
                "There should never be a disagreement: a cached value is one vanilla itself " +
                "produced for the same coordinates.",
                advanced: true);

            // Bound during plugin Awake, so this is the Unity main thread.
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        // Only exists so that a third-party caller passing arbitrary points - vanilla passes zone
        // centres and nothing else - cannot grow this without bound. A vanilla world needs ~78k
        // entries and a 20km ExpandWorldSize map ~311k, both well under the cap.
        private const int MaxEntries = 1 << 20;

        /// <summary>
        /// Exact (x, z). GetBiomeArea reads only those two components (WorldGenerator.cs:578-586),
        /// so dropping y is exact rather than an approximation.
        /// </summary>
        /// <remarks>
        /// Deliberately not keyed on a zone id. Rounding a position to the zone containing it
        /// would hand one zone's answer to any caller that does not pass zone centres, which is a
        /// wrong answer rather than a slow one. Exact float equality can only ever miss.
        /// float.Equals, not ==, so a NaN coordinate matches itself and stays consistent with
        /// GetHashCode instead of silently accumulating unreachable entries.
        /// </remarks>
        internal readonly struct Key : IEquatable<Key> {
            private readonly float m_x;
            private readonly float m_z;

            internal Key(float x, float z) {
                m_x = x;
                m_z = z;
            }

            public bool Equals(Key other) => m_x.Equals(other.m_x) && m_z.Equals(other.m_z);

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() => (m_x.GetHashCode() * 397) ^ m_z.GetHashCode();

            public override string ToString() => $"({m_x}, {m_z})";
        }

        /// <summary>What the prefix looked up, handed to the postfix.</summary>
        internal struct Probe {
            internal Key m_key;
            internal bool m_active;
            internal bool m_hit;
            internal Heightmap.BiomeArea m_cached;
        }

        private static readonly Dictionary<Key, Heightmap.BiomeArea> Cache =
            new Dictionary<Key, Heightmap.BiomeArea>();

        // The WorldGenerator that filled the cache. Same trick as RunMode.Resolve: the instance
        // IS the invalidation. A new world means a new WorldGenerator with different noise
        // offsets, and there is no hook to remember to add and nothing to forget.
        private static WorldGenerator _owner;

        private static int _mainThreadId;
        private static int _hits;
        private static int _misses;
        private static int _divergences;
        private static bool _cappedWarned;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(WorldGenerator.GetBiomeArea))]
        private static bool GetBiomeAreaPrefix(
                WorldGenerator __instance,
                Vector3 point,
                ref Heightmap.BiomeArea __result,
                out Probe __state) {
            __state = default;

            // A plain Dictionary, because vanilla's only caller is the main-thread location
            // coroutine. Anything calling this from a worker thread gets vanilla rather than a
            // torn read - the check costs an int compare against nine Perlin chains.
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) { return true; }

            if (!ReferenceEquals(__instance, _owner)) {
                Clear();
                _owner = __instance;
            }

            __state.m_key = new Key(point.x, point.z);
            __state.m_active = true;

            if (!Cache.TryGetValue(__state.m_key, out Heightmap.BiomeArea cached)) {
                _misses++;
                return true;
            }

            _hits++;
            __state.m_hit = true;
            __state.m_cached = cached;

            // Verifying: run vanilla anyway so the postfix has something to compare against.
            if (Verify != null && Verify.Value) { return true; }

            __result = cached;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(WorldGenerator.GetBiomeArea))]
        private static void GetBiomeAreaPostfix(Heightmap.BiomeArea __result, Probe __state) {
            if (!__state.m_active) { return; }

            if (__state.m_hit) {
                // With verify off the prefix already answered from the cache and skipped the
                // original, so __result IS m_cached here and comparing them proves nothing.
                if (Verify == null || !Verify.Value) { return; }

                if (__state.m_cached != __result) {
                    _divergences++;
                    Cache[__state.m_key] = __result;
                    Logger.LogError(
                        $"Location biome area verify: DIVERGED at {__state.m_key} (cached: " +
                        $"{__state.m_cached}, vanilla: {__result}). Vanilla's answer was used. " +
                        "Please report this - leave 'Fix Location Biome Area Rescan' off until " +
                        "it is understood.");
                }

                return;
            }

            if (Cache.Count >= MaxEntries) {
                if (!_cappedWarned) {
                    _cappedWarned = true;
                    Logger.LogWarning(
                        $"Location biome area cache hit its {MaxEntries} entry cap and stopped " +
                        "growing; further lookups fall through to vanilla. This should not " +
                        "happen on any normal world size - please report it.");
                }

                return;
            }

            Cache[__state.m_key] = __result;
        }

        // Vanilla flips this once when placement finishes (ZoneSystem.cs:939). The cache does
        // nothing for the rest of the session, so this is where it is worth reporting.
        [HarmonyPatch(typeof(ZoneSystem), "set_LocationsGenerated")]
        internal static class GenerationCompleteHook {
            [HarmonyPostfix]
            private static void Postfix(bool value) {
                if (!value) { return; }
                if (_hits == 0 && _misses == 0) { return; }

                long total = (long)_hits + _misses;
                Logger.LogInfo(
                    $"Location biome area cache: {_hits} of {total} lookups answered from " +
                    $"{Cache.Count} cached zone(s), avoiding {(long)_hits * 9} terrain noise " +
                    "samples.");

                if (Verify != null && Verify.Value) {
                    Logger.LogInfo(
                        $"Location biome area verify: {_hits} comparison(s), {_divergences} " +
                        "divergence(s).");
                }

                // The cache stays - it is still valid for this world - but the counters restart,
                // so a later genloc reports its own run rather than a running total.
                ResetCounters();
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                Clear();
                _owner = null;
            }
        }

        private static void Clear() {
            Cache.Clear();
            ResetCounters();
        }

        private static void ResetCounters() {
            _hits = 0;
            _misses = 0;
            _divergences = 0;
            _cappedWarned = false;
        }
    }
}
