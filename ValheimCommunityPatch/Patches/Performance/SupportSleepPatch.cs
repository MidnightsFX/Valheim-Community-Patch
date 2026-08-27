using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: WearNTear.UpdateSupport already has a cache fast path, but it RE-VALIDATES
    // the cache on every visit - for each cached support collider it re-resolves the owning piece,
    // reads its transform position and re-reads its support value, all native or near-native calls
    // (WearNTear.cs:421-452). WearNTearUpdater visits pieces continuously (a nominal 1-second
    // sweep, capped at 100 pieces per frame, so a 60-90k-piece base runs it saturated at a
    // ~20-second cycle), which makes the validation itself the cost: ~100 ms of every second
    // standing in a large base, spent re-proving that nothing changed.
    //
    // Fix: sleep the check instead of re-running it. A piece's support is a pure function of its
    // neighbourhood - the pieces whose colliders its overlap boxes see, their support values, and
    // the terrain - so a piece whose support was computed once may skip UpdateSupport entirely
    // until one of the events that can change that neighbourhood fires:
    //
    //  - ClearCachedSupport ran on it. This is the funnel for every vanilla invalidation signal:
    //    terrain rebuilds (via the heightmap registry or vanilla's event), the cross-client RPC,
    //    and the m_clearCachedSupport broadcast a freshly PLACED piece performs on its first
    //    UpdateSupport (OnPlaced sets the flag; WearNTear.cs:467-485 delivers it to every
    //    overlapping piece as a direct ClearCachedSupport call).
    //  - A neighbouring piece was destroyed. Vanilla notices this by tripping over the dead
    //    collider during validation; a sleeping piece cannot, so OnDestroy wakes everything whose
    //    support region overlaps the dead piece. Candidates come from a coarse world-grid of
    //    registered support envelopes, then an exact envelope-overlap test picks the true
    //    neighbours - measured before that test existed, a single wake in a dense base dirtied
    //    every piece sharing a cell (hundreds) instead of the handful that actually touch, and
    //    the spurious recomputes plus their bookkeeping were ~30 ms of every second on their own.
    //  - A neighbour's support VALUE changed. Support propagates as a relaxation wave - each
    //    piece's value feeds its neighbours' - so whenever a non-skipped UpdateSupport produces a
    //    different m_support than before, its grid neighbours are woken. Waves therefore travel
    //    exactly as far as they would in vanilla, and a structure at equilibrium goes fully
    //    quiet. This is the same fixpoint vanilla converges to; only the redundant confirmations
    //    of already-converged values are gone.
    //
    // Pieces that never computed under this tracking run vanilla until they have; pieces without
    // support (below their material minimum - failing, about to collapse) are never slept, which
    // matches vanilla keeping them on the full path (WearNTear.cs:572-574). As a final net under
    // anything unforeseen (a mod moving built pieces - vanilla's position check would notice,
    // sleep would not), a piece revalidates through vanilla after at most MaxSkipStreak
    // consecutive skips.
    //
    // Two further wake sources close holes found through live verify divergences (five in 575k
    // visits, every one at the streaming ring's edge):
    //  - A piece's colliders join the support world at Awake, but the envelope grid only learns
    //    it at its lazy SetupColliders (owner-only, 30 s after spawn at the earliest). So deaths
    //    and value changes of never-registered pieces wake through a conservative box around
    //    their position instead of an envelope - and every Awake wakes the envelope-overlapping
    //    sleepers around the new piece. The arrival wake is parity, not paranoia: vanilla's fast
    //    path falls through to a full recompute for any piece whose own support tops its cached
    //    neighbours' (WearNTear.cs:449), so vanilla re-detects returning support within a sweep.
    //    Measured signature of the miss: a slept stair frozen at 627 while vanilla re-found
    //    terrain contact worth its material max of 1000.
    //  - UpdateWear writes m_support = GetMaxSupport() directly for pieces outside the active
    //    area (WearNTear.cs:309), bypassing UpdateSupport and this class's bookkeeping entirely.
    //    A before/after compare around UpdateWear catches any support write UpdateSupport did
    //    not perform and treats it as the value-change event it is.
    //
    // "Verify Support Sleep" runs vanilla on every visit while predicting what sleep would have
    // skipped, and flags any visit where the prediction said quiet but vanilla's recompute
    // changed the value - the one way this fix could be wrong.
    //
    // Both: a dedicated server owns and updates the pieces in its active area.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(WearNTear))]
    internal static class SupportSleepPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SupportSleepPatch),
                ValConfig.SectionPerformance,
                "Fix Idle Support Checks",
                true,
                "Lets a building piece skip re-checking its structural support while nothing " +
                "that could change it has happened - no neighbour built, destroyed or changed, " +
                "no terrain edit. In a large base these re-checks are the single biggest steady " +
                "cost while standing still. Support changes still propagate exactly as before; " +
                "only the re-confirmation of unchanged values is skipped.");

            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Support Sleep",
                false,
                "Diagnostic. Runs the vanilla support check on every visit while predicting " +
                "what 'Fix Idle Support Checks' would have skipped, and logs any visit where a " +
                "predicted-quiet piece's support actually changed. Costs everything the fix " +
                "saves, so leave it off unless you are validating the predictions.",
                advanced: true);
        }

        // Wakes per piece are rare events; the streak cap is the hygiene net for signals nothing
        // models (see header). At a saturated updater cycle this bounds unnoticed staleness to a
        // few minutes, on a value that only goes stale if a mod moves built pieces.
        private const int MaxSkipStreak = 9;

        // Coarse world grid of support envelopes, XZ plane. 8 m cells: a piece's envelope spans
        // 1-4 cells, so waking a neighbourhood touches a handful of short lists.
        private const float CellSize = 8f;

        // Wake box for pieces that never built an envelope (see header): covers the collider
        // reach of the largest building pieces from their origin; the sleeping side's own
        // registered envelope supplies the other half of the overlap.
        private const float FallbackWakeRadius = 5f;

        internal sealed class PieceState {
            public bool m_computed;
            public bool m_dirty;
            public int m_skips;
        }

        private struct Envelope {
            public int m_x0, m_z0, m_x1, m_z1;
            public float m_minX, m_minZ, m_maxX, m_maxZ;
        }

        // Grid entries carry the envelope alongside the piece so the overlap test below reads
        // four floats straight out of the candidate list - no per-candidate dictionary probe,
        // which is exactly the cost the test exists to avoid.
        private struct GridEntry {
            public WearNTear m_piece;
            public float m_minX, m_minZ, m_maxX, m_maxZ;
        }

        private static readonly Dictionary<WearNTear, PieceState> States = new Dictionary<WearNTear, PieceState>();
        private static readonly Dictionary<long, List<GridEntry>> Grid = new Dictionary<long, List<GridEntry>>();
        private static readonly Dictionary<WearNTear, Envelope> Registered = new Dictionary<WearNTear, Envelope>();

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        private const int VerifyReportInterval = 25000;
        private static bool _verifyActive;
        private static long _verifyEvaluated;
        private static long _verifyWouldSkip;
        private static long _verifyDivergences;
        private static int _evaluatedSinceReport;

        internal struct Snapshot {
            public PieceState m_state;
            public float m_prevSupport;
            public bool m_skipped;
            public bool m_predictedSkip;
        }

        private static PieceState GetState(WearNTear piece) {
            if (!States.TryGetValue(piece, out PieceState state)) {
                state = new PieceState();
                States.Add(piece, state);
            }

            return state;
        }

        // ---- the sleep decision --------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch("UpdateSupport")]
        private static bool UpdateSupportPrefix(WearNTear __instance, out Snapshot __state) {
            __state = default;
            if (Enabled == null || !Enabled.Value || !HooksHealthy()) { return true; }

            PieceState state = GetState(__instance);
            __state.m_state = state;
            __state.m_prevSupport = __instance.m_support;

            bool wouldSkip = state.m_computed && !state.m_dirty && state.m_skips < MaxSkipStreak
                && __instance.m_support >= __instance.GetMinSupport();

            if (Verify != null && Verify.Value) {
                _verifyActive = true;
                _verifyEvaluated++;
                if (wouldSkip) { _verifyWouldSkip++; }
                __state.m_predictedSkip = wouldSkip;

                if (++_evaluatedSinceReport >= VerifyReportInterval) {
                    _evaluatedSinceReport = 0;
                    LogVerifySummary("periodic");
                }

                return true;
            }

            if (_verifyActive) {
                _verifyActive = false;
                LogVerifySummary("final");
                _verifyEvaluated = 0;
                _verifyWouldSkip = 0;
                _verifyDivergences = 0;
                _evaluatedSinceReport = 0;
            }

            if (wouldSkip) {
                state.m_skips++;
                __state.m_skipped = true;
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch("UpdateSupport")]
        private static void UpdateSupportPostfix(WearNTear __instance, Snapshot __state) {
            PieceState state = __state.m_state;
            if (state == null || __state.m_skipped) { return; }

            // Vanilla ran to completion: this piece's answer is now current. The marker tells
            // the UpdateWear guard below that any support change this call was handled here.
            _supportHandledFor = __instance;
            bool changed = !__instance.m_support.Equals(__state.m_prevSupport);

            if (__state.m_predictedSkip && changed) {
                _verifyDivergences++;
                Logger.LogError(
                    $"Support sleep verify: DIVERGED on '{__instance.name}' - predicted quiet, " +
                    $"but support changed {__state.m_prevSupport} -> {__instance.m_support}. " +
                    "A wake signal is missing. Please report this - leave 'Fix Idle Support " +
                    "Checks' off until it is understood.");
            }

            state.m_computed = true;
            state.m_dirty = false;
            state.m_skips = 0;

            // The relaxation wave: a changed value is exactly the event neighbours must see.
            if (changed) { DirtyNeighbours(__instance); }
        }

        // Every vanilla invalidation signal funnels through here (see header).
        [HarmonyPostfix]
        [HarmonyPatch("ClearCachedSupport")]
        private static void ClearCachedSupportPostfix(WearNTear __instance) {
            if (States.TryGetValue(__instance, out PieceState state)) { state.m_dirty = true; }
        }

        // Set by UpdateSupportPostfix within an UpdateWear call; a support value that changed
        // across UpdateWear WITHOUT this marker was written behind UpdateSupport's back - the
        // outside-active-area path is the one vanilla site that does that (see header).
        private static WearNTear _supportHandledFor;

        [HarmonyPrefix]
        [HarmonyPatch("UpdateWear")]
        private static void UpdateWearPrefix(WearNTear __instance, out float __state) {
            __state = __instance.m_support;
            _supportHandledFor = null;
        }

        [HarmonyPostfix]
        [HarmonyPatch("UpdateWear")]
        private static void UpdateWearPostfix(WearNTear __instance, float __state) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (ReferenceEquals(_supportHandledFor, __instance)) { return; }
            if (__instance.m_support.Equals(__state)) { return; }

            // The piece left the active area and had max support stamped on it; it must not
            // sleep on that value when it comes back, and neighbours read it like any change.
            GetState(__instance).m_dirty = true;
            DirtyNeighbours(__instance);
        }

        // A new piece's colliders join the support world here, long before its lazy
        // SetupColliders registers an envelope - wake the sleepers around it so they re-detect
        // arriving support exactly as vanilla's fast path would (see header).
        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(WearNTear __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            Vector3 position = __instance.transform.position;
            WakeOverlapping(
                position.x - FallbackWakeRadius, position.z - FallbackWakeRadius,
                position.x + FallbackWakeRadius, position.z + FallbackWakeRadius, __instance);
        }

        // ---- the envelope grid ---------------------------------------------------------------

        private static long CellKey(int x, int z) => ((long)x << 32) | (uint)z;

        [HarmonyPostfix]
        [HarmonyPatch("SetupColliders")]
        private static void SetupCollidersPostfix(WearNTear __instance) {
            List<WearNTear.BoundData> bounds = __instance.m_bounds;
            if (bounds == null || bounds.Count == 0) { return; }
            if (Registered.ContainsKey(__instance)) { Unregister(__instance); }

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < bounds.Count; i++) {
                WearNTear.BoundData bound = bounds[i];
                // m_size is half-extents; the magnitude over-covers any rotation.
                float radius = bound.m_size.magnitude;
                if (bound.m_pos.x - radius < minX) { minX = bound.m_pos.x - radius; }
                if (bound.m_pos.z - radius < minZ) { minZ = bound.m_pos.z - radius; }
                if (bound.m_pos.x + radius > maxX) { maxX = bound.m_pos.x + radius; }
                if (bound.m_pos.z + radius > maxZ) { maxZ = bound.m_pos.z + radius; }
            }

            Envelope envelope = new Envelope {
                m_x0 = Mathf.FloorToInt(minX / CellSize),
                m_z0 = Mathf.FloorToInt(minZ / CellSize),
                m_x1 = Mathf.FloorToInt(maxX / CellSize),
                m_z1 = Mathf.FloorToInt(maxZ / CellSize),
                m_minX = minX,
                m_minZ = minZ,
                m_maxX = maxX,
                m_maxZ = maxZ,
            };

            GridEntry entry = new GridEntry {
                m_piece = __instance, m_minX = minX, m_minZ = minZ, m_maxX = maxX, m_maxZ = maxZ,
            };

            for (int x = envelope.m_x0; x <= envelope.m_x1; x++) {
                for (int z = envelope.m_z0; z <= envelope.m_z1; z++) {
                    long key = CellKey(x, z);
                    if (!Grid.TryGetValue(key, out List<GridEntry> cell)) {
                        cell = new List<GridEntry>();
                        Grid.Add(key, cell);
                    }

                    cell.Add(entry);
                }
            }

            Registered[__instance] = envelope;
        }

        private static void Unregister(WearNTear piece) {
            if (!Registered.TryGetValue(piece, out Envelope envelope)) { return; }

            for (int x = envelope.m_x0; x <= envelope.m_x1; x++) {
                for (int z = envelope.m_z0; z <= envelope.m_z1; z++) {
                    if (!Grid.TryGetValue(CellKey(x, z), out List<GridEntry> cell)) { continue; }

                    for (int i = 0; i < cell.Count; i++) {
                        if (ReferenceEquals(cell[i].m_piece, piece)) {
                            cell[i] = cell[cell.Count - 1];
                            cell.RemoveAt(cell.Count - 1);
                            break;
                        }
                    }

                    if (cell.Count == 0) { Grid.Remove(CellKey(x, z)); }
                }
            }

            Registered.Remove(piece);
        }

        private static void DirtyNeighbours(WearNTear piece) {
            if (Registered.TryGetValue(piece, out Envelope e)) {
                WakeOverlapping(e.m_minX, e.m_minZ, e.m_maxX, e.m_maxZ, piece);
                return;
            }

            // Never registered (SetupColliders is lazy), but its colliders were live and may
            // have supported sleepers - wake by a conservative box around its position.
            Vector3 position = piece.transform.position;
            WakeOverlapping(
                position.x - FallbackWakeRadius, position.z - FallbackWakeRadius,
                position.x + FallbackWakeRadius, position.z + FallbackWakeRadius, piece);
        }

        private static void WakeOverlapping(float minX, float minZ, float maxX, float maxZ, WearNTear exclude) {
            int x0 = Mathf.FloorToInt(minX / CellSize), x1 = Mathf.FloorToInt(maxX / CellSize);
            int z0 = Mathf.FloorToInt(minZ / CellSize), z1 = Mathf.FloorToInt(maxZ / CellSize);

            for (int x = x0; x <= x1; x++) {
                for (int z = z0; z <= z1; z++) {
                    if (!Grid.TryGetValue(CellKey(x, z), out List<GridEntry> cell)) { continue; }

                    for (int i = 0; i < cell.Count; i++) {
                        GridEntry other = cell[i];

                        // Exact envelope overlap: only pieces whose support region can actually
                        // see the event wake up. Envelopes already carry the overlap-box margin,
                        // so this is a conservative superset of the true neighbour set - but a
                        // ~20x smaller one than "shares a grid cell".
                        if (other.m_minX > maxX || other.m_maxX < minX
                            || other.m_minZ > maxZ || other.m_maxZ < minZ) { continue; }

                        if (!ReferenceEquals(other.m_piece, exclude)) { GetState(other.m_piece).m_dirty = true; }
                    }
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("OnDestroy")]
        private static void OnDestroyPostfix(WearNTear __instance) {
            // Wake the neighbourhood BEFORE unregistering, then forget the piece entirely.
            DirtyNeighbours(__instance);
            Unregister(__instance);
            States.Remove(__instance);
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                States.Clear();
                Grid.Clear();
                Registered.Clear();
            }
        }

        private static void LogVerifySummary(string kind) {
            Logger.LogInfo(
                $"Support sleep verify ({kind}): {_verifyEvaluated} visit(s), would have " +
                $"skipped {_verifyWouldSkip}, {_verifyDivergences} divergence(s).");
        }

        // ---- hook health ---------------------------------------------------------------------

        /// A sleeping piece is woken ONLY by the hooks in this class, so the sleep decision
        /// stands down to vanilla's revalidation if any of them is missing.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            _hooksHealthy =
                HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "ClearCachedSupport"))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "OnDestroy"))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "SetupColliders"))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "UpdateSupport"))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "Awake"))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "UpdateWear"));

            if (!_hooksHealthy) {
                Logger.LogError(
                    "Support sleep: a wake hook is not attached, so pieces cannot sleep safely " +
                    "and support checks are running vanilla for this session. This usually " +
                    "means a Valheim update changed those methods - look for the patch failure " +
                    "logged at startup.");
            }

            return _hooksHealthy;
        }

        private static bool HasOurPostfix(MethodBase target) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            foreach (Patch patch in info.Postfixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != typeof(SupportSleepPatch)) { continue; }
                return true;
            }

            return false;
        }
    }
}
