using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Support Lookup Cost: "which building piece owns this collider" is a table lookup
    // instead of a native hierarchy walk.
    //
    // WearNTear.UpdateSupport calls Collider.GetComponentInParent<WearNTear>() at three sites:
    // once per cached support collider on every invocation, even when the cache holds, and once
    // per overlap hit in the clear-broadcast and main loops. It also tests "is this collider
    // mine" with LINQ Contains over its own collider array, an allocating enumerator per hit.
    // WearNTearUpdater visits every loaded piece continuously.
    //
    // A transpiler swaps the three walks for ResolveSupport and the two Contains calls for
    // IsOwnCollider. ResolveSupport reads a collider-to-piece dictionary that is filled when a
    // piece builds its collider list (SetupColliders, which is lazy) and learned on a miss, so a
    // cold table costs exactly vanilla, and is cleaned through a reverse map in the shared
    // OnDestroy postfix. Every call site Unity-null-checks the result, so a stale entry for a
    // destroyed piece behaves like vanilla's "no ancestor found". The lookup stands down to the
    // walk if either maintenance hook failed to attach. GetCOM also fetches the transform once
    // for an expression vanilla fetched it twice for.
    //
    // A GetSupport prefix that once lived here was removed after measurement: intercepting a
    // method that hot cost far more than the fallback it guarded. Do not reintroduce it as a
    // prefix. Both: a dedicated server runs UpdateSupport for its active area. Provenance: the
    // map-probe form corroborated by ontrigger's ValheimPerformanceOptimizations (MIT).
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

        // Both keyed on GetInstanceID(); see TeardownHooks for the rationale and invariant. On
        // the single-probe read path the id lookup costs about as much as the object key it
        // replaced; it is int-keyed so ColliderOwner agrees with RegisteredBy, which is
        // multi-probe on teardown.
        private static readonly Dictionary<int, WearNTear> ColliderOwner = new Dictionary<int, WearNTear>();
        private static readonly Dictionary<int, List<int>> RegisteredBy = new Dictionary<int, List<int>>();

        // Without both hooks the map silently goes stale.
        private static readonly HookHealth Hooks = new HookHealth(
            "Support lookup",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(WearNTear), "SetupColliders"), typeof(WearSupportLookupPatch))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(WearNTear), "OnDestroy"), typeof(TeardownHooks.PieceHook)));

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

        /// <summary>The destroy half, called from TeardownHooks' one WearNTear.OnDestroy postfix.</summary>
        internal static void OnPieceDestroyed(int pieceId) {
            if (!RegisteredBy.TryGetValue(pieceId, out List<int> owned)) { return; }

            for (int i = 0; i < owned.Count; i++) {
                // Only unmap keys that still point at this piece.
                if (ColliderOwner.TryGetValue(owned[i], out WearNTear current)
                    && !ReferenceEquals(current, null) && current.GetInstanceID() == pieceId) {
                    ColliderOwner.Remove(owned[i]);
                }
            }

            RegisteredBy.Remove(pieceId);
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                ColliderOwner.Clear();
                RegisteredBy.Clear();
            }
        }

        // ---- the lookup ----------------------------------------------------------------------

        // Replaces collider.GetComponentInParent<WearNTear>(). A hit whose piece died is a
        // fake-null the call sites already handle; a miss falls back to the walk and learns it.
        public static WearNTear ResolveSupport(Collider collider) {
            if (!Hooks.Healthy) { return collider.GetComponentInParent<WearNTear>(); }

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

        // Replaces ((IEnumerable<Collider>)m_colliders).Contains(collider). A piece's own
        // colliders are registered before UpdateSupport can reach this check, so "is this
        // collider mine" is one probe plus a reference compare against the array vanilla pushed.
        public static bool IsOwnCollider(IEnumerable<Collider> ownColliders, Collider candidate) {
            if (Hooks.Healthy && ColliderOwner.TryGetValue(candidate.GetInstanceID(), out WearNTear owner)) {
                return ReferenceEquals(owner.m_colliders, ownColliders);
            }

            // Not a piece collider, or unhealthy hooks: vanilla's answer without the enumerator.
            if (ownColliders is Collider[] array) {
                for (int i = 0; i < array.Length; i++) {
                    if (ReferenceEquals(array[i], candidate)) { return true; }
                }

                return false;
            }

            return ownColliders.Contains(candidate);
        }

        // Vanilla fetches the transform twice for this one expression; once serves both reads.
        [HarmonyPrefix]
        [HarmonyPatch("GetCOM")]
        private static bool GetCOMPrefix(WearNTear __instance, ref Vector3 __result) {
            Transform transform = __instance.transform;
            __result = transform.position + transform.rotation * __instance.m_comOffset;
            return false;
        }

        // Both replacements are required, so this backs out unless it can make all five swaps.
        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("UpdateSupport")]
        private static IEnumerable<CodeInstruction> UpdateSupportTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replaced = 0;
            int containsReplaced = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].Calls(GetComponentInParentMethod)) {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = ResolveSupportMethod;
                    replaced++;
                } else if (codes[i].Calls(EnumerableContainsMethod)) {
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
    }
}
