using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Idle Scene Sweep: the 30 Hz object create/destroy pass is skipped when nothing that
    // could change its outcome has happened since the last one.
    //
    // ZNetScene.CreateDestroyObjects is O(every loaded object) end to end: it rebuilds the near
    // and distant ZDO lists from the sector stores, scans every near ZDO for its Created flag,
    // sorts the candidates, then earmarks every ZDO and walks every instance to find removals.
    // None of that depends on anything having changed, and in a big base it was measured at 17%
    // of all frame time re-deriving the same answer as 33 ms ago.
    //
    // Three unconditional hooks track change cheaply. ZDOMan.AddToSector and RemoveFromSector are
    // the only mutators of the sector stores; they XOR the ZDO's id into a ring-membership hash
    // whenever the touched sector lies inside the streamed ring, so an in-ring crossing or the
    // sector bounce every ZDO-data packet performs cancels out while a real arrival, departure,
    // spawn or despawn changes the hash. ZDO.set_Created is a plain counter for the sectorless
    // paths. A pass is skipped only when the scene, reference zone, ring hash, created counter
    // and ring sizes all match the snapshot taken after the last full pass, that pass ended with
    // nothing pending, and fewer than 30 skips have run since: one full pass per second stays as
    // hygiene for anything untracked. On a busy server, ghost-zone generation for peers keeps
    // the hash moving and the pass rarely idles, which is a graceful fallback to vanilla.
    //
    // Composition: this must stay a prefix on CreateDestroyObjects that returns true or false, so
    // RemoveObjectsNrePatch and the sweep prefixes below it see a full pass exactly as vanilla
    // would. Both: a dedicated server runs the same pass over its origin-area set.
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

        // The streamed ring the hash filter tests against; valid from the first pass on, and
        // aligned to the pass before it runs so a mid-pass change always registers.
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

        // A missing hook means a whole class of scene change goes uncounted and the skip would
        // hide real work, so the answer gates the fix entirely.
        private static readonly HookHealth Hooks = new HookHealth(
            "Scene idle skip",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), "AddToSector"), typeof(AddToSectorHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), "RemoveFromSector"), typeof(RemoveFromSectorHook))
               && PatchHelper.HasHook(AccessTools.DeclaredPropertySetter(typeof(ZDO), nameof(ZDO.Created)), typeof(CreatedSetterHook)));

        // Verify telemetry. "0 divergences" only means something if the skip predicate actually
        // armed, so the summary reports how often it would have skipped.
        private const int VerifyReportInterval = 900;
        private static bool _verifyActive;
        private static int _verifyPasses;
        private static int _verifyWouldSkip;
        private static int _verifyDivergences;
        private static int _passesSinceReport;

        // ---- change hooks (maintenance runs unconditionally) ---------------------------------

        // AddToSector receives the new sector and RemoveFromSector the old one, so each firing is
        // filtered by the sector it actually touched.
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
            if (!Hooks.Healthy) { return true; }

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

            // Cleared here and only re-armed by the postfix, so an exception mid-pass cannot
            // leave a stale armed skip.
            _idle = false;
            _wouldSkip = canSkip;
            _zoneAtPrefix = zone;

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

            // The pass's own creations and removals bumped the counters, so a pass that did
            // anything fails this and the next pass runs full.
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

            // Counting pending candidates over tens of thousands of entries per pass while the
            // world streams in was measured at whole seconds of login time, so only count when
            // the answer can matter.
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

        // Uncreated candidates whose prefab resolves. A permanently unresolvable ZDO (a removed
        // mod's object, which vanilla re-enumerates forever) must not hold the scene out of idle.
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
    }
}
