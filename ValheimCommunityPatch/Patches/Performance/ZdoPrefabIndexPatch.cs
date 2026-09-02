using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Prefab Query Scan: "every ZDO of this prefab" is answered from an index instead of a
    // scan of the whole world.
    //
    // ZDOMan.GetAllZDOsWithPrefabIterative walks every sector list plus the outside-sector map,
    // dereferencing every ZDO to compare its prefab hash. Vanilla only calls it from a console
    // command, but it is the API mods use, and several popular ones call it every ZoneSystem tick.
    //
    // ZDOs are bucketed by prefab hash as the prefab is assigned (ZDO.SetPrefab and ZDO.Deserialize
    // postfixes, a HandleDestroyedZDO postfix for removal, a full rebuild after ZDOMan.Load and a
    // clear on ShutDown), so the query returns O(matches). The replaced method's contract is kept:
    // an iteration already in flight (index != 0) is finished by vanilla, a fresh one completes in
    // one call, the final RemoveAll over the caller's whole list runs as vanilla's does, and the
    // cursor is left where a finished vanilla iteration leaves it. Result order changes from
    // sector-grouped to bucket order; callers treat the list as a set. Maintenance is
    // unconditional; the read stands down to vanilla if any hook failed to attach.
    //
    // Both: the callers that matter run on every side.
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

        // ZDOID -> the bucket it is filed under. Authoritative for untracking, because by the time
        // HandleDestroyedZDO's postfix runs the ZDO has been reset and pooled.
        private static readonly Dictionary<ZDOID, int> PrefabOf = new Dictionary<ZDOID, int>();
        private static readonly Dictionary<int, HashSet<ZDOID>> ByPrefab = new Dictionary<int, HashSet<ZDOID>>();

        private static readonly List<ZDO> IndexScratch = new List<ZDO>();
        private static readonly List<ZDO> VanillaScratch = new List<ZDO>();
        private static readonly List<ZDOID> StaleScratch = new List<ZDOID>();

        private static readonly Predicate<ZDO> InvalidZdo = zdo => !zdo.IsValid();

        // Checked against the hook class, not merely "some patch of ours": OrphanZdoIndexPatch
        // also patches HandleDestroyedZDO and Load, and its presence proves nothing about this index.
        private static readonly HookHealth Hooks = new HookHealth(
            "Prefab index",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.SetPrefab)), typeof(SetPrefabHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.Deserialize)), typeof(DeserializeHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO)), typeof(HandleDestroyedZdoHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.Load)), typeof(ZdoManLoadHook)));

        // ---- index maintenance -------------------------------------------------------------

        private static void Track(ZDOID uid, int prefab) {
            if (uid == ZDOID.None) { return; }

            // Hash 0 is the unassigned state; the read path falls back to vanilla for a query
            // that hashes to 0, so the degenerate case stays correct.
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

                // Transient prefabs (projectiles, vfx) would otherwise leave empties for the session.
                if (bucket.Count == 0) { ByPrefab.Remove(prefab); }
            }
        }

        private static void ClearIndex() {
            PrefabOf.Clear();
            ByPrefab.Clear();
        }

        // Full rebuild from live state, only at world load where an O(world) pass is already
        // unavoidable. Covers ZDO.Load's direct m_prefab writes.
        private static void RebuildIndex(ZDOMan zdoMan) {
            ClearIndex();

            foreach (KeyValuePair<ZDOID, ZDO> pair in zdoMan.m_objectsByID) {
                ZDO zdo = pair.Value;
                if (zdo.m_prefab == 0) { continue; }

                Track(zdo.m_uid, zdo.m_prefab);
            }
        }

        // ---- hooks -------------------------------------------------------------------------

        // ZDO.Reset is deliberately not hooked: every ZDOPool.Release site is covered by the hooks
        // below, and Reset also runs on millions of save clones carrying the live ZDO's uid.

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

        // The network path writes m_prefab directly. The no-change case is one int compare.
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
            if (!Hooks.Healthy) { return true; }

            // An iteration already in flight finishes under vanilla.
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

                found = VanillaScratch;
            }

            for (int i = 0; i < found.Count; i++) { zdos.Add(found[i]); }

            // Vanilla's completion filters the caller's entire accumulated list.
            zdos.RemoveAll(InvalidZdo);

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

            // Should find nothing, since every path out of m_objectsByID maintains the index;
            // cleaning up anyway keeps a missed path from growing the index for the session.
            // Deferred because Untrack mutates the bucket being iterated.
            for (int i = 0; i < StaleScratch.Count; i++) { Untrack(StaleScratch[i]); }
            StaleScratch.Clear();
        }

        // Vanilla's drain, read-only, for the verify path.
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
    }
}
