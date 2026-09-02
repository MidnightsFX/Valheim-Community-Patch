using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: Heightmap.FindHeightmap(point) is a linear scan of every loaded zone heightmap,
    // and the IsPointInside test it runs per candidate reads transform.position - a native interop
    // call - each time:
    //
    //   foreach (Heightmap heightmap in Heightmap.s_heightmaps)
    //     if (heightmap.IsPointInside(point)) return heightmap;
    //
    // The static HaveQueuedRebuild(point, radius) has the same shape through the radius overload, and
    // ClutterSystem.IsHeightmapReady runs it every LateUpdate. With the ~50-100 heightmaps of a busy
    // area that is thousands of interop calls per second for lookups whose answer is a pure function
    // of geometry that never changes: a zone heightmap is instantiated at its zone centre and never
    // moves (only the distant-LOD maps move, and vanilla itself excludes those from s_heightmaps,
    // Heightmap.cs:117-120).
    //
    // Fix: mirror the registry with cached centres and a zone-keyed dictionary. FindHeightmap becomes
    // one ZoneSystem.GetZone plus a dictionary hit in the common case, and the fallback scan runs on
    // cached floats instead of transform reads. HaveQueuedRebuild(point, radius) becomes a pure-math
    // scan. The radius overload of FindHeightmap is deliberately left vanilla: terrain-op fan-out
    // writes terrain data through it, and this fix does not get to trade that risk for microseconds.
    //
    // Known divergence, by design: for a point exactly on a shared zone edge - which both zones'
    // inclusive bounds contain - vanilla returns whichever qualifying map registered first, an order
    // accident; the fast path returns the map of the zone GetZone assigns the point to. Both contain
    // the point, and shared-edge vertices are identical between neighbouring maps (see
    // SeamlessNormalsPatch's header), so no caller can tell the difference. The Verify toggle exists
    // to prove that empirically on a live game.
    //
    // Both: FindHeightmap runs on dedicated servers too, via ZoneSystem ground queries and
    // StaticPhysics.
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

        // Mirrors s_heightmaps: same membership, same order, so the fallback scan preserves
        // vanilla's first-match answer. ByZone is the fast path on top.
        private static readonly List<Entry> Registered = new List<Entry>();
        private static readonly Dictionary<Vector2i, Entry> ByZone = new Dictionary<Vector2i, Entry>();

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

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
        /// otherwise pay a native transform read per query (HeightmapSampling's data paths).
        /// </summary>
        /// <remarks>
        /// Returns false only when the registry cannot serve (hooks unhealthy) - the
        /// caller then falls back to Heightmap.FindHeightmap plus a live transform read. A true
        /// return with a null <paramref name="hmap"/> is a definitive miss: no registered map
        /// contains the point, exactly as vanilla's scan would conclude.
        /// </remarks>
        internal static bool TryGetCached(Vector3 point, out Heightmap hmap, out Vector3 origin) {
            hmap = null;
            origin = default;

            if (!HooksHealthy()) { return false; }

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

            // Two maps claiming one zone should not happen for zone terrain - UpdateTTL destroys a
            // zone before PokeLocalZone recreates it on a later tick - but a mod-created heightmap
            // could. Last writer wins; the containment check and scan fallback keep lookups correct.
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

        // Mirrors vanilla's registration condition exactly (Heightmap.Awake, Heightmap.cs:117-120).
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

        // Zone heightmaps never move, so this refresh should always be a no-op re-file. It exists
        // so that if anything ever does relocate a registered map, the next Regenerate - which any
        // meaningful move must trigger to be visible at all - re-syncs the cache instead of leaving
        // it silently wrong.
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

        private static bool Contains(in Entry entry, Vector3 point) {
            // Vanilla's IsPointInside with radius 0, on the cached centre: inclusive on all edges.
            return point.x >= entry.m_cx - entry.m_half && point.x <= entry.m_cx + entry.m_half
                && point.z >= entry.m_cz - entry.m_half && point.z <= entry.m_cz + entry.m_half;
        }

        private static Heightmap FastFind(Vector3 point) {
            if (ByZone.TryGetValue(ZoneSystem.GetZone(point), out Entry entry) && Contains(entry, point)) {
                return entry.m_hmap;
            }

            // Mod-created maps that are not zone-aligned, and the brief window between spawn ticks.
            // Same order as s_heightmaps, so the first match is the same map vanilla's scan finds.
            for (int i = 0; i < Registered.Count; i++) {
                if (Contains(Registered[i], point)) { return Registered[i].m_hmap; }
            }

            return null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Heightmap.FindHeightmap), typeof(Vector3))]
        private static bool FindHeightmapPrefix(Vector3 point, ref Heightmap __result) {
            if (!HooksHealthy()) { return true; }

            Heightmap fast = FastFind(point);

            if (Verify != null && Verify.Value) {
                Heightmap vanilla = VanillaFind(point);

                if (!ReferenceEquals(fast, vanilla)) {
                    // Different instances that both contain the point is the documented shared-edge
                    // tie, not a defect. Anything else is.
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

        // The ClutterSystem hot path: called every LateUpdate to decide whether grass may generate.
        // Vanilla materialises the radius overload's list just to ask a yes/no question; this is the
        // same inclusive bounds test and the same m_doLateUpdate read, minus the interop.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Heightmap.HaveQueuedRebuild), typeof(Vector3), typeof(float))]
        private static bool HaveQueuedRebuildPrefix(Vector3 point, float radius, ref bool __result) {
            if (!HooksHealthy()) { return true; }

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

        // ---- hook health -------------------------------------------------------------------

        /// True only when every maintenance hook is attached by us. A missing one means the registry
        /// silently diverges from s_heightmaps, and a wrong FindHeightmap answer feeds terrain
        /// queries game-wide - so the answer gates the fix entirely.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            string missing = null;
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(Heightmap), "Awake"), "Heightmap.Awake", ref missing);
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(Heightmap), "OnDestroy"), "Heightmap.OnDestroy", ref missing);
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.Regenerate)), "Heightmap.Regenerate", ref missing);

            _hooksHealthy = missing == null;

            if (!_hooksHealthy) {
                Logger.LogError(
                    $"Heightmap registry: the hook on {missing} is not attached, so the registry " +
                    "cannot be trusted and terrain tile lookups have fallen back to vanilla's scan " +
                    "for this session. This usually means a Valheim update changed that method - " +
                    "look for the patch failure logged at startup.");
            }

            return _hooksHealthy;
        }

        private static void NoteIfUnhooked(MethodBase target, string label, ref string missing) {
            bool ours = false;
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);

            if (info != null) {
                foreach (Patch patch in info.Postfixes) {
                    if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                    ours = true;
                    break;
                }

                if (!ours) {
                    foreach (Patch patch in info.Prefixes) {
                        if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                        ours = true;
                        break;
                    }
                }
            }

            if (ours) { return; }

            missing = missing == null ? label : missing + " and " + label;
        }
    }
}
