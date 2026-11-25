using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Prefab Query Scan: GetAllZDOsWithPrefabIterative scans every ZDO in the world, and mods call it every tick.
    // ZDO hooks keep an index by prefab hash, and a prefix on the query answers a fresh iteration from it in one call.
    // Prefab queries cost the number of matches instead of the size of the world.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class ZdoPrefabIndexPatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Prefab Index",
                false,
                "Diagnostic. Runs vanilla's whole-world scan beside the index on every prefab query, uses " +
                "vanilla's result and logs any disagreement; costs the scan 'Fix Prefab Query Scan' avoids.",
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

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ZdoPrefabIndexPatch));

        // Checked against the hook class, not merely "some patch of ours": OrphanZdoIndexPatch
        // also patches HandleDestroyedZDO and Load, and its presence proves nothing about this index.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(ZdoPrefabIndexPatch),
            "Prefab index",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.SetPrefab)), typeof(SetPrefabHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.Deserialize)), typeof(DeserializeHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO)), typeof(HandleDestroyedZdoHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.Load)), typeof(ZdoManLoadHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.LoadChunks)), typeof(ZdoManLoadChunksHook)));

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
        // unavoidable. Covers ZDO.Load's direct m_prefab writes, on both load paths.
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

        // The chunked save format loads a world without ever entering ZDOMan.Load, so without
        // this the index would stay empty for every world saved in that format.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.LoadChunks))]
        internal static class ZdoManLoadChunksHook {
            [HarmonyPostfix]
            private static void Postfix(ZDOMan __instance) => RebuildIndex(__instance);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.ShutDown))]
        internal static class ZdoManShutDownHook {
            [HarmonyPostfix]
            private static void Postfix() => ClearIndex();
        }

        // ---- the query ---------------------------------------------------------------------

        // Results come in bucket order rather than vanilla's sector order; callers treat the list
        // as a set.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZDOMan.GetAllZDOsWithPrefabIterative))]
        private static bool GetAllZDOsWithPrefabIterativePrefix(
            ZDOMan __instance, string prefab, List<ZDO> zdos, ref int index, ref bool __result) {
            if (ApiSwitch.Off || !Hooks.Healthy) { return true; }

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

            // Where a finished vanilla iteration leaves the cursor.
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

            foreach (List<ZDO> list in zdoMan.m_portalObjects.Values) {
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
                "report this - leave 'Verify Prefab Index' on until it is understood, so every " +
                "query uses the game's own scan.");
        }
    }
}
