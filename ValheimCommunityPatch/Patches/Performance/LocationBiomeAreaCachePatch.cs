using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Location Biome Area Rescan: location generation remembers each zone's biome-area answer
    // instead of recomputing it tens of millions of times.
    //
    // ZoneSystem.GenerateLocationsTimeSliced makes 100,000 placement attempts per location type
    // and gates every attempt on WorldGenerator.GetBiomeArea(zone centre), which is nine
    // GetBiome calls through the multi-octave noise chain. The positions are zone centres, about
    // 78,000 on a vanilla world, so across ~150 location types that is on the order of 10^8 noise
    // evaluations to derive 78,000 distinct values, repeated on every genloc.
    //
    // A prefix answers repeats from a cache keyed on exact (x, z); a postfix records what vanilla
    // computed on a miss. The value handed back is the one vanilla itself produced for that input
    // and the random stream is untouched, so worlds come out identical and this is safe on an
    // existing save. GetBiomeArea has exactly one caller, a main-thread coroutine, which is why the
    // cache sits here and not on GetBiome, which the terrain build thread also calls. The cache is
    // owned by the WorldGenerator instance that filled it, so a new world invalidates it.
    //
    // Server: only the world owner generates locations.
    // Provenance: the observation, not the code, from worldGenAccelerator (jneb802 / warpalicious,
    // MIT), which trades vanilla world layout for more speed; that trade is declined here.
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

        // Only so a third-party caller passing arbitrary points cannot grow the cache without
        // bound. A vanilla world needs ~78k entries, a 20 km world ~311k.
        private const int MaxEntries = 1 << 20;

        /// <summary>
        /// Exact (x, z). GetBiomeArea reads only those two components, so dropping y is exact.
        /// Not rounded to a zone, which would hand one zone's answer to any caller that does not
        /// pass zone centres. float.Equals rather than == so NaN matches itself.
        /// </summary>
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

        internal struct Probe {
            internal Key m_key;
            internal bool m_active;
            internal bool m_hit;
            internal Heightmap.BiomeArea m_cached;
        }

        private static readonly Dictionary<Key, Heightmap.BiomeArea> Cache =
            new Dictionary<Key, Heightmap.BiomeArea>();

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

            // A plain Dictionary is safe because vanilla's only caller is the main thread; anything
            // else gets vanilla rather than a torn read.
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

        // Vanilla flips this once when placement finishes, which is where the numbers are worth
        // reporting. The cache stays valid for this world; only the counters restart.
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
