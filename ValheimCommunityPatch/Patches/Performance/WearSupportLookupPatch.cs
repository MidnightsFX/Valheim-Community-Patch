using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Support Lookup Cost: UpdateSupport walks the hierarchy for every overlap hit to find the piece that owns it.
    // Transpiles UpdateSupport to use a collider-to-piece table and an array scan in place of the walks and LINQ.
    // Structural support checks on every loaded building cost less, on clients and servers alike.
    // Provenance: map-probe form corroborated by ontrigger's ValheimPerformanceOptimizations (MIT).
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(WearNTear))]
    internal static class WearSupportLookupPatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Support Lookup",
                false,
                "Diagnostic. Resolves every support collider through both the lookup table and the game's " +
                "own walk, acts on the game's answer and logs any disagreement; costs the walk this fix saves.",
                advanced: true);
        }

        // Both keyed on GetInstanceID(); see TeardownHooks for the rationale and invariant. On
        // the single-probe read path the id lookup costs about as much as the object key it
        // replaced; it is int-keyed so ColliderOwner agrees with RegisteredBy, which is
        // multi-probe on teardown.
        private static readonly Dictionary<int, WearNTear> ColliderOwner = new Dictionary<int, WearNTear>();
        private static readonly Dictionary<int, List<int>> RegisteredBy = new Dictionary<int, List<int>>();

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(WearSupportLookupPatch));

        // Without both hooks the map silently goes stale.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(WearSupportLookupPatch),
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

        // 1.0.21 replaced the walk over cached support colliders with this list, so a build
        // without it still has three walks.
        private static readonly int ExpectedWalks =
            AccessTools.DeclaredField(typeof(WearNTear), "m_supportWearNTear") != null ? 2 : 3;

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

        // SetupColliders runs lazily, so colliders seen before it are learned on a miss in ResolveSupport.
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
            if (ApiSwitch.Off || !Hooks.Healthy) { return collider.GetComponentInParent<WearNTear>(); }

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
                        "Please report this - leave 'Verify Support Lookup' on until it is " +
                        "understood, so every lookup uses the game's own answer.");
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

        // Replaces ((IEnumerable<Collider>)m_colliders).Contains(collider) with the same answer
        // and no enumerator. It reads the piece's own array rather than the owner table, which
        // also holds colliders learned after the array was built (an item stand's attached item).
        public static bool IsOwnCollider(IEnumerable<Collider> ownColliders, Collider candidate) {
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
            if (ApiSwitch.Off) { return true; }

            Transform transform = __instance.transform;
            __result = transform.position + transform.rotation * __instance.m_comOffset;
            return false;
        }

        // Both replacements are required, so this backs out unless it can make every swap. Do not add a
        // GetSupport prefix: measured, intercepting a method that hot cost more than the walk it saved.
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

            if (replaced != ExpectedWalks || containsReplaced != 2) {
                PatchHelper.WarnOnce(
                    $"WearNTear.UpdateSupport: expected {ExpectedWalks} GetComponentInParent<WearNTear> and 2 " +
                    $"Enumerable.Contains calls, found {replaced} and {containsReplaced}, so " +
                    "this fix is inactive. Another mod has most likely already rewritten the " +
                    "method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
