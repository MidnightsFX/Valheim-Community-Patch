using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: ZDOMan.RemoveOrphanNonPersistentZDOS (ZDOMan.cs:895) walks *every* ZDO in the
    // world to find the handful left ownerless by a disconnect. It runs on every disconnect, from
    // ZNet.Disconnect -> ClearPlayerData (ZNet.cs:774) -> ZDOMan.RemovePeer (:389):
    //
    //     foreach (KeyValuePair<ZDOID, ZDO> kv in this.m_objectsByID)
    //       if (!kv.Value.Persistent && (!kv.Value.HasOwner() || !IsPeerConnected(kv.Value.GetOwner())))
    //         ... DestroyZDO(kv.Value);
    //
    // Persistent and HasOwner are cheap bit tests on m_dataFlags, but reaching them dereferences a
    // separate heap object once per entry. On a 5.2 M-ZDO world that is 5.2 M pointer chases across
    // a multi-gigabyte heap - essentially all cache misses - plus a linear scan of every peer and a
    // dictionary lookup for each candidate's owner. Hundreds of milliseconds of frozen main thread,
    // every time somebody logs out, to find a few dozen objects.
    //
    // Fix: keep the answer instead of recomputing it. Non-persistent ZDOs are bucketed by owner as
    // ownership changes, so the sweep visits only the buckets whose owner is gone. O(orphans)
    // instead of O(world).
    //
    // The index is maintained regardless of the config toggle, even when this fix is switched off. If
    // maintenance were behind the toggle, switching it on mid-session would consult an index that had
    // missed every change before the switch, and orphans would leak forever. Only the sweep reads the
    // toggle; maintenance is a few hash operations per ownership change.
    //
    // It is gated on the network role, which is a different thing: the sweep is only ever reached on
    // a server (ZNet.Disconnect -> ClearPlayerData -> ZDOMan.RemovePeer, itself behind IsServer), so
    // on a joined client these hooks would feed an index nothing ever reads. Unlike the toggle, the
    // role is fixed for the whole session, so there is no mid-session switch to miss.
    //
    // Correctness is the whole risk here: a stale index either leaks non-persistent ZDOs or destroys
    // live ones. Two things guard it. The hooks are checked at first use and the whole fix stands
    // down to vanilla if any of them failed to attach - this mod patches each class independently,
    // so a partial failure is possible and would otherwise be silent. And "Verify Orphan Index"
    // runs both the index and vanilla's full scan, acts on vanilla's answer, and logs any
    // divergence, so the index can be proven on a live server before it is trusted.
    //
    // Server. Unlike the other server-side fixes this one does need a runtime role check as well:
    // the sweep is behind vanilla's own IsServer gate, but the index *maintenance* hooks below sit
    // on ZDO.SetOwnerInternal and ZDO.Persistent, which fire on a joined client too and are hot.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class OrphanZdoIndexPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(OrphanZdoIndexPatch),
                ValConfig.SectionPerformance,
                "Fix Disconnect ZDO Sweep",
                true,
                "Replaces the whole-world scan the server runs on every disconnect to clean up a " +
                "departing player's temporary objects with an owner-indexed lookup. Vanilla walks " +
                "every ZDO in the world to find a few dozen, so on a large world every logout is a " +
                "main-thread freeze that grows with the size of the world.");

            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Verify Orphan Index",
                false,
                "Diagnostic. Runs both the indexed sweep and vanilla's full scan on every " +
                "disconnect, acts on vanilla's result, and logs any disagreement. Costs the full " +
                "scan this fix exists to avoid, so leave it off unless you are validating the index.",
                advanced: true);
        }

        // ZDOID -> the bucket that ZDO is filed under. Authoritative: removal reads this rather
        // than live state, because by the time HandleDestroyedZDO's postfix runs the ZDO has
        // already been reset and pooled and its owner is unrecoverable.
        private static readonly Dictionary<ZDOID, long> OwnerOf = new Dictionary<ZDOID, long>();
        private static readonly Dictionary<long, HashSet<ZDOID>> ByOwner = new Dictionary<long, HashSet<ZDOID>>();
        private static readonly HashSet<ZDOID> Unowned = new HashSet<ZDOID>();

        private static readonly HashSet<long> ConnectedScratch = new HashSet<long>();
        private static readonly List<ZDO> OrphanScratch = new List<ZDO>();
        private static readonly List<ZDO> VanillaScratch = new List<ZDO>();
        private static readonly List<ZDOID> StaleScratch = new List<ZDOID>();

        // Hooks are verified once, lazily: this mod applies each patch class on its own, so some
        // of ours may be live while others are not, and Awake is too early to ask Harmony.
        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        private const long NoOwner = 0L;

        // ---- index maintenance -------------------------------------------------------------

        // RunMode.IsServer is the shared helper: it caches the role against the ZNet instance that
        // answered, so hosting a world and then joining someone else's in the same process
        // re-resolves on its own without this patch having to reset anything on shutdown.
        private static void Track(ZDOID uid, long owner) {
            if (!RunMode.IsServer) { return; }

            // Every load path sets m_uid before Persistent (ZDO.Load:911, ZDOMan.Load:232) and
            // Deserialize only ever runs on a ZDO that already has one, so this should not fire.
            // It is here because the cost is one comparison and the failure it prevents - a
            // phantom None entry that the sweep would try to destroy every disconnect - is silent.
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

                // Buckets are keyed by peer uid, so leaving empties behind would accumulate one
                // per player who has ever connected. The sweep iterates the bucket list.
                if (bucket.Count == 0) { ByOwner.Remove(owner); }
            }
        }

        private static void ClearIndex() {
            OwnerOf.Clear();
            ByOwner.Clear();
            Unowned.Clear();
        }

        /// Full rebuild from live state. Only ever runs at world load, where an O(world) pass is
        /// already unavoidable, and it gives the index a known-good baseline rather than trusting
        /// that every hook fired correctly through the load.
        private static void RebuildIndex(ZDOMan zdoMan) {
            ClearIndex();

            foreach (KeyValuePair<ZDOID, ZDO> pair in zdoMan.m_objectsByID) {
                ZDO zdo = pair.Value;
                if (zdo.Persistent) { continue; }

                Track(zdo.m_uid, zdo.HasOwner() ? zdo.GetOwner() : NoOwner);
            }
        }

        // ---- hooks -------------------------------------------------------------------------

        // The single choke point for every ownership change: ZDO.SetOwner (:1037) and
        // ZDOMan.CreateNewZDO (:313) both route through it.
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal))]
        internal static class SetOwnerInternalHook {
            [HarmonyPostfix]
            static void Postfix(ZDO __instance, long uid) {
                if (__instance.Persistent) { return; }

                Track(__instance.m_uid, uid);
            }
        }

        // Covers all four callers: ZDO.Load (:798), the two deserialize paths (:613, :913) and
        // ZNetView.Awake. Clients can flip this at runtime over ZDOData, so it is not load-only.
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

        // Takes the uid by value, so it still works after the ZDO itself has been pooled.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO))]
        internal static class HandleDestroyedZdoHook {
            [HarmonyPostfix]
            static void Postfix(ZDOID uid) => Untrack(uid);
        }

        // No hook on ZDO.Reset, deliberately. It looks like the natural place to catch a ZDO going
        // back to the pool, but every ZDOPool.Release call site is already covered: HandleDestroyedZDO
        // (ZDOMan.cs:550) by the hook above, and the bulk releases in ZDOMan.Load (:215) and
        // WarnAndRemoveBrokenZDOs (:199) by the Load rebuild below, and ShutDown (:87) by its clear.
        //
        // Hooking it would also be actively expensive and slightly unsafe. Reset runs once per ZDO
        // per vanilla world save - GetSaveClone clones every persistent ZDO and ZDOMan.cs:123 resets
        // them all afterwards - so on this world that is 5.2 M extra trampolines and dictionary
        // probes per save, to catch nothing. And ZDO.Clone is a MemberwiseClone (ZDO.cs:73), so each
        // clone carries the *live* ZDO's m_uid: untracking on it would reach through to the original.
        // Harmless only because GetSaveClone filters to persistent ZDOs and those are never indexed,
        // which is a vanilla detail this fix should not be resting on.

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Load))]
        internal static class ZdoManLoadHook {
            [HarmonyPostfix]
            static void Postfix(ZDOMan __instance) => RebuildIndex(__instance);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.ShutDown))]
        internal static class ZdoManShutDownHook {
            // Only the index needs clearing here. The cached network role used to be reset alongside
            // it, but RunMode keys its cache on the ZNet instance itself, so the next session
            // re-resolves whether or not this hook runs - which matters, because ZNet.StopAll skips
            // ZDOMan.ShutDown entirely when it is suspending rather than quitting (ZNet.cs:397).
            [HarmonyPostfix]
            static void Postfix() => ClearIndex();
        }

        // ---- the sweep ---------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZDOMan.RemoveOrphanNonPersistentZDOS))]
        private static bool RemoveOrphanNonPersistentZDOSPrefix(ZDOMan __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }
            if (!HooksHealthy()) { return true; }

            bool verify = Verify != null && Verify.Value;

            ConnectedScratch.Clear();
            ConnectedScratch.Add(__instance.m_sessionID);

            List<ZDOMan.ZDOPeer> peers = __instance.m_peers;
            for (int i = 0; i < peers.Count; i++) {
                ConnectedScratch.Add(peers[i].m_peer.m_uid);
            }

            // Materialised before any destroying starts: SetOwner below mutates the very buckets
            // this is reading.
            OrphanScratch.Clear();
            CollectIndexed(__instance);

            if (verify) {
                VanillaScratch.Clear();
                CollectFullScan(__instance);
                ReportDivergence();

                // Act on vanilla's answer while verifying, so a bad index cannot do damage during
                // the very run that is meant to expose it.
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

            // A ZDO leaves m_objectsByID only through HandleDestroyedZDO, Load or ShutDown, all of
            // which untrack it, so this should find nothing. Cleaning up anyway keeps a missed
            // path from turning into an index that grows for the life of the server - deferred,
            // because Untrack mutates the very sets iterated above.
            for (int i = 0; i < StaleScratch.Count; i++) { Untrack(StaleScratch[i]); }
            StaleScratch.Clear();
        }

        /// Vanilla's predicate, read-only, for the verify pass.
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
                "'Fix Disconnect ZDO Sweep' off until it is understood.");
        }

        private static void Destroy(ZDOMan zdoMan, List<ZDO> orphans) {
            for (int i = 0; i < orphans.Count; i++) {
                ZDO zdo = orphans[i];

                // Vanilla logs the *old* owner, before seizing ownership. Kept verbatim, spam and
                // all - this fix is about the scan, not about what vanilla chooses to log - down to
                // the explicit ToString calls, which keep the struct out of String.Concat(object).
                ZLog.Log("Destroying abandoned non persistent zdo " + zdo.m_uid.ToString()
                         + " owner " + zdo.GetOwner().ToString());
                zdo.SetOwner(zdoMan.m_sessionID);
                zdoMan.DestroyZDO(zdo);
            }
        }

        // ---- hook health -------------------------------------------------------------------

        /// True only when every hook the index depends on is actually attached by us. A missing
        /// one means the index silently stops tracking a whole class of change, and acting on it
        /// would destroy live objects or leak dead ones - so the answer gates the fix entirely.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            string missing = null;
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.SetOwnerInternal)),
                           "ZDO.SetOwnerInternal", ref missing);
            NoteIfUnhooked(AccessTools.DeclaredPropertySetter(typeof(ZDO), nameof(ZDO.Persistent)),
                           "ZDO.set_Persistent", ref missing);
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO)),
                           "ZDOMan.HandleDestroyedZDO", ref missing);
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.Load)),
                           "ZDOMan.Load", ref missing);

            _hooksHealthy = missing == null;

            if (!_hooksHealthy) {
                Logger.LogError(
                    $"Orphan index: the hook on {missing} is not attached, so the index cannot be " +
                    "trusted and the disconnect sweep has fallen back to vanilla's whole-world scan " +
                    "for this session. This usually means a Valheim update changed that method - " +
                    "look for the patch failure logged at startup.");
            }

            return _hooksHealthy;
        }

        /// Appends `label` to `missing` when the method is gone or carries no patch of ours.
        /// Checks for *our* patch specifically: another mod patching the same method proves
        /// nothing about whether we did.
        private static void NoteIfUnhooked(MethodBase target, string label, ref string missing) {
            bool ours = false;
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);

            if (info != null) {
                foreach (Patch patch in info.Postfixes) {
                    if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                    ours = true;
                    break;
                }

                if (!ours) {
                    foreach (Patch patch in info.Prefixes) {
                        if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                        ours = true;
                        break;
                    }
                }
            }

            if (ours) { return; }

            missing = missing == null ? label : missing + " and " + label;
        }
    }
}
