using System.Collections.Generic;
using BepInEx.Configuration;
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
    // of interop calls per frame; profiling attributed ~300 seconds of a day-long session to these
    // loops - 42% of all time in stutter-heavy seconds of a Mistlands run.
    //
    // Fix: snapshot every mister's and demister's (position, range) into plain arrays once per
    // frame, lazily on the first query, and run the same arithmetic over the snapshot. Tens of
    // interop calls per frame replace thousands. The comparison math mirrors vanilla line for line.
    //
    // Staleness is bounded by one frame, which is within vanilla's own tolerance: ParticleMist
    // updates on a 100 ms accumulator and Mister.GetDemistersSorted snapshots distances once per
    // tick, so vanilla already treats these positions as constant across a whole tick. Demisters do
    // move (a carried torch), by well under a metre per frame against ranges of several metres. A
    // mister enabled mid-frame is invisible to queries until the next frame - one frame of fog
    // emission difference, purely cosmetic. Nothing here is networked.
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
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(MisterCachePatch),
                ValConfig.SectionPerformance,
                "Fix Mist Query Overhead",
                true,
                "Snapshots mist volume positions once per frame instead of reading them from the " +
                "engine per emitted particle. In the Mistlands, vanilla's mist emission does thousands " +
                "of native position reads per frame, which is a large share of the biome's frame cost.");
        }

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

        // Grow-only, so steady state allocates nothing. Counts track the live prefix.
        private static MisterSnap[] Misters = new MisterSnap[64];
        private static int MisterCount;
        private static DemisterSnap[] Demisters = new DemisterSnap[64];
        private static int DemisterCount;
        private static int SnapFrame = -1;

        private static void EnsureFresh() {
            int frame = Time.frameCount;
            if (frame == SnapFrame) { return; }
            SnapFrame = frame;

            List<Mister> misters = Mister.GetMisters();
            if (Misters.Length < misters.Count) { Misters = new MisterSnap[Mathf.NextPowerOfTwo(misters.Count)]; }
            MisterCount = misters.Count;
            for (int i = 0; i < misters.Count; i++) {
                Mister m = misters[i];
                Misters[i] = new MisterSnap {
                    m_mister = m,
                    m_pos = m.transform.position,
                    m_radius = m.m_radius,
                    m_height = m.m_height,
                };
            }

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

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Mister.InsideMister))]
        private static bool InsideMisterPrefix(Vector3 p, float radius, ref bool __result) {
            if (Enabled == null || !Enabled.Value) { return true; }

            EnsureFresh();
            __result = false;
            for (int i = 0; i < MisterCount; i++) {
                ref MisterSnap snap = ref Misters[i];
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
            if (Enabled == null || !Enabled.Value) { return true; }

            EnsureFresh();
            __result = false;
            for (int i = 0; i < MisterCount; i++) {
                ref MisterSnap snap = ref Misters[i];

                // ReferenceEquals is enough: the snapshot only holds components that were alive in
                // the registry this frame, so Unity's alive-checking == has nothing extra to add.
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
            if (Enabled == null || !Enabled.Value) { return true; }

            EnsureFresh();

            // Vanilla reads its own live position here too; one interop call per mister per tick.
            Vector3 position = __instance.transform.position;
            float radius = __instance.m_radius;
            float height = __instance.m_height;

            __result = false;
            for (int i = 0; i < MisterCount; i++) {
                ref MisterSnap snap = ref Misters[i];
                if (ReferenceEquals(snap.m_mister, __instance)) { continue; }

                if (Vector3.Distance(position, snap.m_pos) + radius + thickness < snap.m_radius
                    && position.y + height < snap.m_pos.y + snap.m_height) {
                    __result = true;
                    break;
                }
            }

            return false;
        }

        [HarmonyPatch(typeof(ParticleMist))]
        internal static class DemisterQueryHooks {
            // The fields list vanilla iterates is rebuilt every tick from Demister.GetDemisters() -
            // the same registry the snapshot reads - so membership is identical; its distance sort
            // only changes early-exit order of a boolean any-test, never the answer.
            [HarmonyPrefix]
            [HarmonyPatch("IsInsideOtherDemister")]
            private static bool IsInsideOtherDemisterPrefix(
                List<Demister> fields, Vector3 p, float radius, Demister ignore, ref bool __result) {
                if (Enabled == null || !Enabled.Value) { return true; }

                EnsureFresh();
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
                if (Enabled == null || !Enabled.Value) { return true; }

                EnsureFresh();
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
        }
    }
}
