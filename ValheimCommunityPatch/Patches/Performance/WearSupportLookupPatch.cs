using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: WearNTear.UpdateSupport resolves "which building piece owns this collider"
    // with Component.GetComponentInParent<WearNTear>() - a native hierarchy walk - at three call
    // sites (WearNTear.cs:431, :474, :500): once per cached support collider on EVERY invocation
    // (even when the cache holds and the method early-returns), and once per overlap hit in the
    // clear-broadcast and main processing loops. WearNTearUpdater slices these checks over every
    // loaded piece continuously, so in a large base the hierarchy walks alone were measured at
    // ~11.5 seconds of a 10-minute session - a fifth of the whole wear-and-tear cost.
    //
    // Fix: a collider-to-piece dictionary. Registered when a piece builds its collider list
    // (WearNTear.SetupColliders - which is LAZY, and WearNTear has no OnEnable/OnDisable, only
    // Awake/OnDestroy, so the map is keyed to component lifetime, not enabled state - a disabled
    // piece still physically supports its neighbours and must stay resolvable), learned on miss
    // (a neighbour that has not run SetupColliders yet resolves via the original walk and is
    // remembered - so a cold or incomplete map costs exactly vanilla), and cleaned up through a
    // reverse map on OnDestroy, where the child colliders may already be gone.
    //
    // Equivalence: every call site Unity-null-checks the result, so a stale entry whose piece was
    // destroyed behaves exactly like vanilla's "no ancestor found" as long as cleanup runs - and
    // if the maintenance hooks failed to attach, ResolveSupport stands down to the original walk
    // permanently. The only way the map could return a live *wrong* answer is a collider being
    // re-parented from one living piece to another, which no vanilla path does.
    //
    // One more per-call cost in the same method is recovered below (corroborated by ontrigger's
    // ValheimPerformanceOptimizations, MIT,
    // https://github.com/ontrigger/ValheimPerformanceOptimizations): the LINQ own-collider Contains
    // scans become map probes (IsOwnCollider).
    //
    // A second one used to live here and was REMOVED after measurement: a GetSupport prefix that
    // stopped GetMaxSupport being evaluated as an eager default argument on the non-owner path.
    // Measured 2026-09-01: the interception cost 12.04 ms/s on one of the hottest small methods in
    // the game, guarding a GetMaterialProperties that cost 0.07 ms/s across the whole session, and
    // the A/B run with it disabled measured marginally better frame stats than baseline. Vanilla's
    // eager default is cheaper than any interception of a method this hot; do not reintroduce this
    // as a prefix. (A transpiler form would be legitimate but its ceiling is ~1 ms/s.)
    // See Investigations/2026-09-01-wearntear-support-round.md.
    //
    // Not recovered, deliberately: the OverlapBoxNonAlloc calls themselves and the spread of
    // native property reads (attachedRigidbody, isTrigger, transform positions) - caching those
    // means caching mutable engine state for thin gains. Negative caching of non-piece colliders
    // (rocks etc.) was evaluated and rejected: no destroy signal exists for those keys, so the
    // cache would leak; the terrain-layer check already short-circuits the most common case.
    // A centre-of-mass CACHE was tried here and withdrawn: measured, the per-frame memo spent
    // 13.7 ms of every second probing its map to avoid about 7 ms of transform reads. What
    // survives is the cheap half - vanilla fetches the transform twice for one expression, and
    // once is enough - which beats both the memo and vanilla with no bookkeeping at all.
    //
    // Both: a dedicated server runs UpdateSupport for the pieces in its own active area.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(WearNTear))]
    internal static class WearSupportLookupPatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Support Lookup",
                false,
                "Diagnostic. Resolves every support collider both through the lookup table and " +
                "vanilla's hierarchy walk, acts on vanilla's answer, and logs any disagreement. " +
                "Costs the walk this fix exists to avoid, so leave it off unless you are " +
                "validating the table.",
                advanced: true);
        }

        // Positive entries only. A destroyed piece's entry stays removable because the reverse map
        // holds the collider keys, not the colliders, so cleanup never has to touch a fake-null.
        //
        // Both maps are keyed on GetInstanceID(), not on the Collider / WearNTear itself: a
        // Dictionary keyed on a UnityEngine.Object pays a native CompareBaseObjects call on every
        // probe. That matters twice over here. On the teardown path a piece unmapped two entries
        // per collider it owned, so a four-collider piece paid eight native compares just to
        // leave. On the READ path the trade is NOT a win and the comment here used to claim it
        // was: ResolveSupport does a single probe per call, so keying on the id swaps one native
        // call (Equals) for another (GetInstanceID), measured at 1.06 ms/s. It is kept int-keyed
        // only because ColliderOwner has to agree with RegisteredBy, which is genuinely
        // multi-probe. See the measured caveat in TeardownHooks.
        // RegisteredBy holds collider ids rather than colliders because
        // unmapping only ever needs the key. See TeardownHooks for the liveness invariant an int
        // key depends on.
        private static readonly Dictionary<int, WearNTear> ColliderOwner = new Dictionary<int, WearNTear>();
        private static readonly Dictionary<int, List<int>> RegisteredBy = new Dictionary<int, List<int>>();


        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        // Verify-mode telemetry: comparison volume proves the verify actually exercised the
        // table, not just that nothing complained.
        private const int VerifyReportInterval = 25000;
        private static bool _verifyActive;
        private static long _verifyComparisons;
        private static long _verifyDivergences;
        private static int _comparisonsSinceReport;

        private static readonly MethodInfo GetComponentInParentMethod =
            AccessTools.Method(typeof(Component), nameof(Component.GetComponentInParent),
                new Type[0], new[] { typeof(WearNTear) });
        private static readonly MethodInfo ResolveSupportMethod =
            AccessTools.Method(typeof(WearSupportLookupPatch), nameof(ResolveSupport));
        private static readonly MethodInfo EnumerableContainsMethod =
            typeof(Enumerable).GetMethods()
                .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(Collider));
        private static readonly MethodInfo IsOwnColliderMethod =
            AccessTools.Method(typeof(WearSupportLookupPatch), nameof(IsOwnCollider));

        // ---- map maintenance (unconditional) -------------------------------------------------

        private static void Register(WearNTear piece, Collider collider) {
            if (collider == null) { return; }

            int colliderId = collider.GetInstanceID();
            ColliderOwner[colliderId] = piece;

            int pieceId = piece.GetInstanceID();
            if (!RegisteredBy.TryGetValue(pieceId, out List<int> owned)) {
                owned = new List<int>();
                RegisteredBy.Add(pieceId, owned);
            }

            owned.Add(colliderId);
        }

        [HarmonyPostfix]
        [HarmonyPatch("SetupColliders")]
        private static void SetupCollidersPostfix(WearNTear __instance) {
            Collider[] colliders = __instance.m_colliders;
            if (colliders == null) { return; }

            for (int i = 0; i < colliders.Length; i++) { Register(__instance, colliders[i]); }
        }

        /// <summary>
        /// The destroy half of the maps, called from this mod's one WearNTear.OnDestroy postfix.
        /// </summary>
        internal static void OnPieceDestroyed(int pieceId) {
            if (!RegisteredBy.TryGetValue(pieceId, out List<int> owned)) { return; }

            for (int i = 0; i < owned.Count; i++) {
                // Only unmap keys that still point at this piece: learn-on-miss may have
                // re-attributed a collider in an exotic re-parenting scenario.
                if (ColliderOwner.TryGetValue(owned[i], out WearNTear current)
                    && !ReferenceEquals(current, null) && current.GetInstanceID() == pieceId) {
                    ColliderOwner.Remove(owned[i]);
                }
            }

            RegisteredBy.Remove(pieceId);
        }

        // A mod suppressing OnDestroy (or a scene teardown race) must not leak the maps across
        // sessions; Shutdown is the session boundary.
        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                ColliderOwner.Clear();
                RegisteredBy.Clear();
            }
        }

        // ---- the lookup ----------------------------------------------------------------------

        // Replaces collider.GetComponentInParent<WearNTear>() at the three UpdateSupport call
        // sites. Must be behaviourally identical to the walk: a dictionary hit whose piece died
        // is a fake-null return the call sites already handle; a miss falls back to the walk and
        // learns the answer.
        public static WearNTear ResolveSupport(Collider collider) {
            if (!HooksHealthy()) { return collider.GetComponentInParent<WearNTear>(); }

            if (Verify != null && Verify.Value) {
                _verifyActive = true;
                _verifyComparisons++;

                ColliderOwner.TryGetValue(collider.GetInstanceID(), out WearNTear cached);
                WearNTear walked = collider.GetComponentInParent<WearNTear>();

                // Both-dead counts as agreement: the call sites treat fake-null and null alike.
                bool cachedDead = cached == null;
                bool walkedDead = walked == null;
                if (!(cachedDead && walkedDead) && !ReferenceEquals(cached, walked) && !cachedDead) {
                    _verifyDivergences++;
                    Logger.LogError(
                        $"Support lookup verify: DIVERGED on collider '{collider.name}' " +
                        $"(table: {(cachedDead ? "null" : cached.name)}, walk: " +
                        $"{(walkedDead ? "null" : walked.name)}). Vanilla's answer was used. " +
                        "Please report this - leave 'Fix Support Lookup Cost' off until it is " +
                        "understood.");
                }

                if (++_comparisonsSinceReport >= VerifyReportInterval) {
                    _comparisonsSinceReport = 0;
                    LogVerifySummary("periodic");
                }

                return walked;
            }

            if (_verifyActive) {
                _verifyActive = false;
                LogVerifySummary("final");
                _verifyComparisons = 0;
                _verifyDivergences = 0;
                _comparisonsSinceReport = 0;
            }

            if (ColliderOwner.TryGetValue(collider.GetInstanceID(), out WearNTear owner)) { return owner; }

            WearNTear found = collider.GetComponentInParent<WearNTear>();
            if (found != null) { Register(found, collider); }

            return found;
        }

        private static void LogVerifySummary(string kind) {
            Logger.LogInfo(
                $"Support lookup verify ({kind}): {_verifyComparisons} comparison(s), " +
                $"{_verifyDivergences} divergence(s).");
        }

        // Replaces the LINQ ((IEnumerable<Collider>)m_colliders).Contains(collider) at both
        // UpdateSupport call sites - an allocating enumerator plus an O(n) scan per overlap hit.
        // A collider's owning piece is already in the map, and a piece's own colliders are
        // registered before UpdateSupport can reach this check (SetupColliders runs first), so
        // "is this collider mine" is one dictionary probe plus a reference compare against the
        // very array vanilla pushed. Signature matches the replaced call's stack exactly.
        // Provenance: corroborated by ontrigger's ValheimPerformanceOptimizations (MIT).
        public static bool IsOwnCollider(IEnumerable<Collider> ownColliders, Collider candidate) {
            if (HooksHealthy() && ColliderOwner.TryGetValue(candidate.GetInstanceID(), out WearNTear owner)) {
                return ReferenceEquals(owner.m_colliders, ownColliders);
            }

            // Unregistered candidate (not a piece collider) or unhealthy hooks: vanilla's
            // answer, without the enumerator allocation.
            if (ownColliders is Collider[] array) {
                for (int i = 0; i < array.Length; i++) {
                    if (ReferenceEquals(array[i], candidate)) { return true; }
                }

                return false;
            }

            return ownColliders.Contains(candidate);
        }

        // Vanilla fetches the transform TWICE for this one expression (WearNTear.cs:594); one
        // fetch serves both reads. Value-identical.
        //
        // This was briefly a per-frame memo instead, on the reasoning that neighbours repeat
        // heavily inside a sweep. Measured, the memo cost 13.7 ms of every second in dictionary
        // probing to avoid roughly 7 ms of transform reads - a Unity object key hashes and
        // compares fast, but the map is thousands of entries and every hit still copies the
        // record out. The plain single-fetch form is cheaper than both the memo and vanilla, so
        // that is what this is.
        [HarmonyPrefix]
        [HarmonyPatch("GetCOM")]
        private static bool GetCOMPrefix(WearNTear __instance, ref Vector3 __result) {
            Transform transform = __instance.transform;
            __result = transform.position + transform.rotation * __instance.m_comOffset;
            return false;
        }

        // Priority.Last, for the reason in ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("UpdateSupport")]
        private static IEnumerable<CodeInstruction> UpdateSupportTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replaced = 0;
            int containsReplaced = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].Calls(GetComponentInParentMethod)) {
                    // Same stack shape: [collider] -> WearNTear. A one-for-one operand rewrite.
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = ResolveSupportMethod;
                    replaced++;
                } else if (codes[i].Calls(EnumerableContainsMethod)) {
                    // Same stack shape: [IEnumerable<Collider>, Collider] -> bool.
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = IsOwnColliderMethod;
                    containsReplaced++;
                }
            }

            if (replaced != 3 || containsReplaced != 2) {
                Logger.LogWarning(
                    $"WearNTear.UpdateSupport: expected 3 GetComponentInParent<WearNTear> and 2 " +
                    $"Enumerable.Contains calls, found {replaced} and {containsReplaced}, so " +
                    "this fix is inactive. Another mod has most likely already rewritten the " +
                    "method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }

        // ---- hook health ---------------------------------------------------------------------

        /// Without the SetupColliders and OnDestroy hooks the map silently goes stale, so the
        /// lookup stands down to the plain walk when either is missing.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            _hooksHealthy =
                HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "SetupColliders"))
                && HasOurPostfixIn(AccessTools.DeclaredMethod(typeof(WearNTear), "OnDestroy"), typeof(TeardownHooks.PieceHook));

            if (!_hooksHealthy) {
                Logger.LogError(
                    "Support lookup: a maintenance hook is not attached, so the lookup table " +
                    "cannot be trusted and support checks have fallen back to vanilla's hierarchy " +
                    "walk for this session. This usually means a Valheim update changed those " +
                    "methods - look for the patch failure logged at startup.");
            }

            return _hooksHealthy;
        }

        private static bool HasOurPostfix(MethodBase target) =>
            HasOurPostfixIn(target, typeof(WearSupportLookupPatch));

        private static bool HasOurPostfixIn(MethodBase target, Type hookClass) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            foreach (Patch patch in info.Postfixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookClass) { continue; }
                return true;
            }

            return false;
        }
    }
}
