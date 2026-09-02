using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: ZNetScene.CreateDestroyObjects runs 30 times a second and is O(every loaded
    // object) end to end - it rebuilds the near and distant ZDO lists from the sector stores,
    // enumerates every near ZDO checking its Created flag (and sorts the candidates), and then
    // earmarks every ZDO and walks every live instance to find ones to remove. None of that work
    // depends on anything having changed. In a 60-90k-instance base the pass was measured at 17%
    // of ALL frame time - ~97 seconds of a 10-minute window - almost entirely re-deriving the
    // same answer as 33 ms ago.
    //
    // Fix: prove "nothing changed" cheaply and skip the pass. Three unconditional hooks sit at
    // the choke points every relevant change must cross:
    //
    //  - ZDOMan.AddToSector / RemoveFromSector - verified to be the only mutators of both sector
    //    stores (including the outside-sector map): every ZDO creation on any path (local Awake,
    //    network RPC_ZDOData, ghost-zone generation), every destroy, every sector crossing
    //    (ZDO.SetSector early-outs on same sector, so intra-sector movement costs nothing), and
    //    InvalidateSector all pass through them. These maintain a RING-MEMBERSHIP HASH rather
    //    than a raw counter: the ZDO's id is XORed into a running hash only when the touched
    //    sector lies inside the streamed ring (reference zone +/- activeArea+activeDistantArea -
    //    exactly the span FindSectorObjects walks, ZDOMan.cs:701-727). What the pass consumes is
    //    the ring's *union*, and the XOR algebra tracks precisely that: an animal crossing
    //    between two in-ring sectors XORs twice and cancels; the invalidate-sector bounce every
    //    ZDO-data packet performs (out to a sentinel and back within one handler, ZDOMan.cs:634)
    //    is one filtered pair and one cancelling pair; a genuine arrival, departure, spawn or
    //    despawn changes the hash. A raw counter - the first version of this fix - was measured
    //    engaging on only ~15% of passes in a lively multiplayer base because of exactly that
    //    packet-driven bounce noise. Hash collisions (a changed set XORing back to its old value)
    //    would need pairwise id-hash coincidence and are bounded by the hygiene pass regardless.
    //    Until the first idle snapshot caches ring bounds, the hooks hash everything -
    //    conservative, never wrong.
    //  - ZDO.set_Created - a plain counter for the sectorless paths: ZNetScene.AddInstance and,
    //    critically, ResetZDO from ZNetScene.Destroy on a non-owned ZDO, where vanilla re-creates
    //    the object on the next pass and so must we. Save clones reset their flags through direct
    //    field writes on the clone, so saving neither bumps nor misses anything. ZDOMan.Load
    //    hashes once per loaded ZDO - a fraction of a second added to a world load that already
    //    takes tens of seconds.
    //
    // The pass is skipped only when: the same ZNetScene instance (instance identity is the
    // session invalidation, the RunMode pattern), the reference zone is unchanged, the version is
    // unchanged, the active-area sizes are unchanged, the previous full pass ended with nothing
    // pending, and fewer than 30 consecutive skips have happened - one full vanilla-shape pass
    // per second runs regardless, as hygiene for the untracked residue (a mod destroying a
    // GameObject directly, direct sector-store mutation, late prefab registration), each of which
    // is thereby at most ~1 second stale and most of which vanilla itself mishandles identically.
    //
    // "Nothing pending" after a full pass means: zero near candidates still uncreated whose
    // prefab actually resolves (the prefab filter is what lets a world with orphaned modded ZDOs
    // - which vanilla clients re-enumerate forever - still reach idle), zero such distant
    // candidates, and ZoneSystem.IsActiveAreaLoaded (CreateObjectsSorted early-returns without
    // touching its candidate list when zones are still loading, so the list is stale then).
    // The version is also compared across the pass itself, so the pass's own creations and
    // removals keep the next pass full - idle converges one pass after true quiescence.
    //
    // Composition contract with RemoveObjectsNrePatch (Patches/Correctness): this class must stay
    // a prefix on CreateDestroyObjects that returns true or false, never a replacement. A skipped
    // pass simply never invokes RemoveObjects; a full pass reaches that patch's prefix exactly as
    // vanilla would. Its orphan detection consequently defers by at most the hygiene interval.
    //
    // Known degradation, not a bug: on a busy server, ghost-zone generation for exploring peers
    // bumps the global version constantly, so the pass rarely idles there - graceful fallback to
    // vanilla behaviour. Scoping the version to the local active ring is a possible v2.
    //
    // Both: a dedicated server runs the same pass over its origin-area set.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class SceneIdleSkipPatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Scene Idle Skip",
                false,
                "Diagnostic. Predicts whether each object pass could be skipped, always runs it " +
                "anyway, and logs whenever a pass predicted skippable did real work. Costs the " +
                "passes this fix exists to avoid, so leave it off unless you are validating the " +
                "skip conditions.",
                advanced: true);
        }

        // One full pass per second at the 30 Hz pass rate.
        private const int HygieneInterval = 30;

        private static long _ringHash;
        private static long _createdVersion;

        // The streamed ring the hash filter tests against; valid only from the first idle
        // snapshot on, and always refreshed before a skip can rely on it (arming and skipping
        // both require the reference zone to match the snapshot).
        private static bool _ringValid;
        private static int _ringMinX;
        private static int _ringMaxX;
        private static int _ringMinY;
        private static int _ringMaxY;

        private static ZNetScene _snapshotScene;
        private static Vector2i _snapshotZone;
        private static long _snapshotRingHash;
        private static long _snapshotCreatedVersion;
        private static int _snapshotActiveArea;
        private static int _snapshotDistantArea;
        private static bool _idle;
        private static int _skipsSinceFullPass;

        private static Vector2i _zoneAtPrefix;
        private static long _ringHashAtPrefix;
        private static long _createdAtPrefix;
        private static bool _ranFullPass;
        private static bool _wouldSkip;

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        // Verify-mode engagement telemetry. "0 divergences" is only evidence if the skip
        // predicate actually armed during the session, so the verify reports how often it would
        // have skipped - the number that decides whether the fix engages in a given base at all.
        private const int VerifyReportInterval = 900; // ~30s of passes at 30 Hz
        private static bool _verifyActive;
        private static int _verifyPasses;
        private static int _verifyWouldSkip;
        private static int _verifyDivergences;
        private static int _passesSinceReport;

        // ---- change hooks (maintenance runs unconditionally) ---------------------------------

        // AddToSector receives the NEW sector, RemoveFromSector the OLD one (ZDO.SetSector,
        // ZDO.cs:198-204), so each firing is filtered by the sector it actually touched - which
        // is what makes in-ring crossings and invalidate bounces cancel while entries and exits
        // register.
        private static void OnSectorTouched(ZDO zdo, Vector2i sector) {
            if (_ringValid
                && (sector.x < _ringMinX || sector.x > _ringMaxX || sector.y < _ringMinY || sector.y > _ringMaxY)) {
                return;
            }

            _ringHash ^= (uint)zdo.m_uid.GetHashCode();
        }

        [HarmonyPatch(typeof(ZDOMan), "AddToSector")]
        internal static class AddToSectorHook {
            [HarmonyPostfix]
            private static void Postfix(ZDO zdo, Vector2i sector) => OnSectorTouched(zdo, sector);
        }

        [HarmonyPatch(typeof(ZDOMan), "RemoveFromSector")]
        internal static class RemoveFromSectorHook {
            [HarmonyPostfix]
            private static void Postfix(ZDO zdo, Vector2i sector) => OnSectorTouched(zdo, sector);
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Created), MethodType.Setter)]
        internal static class CreatedSetterHook {
            [HarmonyPostfix]
            private static void Postfix() => _createdVersion++;
        }

        // ---- the skip ------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch("CreateDestroyObjects")]
        private static bool CreateDestroyObjectsPrefix(ZNetScene __instance) {
            _ranFullPass = false;
            if (!HooksHealthy()) { return true; }

            ZoneSystem zoneSystem = ZoneSystem.instance;
            ZNet znet = ZNet.instance;
            if (ReferenceEquals(zoneSystem, null) || ReferenceEquals(znet, null)) { return true; }

            Vector2i zone = ZoneSystem.GetZone(znet.GetReferencePosition());
            bool canSkip = _idle
                && ReferenceEquals(__instance, _snapshotScene)
                && zone == _snapshotZone
                && _ringHash == _snapshotRingHash
                && _createdVersion == _snapshotCreatedVersion
                && zoneSystem.m_activeArea == _snapshotActiveArea
                && zoneSystem.m_activeDistantArea == _snapshotDistantArea
                && _skipsSinceFullPass < HygieneInterval;

            bool verify = Verify != null && Verify.Value;

            // Closing summary when Verify switches off, so a verify session always ends with its
            // engagement numbers even if nobody watched the periodic lines.
            if (_verifyActive && !verify) {
                _verifyActive = false;
                LogVerifySummary("final");
                _verifyPasses = 0;
                _verifyWouldSkip = 0;
                _verifyDivergences = 0;
                _passesSinceReport = 0;
            }
            _verifyActive = verify;

            if (canSkip && !verify) {
                _skipsSinceFullPass++;
                return false;
            }

            // Cleared before the pass and only re-armed by the postfix, so an exception mid-pass
            // (which suppresses the postfix) can never leave a stale armed skip.
            _idle = false;
            _wouldSkip = canSkip;
            _zoneAtPrefix = zone;

            // Align the hash filter's ring with THIS pass before it runs, so an in-ring change
            // arriving mid-pass is guaranteed to register and block arming - the filter is never
            // stale for any pass that could go on to skip.
            int span = zoneSystem.m_activeArea + zoneSystem.m_activeDistantArea;
            _ringMinX = zone.x - span;
            _ringMaxX = zone.x + span;
            _ringMinY = zone.y - span;
            _ringMaxY = zone.y + span;
            _ringValid = true;

            _ringHashAtPrefix = _ringHash;
            _createdAtPrefix = _createdVersion;
            _skipsSinceFullPass = 0;
            _ranFullPass = true;
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch("CreateDestroyObjects")]
        private static void CreateDestroyObjectsPostfix(ZNetScene __instance) {
            if (!_ranFullPass) { return; }
            _ranFullPass = false;

            // The pass's own creations and removals bumped the counters through the hooks, so a
            // pass that did anything fails this and the next pass runs full - conservative by
            // construction.
            bool versionQuiet = _ringHash == _ringHashAtPrefix && _createdVersion == _createdAtPrefix;
            bool predictedSkip = _wouldSkip;
            _wouldSkip = false;

            if (_verifyActive) {
                _verifyPasses++;
                if (predictedSkip) { _verifyWouldSkip++; }

                if (++_passesSinceReport >= VerifyReportInterval) {
                    _passesSinceReport = 0;
                    LogVerifySummary("periodic");
                }
            }

            // While the world streams in, the version is never quiet and the candidate list holds
            // tens of thousands of entries - counting them per pass just to conclude "not idle"
            // was measured at whole seconds of login time. Only count when the answer can matter:
            // the pass was quiet, or a predicted skip needs the numbers for its divergence report.
            if (!versionQuiet && !predictedSkip) { return; }

            int pendingNear = CountPending(__instance, __instance.m_tempCurrentObjects2);
            int pendingDistant = CountPending(__instance, __instance.m_tempCurrentDistantObjects);
            bool areaLoaded = ZoneSystem.instance != null && ZoneSystem.instance.IsActiveAreaLoaded();

            if (predictedSkip) {
                if (!versionQuiet || pendingNear > 0 || pendingDistant > 0 || __instance.m_tempRemoved.Count > 0) {
                    _verifyDivergences++;
                    Logger.LogError(
                        $"Scene idle skip verify: DIVERGED - a pass predicted skippable did work " +
                        $"(ring hash changed {(_ringHash != _ringHashAtPrefix)}, created delta " +
                        $"{_createdVersion - _createdAtPrefix}, pending near {pendingNear}, " +
                        $"pending distant {pendingDistant}, removed " +
                        $"{__instance.m_tempRemoved.Count}). Vanilla ran, so nothing was lost. " +
                        "Please report this - leave 'Verify Scene Idle Skip' on until it is " +
                        "understood, since verify mode always runs the full pass.");
                }
            }

            _idle = versionQuiet && pendingNear == 0 && pendingDistant == 0 && areaLoaded;
            if (!_idle) { return; }

            _snapshotScene = __instance;
            _snapshotZone = _zoneAtPrefix;
            _snapshotRingHash = _ringHash;
            _snapshotCreatedVersion = _createdVersion;
            _snapshotActiveArea = ZoneSystem.instance.m_activeArea;
            _snapshotDistantArea = ZoneSystem.instance.m_activeDistantArea;
        }

        private static void LogVerifySummary(string kind) {
            Logger.LogInfo(
                $"Idle skip verify ({kind}): would have skipped {_verifyWouldSkip} of " +
                $"{_verifyPasses} passes, {_verifyDivergences} divergence(s). A large skip share " +
                "with zero divergences means the fix will engage in this area once Verify is off.");
        }

        // Uncreated candidates whose prefab actually resolves. A permanently-unresolvable ZDO (a
        // removed mod's object, which a vanilla client re-enumerates forever without ever
        // creating) must not hold the scene out of idle; a prefab registered later is caught by
        // the hygiene pass.
        private static int CountPending(ZNetScene scene, List<ZDO> zdos) {
            int pending = 0;
            for (int i = 0; i < zdos.Count; i++) {
                ZDO zdo = zdos[i];
                if (zdo.Created) { continue; }
                if (scene.GetPrefab(zdo.GetPrefab()) == null) { continue; }

                pending++;
            }

            return pending;
        }

        // ---- hook health -----------------------------------------------------------------------

        /// A missing version hook means a whole class of scene change goes uncounted and the skip
        /// would hide real work, so the answer gates the fix entirely.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            _hooksHealthy =
                HasOurPostfix(AccessTools.DeclaredMethod(typeof(ZDOMan), "AddToSector"), typeof(AddToSectorHook))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(ZDOMan), "RemoveFromSector"), typeof(RemoveFromSectorHook))
                && HasOurPostfix(AccessTools.DeclaredPropertySetter(typeof(ZDO), nameof(ZDO.Created)), typeof(CreatedSetterHook));

            if (!_hooksHealthy) {
                Logger.LogError(
                    "Scene idle skip: a version hook is not attached, so unchanged passes cannot " +
                    "be proven unchanged and the object pass runs vanilla every tick for this " +
                    "session. This usually means a Valheim update changed those methods - look " +
                    "for the patch failure logged at startup.");
            }

            return _hooksHealthy;
        }

        private static bool HasOurPostfix(MethodBase target, System.Type hookType) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            foreach (Patch patch in info.Postfixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookType) { continue; }
                return true;
            }

            return false;
        }
    }
}
