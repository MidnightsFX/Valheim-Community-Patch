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
    //    Deaths arrive in STORMS - a streamed-out zone column, a collapsing structure - so the
    //    wake boxes are collected and swept once per frame instead of per piece: every grid cell
    //    the storm reaches is scanned exactly once against the boxes that reach it, and a piece
    //    the first box woke costs one branch for each later box rather than a fresh overlap
    //    test. The dirty set is identical either way - dirty is set-only, and nothing reads it
    //    between the deaths in one frame's destruction phase - only the sweep is deduplicated.
    //  - A neighbour's support VALUE changed. Support propagates as a relaxation wave - each
    //    piece's value feeds its neighbours' - so whenever a non-skipped UpdateSupport produces a
    //    different m_support than before, its grid neighbours are woken. Waves therefore travel
    //    exactly as far as they would in vanilla, and a structure at equilibrium goes fully
    //    quiet. This is the same fixpoint vanilla converges to; only the redundant confirmations
    //    of already-converged values are gone.
    //
    //    "Different" is a small TOLERANCE rather than an exact float compare, and the reason
    //    originally given for it is now known to be wrong. The claim was that
    //    Physics.OverlapBoxNonAlloc's fixed 128-collider buffer (WearNTear.cs:59) truncates in a
    //    dense base and returns an unstable subset, so a recompute could wobble with nothing
    //    having changed. Instrumented directly - the same boxes re-run into a buffer four times
    //    the size - that never happened once: zero of ~18700 sampled boxes reached 128, the
    //    worst was 82, and the typical box holds around 60. The truncation story is dead in both
    //    its forms. What the wave actually carries is real changes, seeded by arrivals (see the
    //    first-compute note in UpdateSupportPrefix). The tolerance is kept because it is free and
    //    bounded, not because it is the cure: each propagation hop multiplies by at most one
    //    (a = max(support - loss * distance * support)), so a suppressed delta of at most
    //    epsilon stays at most epsilon downstream instead of accumulating per hop; the piece
    //    still stores the exact recomputed m_support, so only the NOTIFICATION is gated;
    //    collapse decisions compare against GetMinSupport() on values in the hundreds-to-1000
    //    range, orders of magnitude above the default; and MaxSkipStreak forces every piece
    //    through a full revalidation regardless. Set it to 0 for the exact compare.
    //
    //    What the fan-out cost actually was: the SCAN, not the wake count. Measured 613
    //    candidates examined per wave to wake 1.6 pieces - a 392:1 reject ratio - because in a
    //    running wave nearly everything a wake reaches is already dirty. Each cell therefore
    //    tracks how many of its entries belong to a piece that is NOT dirty, and a cell at zero
    //    is skipped whole. That count is exact only because SetDirty below is the single writer
    //    of m_dirty; every wake source goes through it.
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
    // This class also carries the second sleep tier, "Fix Idle Wear Visits": skipping the WHOLE
    // UpdateWear visit - not just its support half - for pieces where every remaining input is
    // provably quiet too. The predicate, its weather-epoch machinery and its own verify live at
    // the wear-visit section below; it shares this class because it is built on the same
    // per-piece state and wake infrastructure.
    //
    // Both: a dedicated server owns and updates the pieces in its active area.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(WearNTear))]
    internal static class SupportSleepPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verify;
        internal static ConfigEntry<bool> WearEnabled;
        internal static ConfigEntry<bool> WearVerify;
        internal static ConfigEntry<float> WakeEpsilon;
        internal static ConfigEntry<bool> WakeStats;

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

            WearEnabled = ValConfig.BindFixToggle(
                typeof(SupportSleepPatch),
                ValConfig.SectionPerformance,
                "Fix Idle Wear Visits",
                true,
                "Extends the support sleep to the whole per-piece wear visit: a piece the " +
                "support sleep already proved quiet, owned by this machine, away from water, " +
                "outside the Ashlands, and either in dry weather or safely under a roof skips " +
                "its entire update - the engine-call overhead of visiting tens of thousands of " +
                "pieces just to conclude nothing wears right now. Weather changes, roof " +
                "changes, damage and repairs wake pieces immediately; exposed pieces in wet " +
                "weather and everything in the Ashlands run exactly vanilla.");

            WearVerify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Wear Sleep",
                false,
                "Diagnostic. Runs the vanilla wear visit on every piece while predicting what " +
                "'Fix Idle Wear Visits' would have skipped, and logs any visit where a " +
                "predicted-quiet piece's support, health or wetness actually changed. Costs " +
                "everything the fix saves, so leave it off unless you are validating the " +
                "predictions.",
                advanced: true);

            WakeEpsilon = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Support Change Threshold",
                0.01f,
                "How much a piece's structural support must change before its neighbours are " +
                "re-checked. The support calculation samples its surroundings through a " +
                "fixed-size buffer that the game truncates in dense builds, so a recomputed " +
                "value can wobble in its last decimals with nothing having changed - and an " +
                "exact comparison turns that wobble into an endless chain of re-checks. " +
                "Support values run to a thousand, so the default is far below anything that " +
                "affects whether a build stands. 0 restores the exact comparison.",
                advanced: true,
                valMin: 0f,
                valMax: 1f);

            WakeStats = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Log Support Wake Stats",
                false,
                "Diagnostic. Periodically logs how the support wake traffic breaks down - first " +
                "computations, how far recomputed values actually moved, how many neighbours " +
                "each wake touched, and how often the game's own surroundings scan overflows " +
                "its fixed buffer. Samples that last one on a fraction of checks because it " +
                "costs a real physics query, so this is not free - but it is cheap enough to " +
                "leave on while measuring, and it is how 'Support Change Threshold' gets sized.",
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

            // The last value a real recompute produced, and whether there has been one. This is
            // the truth the stored support should hold; see the stamp repair in UpdateWearPostfix.
            public bool m_hasRealSupport;
            public float m_realSupport;

            // The grid cells this piece is registered in, so a dirty transition can adjust their
            // clean counts without a lookup. Null while unregistered.
            public Cell[] m_cells;

            // Wear-sleep bookkeeping (see the wear-visit section of the header).
            public bool m_wetSleepable;    // last ran visit found it roofed and dry (may sleep while wet)
            public bool m_wearWake;        // damage/repair/roof-change; next visit must run
            public bool m_geoCached;       // zone and height captured (static pieces, captured once)
            public Vector2i m_zone;
            public float m_y;
        }

        private struct Envelope {
            // The state object registration stamped into this piece's grid entries. Keeping it
            // here is what lets Unregister find them by identity without depending on the piece
            // still being in States - i.e. without the order of the two removals in OnDestroy
            // being load bearing.
            public PieceState m_state;
            public int m_x0, m_z0, m_x1, m_z1;
            public float m_minX, m_minZ, m_maxX, m_maxZ;
            public float m_minY, m_maxY;
        }

        // Grid entries carry the envelope so the overlap test below reads four floats straight
        // out of the candidate array - no per-candidate dictionary probe, which is exactly the
        // cost the test exists to avoid - and the piece's state object for the same reason, so a
        // wake sets a bool through a reference the entry already holds and a wake storm never
        // touches the state map at all. The state doubles as the entry's identity: registration
        // below is the only site that builds an entry, and a piece leaves the map and the grid in
        // the same breath (OnDestroy below), so the reference can neither outlive its dictionary
        // slot nor be shared with another piece.
        internal struct GridEntry {
            public PieceState m_state;
            public float m_minX, m_minZ, m_maxX, m_maxZ;
            public float m_minY, m_maxY;
        }

        // A cell holds its entries as a bare array plus a count rather than a List: the wake scan
        // walks these in their thousands, and List's indexer copies the whole struct out through
        // a bounds-checked property call - measured at 14 ms of every second in the indexer
        // alone, more than the wake logic it was feeding.
        internal sealed class Cell {
            public GridEntry[] m_entries = new GridEntry[4];
            public int m_count;

            // How many of those entries belong to a piece that is NOT already dirty. A wake can
            // only ever change a clean piece, so a cell at zero is skipped whole - which is the
            // common case once a wave is running: measured 613 candidates examined per wave to
            // wake 1.6 pieces, because almost everything a wake reaches is awake already.
            public int m_clean;

            public void Add(GridEntry entry) {
                if (m_count == m_entries.Length) {
                    System.Array.Resize(ref m_entries, m_count * 2);
                }

                m_entries[m_count++] = entry;
            }

            // Order within a cell means nothing, so removal is a swap with the tail. The vacated
            // slot is cleared so a removed piece's state is not held live by the array.
            public void RemoveAt(int index) {
                m_entries[index] = m_entries[--m_count];
                m_entries[m_count] = default(GridEntry);
            }
        }

        // One destroy's wake, captured as a box because the piece's envelope is unregistered and
        // its transform gone microseconds later; the sweep itself runs at the flush.
        private struct WakeBox {
            public float m_minX, m_minZ, m_maxX, m_maxZ;
            public float m_minY, m_maxY;
            public int m_x0, m_z0, m_x1, m_z1;
        }

        private static readonly Dictionary<WearNTear, PieceState> States = new Dictionary<WearNTear, PieceState>();
        private static readonly Dictionary<long, Cell> Grid = new Dictionary<long, Cell>();
        private static readonly Dictionary<WearNTear, Envelope> Registered = new Dictionary<WearNTear, Envelope>();

        // This frame's destroy wakes, and the per-cell scratch the flush buckets them into.
        // The lists are pooled rather than reallocated: a storm's cell count is a high-water
        // mark reached once, and the flush runs on every frame a death happened.
        private static readonly List<WakeBox> PendingWakes = new List<WakeBox>();
        private static readonly Dictionary<long, List<int>> WakeCells = new Dictionary<long, List<int>>();
        private static readonly Stack<List<int>> WakeCellPool = new Stack<List<int>>();

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        // Wake traffic breakdown. Sampled only while "Log Support Wake Stats" is on - the flag is
        // refreshed once a frame so the hot paths read a field, not a config entry.
        private static bool _statsOn;
        private static float _statsSince;
        private static long _statVisits, _statFirstCompute, _statIdentical;

        // Changes bucketed RELATIVE to the piece's material maximum, because that is the only
        // form a threshold could take: maxSupport runs from 100 (wood) to 2000 (ashstone), so one
        // absolute number does not mean the same thing on both. These buckets are what a relative
        // "Support Change Threshold" would be sized from.
        private static long _statRelTenth, _statRelOne, _statRelTen, _statRelHuge;
        private static long _statWaves, _wakeCandidates, _wakeWoken, _wakeCellsSkipped;

        // Hypothesis 2 - the out-of-area max-support stamp. UpdateWear writes
        // m_support = GetMaxSupport() for pieces outside the active area (WearNTear.cs:309),
        // bypassing UpdateSupport entirely; when the piece comes back the recompute restores its
        // real value. Both writes are large changes, and the ring boundary sweeps thousands of
        // pieces as the player moves, so if this is the wave's engine these three counters are
        // where it shows: stamps written, recomputes leaving max, recomputes arriving at max.
        private static long _statOutOfAreaStamp, _statLeftMax, _statReachedMax;

        // Hypothesis 1 - the overlap buffer. Vanilla samples each bound box into a fixed
        // 128-collider array (WearNTear.cs:59); past that PhysX truncates and the surviving
        // subset is not guaranteed stable, so a recompute can lose a dominant supporter outright
        // and swing by a lot rather than by a little. The count is a LOCAL inside vanilla's
        // method, so it is re-measured here into a bigger buffer - which costs a real physics
        // query, hence one recompute in ProbeInterval rather than all of them.
        private const int ProbeInterval = 32;
        private static readonly Collider[] ProbeBuffer = new Collider[512];
        private static int _probeCountdown;
        private static long _probeBoxes, _probeSaturated, _probeWorst;

        private const float WakeStatsIntervalSeconds = 30f;

        private static void SampleWakeStats() {
            bool on = WakeStats != null && WakeStats.Value;
            if (on != _statsOn) {
                _statsOn = on;
                _statsSince = Time.time;
                ClearWakeStats();
                if (!on) { return; }
            }

            if (!on || Time.time - _statsSince < WakeStatsIntervalSeconds) { return; }

            float seconds = Time.time - _statsSince;
            Logger.LogInfo(
                $"Support wake stats ({seconds:F0}s): {_statVisits} recompute(s) - " +
                $"{_statFirstCompute} first, {_statIdentical} identical; changed by " +
                $"<=0.1% {_statRelTenth}, <=1% {_statRelOne}, <=10% {_statRelTen}, " +
                $">10% {_statRelHuge} of max support. {_statWaves} wave(s) fanned out over " +
                $"{_wakeCandidates} candidate(s), waking {_wakeWoken}; " +
                $"{_wakeCellsSkipped} fully-awake cell(s) skipped. " +
                $"Out-of-area max-support stamps {_statOutOfAreaStamp}; changes leaving max " +
                $"{_statLeftMax}, reaching max {_statReachedMax}. " +
                $"Overlap probe (1 in {ProbeInterval}): {_probeBoxes} box(es), {_probeSaturated} " +
                $"at or over the {WearNTear.s_tempColliders.Length} limit, worst {_probeWorst}.");

            _statsSince = Time.time;
            ClearWakeStats();
        }

        /// Re-runs this piece's bound boxes into a buffer four times vanilla's, purely to learn
        /// how many colliders were really in them. Same boxes, same layer mask, so a result at or
        /// above vanilla's array length is a box vanilla truncated.
        private static void ProbeOverlapLimit(WearNTear piece) {
            List<WearNTear.BoundData> bounds = piece.m_bounds;
            if (bounds == null || WearNTear.s_rayMask == 0) { return; }

            int vanillaLimit = WearNTear.s_tempColliders.Length;

            for (int i = 0; i < bounds.Count; i++) {
                WearNTear.BoundData bound = bounds[i];
                int found = Physics.OverlapBoxNonAlloc(
                    bound.m_pos, bound.m_size, ProbeBuffer, bound.m_rot, WearNTear.s_rayMask);

                _probeBoxes++;
                if (found >= vanillaLimit) { _probeSaturated++; }
                if (found > _probeWorst) { _probeWorst = found; }
            }
        }

        private static void ClearWakeStats() {
            _statVisits = 0;
            _statFirstCompute = 0;
            _statIdentical = 0;
            _statRelTenth = 0;
            _statRelOne = 0;
            _statRelTen = 0;
            _statRelHuge = 0;
            _statWaves = 0;
            _wakeCandidates = 0;
            _wakeWoken = 0;
            _wakeCellsSkipped = 0;
            _statOutOfAreaStamp = 0;
            _statLeftMax = 0;
            _statReachedMax = 0;
            _probeBoxes = 0;
            _probeSaturated = 0;
            _probeWorst = 0;
        }

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

        /// The ONLY place m_dirty is written. The per-cell clean counts are exact only if every
        /// transition passes through here.
        private static void SetDirty(PieceState state, bool dirty) {
            if (state.m_dirty == dirty) { return; }
            state.m_dirty = dirty;

            Cell[] cells = state.m_cells;
            if (cells == null) { return; }

            int delta = dirty ? -1 : 1;
            for (int i = 0; i < cells.Length; i++) { cells[i].m_clean += delta; }
        }

        private static PieceState GetState(WearNTear piece) {
            if (!States.TryGetValue(piece, out PieceState state)) {
                state = new PieceState();
                States.Add(piece, state);
            }

            return state;
        }

        // ---- the sleep decision --------------------------------------------------------------

        // Returns 0 when the support check may sleep, else the index of the FIRST failing
        // condition. The sibling wear predicate has always reported its reasons; this one did
        // not, and a soak that reported "would have skipped 0 of 229431" with no reasons was
        // undiagnosable - which is the whole argument for having them.
        private static readonly string[] SupportBlockNames = {
            "sleepable", "support-cold", "support-dirty", "streak-cap", "unsupported",
        };
        private static readonly long[] SupportBlockCounts = new long[5];

        private static int SupportBlockReason(WearNTear piece, PieceState state) {
            if (!state.m_computed) { return 1; }
            if (state.m_dirty) { return 2; }
            if (state.m_skips >= MaxSkipStreak) { return 3; }
            if (piece.m_support < piece.GetMinSupport()) { return 4; }
            return 0;
        }

        [HarmonyPrefix]
        [HarmonyPatch("UpdateSupport")]
        private static bool UpdateSupportPrefix(WearNTear __instance, out Snapshot __state) {
            __state = default;
            FlushDestroyWakes();
            if (Enabled == null || !Enabled.Value || !HooksHealthy()) { return true; }

            PieceState state = GetState(__instance);
            __state.m_state = state;
            __state.m_prevSupport = __instance.m_support;

            // A piece that has never computed under this tracking may still be sitting on the
            // Awake placeholder - the restore in AwakePostfix covers pieces that woke while this
            // fix was on, this covers everything else - so a first compute measures "changed"
            // against the persisted value rather than against GetMaxSupport(). Measured before
            // both: ~450 pieces a second enter tracking while streaming, and treating each
            // arrival's placeholder-to-real drop as a change seeded a near-critical relaxation
            // cascade at ~5.5 recomputes per arrival, which is why the support sleep could never
            // engage - 85% of visits found the piece already dirty.
            if (!state.m_computed && __instance.m_nview != null && __instance.m_nview.IsValid()
                && __instance.m_nview.GetZDO().GetFloat(ZDOVars.s_support, out float stored)) {
                __state.m_prevSupport = stored;
            }

            int blockReason = SupportBlockReason(__instance, state);
            bool wouldSkip = blockReason == 0;

            if (Verify != null && Verify.Value) {
                _verifyActive = true;
                _verifyEvaluated++;
                SupportBlockCounts[blockReason]++;
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
                _wearSkipped = 0;
                System.Array.Clear(SupportBlockCounts, 0, SupportBlockCounts.Length);
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
            float delta = __instance.m_support - __state.m_prevSupport;
            if (delta < 0f) { delta = -delta; }

            // See the header: an exact compare does not converge, because the recompute's own
            // sampling wobbles in a dense base.
            float epsilon = WakeEpsilon != null ? WakeEpsilon.Value : 0f;
            bool changed = epsilon > 0f ? delta > epsilon : delta != 0f;

            if (_statsOn) {
                _statVisits++;
                if (!state.m_computed) { _statFirstCompute++; }
                else if (delta == 0f) { _statIdentical++; }
                else {
                    float maxSupport = __instance.GetMaxSupport();
                    float relative = maxSupport > 0f ? delta / maxSupport : 1f;

                    if (relative <= 0.001f) { _statRelTenth++; }
                    else if (relative <= 0.01f) { _statRelOne++; }
                    else if (relative <= 0.1f) { _statRelTen++; }
                    else { _statRelHuge++; }

                    if (__state.m_prevSupport.Equals(maxSupport)) { _statLeftMax++; }
                    if (__instance.m_support.Equals(maxSupport)) { _statReachedMax++; }
                }

                if (--_probeCountdown <= 0) {
                    _probeCountdown = ProbeInterval;
                    ProbeOverlapLimit(__instance);
                }
            }

            if (__state.m_predictedSkip && changed) {
                _verifyDivergences++;
                Logger.LogError(
                    $"Support sleep verify: DIVERGED on '{__instance.name}' - predicted quiet, " +
                    $"but support changed {__state.m_prevSupport} -> {__instance.m_support}. " +
                    "A wake signal is missing. Please report this - leave 'Fix Idle Support " +
                    "Checks' off until it is understood.");
            }

            state.m_computed = true;
            state.m_realSupport = __instance.m_support;
            state.m_hasRealSupport = true;
            SetDirty(state, false);
            state.m_skips = 0;

            // The relaxation wave: a changed value is exactly the event neighbours must see.
            if (changed) {
                if (_statsOn) { _statWaves++; }
                DirtyNeighbours(__instance, state);
            }
        }

        // Every vanilla invalidation signal funnels through here (see header).
        [HarmonyPostfix]
        [HarmonyPatch("ClearCachedSupport")]
        private static void ClearCachedSupportPostfix(WearNTear __instance) {
            if (States.TryGetValue(__instance, out PieceState state)) { SetDirty(state, true); }
        }

        // Set by UpdateSupportPostfix within an UpdateWear call; a support value that changed
        // across UpdateWear WITHOUT this marker was written behind UpdateSupport's back - the
        // outside-active-area path is the one vanilla site that does that (see header).
        private static WearNTear _supportHandledFor;

        // ---- the wear-visit sleep ------------------------------------------------------------
        //
        // A whole UpdateWear visit is provably inert - not just its support half - when every
        // input that could make it do something is quiet:
        //  - the support sleep predicate holds (so UpdateSupport would skip anyway);
        //  - this machine OWNS the piece (a non-owner visit is just the health-visual poll that
        //    keeps remote damage visible, so those always run vanilla; ownership is re-read
        //    every visit, so a piece that loses ownership resumes vanilla by itself);
        //  - the world is dry (debounced - see the weather sample below), OR its last ran visit
        //    found it roofed and dry, which holds the whole rain branch provably inert while
        //    wet; exposed pieces never sleep while wet, so weather transitions need no
        //    bookkeeping - the affected pieces observe them on their own next visit;
        //  - the piece sits above y=35 (nothing in the world is underwater above sea level plus
        //    waves, closing the IsUnderWater half of IsWet without a physics query);
        //  - its biome is resolved and not Ashlands (ash/lava timers run vanilla);
        //  - its cached zone is inside the activated area (outside it, vanilla stamps max
        //    support - the piece runs vanilla there, and the write-guard below handles it);
        //  - no damage or repair happened since its last ran visit (those wake it for visuals).
        // A wear-skip counts toward the same hygiene streak as a support-skip, so the periodic
        // full revalidation still happens. Pieces on moving structures are effectively excluded
        // by the support predicate and bounded by that same streak.

        private const float NeverUnderwaterY = 35f;

        // Weather is a DEBOUNCED sample: EnvMan.IsWet reflects the current environment's wet
        // flag, several biome ambients are wet-flagged around the clock (snow, the swamp's
        // default), and a base straddling a biome border can flip the raw value with every few
        // steps the player takes. Requiring the value to hold for a few seconds keeps exposed
        // pieces on a stable vanilla cadence through real weather while ignoring border flap.
        // No per-piece epoch is needed at all: the pieces wetness affects are exactly the ones
        // that never sleep while wet, so they observe every transition on their own next visit,
        // and roofed sleepers are covered by the UpdateCover wake below.
        private const float WeatherDebounceSeconds = 5f;

        private static bool _worldWet = true;
        private static bool _pendingWet = true;
        private static float _pendingSince;
        private static int _centerFrame = -1;
        private static Vector2i _centerZone;
        private static int _activatedRadius;

        // One weather sample and one ring computation per frame, shared by every visit.
        [HarmonyPatch(typeof(WearNTearUpdater))]
        internal static class UpdaterHook {
            [HarmonyPrefix]
            [HarmonyPatch("Update")]
            private static void UpdatePrefix() {
                // Runs ahead of the updater's own sleep check, so this is the once-a-frame drain
                // for the deaths the end of the previous frame queued.
                FlushDestroyWakes();
                SampleWakeStats();

                bool wet = EnvMan.IsWet();
                if (wet != _pendingWet) {
                    _pendingWet = wet;
                    _pendingSince = Time.time;
                }

                if (_pendingWet != _worldWet && Time.time - _pendingSince >= WeatherDebounceSeconds) {
                    _worldWet = _pendingWet;
                }

                int frame = Time.frameCount;
                if (frame != _centerFrame && ZNet.instance != null && ZoneSystem.instance != null) {
                    _centerFrame = frame;
                    _centerZone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
                    _activatedRadius = ZoneSystem.instance.m_activeArea - 1;
                }
            }
        }

        internal struct WearSnapshot {
            public PieceState m_state;
            public float m_prevSupport;
            public float m_prevHealthPct;
            public bool m_prevRainWet;
            public bool m_skipped;
            public bool m_predictedSkip;
        }

        // Counted unconditionally (one increment on an already-taken branch) so the support
        // verify can report it - see LogVerifySummary.
        private static long _wearSkipped;

        private static long _wearVisits;
        private static long _wearWouldSkip;
        private static long _wearDivergences;
        private static int _wearSinceReport;
        private static bool _wearVerifyActive;

        // Returns 0 when the visit may sleep, else the index of the FIRST failing condition -
        // fed to the verify's reason breakdown, because "0 skips" without reasons is
        // undiagnosable (a cold post-login sweep, wet weather and a below-waterline base all
        // look identical from the count alone).
        private static readonly string[] WearBlockNames = {
            "sleepable", "damage-wake", "wet-exposed", "support-cold", "geometry/waterline",
            "biome", "outside-ring", "not-owner", "support-dirty", "streak-cap", "unsupported",
        };
        private static readonly long[] WearBlockCounts = new long[11];

        private static int WearBlockReason(WearNTear piece, PieceState state) {
            if (state.m_wearWake) { return 1; }

            // While the world is wet, only pieces whose last ran visit found them roofed and dry
            // may sleep: a roof holds m_rainWet false and the rain-damage branch inert, vanilla's
            // UpdateCover (a separate path this fix never skips) keeps m_haveRoof fresh during
            // wet weather, and a roof-state change wakes the piece below. Exposed pieces never
            // sleep while wet, which is also what makes transitions self-serving: they observe
            // every weather change on their own next visit, no epoch bookkeeping required. A
            // cold piece (never visited) has m_wetSleepable false and lands here or at the
            // geometry gate, so its first visit always runs and records.
            if (_worldWet && !state.m_wetSleepable) { return 2; }

            // The hygiene streak caps EVERY piece's consecutive skips; for support-wearing
            // pieces UpdateSupport's own postfix resets it on revalidation, for support-less
            // pieces the ran wear visit resets it below.
            if (state.m_skips >= MaxSkipStreak) { return 9; }

            // The rest of the support predicate applies ONLY to pieces that have support wear
            // at all: vanilla never calls UpdateSupport for a piece with m_noSupportWear false
            // (WearNTear.cs:333-338), so such a piece can never become "computed" and is
            // support-quiet by definition.
            if (piece.m_noSupportWear) {
                if (!state.m_computed) { return 3; }
                if (state.m_dirty) { return 8; }
                if (piece.m_support < piece.GetMinSupport()) { return 10; }
            }

            if (!state.m_geoCached || state.m_y <= NeverUnderwaterY) { return 4; }
            if (piece.m_biome == Heightmap.Biome.None || piece.m_biome == Heightmap.Biome.AshLands
                || piece.m_inAshlands) { return 5; }

            int dx = state.m_zone.x - _centerZone.x;
            int dy = state.m_zone.y - _centerZone.y;
            if (dx < 0) { dx = -dx; }
            if (dy < 0) { dy = -dy; }
            if ((dx > dy ? dx : dy) > _activatedRadius) { return 6; }

            if (!piece.m_nview.IsValid() || !piece.m_nview.IsOwner()) { return 7; }

            return 0;
        }

        [HarmonyPrefix]
        [HarmonyPatch("UpdateWear")]
        private static bool UpdateWearPrefix(WearNTear __instance, out WearSnapshot __state) {
            __state = default;
            __state.m_prevSupport = __instance.m_support;
            _supportHandledFor = null;
            FlushDestroyWakes();

            if (WearEnabled == null || !WearEnabled.Value || !HooksHealthy()) { return true; }

            PieceState state = GetState(__instance);
            __state.m_state = state;

            int blockReason = WearBlockReason(__instance, state);
            bool wouldSkip = blockReason == 0;

            if (WearVerify != null && WearVerify.Value) {
                _wearVerifyActive = true;
                _wearVisits++;
                WearBlockCounts[blockReason]++;
                if (wouldSkip) { _wearWouldSkip++; }
                __state.m_predictedSkip = wouldSkip;
                __state.m_prevHealthPct = __instance.m_healthPercentage;
                __state.m_prevRainWet = __instance.m_rainWet;

                if (++_wearSinceReport >= 25000) {
                    _wearSinceReport = 0;
                    LogWearVerifySummary("periodic");
                }

                return true;
            }

            if (_wearVerifyActive) {
                _wearVerifyActive = false;
                LogWearVerifySummary("final");
                _wearVisits = 0;
                _wearWouldSkip = 0;
                _wearDivergences = 0;
                _wearSinceReport = 0;
                System.Array.Clear(WearBlockCounts, 0, WearBlockCounts.Length);
            }

            if (wouldSkip) {
                state.m_skips++;
                __state.m_skipped = true;
                _wearSkipped++;
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch("UpdateWear")]
        private static void UpdateWearPostfix(WearNTear __instance, WearSnapshot __state) {
            if (__state.m_skipped) { return; }

            PieceState state = __state.m_state;
            if (state != null) {
                if (__state.m_predictedSkip
                    && (!__instance.m_support.Equals(__state.m_prevSupport)
                        || !__instance.m_healthPercentage.Equals(__state.m_prevHealthPct)
                        || __instance.m_rainWet != __state.m_prevRainWet)) {
                    _wearDivergences++;
                    Logger.LogError(
                        $"Wear sleep verify: DIVERGED on '{__instance.name}' - predicted quiet, " +
                        "but the visit changed support, health or wetness. A wake signal is " +
                        "missing. Please report this - leave 'Fix Idle Wear Visits' off until " +
                        "it is understood.");
                }

                // A ran visit refreshed the visuals and re-established wet-sleep eligibility.
                // m_haveRoof can be stale in long dry spells (vanilla only refreshes cover while
                // wet), so a roof lost during dry weather can leave this true into the first wet
                // minute - bounded harmless: UpdateCover corrects the roof within seconds of wet
                // starting, the wake hook below fires on the change, and rain wear needs a full
                // wet minute before any damage tick.
                state.m_wetSleepable = __instance.m_haveRoof && !__instance.m_rainWet;
                state.m_wearWake = false;

                // Support-less pieces never reach UpdateSupport, whose postfix is what resets
                // the streak for everything else - their cap-forced visit resets it here.
                if (!__instance.m_noSupportWear) { state.m_skips = 0; }

                if (!state.m_geoCached && __instance.m_nview != null && __instance.m_nview.IsValid()) {
                    state.m_zone = __instance.m_nview.GetZDO().GetSector();
                    state.m_y = __instance.transform.position.y;
                    state.m_geoCached = true;
                }
            }

            if (Enabled == null || !Enabled.Value) { return; }
            if (ReferenceEquals(_supportHandledFor, __instance)) {
                return;
            }

            // Deliberately not __state.m_state: that is only populated when the wear fix is on,
            // and this repair belongs to the support fix, whose gate is above.
            States.TryGetValue(__instance, out PieceState tracked);
            RepairStampedSupport(__instance, tracked);

            if (__instance.m_support.Equals(__state.m_prevSupport)) { return; }

            // The piece left the active area and had max support stamped on it; it must not
            // sleep on that value when it comes back, and neighbours read it like any change.
            if (_statsOn) { _statOutOfAreaStamp++; }

            PieceState changedState = GetState(__instance);
            SetDirty(changedState, true);
            DirtyNeighbours(__instance, changedState);
        }

        /// UpdateWear stamps m_support = GetMaxSupport() for pieces outside the active area AND
        /// PERSISTS it (WearNTear.cs:308-310), so the stored support - the only thing a non-owner
        /// ever reads, and what a returning piece restores from at Awake - is overwritten with a
        /// placeholder every time the ring edge sweeps past. That is what made the arrival repair
        /// ineffective for exactly the pieces that needed it: they came back holding a maximum,
        /// recomputed down to their real value, and woke their neighbourhood for it.
        ///
        /// The in-memory stamp is left alone - it is what keeps an unwatched structure from
        /// failing its support check - and only the stored copy is put back to the last value a
        /// real recompute produced. A non-owner then reads the truth instead of a placeholder,
        /// which is strictly better information, and no damage or collapse decision changes
        /// because those are taken by the owner from m_support.
        ///
        /// Runs only on visits where UpdateSupport did not, which for an out-of-area piece is all
        /// of them, and writes only when the stored value is actually the placeholder - so a
        /// repeated stamp is repaired once rather than churning the ZDO every visit.
        private static void RepairStampedSupport(WearNTear piece, PieceState state) {
            if (state == null || !state.m_hasRealSupport) { return; }
            if (piece.m_nview == null || !piece.m_nview.IsValid()) { return; }

            float maxSupport = piece.GetMaxSupport();
            if (state.m_realSupport.Equals(maxSupport)) { return; }

            ZDO zdo = piece.m_nview.GetZDO();
            if (!zdo.GetFloat(ZDOVars.s_support, out float stored) || !stored.Equals(maxSupport)) { return; }

            zdo.Set(ZDOVars.s_support, state.m_realSupport);
        }

        // A roof appearing or disappearing changes whether a wet-sleeping piece is actually
        // protected; vanilla refreshes m_haveRoof through UpdateCover on its own cadence, so the
        // transition is caught here and wakes the piece for a vanilla visit that re-records its
        // wet-sleep eligibility.
        [HarmonyPrefix]
        [HarmonyPatch("UpdateCover")]
        private static void UpdateCoverPrefix(WearNTear __instance, out bool __state) {
            __state = __instance.m_haveRoof;
        }

        [HarmonyPostfix]
        [HarmonyPatch("UpdateCover")]
        private static void UpdateCoverPostfix(WearNTear __instance, bool __state) {
            if (__instance.m_haveRoof == __state) { return; }
            if (States.TryGetValue(__instance, out PieceState state)) { state.m_wearWake = true; }
        }

        // Damage and repair are the two owner-side events that change what UpdateVisual shows;
        // both wake the piece for one vanilla visit. Unconditional, like all wake maintenance.
        [HarmonyPostfix]
        [HarmonyPatch("ApplyDamage")]
        private static void ApplyDamagePostfix(WearNTear __instance) {
            if (States.TryGetValue(__instance, out PieceState state)) { state.m_wearWake = true; }
        }

        [HarmonyPostfix]
        [HarmonyPatch("RPC_Repair")]
        private static void RPC_RepairPostfix(WearNTear __instance) {
            if (States.TryGetValue(__instance, out PieceState state)) { state.m_wearWake = true; }
        }

        // A new piece's colliders join the support world here, long before its lazy
        // SetupColliders registers an envelope - wake the sleepers around it so they re-detect
        // arriving support exactly as vanilla's fast path would (see header).
        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(WearNTear __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            // Vanilla's Awake stamps m_support = GetMaxSupport() as a placeholder and never
            // restores the persisted value: it writes ZDOVars.s_support in four places and reads
            // it in exactly one, the NON-owner branch of GetSupport (WearNTear.cs:207). So an
            // owned piece advertises an optimistic maximum to every neighbour that recomputes
            // between its Awake and its first UpdateSupport, and then drops to the real value -
            // a change out of nowhere that wakes the neighbourhood. Restoring the stored value
            // here closes both halves: neighbours read what the piece actually last computed
            // (strictly better information than a placeholder, and exactly what a non-owner
            // would have read for the same piece), and the first recompute lands on the value it
            // started from, so a piece returning to an unchanged base seeds no wave at all.
            // UpdateWear consults HaveSupport only AFTER UpdateSupport has overwritten this, so
            // no collapse or damage decision is taken on the restored value.
            if (__instance.m_nview != null && __instance.m_nview.IsValid()
                && __instance.m_nview.GetZDO().GetFloat(ZDOVars.s_support, out float stored)) {
                __instance.m_support = stored;
            }

            // A piece is never in the grid at Awake - registration is lazy, from UpdateSupport -
            // so it cannot be a candidate for its own arrival wake and needs no exclude beyond
            // whatever state it already has.
            States.TryGetValue(__instance, out PieceState state);

            Vector3 position = __instance.transform.position;
            WakeOverlapping(
                position.x - FallbackWakeRadius, position.z - FallbackWakeRadius,
                position.x + FallbackWakeRadius, position.z + FallbackWakeRadius,
                position.y - FallbackWakeRadius, position.y + FallbackWakeRadius, state);
        }

        // ---- the envelope grid ---------------------------------------------------------------

        private static long CellKey(int x, int z) => ((long)x << 32) | (uint)z;

        [HarmonyPostfix]
        [HarmonyPatch("SetupColliders")]
        private static void SetupCollidersPostfix(WearNTear __instance) {
            List<WearNTear.BoundData> bounds = __instance.m_bounds;
            if (bounds == null || bounds.Count == 0) { return; }
            if (Registered.ContainsKey(__instance)) { Unregister(__instance); }

            float minX = float.MaxValue, minZ = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < bounds.Count; i++) {
                WearNTear.BoundData bound = bounds[i];

                // The oriented box's true axis-aligned extent, not a sphere around it. The
                // previous m_size.magnitude was a worst-case radius that assumed the box's
                // diagonal could point along any axis, which for the flat pieces most of a base
                // is made of over-covers enormously in Y: a floor with half-extents (1, 0.1, 1)
                // got a vertical reach of 1.42 instead of 0.1, so its envelope swallowed the
                // floors above and below it and the Y test could not tell them apart. Projecting
                // the half-extents through the absolute rotation is exact for the axis-aligned
                // and quarter-turned pieces that dominate, and never smaller than the truth.
                Matrix4x4 rotation = Matrix4x4.Rotate(bound.m_rot);
                Vector3 half = bound.m_size;
                float ex = Mathf.Abs(rotation.m00) * half.x + Mathf.Abs(rotation.m01) * half.y + Mathf.Abs(rotation.m02) * half.z;
                float ey = Mathf.Abs(rotation.m10) * half.x + Mathf.Abs(rotation.m11) * half.y + Mathf.Abs(rotation.m12) * half.z;
                float ez = Mathf.Abs(rotation.m20) * half.x + Mathf.Abs(rotation.m21) * half.y + Mathf.Abs(rotation.m22) * half.z;

                if (bound.m_pos.x - ex < minX) { minX = bound.m_pos.x - ex; }
                if (bound.m_pos.z - ez < minZ) { minZ = bound.m_pos.z - ez; }
                if (bound.m_pos.y - ey < minY) { minY = bound.m_pos.y - ey; }
                if (bound.m_pos.x + ex > maxX) { maxX = bound.m_pos.x + ex; }
                if (bound.m_pos.z + ez > maxZ) { maxZ = bound.m_pos.z + ez; }
                if (bound.m_pos.y + ey > maxY) { maxY = bound.m_pos.y + ey; }
            }

            Envelope envelope = new Envelope {
                m_state = GetState(__instance),
                m_x0 = Mathf.FloorToInt(minX / CellSize),
                m_z0 = Mathf.FloorToInt(minZ / CellSize),
                m_x1 = Mathf.FloorToInt(maxX / CellSize),
                m_z1 = Mathf.FloorToInt(maxZ / CellSize),
                m_minX = minX,
                m_minZ = minZ,
                m_maxX = maxX,
                m_maxZ = maxZ,
                m_minY = minY,
                m_maxY = maxY,
            };

            GridEntry entry = new GridEntry {
                m_state = envelope.m_state,
                m_minX = minX, m_minZ = minZ, m_maxX = maxX, m_maxZ = maxZ,
                m_minY = minY, m_maxY = maxY,
            };

            PieceState state = envelope.m_state;
            Cell[] cells = new Cell[(envelope.m_x1 - envelope.m_x0 + 1) * (envelope.m_z1 - envelope.m_z0 + 1)];
            int next = 0;

            for (int x = envelope.m_x0; x <= envelope.m_x1; x++) {
                for (int z = envelope.m_z0; z <= envelope.m_z1; z++) {
                    long key = CellKey(x, z);
                    if (!Grid.TryGetValue(key, out Cell cell)) {
                        cell = new Cell();
                        Grid.Add(key, cell);
                    }

                    cell.Add(entry);
                    if (!state.m_dirty) { cell.m_clean++; }
                    cells[next++] = cell;
                }
            }

            state.m_cells = cells;
            Registered[__instance] = envelope;
        }

        private static void Unregister(WearNTear piece) {
            if (!Registered.TryGetValue(piece, out Envelope envelope)) { return; }

            for (int x = envelope.m_x0; x <= envelope.m_x1; x++) {
                for (int z = envelope.m_z0; z <= envelope.m_z1; z++) {
                    long key = CellKey(x, z);
                    if (!Grid.TryGetValue(key, out Cell cell)) { continue; }

                    for (int i = 0; i < cell.m_count; i++) {
                        if (ReferenceEquals(cell.m_entries[i].m_state, envelope.m_state)) {
                            cell.RemoveAt(i);
                            if (!envelope.m_state.m_dirty) { cell.m_clean--; }
                            break;
                        }
                    }

                    if (cell.m_count == 0) { Grid.Remove(key); }
                }
            }

            envelope.m_state.m_cells = null;
            Registered.Remove(piece);
        }

        // The caller passes the state it already has in hand; the whole point of the entries
        // carrying their state is that this path never probes the state map.
        private static void DirtyNeighbours(WearNTear piece, PieceState state) {
            if (Registered.TryGetValue(piece, out Envelope e)) {
                WakeOverlapping(e.m_minX, e.m_minZ, e.m_maxX, e.m_maxZ, e.m_minY, e.m_maxY, state);
                return;
            }

            // Never registered (SetupColliders is lazy), but its colliders were live and may
            // have supported sleepers - wake by a conservative box around its position.
            Vector3 position = piece.transform.position;
            WakeOverlapping(
                position.x - FallbackWakeRadius, position.z - FallbackWakeRadius,
                position.x + FallbackWakeRadius, position.z + FallbackWakeRadius,
                position.y - FallbackWakeRadius, position.y + FallbackWakeRadius, state);
        }

        // The cell grid is XZ only - cells are cheap and a column of them is a short lookup - but
        // the overlap test is 3D. Without the Y test a change on one floor woke every piece in
        // the same XZ footprint on every floor above and below it, which in a multi-storey base
        // is most of the building: the exact "wakes hundreds instead of the handful that actually
        // touch" failure this test was introduced to fix in XZ, never applied to the vertical.
        private static void WakeOverlapping(
            float minX, float minZ, float maxX, float maxZ, float minY, float maxY, PieceState exclude) {
            int x0 = Mathf.FloorToInt(minX / CellSize), x1 = Mathf.FloorToInt(maxX / CellSize);
            int z0 = Mathf.FloorToInt(minZ / CellSize), z1 = Mathf.FloorToInt(maxZ / CellSize);

            for (int x = x0; x <= x1; x++) {
                for (int z = z0; z <= z1; z++) {
                    if (!Grid.TryGetValue(CellKey(x, z), out Cell cell)) { continue; }

                    // Every piece registered here is already awake - nothing a scan could add.
                    if (cell.m_clean == 0) {
                        if (_statsOn) { _wakeCellsSkipped++; }
                        continue;
                    }

                    GridEntry[] entries = cell.m_entries;
                    int count = cell.m_count;
                    if (_statsOn) { _wakeCandidates += count; }

                    for (int i = 0; i < count; i++) {
                        PieceState state = entries[i].m_state;

                        // Already awake: nothing a further wake could add, and skipping here
                        // costs one field read through a reference the entry already holds.
                        if (state.m_dirty) { continue; }

                        // Exact envelope overlap: only pieces whose support region can actually
                        // see the event wake up. Envelopes already carry the overlap-box margin,
                        // so this is a conservative superset of the true neighbour set - but a
                        // ~20x smaller one than "shares a grid cell".
                        if (entries[i].m_minX > maxX || entries[i].m_maxX < minX
                            || entries[i].m_minZ > maxZ || entries[i].m_maxZ < minZ
                            || entries[i].m_minY > maxY || entries[i].m_maxY < minY) { continue; }

                        if (ReferenceEquals(state, exclude)) { continue; }

                        SetDirty(state, true);
                        if (_statsOn) { _wakeWoken++; }
                    }
                }
            }
        }

        // ---- batched destroy wakes -----------------------------------------------------------

        [HarmonyPostfix]
        [HarmonyPatch("OnDestroy")]
        private static void OnDestroyPostfix(WearNTear __instance) {
            // Capture the wake box BEFORE unregistering - the envelope and the transform are
            // both gone after this - then forget the piece entirely. The sweep runs at the flush
            // below, by which point this piece is out of the grid and so cannot be a candidate
            // for its own wake; that is why the box carries no exclude.
            QueueDestroyWake(__instance);
            Unregister(__instance);
            States.Remove(__instance);
        }

        private static void QueueDestroyWake(WearNTear piece) {
            float minX, minZ, maxX, maxZ, minY, maxY;
            if (Registered.TryGetValue(piece, out Envelope e)) {
                minX = e.m_minX; minZ = e.m_minZ; maxX = e.m_maxX; maxZ = e.m_maxZ;
                minY = e.m_minY; maxY = e.m_maxY;
            } else {
                // Never registered (SetupColliders is lazy) - the same conservative box
                // DirtyNeighbours falls back to for that case.
                Vector3 position = piece.transform.position;
                minX = position.x - FallbackWakeRadius; minZ = position.z - FallbackWakeRadius;
                maxX = position.x + FallbackWakeRadius; maxZ = position.z + FallbackWakeRadius;
                minY = position.y - FallbackWakeRadius; maxY = position.y + FallbackWakeRadius;
            }

            PendingWakes.Add(new WakeBox {
                m_minX = minX, m_minZ = minZ, m_maxX = maxX, m_maxZ = maxZ,
                m_minY = minY, m_maxY = maxY,
                m_x0 = Mathf.FloorToInt(minX / CellSize), m_x1 = Mathf.FloorToInt(maxX / CellSize),
                m_z0 = Mathf.FloorToInt(minZ / CellSize), m_z1 = Mathf.FloorToInt(maxZ / CellSize),
            });
        }

        /// Drains this frame's destroy wakes. Called from the updater's per-frame prefix - which
        /// runs even on the frames the updater itself sleeps, so the queue never carries over -
        /// and from both read paths, so no visit can consult a sleep state a queued death would
        /// have invalidated, whatever called it.
        private static void FlushDestroyWakes() {
            if (PendingWakes.Count == 0) { return; }

            // A lone death - the common case outside a storm - is not worth the bucketing.
            if (PendingWakes.Count == 1) {
                WakeBox only = PendingWakes[0];
                WakeOverlapping(
                    only.m_minX, only.m_minZ, only.m_maxX, only.m_maxZ,
                    only.m_minY, only.m_maxY, null);
                PendingWakes.Clear();
                return;
            }

            for (int i = 0; i < PendingWakes.Count; i++) {
                WakeBox box = PendingWakes[i];
                for (int x = box.m_x0; x <= box.m_x1; x++) {
                    for (int z = box.m_z0; z <= box.m_z1; z++) {
                        long key = CellKey(x, z);
                        // Cells the storm emptied outright have nothing left to wake.
                        if (!Grid.ContainsKey(key)) { continue; }

                        if (!WakeCells.TryGetValue(key, out List<int> boxes)) {
                            boxes = WakeCellPool.Count > 0 ? WakeCellPool.Pop() : new List<int>();
                            WakeCells.Add(key, boxes);
                        }

                        boxes.Add(i);
                    }
                }
            }

            foreach (KeyValuePair<long, List<int>> cellBoxes in WakeCells) {
                Cell cell = Grid[cellBoxes.Key];
                if (cell.m_clean == 0) {
                    if (_statsOn) { _wakeCellsSkipped++; }
                    continue;
                }

                GridEntry[] entries = cell.m_entries;
                int count = cell.m_count;
                List<int> boxes = cellBoxes.Value;
                if (_statsOn) { _wakeCandidates += count; }

                for (int i = 0; i < count; i++) {
                    PieceState state = entries[i].m_state;

                    // Dirty is set-only within a flush, so a piece the first box woke costs one
                    // branch for every later box that reaches it - that, plus scanning each cell
                    // once, is what keeps a dense collapse off boxes-times-entries.
                    if (state.m_dirty) { continue; }

                    for (int b = 0; b < boxes.Count; b++) {
                        WakeBox box = PendingWakes[boxes[b]];
                        if (entries[i].m_minX > box.m_maxX || entries[i].m_maxX < box.m_minX
                            || entries[i].m_minZ > box.m_maxZ || entries[i].m_maxZ < box.m_minZ
                            || entries[i].m_minY > box.m_maxY || entries[i].m_maxY < box.m_minY) { continue; }

                        SetDirty(state, true);
                        if (_statsOn) { _wakeWoken++; }
                        break;
                    }
                }

                boxes.Clear();
                WakeCellPool.Push(boxes);
            }

            WakeCells.Clear();
            PendingWakes.Clear();
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                States.Clear();
                Grid.Clear();
                Registered.Clear();
                PendingWakes.Clear();
                WakeCells.Clear();
                WakeCellPool.Clear();
            }
        }

        private static void LogVerifySummary(string kind) {
            var reasons = new System.Text.StringBuilder();
            for (int i = 1; i < SupportBlockCounts.Length; i++) {
                if (SupportBlockCounts[i] == 0) { continue; }
                if (reasons.Length > 0) { reasons.Append(", "); }
                reasons.Append(SupportBlockNames[i]).Append(' ').Append(SupportBlockCounts[i]);
            }

            // The wear-skip count is reported here even when the wear verify is off, because it
            // is the other half of the answer: this predicate is only ever evaluated on visits
            // the wear sleep already let through, and four of ITS block reasons force this one
            // to fail. A big wear-skip count next to zero support skips means subsumed, not
            // broken.
            Logger.LogInfo(
                $"Support sleep verify ({kind}): {_verifyEvaluated} visit(s) reached the support " +
                $"check, would have skipped {_verifyWouldSkip}, {_verifyDivergences} " +
                $"divergence(s). Blocked by: {(reasons.Length > 0 ? reasons.ToString() : "nothing")}. " +
                $"The wear sleep skipped {_wearSkipped} visit(s) before this point.");
        }

        private static void LogWearVerifySummary(string kind) {
            var reasons = new System.Text.StringBuilder();
            for (int i = 1; i < WearBlockCounts.Length; i++) {
                if (WearBlockCounts[i] == 0) { continue; }
                if (reasons.Length > 0) { reasons.Append(", "); }
                reasons.Append(WearBlockNames[i]).Append(' ').Append(WearBlockCounts[i]);
            }

            Logger.LogInfo(
                $"Wear sleep verify ({kind}): {_wearVisits} visit(s) over {States.Count} " +
                $"tracked piece(s), would have skipped {_wearWouldSkip}, " +
                $"{_wearDivergences} divergence(s). " +
                $"Blocked by: {(reasons.Length > 0 ? reasons.ToString() : "nothing")}.");
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
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "UpdateWear"))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "ApplyDamage"))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "RPC_Repair"))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "UpdateCover"))
                && HasOurHook(AccessTools.DeclaredMethod(typeof(WearNTearUpdater), "Update"), typeof(UpdaterHook));

            if (!_hooksHealthy) {
                Logger.LogError(
                    "Support sleep: a wake hook is not attached, so pieces cannot sleep safely " +
                    "and support checks are running vanilla for this session. This usually " +
                    "means a Valheim update changed those methods - look for the patch failure " +
                    "logged at startup.");
            }

            return _hooksHealthy;
        }

        private static bool HasOurPostfix(MethodBase target) => HasOurHook(target, typeof(SupportSleepPatch));

        private static bool HasOurHook(MethodBase target, System.Type hookClass) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            foreach (Patch patch in info.Postfixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookClass) { continue; }
                return true;
            }

            foreach (Patch patch in info.Prefixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookClass) { continue; }
                return true;
            }

            return false;
        }
    }
}
