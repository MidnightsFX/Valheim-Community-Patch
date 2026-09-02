using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Disconnect ZDO Sweep: the sweep for objects a departing player left ownerless visits
    // only that player's own objects instead of every ZDO in the world.
    //
    // ZDOMan.RemoveOrphanNonPersistentZDOS runs on every disconnect and walks m_objectsByID in
    // full, dereferencing every ZDO to test Persistent and HasOwner. On a large world that is
    // millions of scattered heap reads to find a few dozen objects: a main-thread freeze on every
    // logout that grows with the size of the world.
    //
    // Non-persistent ZDOs are bucketed by owner as ownership changes (ZDO.SetOwnerInternal is the
    // single choke point; the Persistent setter covers the flag flipping either way;
    // HandleDestroyedZDO removes; ZDOMan.Load rebuilds; ShutDown clears), so the sweep visits
    // only the buckets whose owner is no longer connected. Maintenance is gated on RunMode.IsServer
    // because the hooks are hot and a joined client never runs the sweep; the role is fixed for a
    // session. The sweep stands down to vanilla if any hook failed to attach. ZDO.Reset is
    // deliberately not hooked: every ZDOPool.Release site is already covered, and Reset also runs
    // on millions of save clones carrying the live ZDO's uid.
    //
    // Server: the sweep is only reached behind vanilla's IsServer gate.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class OrphanZdoIndexPatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Orphan Index",
                false,
                "Diagnostic. Runs both the indexed sweep and vanilla's full scan on every " +
                "disconnect, acts on vanilla's result, and logs any disagreement. Costs the full " +
                "scan this fix exists to avoid, so leave it off unless you are validating the index.",
                advanced: true);
        }

        // ZDOID -> the bucket it is filed under. Authoritative for removal, because by the time
        // HandleDestroyedZDO's postfix runs the ZDO has been reset and pooled.
        private static readonly Dictionary<ZDOID, long> OwnerOf = new Dictionary<ZDOID, long>();
        private static readonly Dictionary<long, HashSet<ZDOID>> ByOwner = new Dictionary<long, HashSet<ZDOID>>();
        private static readonly HashSet<ZDOID> Unowned = new HashSet<ZDOID>();

        private static readonly HashSet<long> ConnectedScratch = new HashSet<long>();
        private static readonly List<ZDO> OrphanScratch = new List<ZDO>();
        private static readonly List<ZDO> VanillaScratch = new List<ZDO>();
        private static readonly List<ZDOID> StaleScratch = new List<ZDOID>();

        private const long NoOwner = 0L;

        // A missing hook means the index stops tracking a whole class of change, and acting on
        // it would destroy live objects or leak dead ones.
        private static readonly HookHealth Hooks = new HookHealth(
            "Orphan index",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.SetOwnerInternal)), typeof(SetOwnerInternalHook))
               && PatchHelper.HasHook(AccessTools.DeclaredPropertySetter(typeof(ZDO), nameof(ZDO.Persistent)), typeof(PersistentSetterHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO)), typeof(HandleDestroyedZdoHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.Load)), typeof(ZdoManLoadHook)));

        // ---- index maintenance -------------------------------------------------------------

        private static void Track(ZDOID uid, long owner) {
            if (!RunMode.IsServer) { return; }
            if (uid == ZDOID.None) { return; }

            if (OwnerOf.TryGetValue(uid, out long existing)) {
                if (existing == owner) { return; }
                Untrack(uid);
            }

            OwnerOf[uid] = owner;

            if (owner == NoOwner) {
                Unowned.Add(uid);
                return;
            }

            if (!ByOwner.TryGetValue(owner, out HashSet<ZDOID> bucket)) {
                bucket = new HashSet<ZDOID>();
                ByOwner.Add(owner, bucket);
            }

            bucket.Add(uid);
        }

        private static void Untrack(ZDOID uid) {
            if (!RunMode.IsServer) { return; }
            if (!OwnerOf.TryGetValue(uid, out long owner)) { return; }

            OwnerOf.Remove(uid);

            if (owner == NoOwner) {
                Unowned.Remove(uid);
                return;
            }

            if (ByOwner.TryGetValue(owner, out HashSet<ZDOID> bucket)) {
                bucket.Remove(uid);

                // Buckets are keyed by peer, so empties would accumulate one per player ever seen.
                if (bucket.Count == 0) { ByOwner.Remove(owner); }
            }
        }

        private static void ClearIndex() {
            OwnerOf.Clear();
            ByOwner.Clear();
            Unowned.Clear();
        }

        // Full rebuild from live state, only at world load where an O(world) pass is already
        // unavoidable.
        private static void RebuildIndex(ZDOMan zdoMan) {
            ClearIndex();

            foreach (KeyValuePair<ZDOID, ZDO> pair in zdoMan.m_objectsByID) {
                ZDO zdo = pair.Value;
                if (zdo.Persistent) { continue; }

                Track(zdo.m_uid, zdo.HasOwner() ? zdo.GetOwner() : NoOwner);
            }
        }

        // ---- hooks -------------------------------------------------------------------------

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal))]
        internal static class SetOwnerInternalHook {
            [HarmonyPostfix]
            static void Postfix(ZDO __instance, long uid) {
                if (__instance.Persistent) { return; }

                Track(__instance.m_uid, uid);
            }
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Persistent), MethodType.Setter)]
        internal static class PersistentSetterHook {
            [HarmonyPostfix]
            static void Postfix(ZDO __instance) {
                if (__instance.Persistent) {
                    Untrack(__instance.m_uid);
                } else {
                    Track(__instance.m_uid, __instance.HasOwner() ? __instance.GetOwner() : NoOwner);
                }
            }
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO))]
        internal static class HandleDestroyedZdoHook {
            [HarmonyPostfix]
            static void Postfix(ZDOID uid) => Untrack(uid);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Load))]
        internal static class ZdoManLoadHook {
            [HarmonyPostfix]
            static void Postfix(ZDOMan __instance) => RebuildIndex(__instance);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.ShutDown))]
        internal static class ZdoManShutDownHook {
            [HarmonyPostfix]
            static void Postfix() => ClearIndex();
        }

        // ---- the sweep ---------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZDOMan.RemoveOrphanNonPersistentZDOS))]
        private static bool RemoveOrphanNonPersistentZDOSPrefix(ZDOMan __instance) {
            if (!Hooks.Healthy) { return true; }

            bool verify = Verify != null && Verify.Value;

            ConnectedScratch.Clear();
            ConnectedScratch.Add(__instance.m_sessionID);

            List<ZDOMan.ZDOPeer> peers = __instance.m_peers;
            for (int i = 0; i < peers.Count; i++) {
                ConnectedScratch.Add(peers[i].m_peer.m_uid);
            }

            // Materialised before any destroying starts, because SetOwner mutates these buckets.
            OrphanScratch.Clear();
            CollectIndexed(__instance);

            if (verify) {
                VanillaScratch.Clear();
                CollectFullScan(__instance);
                ReportDivergence();

                Destroy(__instance, VanillaScratch);
                VanillaScratch.Clear();
                OrphanScratch.Clear();
                return false;
            }

            Destroy(__instance, OrphanScratch);
            OrphanScratch.Clear();
            return false;
        }

        private static void CollectIndexed(ZDOMan zdoMan) {
            StaleScratch.Clear();

            foreach (ZDOID uid in Unowned) {
                ZDO zdo = zdoMan.GetZDO(uid);
                if (zdo == null) { StaleScratch.Add(uid); } else { OrphanScratch.Add(zdo); }
            }

            foreach (KeyValuePair<long, HashSet<ZDOID>> bucket in ByOwner) {
                if (ConnectedScratch.Contains(bucket.Key)) { continue; }

                foreach (ZDOID uid in bucket.Value) {
                    ZDO zdo = zdoMan.GetZDO(uid);
                    if (zdo == null) { StaleScratch.Add(uid); } else { OrphanScratch.Add(zdo); }
                }
            }

            // Should find nothing, since every path out of m_objectsByID untracks; cleaning up
            // anyway keeps a missed path from growing the index for the life of the server.
            for (int i = 0; i < StaleScratch.Count; i++) { Untrack(StaleScratch[i]); }
            StaleScratch.Clear();
        }

        // Vanilla's predicate, read-only, for the verify path.
        private static void CollectFullScan(ZDOMan zdoMan) {
            foreach (KeyValuePair<ZDOID, ZDO> pair in zdoMan.m_objectsByID) {
                ZDO zdo = pair.Value;
                if (zdo.Persistent) { continue; }
                if (zdo.HasOwner() && ConnectedScratch.Contains(zdo.GetOwner())) { continue; }

                VanillaScratch.Add(zdo);
            }
        }

        private static void ReportDivergence() {
            HashSet<ZDOID> indexed = new HashSet<ZDOID>();
            for (int i = 0; i < OrphanScratch.Count; i++) { indexed.Add(OrphanScratch[i].m_uid); }

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
                    $"Orphan index verify: agreed on {VanillaScratch.Count} orphan(s) out of " +
                    $"{OwnerOf.Count} tracked non-persistent ZDO(s).");
                return;
            }

            Logger.LogError(
                $"Orphan index verify: DIVERGED. The full scan found {missed} orphan(s) the index " +
                $"missed (first {firstMissed}), and the index claimed {extra} the full scan did not " +
                $"(first {firstExtra}). Vanilla's result was used. Please report this - leave " +
                "'Verify Orphan Index' on until it is understood, since the verify pass acts on " +
                "vanilla's answer.");
        }

        private static void Destroy(ZDOMan zdoMan, List<ZDO> orphans) {
            for (int i = 0; i < orphans.Count; i++) {
                ZDO zdo = orphans[i];

                // Vanilla's log line, old owner and all.
                ZLog.Log("Destroying abandoned non persistent zdo " + zdo.m_uid.ToString()
                         + " owner " + zdo.GetOwner().ToString());
                zdo.SetOwner(zdoMan.m_sessionID);
                zdoMan.DestroyZDO(zdo);
            }
        }
    }
}
