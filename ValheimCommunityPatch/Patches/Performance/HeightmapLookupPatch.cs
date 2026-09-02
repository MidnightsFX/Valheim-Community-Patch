using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Heightmap Lookup Scan: "which terrain tile is this point on" is a zone-keyed lookup
    // instead of a scan of every loaded tile with a native position read per candidate.
    //
    // Heightmap.FindHeightmap(point) loops over s_heightmaps calling IsPointInside, which reads
    // transform.position each time, and HaveQueuedRebuild(point, radius) has the same shape.
    // With the 50-100 heightmaps of a busy area that is thousands of native reads a second for
    // answers that never change: a zone heightmap is instantiated at its zone centre and never
    // moves.
    //
    // A registry mirrors s_heightmaps with cached centres, filed by zone. FindHeightmap becomes
    // one ZoneSystem.GetZone plus a dictionary hit, with a scan over cached floats as the fallback
    // for mod-created maps that are not zone-aligned, in s_heightmaps order so the first match is
    // vanilla's. HaveQueuedRebuild becomes pure math. The radius overload of FindHeightmap is
    // left vanilla because terrain-op fan-out writes terrain data through it. For a point exactly
    // on a shared edge vanilla returns whichever map registered first and this returns the zone's
    // own map; both contain the point and share identical edge vertices.
    //
    // Both: FindHeightmap runs on a dedicated server through ground queries and StaticPhysics.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class HeightmapLookupPatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Heightmap Registry",
                false,
                "Diagnostic. Runs both the zone-keyed lookup and vanilla's scan on every terrain tile " +
                "lookup, acts on vanilla's result, and logs any real disagreement. Costs the scan this " +
                "fix exists to avoid, so leave it off unless you are validating the registry.",
                advanced: true);
        }

        private struct Entry {
            public Heightmap m_hmap;
            public float m_cx;
            public float m_cy;
            public float m_cz;
            public float m_half;
        }

        // Same membership and order as s_heightmaps; ByZone is the fast path on top.
        private static readonly List<Entry> Registered = new List<Entry>();
        private static readonly Dictionary<Vector2i, Entry> ByZone = new Dictionary<Vector2i, Entry>();

        // A missing hook means the registry diverges from s_heightmaps and a wrong FindHeightmap
        // answer feeds terrain queries game-wide, so the answer gates the fix entirely.
        private static readonly HookHealth Hooks = new HookHealth(
            "Heightmap registry",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Heightmap), "Awake"), typeof(AwakeHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Heightmap), "OnDestroy"), typeof(DestroyHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.Regenerate)), typeof(RegenerateHook)));

        // ---- registry maintenance ----------------------------------------------------------

        private static Entry MakeEntry(Heightmap hmap) {
            Vector3 position = hmap.transform.position;
            return new Entry {
                m_hmap = hmap,
                m_cx = position.x,
                m_cy = position.y,
                m_cz = position.z,
                m_half = hmap.m_width * hmap.m_scale * 0.5f,
            };
        }

        /// <summary>
        /// Registry-served lookup with the cached transform origin, for in-mod callers that would
        /// otherwise pay a native transform read per query.
        /// </summary>
        /// <remarks>
        /// Returns false only when the registry cannot serve; the caller then falls back to
        /// Heightmap.FindHeightmap plus a live transform read. True with a null
        /// <paramref name="hmap"/> is a definitive miss.
        /// </remarks>
        internal static bool TryGetCached(Vector3 point, out Heightmap hmap, out Vector3 origin) {
            hmap = null;
            origin = default;

            if (!Hooks.Healthy) { return false; }

            if (ByZone.TryGetValue(ZoneSystem.GetZone(point), out Entry entry) && Contains(entry, point)) {
                hmap = entry.m_hmap;
                origin = new Vector3(entry.m_cx, entry.m_cy, entry.m_cz);
                return true;
            }

            for (int i = 0; i < Registered.Count; i++) {
                if (!Contains(Registered[i], point)) { continue; }

                hmap = Registered[i].m_hmap;
                origin = new Vector3(Registered[i].m_cx, Registered[i].m_cy, Registered[i].m_cz);
                return true;
            }

            return true;
        }

        private static void FileByZone(Entry entry) {
            Vector2i zone = ZoneSystem.GetZone(new Vector3(entry.m_cx, 0f, entry.m_cz));

            // Two maps claiming one zone should not happen for zone terrain, but a mod-created
            // heightmap could. Last writer wins; the containment check and scan keep lookups right.
            if (ByZone.TryGetValue(zone, out Entry existing) && !ReferenceEquals(existing.m_hmap, entry.m_hmap)) {
                Logger.LogDebug($"Two heightmaps registered for zone {zone}; keeping the newest.");
            }

            ByZone[zone] = entry;
        }

        private static void Unfile(Heightmap hmap, float cx, float cz) {
            Vector2i zone = ZoneSystem.GetZone(new Vector3(cx, 0f, cz));
            if (ByZone.TryGetValue(zone, out Entry existing) && ReferenceEquals(existing.m_hmap, hmap)) {
                ByZone.Remove(zone);
            }
        }

        // Mirrors vanilla's registration condition in Heightmap.Awake.
        [HarmonyPatch(typeof(Heightmap), "Awake")]
        internal static class AwakeHook {
            [HarmonyPostfix]
            private static void Postfix(Heightmap __instance) {
                if (__instance.m_isDistantLod) { return; }

                Entry entry = MakeEntry(__instance);
                Registered.Add(entry);
                FileByZone(entry);
            }
        }

        [HarmonyPatch(typeof(Heightmap), "OnDestroy")]
        internal static class DestroyHook {
            [HarmonyPostfix]
            private static void Postfix(Heightmap __instance) {
                for (int i = 0; i < Registered.Count; i++) {
                    if (!ReferenceEquals(Registered[i].m_hmap, __instance)) { continue; }

                    Unfile(__instance, Registered[i].m_cx, Registered[i].m_cz);
                    Registered.RemoveAt(i);
                    return;
                }
            }
        }

        // Zone heightmaps never move, so this is normally a no-op. It exists so that if anything
        // ever relocates a registered map, the Regenerate any visible move must trigger re-syncs
        // the cache instead of leaving it wrong.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
        internal static class RegenerateHook {
            [HarmonyPrefix]
            private static void Prefix(Heightmap __instance) {
                if (__instance.m_isDistantLod) { return; }

                for (int i = 0; i < Registered.Count; i++) {
                    if (!ReferenceEquals(Registered[i].m_hmap, __instance)) { continue; }

                    Entry old = Registered[i];
                    Entry fresh = MakeEntry(__instance);
                    if (fresh.m_cx == old.m_cx && fresh.m_cy == old.m_cy && fresh.m_cz == old.m_cz
                        && fresh.m_half == old.m_half) { return; }

                    Unfile(__instance, old.m_cx, old.m_cz);
                    Registered[i] = fresh;
                    FileByZone(fresh);
                    return;
                }
            }
        }

        // ---- lookups -----------------------------------------------------------------------

        // Vanilla's IsPointInside with radius 0, on the cached centre: inclusive on all edges.
        private static bool Contains(in Entry entry, Vector3 point) {
            return point.x >= entry.m_cx - entry.m_half && point.x <= entry.m_cx + entry.m_half
                && point.z >= entry.m_cz - entry.m_half && point.z <= entry.m_cz + entry.m_half;
        }

        private static Heightmap FastFind(Vector3 point) {
            if (ByZone.TryGetValue(ZoneSystem.GetZone(point), out Entry entry) && Contains(entry, point)) {
                return entry.m_hmap;
            }

            for (int i = 0; i < Registered.Count; i++) {
                if (Contains(Registered[i], point)) { return Registered[i].m_hmap; }
            }

            return null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Heightmap.FindHeightmap), typeof(Vector3))]
        private static bool FindHeightmapPrefix(Vector3 point, ref Heightmap __result) {
            if (!Hooks.Healthy) { return true; }

            Heightmap fast = FastFind(point);

            if (Verify != null && Verify.Value) {
                Heightmap vanilla = VanillaFind(point);

                if (!ReferenceEquals(fast, vanilla)) {
                    // Two maps that both contain the point is the shared-edge tie, not a defect.
                    bool tie = fast != null && vanilla != null && fast.IsPointInside(point) && vanilla.IsPointInside(point);
                    if (tie) {
                        Logger.LogDebug($"Heightmap registry verify: shared-edge tie at {point}.");
                    } else {
                        Logger.LogError(
                            $"Heightmap registry verify: DIVERGED at {point} (fast: " +
                            $"{(fast == null ? "null" : fast.transform.position.ToString())}, vanilla: " +
                            $"{(vanilla == null ? "null" : vanilla.transform.position.ToString())}). " +
                            "Vanilla's result was used. Please report this - leave 'Verify Heightmap " +
                            "Registry' on until it is understood, since the verify pass acts on " +
                            "vanilla's answer.");
                    }
                }

                __result = vanilla;
                return false;
            }

            __result = fast;
            return false;
        }

        private static Heightmap VanillaFind(Vector3 point) {
            foreach (Heightmap heightmap in Heightmap.s_heightmaps) {
                if (heightmap.IsPointInside(point)) { return heightmap; }
            }

            return null;
        }

        // The ClutterSystem hot path: the same inclusive bounds test and the same
        // m_doLateUpdate read, minus the native reads and the list vanilla materialises.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Heightmap.HaveQueuedRebuild), typeof(Vector3), typeof(float))]
        private static bool HaveQueuedRebuildPrefix(Vector3 point, float radius, ref bool __result) {
            if (!Hooks.Healthy) { return true; }

            __result = false;
            for (int i = 0; i < Registered.Count; i++) {
                Entry entry = Registered[i];
                if (point.x + radius >= entry.m_cx - entry.m_half && point.x - radius <= entry.m_cx + entry.m_half
                    && point.z + radius >= entry.m_cz - entry.m_half && point.z - radius <= entry.m_cz + entry.m_half
                    && entry.m_hmap.HaveQueuedRebuild()) {
                    __result = true;
                    break;
                }
            }

            return false;
        }
    }
}
