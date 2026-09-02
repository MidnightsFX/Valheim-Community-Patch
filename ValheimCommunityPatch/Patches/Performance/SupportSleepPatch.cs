using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Idle Support Checks and Fix Idle Wear Visits: a building piece skips its structural
    // support re-check, and when everything else is provably quiet its whole wear visit, until
    // an event that could change the answer fires.
    //
    // WearNTear.UpdateSupport has a cache fast path but re-validates the cache on every visit:
    // for each cached support collider it re-resolves the owning piece, reads its position and
    // re-reads its support value. WearNTearUpdater visits every loaded piece continuously, so in
    // a large base the validation itself is the cost, spent re-proving that nothing changed.
    //
    // Support is a pure function of a piece's neighbourhood, so a piece whose support was
    // computed once skips UpdateSupport until one of these wakes it:
    //  - ClearCachedSupport ran on it, the funnel for every vanilla invalidation (terrain edits,
    //    the cross-client RPC, a freshly placed piece's broadcast).
    //  - A neighbour was destroyed. Deaths are queued and swept once per frame against a coarse
    //    XZ grid of support envelopes, with an exact 3D envelope-overlap test picking the true
    //    neighbours. Cells whose every piece is already awake are skipped whole.
    //  - A neighbour's support value changed by more than Support Change Threshold, so the
    //    relaxation wave travels exactly as far as vanilla's and a settled structure goes quiet.
    //  - A new piece arrived: Awake wakes the envelope-overlapping sleepers around it, as
    //    vanilla's fast path would re-detect returning support within a sweep.
    //  - UpdateWear's out-of-area path stamped a new support value behind UpdateSupport's back.
    // Pieces that never computed under this tracking, and pieces below their material minimum,
    // run vanilla, and every piece revalidates after MaxSkipStreak consecutive skips as a net for
    // anything unforeseen. Two repairs stop arrivals looking like changes: Awake restores the
    // persisted support in place of vanilla's max-support placeholder, and the placeholder the
    // out-of-area path persists is put back to the last real value. The one heuristic: a piece
    // that has produced the same value several times running (Settled Piece Patience) may defer
    // a wake that came only from a neighbour's value drifting; structural wakes never wait.
    //
    // The wear-visit sleep skips the whole UpdateWear visit for pieces where every remaining
    // input is quiet too: owned locally, dry or roofed while wet, above the waterline, outside
    // the Ashlands, inside the active area, and no damage or repair since the last visit. Its
    // predicate lives in the wear-visit section below.
    //
    // Both: a dedicated server owns and updates the pieces in its active area.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(WearNTear))]
    internal static class SupportSleepPatch {
        internal static ConfigEntry<bool> Verify;
        internal static ConfigEntry<bool> WearVerify;
        internal static ConfigEntry<float> WakeEpsilon;
        internal static ConfigEntry<int> QuietBackoff;
        internal static ConfigEntry<bool> WakeStats;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Support Sleep",
                false,
                "Diagnostic. Runs the vanilla support check on every visit while predicting " +
                "what 'Fix Idle Support Checks' would have skipped, and logs any visit where a " +
                "predicted-quiet piece's support actually changed. Costs everything the fix " +
                "saves, so leave it off unless you are validating the predictions.",
                advanced: true);

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
                "re-checked. Support propagates through a structure as a wave, and re-checking " +
                "neighbours on every last-decimal drift keeps the whole structure awake; a " +
                "difference this small never accumulates, because each hop can only shrink it. " +
                "Support values run to a thousand, so the default is far below anything that " +
                "affects whether a build stands. 0 restores the exact comparison.",
                advanced: true,
                valMin: 0f,
                valMax: 1f);

            QuietBackoff = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Settled Piece Patience",
                3,
                "How many times in a row a building piece must re-check its support and get the " +
                "same answer before it is allowed to take a slower look when only a neighbour's " +
                "value drifted. In a large base the re-check signal is almost always on, so " +
                "without this the skip never happens; a piece that has proven it is not moving " +
                "can afford to see a small neighbouring drift a little late. Anything structural " +
                "- building, destroying, damage, repairs, terrain edits - is always immediate, " +
                "and any real change resets the piece's patience to zero. 0 turns this off.",
                advanced: true,
                valMin: 0,
                valMax: 10);

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

        // No point counting quiet runs past the threshold's ceiling.
        private const int QuietRunsCap = 16;

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

            // Backoff bookkeeping. m_quietRuns counts consecutive RAN recomputes that produced
            // the same value - empirical evidence the piece is not moving - and m_strongWake
            // records that the wake it is carrying was a structural event rather than a
            // neighbour's value drifting. See MayDeferWeakWake.
            public int m_quietRuns;
            public bool m_strongWake;

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

        // Entries carry the envelope and the piece's state object so a wake reads floats and sets
        // a bool straight from the cell array, with no dictionary probe per candidate. The state
        // reference doubles as the entry's identity for removal.
        internal struct GridEntry {
            public PieceState m_state;
            public float m_minX, m_minZ, m_maxX, m_maxZ;
            public float m_minY, m_maxY;
        }

        // A bare array plus a count rather than a List: the wake scan walks these in their
        // thousands, and List's indexer copies the struct out through a bounds-checked call.
        internal sealed class Cell {
            public GridEntry[] m_entries = new GridEntry[4];
            public int m_count;

            // How many entries belong to a piece that is not already dirty. A wake can only
            // change a clean piece, so a cell at zero is skipped whole.
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

        // Both piece maps are keyed on GetInstanceID(); see TeardownHooks for the rationale and
        // the liveness invariant an int key depends on.
        private static readonly Dictionary<int, PieceState> States = new Dictionary<int, PieceState>();
        private static readonly Dictionary<long, Cell> Grid = new Dictionary<long, Cell>();
        private static readonly Dictionary<int, Envelope> Registered = new Dictionary<int, Envelope>();

        // This frame's destroy wakes, and the pooled per-cell scratch the flush buckets them into.
        private static readonly List<WakeBox> PendingWakes = new List<WakeBox>();
        private static readonly Dictionary<long, List<int>> WakeCells = new Dictionary<long, List<int>>();
        private static readonly Stack<List<int>> WakeCellPool = new Stack<List<int>>();

        // A sleeping piece is woken only by the hooks in this class, so the sleep decision stands
        // down to vanilla's revalidation if any of them is missing.
        private static readonly HookHealth Hooks = new HookHealth(
            "Support sleep",
            () => HasOwnHook("ClearCachedSupport")
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(WearNTear), "OnDestroy"), typeof(TeardownHooks.PieceHook))
               && HasOwnHook("SetupColliders")
               && HasOwnHook("UpdateSupport")
               && HasOwnHook("Awake")
               && HasOwnHook("UpdateWear")
               && HasOwnHook("ApplyDamage")
               && HasOwnHook("RPC_Repair")
               && HasOwnHook("UpdateCover")
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(WearNTearUpdater), "Update"), typeof(UpdaterHook)));

        private static bool HasOwnHook(string wearNTearMethod) =>
            PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(WearNTear), wearNTearMethod), typeof(SupportSleepPatch));

        // "Log Support Wake Stats" counters. The flag is refreshed once a frame so the hot paths
        // read a field, not a config entry.
        private static bool _statsOn;
        private static float _statsSince;
        private static long _statVisits, _statFirstCompute, _statIdentical;

        // Changes bucketed relative to the piece's material maximum, which runs from 100 (wood)
        // to 2000 (ashstone), so one absolute number would not mean the same thing on both.
        private static long _statRelTenth, _statRelOne, _statRelTen, _statRelHuge;
        private static long _statWaves, _wakeCandidates, _wakeWoken, _wakeCellsSkipped;

        // The out-of-area max-support stamp (see UpdateWearPostfix): stamps written, recomputes
        // leaving max, recomputes arriving at max.
        private static long _statOutOfAreaStamp, _statLeftMax, _statReachedMax;
        private static long _statWeakDeferred;

        // Re-runs one recompute in ProbeInterval's bound boxes into a larger buffer to report how
        // often vanilla's fixed 128-collider overlap buffer would have truncated. Costs a real
        // physics query, hence the sampling.
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
                $"Weak wakes deferred by settled pieces: {_statWeakDeferred}. " +
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
            _statWeakDeferred = 0;
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
        private static void SetDirty(PieceState state, bool dirty, bool strong = false) {
            // Before the early-out on purpose: a strong wake arriving at an already-dirty piece
            // still has to upgrade it, or a destroy landing on a piece a neighbour's drift had
            // already flagged would be deferred as if it were the drift.
            if (dirty && strong) { state.m_strongWake = true; }

            if (state.m_dirty == dirty) { return; }
            state.m_dirty = dirty;

            Cell[] cells = state.m_cells;
            if (cells == null) { return; }

            int delta = dirty ? -1 : 1;
            for (int i = 0; i < cells.Length; i++) { cells[i].m_clean += delta; }
        }

        private static PieceState GetState(WearNTear piece) {
            int id = piece.GetInstanceID();
            if (!States.TryGetValue(id, out PieceState state)) {
                state = new PieceState();
                States.Add(id, state);
            }

            return state;
        }

        // ---- the sleep decision --------------------------------------------------------------

        // Returns 0 when the support check may sleep, else the index of the first failing
        // condition, so the verify summary can say why skips did not happen. "dirty" is split
        // because a strong wake means the piece must run, while an unsettled piece is one the
        // backoff would release if only its history were flatter.
        private static readonly string[] SupportBlockNames = {
            "sleepable", "support-cold", "dirty-structural", "streak-cap", "unsupported",
            "dirty-unsettled",
        };
        private static readonly long[] SupportBlockCounts = new long[6];

        private static int SupportBlockReason(WearNTear piece, PieceState state) {
            if (!state.m_computed) { return 1; }
            if (state.m_dirty && !MayDeferWeakWake(state)) { return state.m_strongWake ? 2 : 5; }
            if (state.m_skips >= MaxSkipStreak) { return 3; }
            if (piece.m_support < piece.GetMinSupport()) { return 4; }
            return 0;
        }

        /// The one heuristic in this class. In a large base the recalculation wave is continuous
        /// while anything streams, so the exact predicate almost never fires, yet most of the
        /// recomputes it forces land on the same value. A piece that has produced the same value
        /// several times running may defer a weak wake (a neighbour's value drifted). Structural
        /// wakes set m_strongWake and are never deferred, MaxSkipStreak caps the staleness, and
        /// any real change resets the quiet run. Threshold 0 restores the exact predicate.
        private static bool MayDeferWeakWake(PieceState state) {
            if (state.m_strongWake) { return false; }

            int threshold = QuietBackoff != null ? QuietBackoff.Value : 0;
            return threshold > 0 && state.m_quietRuns >= threshold;
        }

        [HarmonyPrefix]
        [HarmonyPatch("UpdateSupport")]
        private static bool UpdateSupportPrefix(WearNTear __instance, out Snapshot __state) {
            __state = default;
            FlushDestroyWakes();
            if (!Hooks.Healthy) { return true; }

            PieceState state = GetState(__instance);
            __state.m_state = state;
            __state.m_prevSupport = __instance.m_support;

            // A piece that never computed under this tracking may still hold the Awake
            // placeholder, so a first compute measures "changed" against the persisted value
            // rather than against GetMaxSupport(); otherwise every arrival seeds a wave.
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
                if (_statsOn && state.m_dirty) { _statWeakDeferred++; }
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

            // A tolerance so a neighbour's last-decimal drift does not keep the wave alive; the
            // piece still stores the exact value, only the notification is gated.
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
            state.m_strongWake = false;
            state.m_skips = 0;

            // A ran recompute that moved nothing is what earns the backoff; any real change
            // spends the whole run, so the evidence has to be rebuilt from scratch.
            if (changed) { state.m_quietRuns = 0; }
            else if (state.m_quietRuns < QuietRunsCap) { state.m_quietRuns++; }

            // The relaxation wave: a changed value is exactly the event neighbours must see.
            if (changed) {
                if (_statsOn) { _statWaves++; }

                // A neighbour's value moving is the WEAK signal: it is what the backoff above is
                // allowed to defer, precisely because it is the one wake that a piece with a flat
                // history can afford to see late.
                DirtyNeighbours(__instance, state, false);
            }
        }

        // Every vanilla invalidation signal funnels through here (see header).
        [HarmonyPostfix]
        [HarmonyPatch("ClearCachedSupport")]
        private static void ClearCachedSupportPostfix(WearNTear __instance) {
            // Every vanilla invalidation - terrain edits, the cross-client RPC, the broadcast a
            // freshly placed piece performs, remote damage - funnels through here, and all of
            // them are structural.
            if (States.TryGetValue(__instance.GetInstanceID(), out PieceState state)) { SetDirty(state, true, true); }
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
                // Same backoff the support predicate applies - without it the wear visit runs in
                // full for a piece whose support half would have slept, which is most of the cost.
                if (state.m_dirty && !MayDeferWeakWake(state)) { return 8; }
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

            if (!Hooks.Healthy) { return true; }

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

            if (ReferenceEquals(_supportHandledFor, __instance)) {
                return;
            }

            // Deliberately not __state.m_state: that is only populated when the wear prefix
            // engaged, and this repair belongs to the support fix.
            States.TryGetValue(__instance.GetInstanceID(), out PieceState tracked);
            RepairStampedSupport(__instance, tracked);

            if (__instance.m_support.Equals(__state.m_prevSupport)) { return; }

            // The piece left the active area and had max support stamped on it; it must not
            // sleep on that value when it comes back, and neighbours read it like any change.
            if (_statsOn) { _statOutOfAreaStamp++; }

            PieceState changedState = GetState(__instance);
            SetDirty(changedState, true, true);
            DirtyNeighbours(__instance, changedState, true);
        }

        /// UpdateWear stamps m_support = GetMaxSupport() for pieces outside the active area and
        /// persists it, so the stored support (what a non-owner reads, and what a returning piece
        /// restores from at Awake) is overwritten with a placeholder every time the ring edge
        /// sweeps past. The in-memory stamp is left alone, since it keeps an unwatched structure
        /// from failing its support check; only the stored copy is put back to the last real
        /// value, and only when it actually holds the placeholder.
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
            if (States.TryGetValue(__instance.GetInstanceID(), out PieceState state)) { state.m_wearWake = true; }
        }

        // Damage and repair are the two owner-side events that change what UpdateVisual shows;
        // both wake the piece for one vanilla visit. Unconditional, like all wake maintenance.
        [HarmonyPostfix]
        [HarmonyPatch("ApplyDamage")]
        private static void ApplyDamagePostfix(WearNTear __instance) {
            if (States.TryGetValue(__instance.GetInstanceID(), out PieceState state)) { state.m_wearWake = true; }
        }

        [HarmonyPostfix]
        [HarmonyPatch("RPC_Repair")]
        private static void RPC_RepairPostfix(WearNTear __instance) {
            if (States.TryGetValue(__instance.GetInstanceID(), out PieceState state)) { state.m_wearWake = true; }
        }

        // A new piece's colliders join the support world here, long before its lazy
        // SetupColliders registers an envelope, so the sleepers around it are woken to re-detect
        // arriving support as vanilla's fast path would. The wake is weak on purpose: a piece
        // arriving by streaming was always there, and its support is already baked into its
        // neighbours' values. A player building something has its own strong path, the
        // ClearCachedSupport broadcast on the piece's first UpdateSupport.
        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(WearNTear __instance) {
            // Vanilla's Awake stamps m_support = GetMaxSupport() as a placeholder and never
            // restores the persisted value, so an owned piece advertises an optimistic maximum
            // until its first UpdateSupport and then drops to the real value, waking the
            // neighbourhood for a change that never happened. Restoring the stored value here
            // means neighbours read what the piece last computed and the first recompute lands
            // where it started. No collapse or damage decision is taken on the restored value:
            // UpdateWear consults HaveSupport only after UpdateSupport has overwritten it.
            if (__instance.m_nview != null && __instance.m_nview.IsValid()
                && __instance.m_nview.GetZDO().GetFloat(ZDOVars.s_support, out float stored)) {
                __instance.m_support = stored;
            }

            // A piece is never in the grid at Awake - registration is lazy, from UpdateSupport -
            // so it cannot be a candidate for its own arrival wake and needs no exclude beyond
            // whatever state it already has.
            States.TryGetValue(__instance.GetInstanceID(), out PieceState state);

            Vector3 position = __instance.transform.position;
            WakeOverlapping(
                position.x - FallbackWakeRadius, position.z - FallbackWakeRadius,
                position.x + FallbackWakeRadius, position.z + FallbackWakeRadius,
                position.y - FallbackWakeRadius, position.y + FallbackWakeRadius, state, false);
        }

        // ---- the envelope grid ---------------------------------------------------------------

        private static long CellKey(int x, int z) => ((long)x << 32) | (uint)z;

        [HarmonyPostfix]
        [HarmonyPatch("SetupColliders")]
        private static void SetupCollidersPostfix(WearNTear __instance) {
            List<WearNTear.BoundData> bounds = __instance.m_bounds;
            if (bounds == null || bounds.Count == 0) { return; }
            Unregister(__instance.GetInstanceID());

            float minX = float.MaxValue, minZ = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < bounds.Count; i++) {
                WearNTear.BoundData bound = bounds[i];

                // The oriented box's axis-aligned extent, projected through the absolute
                // rotation: exact for the axis-aligned and quarter-turned pieces that dominate
                // and never smaller than the truth. A sphere around the box would swallow the
                // floors above and below a flat piece.
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
            Registered[__instance.GetInstanceID()] = envelope;
        }

        private static void Unregister(int pieceId) {
            if (!Registered.TryGetValue(pieceId, out Envelope envelope)) { return; }

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
            Registered.Remove(pieceId);
        }

        // The caller passes the state it already has in hand; the whole point of the entries
        // carrying their state is that this path never probes the state map.
        private static void DirtyNeighbours(WearNTear piece, PieceState state, bool strong) {
            if (Registered.TryGetValue(piece.GetInstanceID(), out Envelope e)) {
                WakeOverlapping(e.m_minX, e.m_minZ, e.m_maxX, e.m_maxZ, e.m_minY, e.m_maxY, state, strong);
                return;
            }

            // Never registered (SetupColliders is lazy), but its colliders were live and may
            // have supported sleepers - wake by a conservative box around its position.
            Vector3 position = piece.transform.position;
            WakeOverlapping(
                position.x - FallbackWakeRadius, position.z - FallbackWakeRadius,
                position.x + FallbackWakeRadius, position.z + FallbackWakeRadius,
                position.y - FallbackWakeRadius, position.y + FallbackWakeRadius, state, strong);
        }

        // The cell grid is XZ only, but the overlap test is 3D: without the Y test a change on one
        // floor woke every piece in the same footprint on every floor above and below it.
        private static void WakeOverlapping(
            float minX, float minZ, float maxX, float maxZ, float minY, float maxY,
            PieceState exclude, bool strong) {
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

                        SetDirty(state, true, strong);
                        if (_statsOn) { _wakeWoken++; }
                    }
                }
            }
        }

        // ---- batched destroy wakes -----------------------------------------------------------

        /// <summary>
        /// The destroy half of the grid and state map, called from this mod's one
        /// WearNTear.OnDestroy postfix.
        /// </summary>
        internal static void OnPieceDestroyed(WearNTear piece, int pieceId) {
            // Capture the wake box BEFORE unregistering - the envelope and the transform are
            // both gone after this - then forget the piece entirely. The sweep runs at the flush
            // below, by which point this piece is out of the grid and so cannot be a candidate
            // for its own wake; that is why the box carries no exclude.
            QueueDestroyWake(piece, pieceId);
            Unregister(pieceId);
            States.Remove(pieceId);
        }

        private static void QueueDestroyWake(WearNTear piece, int pieceId) {
            float minX, minZ, maxX, maxZ, minY, maxY;
            if (Registered.TryGetValue(pieceId, out Envelope e)) {
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
                    only.m_minY, only.m_maxY, null, true);
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

                        // A death is structural, so it is never deferrable.
                        SetDirty(state, true, true);
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

    }
}
