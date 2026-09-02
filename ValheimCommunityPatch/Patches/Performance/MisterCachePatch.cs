using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: the mist queries are per-candidate loops over live components that read
    // transform.position - a native interop call - once or twice per Mister per check:
    //
    //   foreach (Mister instance in Mister.m_instances)
    //     if (Vector3.Distance(instance.transform.position, p) < instance.m_radius + radius && ...)
    //
    // and ParticleMist.Emit / MisterEmit run these *per emitted particle candidate*, ten times a
    // second, with a further per-particle loop over every Demister reading a native
    // ParticleSystemForceField.endRange property. In the Mistlands that multiplies out to thousands
    // of interop calls per frame.
    //
    // The first version of this fix snapshotted positions once per frame and ran the same loops as
    // pure math. Profiling the result showed two costs it left behind, both real in the Mistlands:
    // the loops are O(particle candidates x ALL loaded misters) - hundreds of misters at dozens of
    // candidates per tick - and the per-frame snapshot rebuild itself re-read every mister's
    // position at several hundred fps. Both are gone:
    //
    //  - The mister snapshot is event-driven. Misters never move - nothing in the vanilla assembly
    //    writes their transforms - so the snapshot rebuilds only when one spawns or despawns
    //    (OnEnable/OnDisable, the registry mutations), with a slow safety refresh (~every 300
    //    frames) as a hedge against a hypothetical modded mister that does move. Demisters keep
    //    the per-frame refresh: they are few and genuinely move (carried torches, ships).
    //
    //  - Misters are bucketed by 64 m zone at rebuild, each inserted into every zone its circle
    //    (radius + a one-zone margin for query radii) overlaps. A query then reads only the bucket
    //    of its own zone: O(nearby misters, a handful) instead of O(all loaded, hundreds). A query
    //    whose radius argument exceeds the margin - nothing in vanilla does this - falls back to
    //    the full snapshot for correctness.
    //
    //  - ParticleMist.FindMaxMistAlltitude fired 20 ZoneSystem.GetGroundHeight raycasts per 100 ms
    //    tick to estimate the local ground level. The probes are terrain-height questions and are
    //    now answered from heightmap data via the shared registry and sampler, with the Random
    //    draws replicated exactly so particle randomness downstream is unchanged.
    //
    // Staleness is bounded by one frame for demisters and by spawn/despawn events for misters -
    // within vanilla's own tolerance, which treats these positions as constant across each 100 ms
    // tick. The comparison math mirrors vanilla line for line. Not networked; cosmetic only.
    //
    // A demister whose m_forceField is missing (a broken modded prefab - vanilla NREs on it in
    // ParticleMist.Update) is snapshotted with range 0, i.e. never inside; that is the one deliberate
    // divergence, robustness over NRE parity.
    //
    // Compatibility: ComfyMods' Dramamist patches ParticleMist.Awake / Demister.OnEnable and the
    // particle trigger modules - no overlap with the methods replaced here; the two compose.
    //
    // Client: the hot loops never run headless. ParticleMist.Update requires Player.m_localPlayer,
    // and the AI checks (BaseAI.CanSeeTarget etc.) short-circuit on m_haveActiveMist, which only a
    // client's ParticleMist.Update ever sets.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Mister))]
    internal static class MisterCachePatch {
        private struct MisterSnap {
            public Mister m_mister;
            public Vector3 m_pos;
            public float m_radius;
            public float m_height;
        }

        private struct DemisterSnap {
            public Demister m_demister;
            public Vector3 m_pos;
            public float m_endRange;
        }

        // Covers every radius argument vanilla passes (demister end ranges plus mist thickness,
        // well under a zone). A query radius beyond this falls back to the full snapshot.
        private const float BucketMargin = 64f;
        private const int SafetyRefreshFrames = 300;

        // Grow-only, so steady state allocates nothing. Counts track the live prefix.
        private static MisterSnap[] Misters = new MisterSnap[64];
        private static int MisterCount;
        private static DemisterSnap[] Demisters = new DemisterSnap[64];
        private static int DemisterCount;

        private static readonly Dictionary<Vector2i, List<int>> MisterBuckets = new Dictionary<Vector2i, List<int>>();
        private static readonly Stack<List<int>> BucketPool = new Stack<List<int>>();
        private static readonly List<int> EmptyBucket = new List<int>();

        private static bool _mistersDirty = true;
        private static int _misterRebuildFrame = int.MinValue;
        private static int _demisterSnapFrame = -1;

        // ---- snapshot maintenance ----------------------------------------------------------

        // The registry mutations (Mister.cs:23-25). Nothing in the vanilla assembly moves a
        // mister after that, which is what makes the event-driven snapshot sound.
        [HarmonyPostfix]
        [HarmonyPatch("OnEnable")]
        private static void OnEnablePostfix() => _mistersDirty = true;

        [HarmonyPostfix]
        [HarmonyPatch("OnDisable")]
        private static void OnDisablePostfix() => _mistersDirty = true;

        private static void EnsureMisters() {
            int frame = Time.frameCount;
            if (!_mistersDirty && frame - _misterRebuildFrame < SafetyRefreshFrames) { return; }
            _mistersDirty = false;
            _misterRebuildFrame = frame;

            List<Mister> misters = Mister.GetMisters();
            if (Misters.Length < misters.Count) { Misters = new MisterSnap[Mathf.NextPowerOfTwo(misters.Count)]; }
            MisterCount = misters.Count;

            foreach (KeyValuePair<Vector2i, List<int>> bucket in MisterBuckets) {
                bucket.Value.Clear();
                BucketPool.Push(bucket.Value);
            }
            MisterBuckets.Clear();

            for (int i = 0; i < misters.Count; i++) {
                Mister m = misters[i];
                Vector3 pos = m.transform.position;
                Misters[i] = new MisterSnap {
                    m_mister = m,
                    m_pos = pos,
                    m_radius = m.m_radius,
                    m_height = m.m_height,
                };

                // Insert into every zone the mister's circle (plus the query margin) overlaps, so
                // a lookup only ever needs the bucket of its own zone.
                float reach = m.m_radius + BucketMargin;
                Vector2i min = ZoneSystem.GetZone(new Vector3(pos.x - reach, 0f, pos.z - reach));
                Vector2i max = ZoneSystem.GetZone(new Vector3(pos.x + reach, 0f, pos.z + reach));
                for (int zx = min.x; zx <= max.x; zx++) {
                    for (int zy = min.y; zy <= max.y; zy++) {
                        Vector2i key = new Vector2i(zx, zy);
                        if (!MisterBuckets.TryGetValue(key, out List<int> bucket)) {
                            bucket = BucketPool.Count > 0 ? BucketPool.Pop() : new List<int>();
                            MisterBuckets.Add(key, bucket);
                        }

                        bucket.Add(i);
                    }
                }
            }
        }

        private static List<int> BucketAt(Vector3 p) {
            return MisterBuckets.TryGetValue(ZoneSystem.GetZone(p), out List<int> bucket) ? bucket : EmptyBucket;
        }

        private static void EnsureDemisters() {
            int frame = Time.frameCount;
            if (frame == _demisterSnapFrame) { return; }
            _demisterSnapFrame = frame;

            List<Demister> demisters = Demister.GetDemisters();
            if (Demisters.Length < demisters.Count) { Demisters = new DemisterSnap[Mathf.NextPowerOfTwo(demisters.Count)]; }
            DemisterCount = demisters.Count;
            for (int i = 0; i < demisters.Count; i++) {
                Demister d = demisters[i];
                ParticleSystemForceField field = d.m_forceField;
                Demisters[i] = new DemisterSnap {
                    m_demister = d,
                    m_pos = d.transform.position,
                    m_endRange = field != null ? field.endRange : 0f,
                };
            }
        }

        // ---- mister queries ------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Mister.InsideMister))]
        private static bool InsideMisterPrefix(Vector3 p, float radius, ref bool __result) {
            EnsureMisters();
            __result = false;

            if (radius > BucketMargin) {
                // No vanilla caller reaches this; correctness over speed for a modded one.
                for (int i = 0; i < MisterCount; i++) {
                    ref MisterSnap snap = ref Misters[i];
                    if (Vector3.Distance(snap.m_pos, p) < snap.m_radius + radius && p.y - radius < snap.m_pos.y + snap.m_height) {
                        __result = true;
                        break;
                    }
                }

                return false;
            }

            List<int> bucket = BucketAt(p);
            for (int b = 0; b < bucket.Count; b++) {
                ref MisterSnap snap = ref Misters[bucket[b]];
                if (Vector3.Distance(snap.m_pos, p) < snap.m_radius + radius && p.y - radius < snap.m_pos.y + snap.m_height) {
                    __result = true;
                    break;
                }
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Mister.IsInsideOtherMister))]
        private static bool IsInsideOtherMisterPrefix(Vector3 p, Mister ignore, ref bool __result) {
            EnsureMisters();
            __result = false;

            List<int> bucket = BucketAt(p);
            for (int b = 0; b < bucket.Count; b++) {
                ref MisterSnap snap = ref Misters[bucket[b]];

                // ReferenceEquals is enough: the snapshot only holds components that were alive in
                // the registry when it was built, and the bucket entry proves membership.
                if (ReferenceEquals(snap.m_mister, ignore)) { continue; }

                if (Vector3.Distance(p, snap.m_pos) < snap.m_radius && p.y < snap.m_pos.y + snap.m_height) {
                    __result = true;
                    break;
                }
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Mister.IsCompletelyInsideOtherMister))]
        private static bool IsCompletelyInsideOtherMisterPrefix(Mister __instance, float thickness, ref bool __result) {
            EnsureMisters();

            // Vanilla reads its own live position here too; one interop call per mister per tick.
            // Any qualifying larger mister strictly contains this position, so its circle overlaps
            // this position's zone and the bucket lookup cannot miss it.
            Vector3 position = __instance.transform.position;
            float radius = __instance.m_radius;
            float height = __instance.m_height;

            __result = false;
            List<int> bucket = BucketAt(position);
            for (int b = 0; b < bucket.Count; b++) {
                ref MisterSnap snap = ref Misters[bucket[b]];
                if (ReferenceEquals(snap.m_mister, __instance)) { continue; }

                if (Vector3.Distance(position, snap.m_pos) + radius + thickness < snap.m_radius
                    && position.y + height < snap.m_pos.y + snap.m_height) {
                    __result = true;
                    break;
                }
            }

            return false;
        }

        // ---- demister queries ----------------------------------------------------------------

        [HarmonyPatch(typeof(ParticleMist))]
        internal static class DemisterQueryHooks {
            // The fields list vanilla iterates is rebuilt every tick from Demister.GetDemisters() -
            // the same registry the snapshot reads - so membership is identical; its distance sort
            // only changes early-exit order of a boolean any-test, never the answer.
            [HarmonyPrefix]
            [HarmonyPatch("IsInsideOtherDemister")]
            private static bool IsInsideOtherDemisterPrefix(
                List<Demister> fields, Vector3 p, float radius, Demister ignore, ref bool __result) {
                EnsureDemisters();
                __result = false;
                for (int i = 0; i < DemisterCount; i++) {
                    ref DemisterSnap snap = ref Demisters[i];
                    if (ReferenceEquals(snap.m_demister, ignore)) { continue; }

                    if (Vector3.Distance(snap.m_pos, p) + radius < snap.m_endRange) {
                        __result = true;
                        break;
                    }
                }

                return false;
            }

            [HarmonyPrefix]
            [HarmonyPatch("InsideDemister")]
            private static bool InsideDemisterPrefix(Vector3 p, ref bool __result) {
                EnsureDemisters();
                __result = false;
                for (int i = 0; i < DemisterCount; i++) {
                    ref DemisterSnap snap = ref Demisters[i];
                    if (Vector3.Distance(snap.m_pos, p) < snap.m_endRange) {
                        __result = true;
                        break;
                    }
                }

                return false;
            }

            // Vanilla estimates the local ground level with 20 Physics.Raycasts per 100 ms tick
            // (ParticleMist.cs:228-247). Each probe is a terrain-height question the heightmap
            // data answers directly. The Random draws are replicated exactly, so everything
            // downstream that shares UnityEngine.Random sees an unchanged sequence.
            [HarmonyPrefix]
            [HarmonyPatch("FindMaxMistAlltitude")]
            private static bool FindMaxMistAlltitudePrefix(
                ParticleMist __instance, float testRange, out float minMistHeight, out float maxMistHeight) {
                Vector3 position = __instance.transform.position;
                float sum = 0f;
                minMistHeight = 99999f;
                for (int i = 0; i < 20; i++) {
                    Vector2 circle = Random.insideUnitCircle;
                    Vector3 probe = position + new Vector3(circle.x, 0f, circle.y) * testRange;
                    float ground = GroundHeight(probe);
                    sum += ground;
                    if (ground < minMistHeight) { minMistHeight = ground; }
                }

                maxMistHeight = sum / 20f + __instance.m_maxMistAltitude;
                return false;
            }

            // The single-point GetGroundHeight returns p.y when its ray misses terrain
            // (ZoneSystem.cs:1608-1611); every fallback here mirrors that.
            private static float GroundHeight(Vector3 probe) {
                if (!HeightmapLookupPatch.TryGetCached(probe, out Heightmap hmap, out Vector3 origin)) {
                    return ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(probe) : probe.y;
                }

                if (hmap == null) { return probe.y; }

                return HeightmapSampling.TryGetHeight(hmap, origin, probe, out float height) ? height : probe.y;
            }
        }
    }
}
