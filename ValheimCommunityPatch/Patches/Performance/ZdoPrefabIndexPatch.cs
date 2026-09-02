using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: ZDOMan.GetAllZDOsWithPrefabIterative answers "every ZDO of this prefab" by
    // scanning the whole world - every sector list plus the outside-sector map - dereferencing
    // every ZDO to compare its prefab hash (ZDOMan.cs:922-956). The iteration is batched (400
    // sector lists per call), which spreads the cost but never reduces it.
    //
    // Vanilla itself only calls this from a console command, but it is the public API mods use,
    // and several popular ones call it *every ZoneSystem.Update tick* from postfixes. Profiling a
    // real modded session attributed ~8.6 seconds of a 10-minute window to these scans - the
    // single largest item inside ZoneSystem.Update during stutter-heavy seconds.
    //
    // Fix: keep the answer instead of recomputing it. ZDOs are bucketed by prefab hash as the
    // prefab is assigned, so the query returns O(matches) instead of O(world).
    //
    // The index is maintained unconditionally (standing rule), and unlike OrphanZdoIndexPatch it
    // is NOT gated on the network role: this method is called on clients, listen hosts and
    // dedicated servers alike.
    //
    // Contract of the replaced method, preserved exactly:
    //  - `index` is a resume cursor. An iteration already in flight (index != 0) is finished by
    //    vanilla, so mods that spread the drain across frames keep their semantics; a fresh
    //    iteration completes in one call, which is a legal fast completion - the bool return
    //    means "iteration complete" and every caller loops until it is true.
    //  - The final call runs zdos.RemoveAll(!IsValid) over the caller's whole accumulated list,
    //    pre-existing entries included. Replicated verbatim.
    //  - Result ordering changes from sector-grouped to bucket order. Callers accumulate into a
    //    list they treat as a set; no vanilla or known mod caller depends on the order, and the
    //    Verify toggle exists to prove equivalence on a live game.
    //
    // Prefab lifecycle, and why these hooks cover it: a ZDO's m_prefab is written by
    // ZDO.SetPrefab (the ZNetView.Awake path and direct mod calls), by ZDO.Deserialize (network
    // sync, ZDO.cs:571), and by the world-load readers inside ZDOMan.Load - covered by a full
    // rebuild there, where an O(world) pass is already unavoidable. ZDOMan.CreateNewZDO's
    // prefabHash parameter is deliberately not hooked: it is only used for the portal-list check
    // and m_prefab stays 0 until SetPrefab or Deserialize runs. ZDO.Reset zeroes m_prefab but is
    // deliberately not hooked, for OrphanZdoIndexPatch's reasons: every ZDOPool.Release site is
    // covered (HandleDestroyedZDO, the Load rebuild, the ShutDown clear), and Reset also runs on
    // millions of save clones - MemberwiseClones carrying the live ZDO's uid - every world save.
    //
    // Both: see above - the callers that matter run on every side.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class ZdoPrefabIndexPatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Prefab Index",
                false,
                "Diagnostic. Runs both the indexed lookup and vanilla's whole-world scan on every " +
                "prefab query, acts on vanilla's result, and logs any disagreement. Costs the scan " +
                "this fix exists to avoid, so leave it off unless you are validating the index.",
                advanced: true);
        }

        // ZDOID -> the prefab bucket that ZDO is filed under. Authoritative: untracking reads this
        // rather than live state, because by the time HandleDestroyedZDO's postfix runs the ZDO
        // has been reset and pooled and its prefab is unrecoverable.
        private static readonly Dictionary<ZDOID, int> PrefabOf = new Dictionary<ZDOID, int>();
        private static readonly Dictionary<int, HashSet<ZDOID>> ByPrefab = new Dictionary<int, HashSet<ZDOID>>();

        private static readonly List<ZDO> IndexScratch = new List<ZDO>();
        private static readonly List<ZDO> VanillaScratch = new List<ZDO>();
        private static readonly List<ZDOID> StaleScratch = new List<ZDOID>();

        // Cached so the per-call RemoveAll does not allocate a delegate.
        private static readonly Predicate<ZDO> InvalidZdo = zdo => !zdo.IsValid();

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        // ---- index maintenance -------------------------------------------------------------

        private static void Track(ZDOID uid, int prefab) {
            // Load paths set m_uid before the prefab, so this should not fire; the cost is one
            // comparison and it prevents a phantom bucket entry nothing could ever resolve.
            if (uid == ZDOID.None) { return; }

            // Hash 0 is the unassigned state (a fresh CreateNewZDO before SetPrefab/Deserialize).
            // A real prefab name never hashes to 0 in practice, and the read path falls back to
            // vanilla for a query that does, so the degenerate case stays correct either way.
            if (prefab == 0) { Untrack(uid); return; }

            if (PrefabOf.TryGetValue(uid, out int existing)) {
                if (existing == prefab) { return; }
                Untrack(uid);
            }

            PrefabOf[uid] = prefab;

            if (!ByPrefab.TryGetValue(prefab, out HashSet<ZDOID> bucket)) {
                bucket = new HashSet<ZDOID>();
                ByPrefab.Add(prefab, bucket);
            }

            bucket.Add(uid);
        }

        private static void Untrack(ZDOID uid) {
            if (!PrefabOf.TryGetValue(uid, out int prefab)) { return; }

            PrefabOf.Remove(uid);

            if (ByPrefab.TryGetValue(prefab, out HashSet<ZDOID> bucket)) {
                bucket.Remove(uid);

                // Buckets are keyed by prefab hash; a world cycles through many transient prefabs
                // (projectiles, vfx), so empties would otherwise accumulate for the session.
                if (bucket.Count == 0) { ByPrefab.Remove(prefab); }
            }
        }

        private static void ClearIndex() {
            PrefabOf.Clear();
            ByPrefab.Clear();
        }

        /// Full rebuild from live state. Only ever runs at world load, where an O(world) pass is
        /// already unavoidable, and it gives the index a known-good baseline covering ZDO.Load's
        /// direct m_prefab writes rather than trusting hooks through the load.
        private static void RebuildIndex(ZDOMan zdoMan) {
            ClearIndex();

            foreach (KeyValuePair<ZDOID, ZDO> pair in zdoMan.m_objectsByID) {
                ZDO zdo = pair.Value;
                if (zdo.m_prefab == 0) { continue; }

                Track(zdo.m_uid, zdo.m_prefab);
            }
        }

        // ---- hooks -------------------------------------------------------------------------

        // The assignment path for new objects (ZNetView.Awake) and for any mod setting a prefab
        // directly. Vanilla early-outs when the value is unchanged, so the postfix's compare is
        // the only steady-state cost.
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetPrefab))]
        internal static class SetPrefabHook {
            [HarmonyPrefix]
            private static void Prefix(ZDO __instance, out int __state) => __state = __instance.m_prefab;

            [HarmonyPostfix]
            private static void Postfix(ZDO __instance, int __state) {
                if (__instance.m_prefab == __state) { return; }

                Track(__instance.m_uid, __instance.m_prefab);
            }
        }

        // The network path: RPC_ZDOData deserializes into both freshly created and existing ZDOs,
        // writing m_prefab directly (ZDO.cs:571). This rides the hottest network path, so the
        // no-change case is one int compare and nothing else.
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        internal static class DeserializeHook {
            [HarmonyPrefix]
            private static void Prefix(ZDO __instance, out int __state) => __state = __instance.m_prefab;

            [HarmonyPostfix]
            private static void Postfix(ZDO __instance, int __state) {
                if (__instance.m_prefab == __state) { return; }

                Track(__instance.m_uid, __instance.m_prefab);
            }
        }

        // Takes the uid by value, so it still works after the ZDO itself has been pooled.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO))]
        internal static class HandleDestroyedZdoHook {
            [HarmonyPostfix]
            private static void Postfix(ZDOID uid) => Untrack(uid);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Load))]
        internal static class ZdoManLoadHook {
            [HarmonyPostfix]
            private static void Postfix(ZDOMan __instance) => RebuildIndex(__instance);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.ShutDown))]
        internal static class ZdoManShutDownHook {
            [HarmonyPostfix]
            private static void Postfix() => ClearIndex();
        }

        // ---- the query ---------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZDOMan.GetAllZDOsWithPrefabIterative))]
        private static bool GetAllZDOsWithPrefabIterativePrefix(
            ZDOMan __instance, string prefab, List<ZDO> zdos, ref int index, ref bool __result) {
            if (!HooksHealthy()) { return true; }

            // An iteration already in flight - a mod holding the cursor across frames - finishes
            // under vanilla, cursor semantics untouched. The next fresh iteration takes the index.
            if (index != 0) { return true; }

            int hash = prefab.GetStableHashCode();
            if (hash == 0) { return true; }

            IndexScratch.Clear();
            CollectIndexed(__instance, hash);
            IndexScratch.RemoveAll(InvalidZdo);

            List<ZDO> found = IndexScratch;

            if (Verify != null && Verify.Value) {
                VanillaScratch.Clear();
                CollectFullScan(__instance, hash);
                VanillaScratch.RemoveAll(InvalidZdo);
                ReportDivergence(prefab);

                // Act on vanilla's answer while verifying, so a bad index cannot mislead a mod
                // during the very run that is meant to expose it.
                found = VanillaScratch;
            }

            for (int i = 0; i < found.Count; i++) { zdos.Add(found[i]); }

            // Vanilla's completion call filters the caller's *entire* accumulated list, including
            // entries it did not add this call. Kept verbatim - it is observable behaviour.
            zdos.RemoveAll(InvalidZdo);

            // Leave the cursor where a finished vanilla iteration leaves it.
            index = __instance.m_objectsBySector.Length;

            IndexScratch.Clear();
            VanillaScratch.Clear();
            __result = true;
            return false;
        }

        private static void CollectIndexed(ZDOMan zdoMan, int hash) {
            if (!ByPrefab.TryGetValue(hash, out HashSet<ZDOID> bucket)) { return; }

            StaleScratch.Clear();

            foreach (ZDOID uid in bucket) {
                ZDO zdo = zdoMan.GetZDO(uid);
                if (zdo == null) { StaleScratch.Add(uid); } else { IndexScratch.Add(zdo); }
            }

            // A ZDO leaves m_objectsByID only through HandleDestroyedZDO, Load or ShutDown, all
            // of which maintain the index, so this should find nothing. Cleaning up anyway keeps
            // a missed path from becoming an index that grows for the life of the session -
            // deferred, because Untrack mutates the bucket being iterated above.
            for (int i = 0; i < StaleScratch.Count; i++) { Untrack(StaleScratch[i]); }
            StaleScratch.Clear();
        }

        /// Vanilla's drain, read-only, with a local cursor: the sector array in 400-list batches,
        /// then the outside-sector map (ZDOMan.cs:922-956, minus the caller-list filter, which the
        /// query above applies to whichever result it uses).
        private static void CollectFullScan(ZDOMan zdoMan, int hash) {
            List<ZDO>[] sectors = zdoMan.m_objectsBySector;
            for (int i = 0; i < sectors.Length; i++) {
                List<ZDO> list = sectors[i];
                if (list == null) { continue; }

                for (int j = 0; j < list.Count; j++) {
                    if (list[j].GetPrefab() == hash) { VanillaScratch.Add(list[j]); }
                }
            }

            foreach (List<ZDO> list in zdoMan.m_objectsByOutsideSector.Values) {
                for (int j = 0; j < list.Count; j++) {
                    if (list[j].GetPrefab() == hash) { VanillaScratch.Add(list[j]); }
                }
            }
        }

        private static void ReportDivergence(string prefab) {
            HashSet<ZDOID> indexed = new HashSet<ZDOID>();
            for (int i = 0; i < IndexScratch.Count; i++) { indexed.Add(IndexScratch[i].m_uid); }

            int missed = 0, extra = 0;
            ZDOID firstMissed = ZDOID.None, firstExtra = ZDOID.None;

            HashSet<ZDOID> scanned = new HashSet<ZDOID>();
            for (int i = 0; i < VanillaScratch.Count; i++) {
                ZDOID uid = VanillaScratch[i].m_uid;
                scanned.Add(uid);
                if (indexed.Contains(uid)) { continue; }

                if (missed == 0) { firstMissed = uid; }
                missed++;
            }

            foreach (ZDOID uid in indexed) {
                if (scanned.Contains(uid)) { continue; }

                if (extra == 0) { firstExtra = uid; }
                extra++;
            }

            if (missed == 0 && extra == 0) {
                Logger.LogInfo(
                    $"Prefab index verify ('{prefab}'): agreed on {VanillaScratch.Count} object(s) " +
                    $"out of {PrefabOf.Count} indexed.");
                return;
            }

            Logger.LogError(
                $"Prefab index verify ('{prefab}'): DIVERGED. The full scan found {missed} " +
                $"object(s) the index missed (first {firstMissed}), and the index claimed {extra} " +
                $"the full scan did not (first {firstExtra}). Vanilla's result was used. Please " +
                "report this - leave 'Fix Prefab Query Scan' off until it is understood.");
        }

        // ---- hook health -------------------------------------------------------------------

        /// True only when every maintenance hook is attached, checked against the hook class that
        /// was supposed to attach it - not merely "some patch of ours" - because this mod also
        /// patches HandleDestroyedZDO and Load from OrphanZdoIndexPatch, and their presence proves
        /// nothing about this index. A missing hook means the index silently stops tracking a
        /// whole class of change, so the answer gates the fix entirely.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            string missing = null;
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.SetPrefab)),
                           typeof(SetPrefabHook), "ZDO.SetPrefab", ref missing);
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.Deserialize)),
                           typeof(DeserializeHook), "ZDO.Deserialize", ref missing);
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO)),
                           typeof(HandleDestroyedZdoHook), "ZDOMan.HandleDestroyedZDO", ref missing);
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.Load)),
                           typeof(ZdoManLoadHook), "ZDOMan.Load", ref missing);

            _hooksHealthy = missing == null;

            if (!_hooksHealthy) {
                Logger.LogError(
                    $"Prefab index: the hook on {missing} is not attached, so the index cannot be " +
                    "trusted and prefab queries have fallen back to vanilla's whole-world scan for " +
                    "this session. This usually means a Valheim update changed that method - look " +
                    "for the patch failure logged at startup.");
            }

            return _hooksHealthy;
        }

        /// Appends `label` to `missing` when the method is gone or carries no patch declared by
        /// `hookType`. Matching the declaring type as well as the owner distinguishes this class's
        /// hooks from this mod's other patches on the same methods.
        private static void NoteIfUnhooked(MethodBase target, Type hookType, string label, ref string missing) {
            bool ours = false;
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);

            if (info != null) {
                foreach (Patch patch in info.Postfixes) {
                    if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                    if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookType) { continue; }
                    ours = true;
                    break;
                }
            }

            if (ours) { return; }

            missing = missing == null ? label : missing + " and " + label;
        }
    }
}
