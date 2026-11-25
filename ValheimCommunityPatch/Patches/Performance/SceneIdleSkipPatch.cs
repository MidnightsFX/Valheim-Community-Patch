using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Idle Scene Sweep: ZNetScene.CreateDestroyObjects walks every loaded object 30 times a second, changed or not.
    // Prefixes CreateDestroyObjects to skip the pass while sector-store and Created hooks show nothing has changed.
    // Big bases stop spending frame time re-deriving the same answer; one full pass a second still runs.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class SceneIdleSkipPatch {
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Scene Idle Skip",
                false,
                "Diagnostic. Runs every object pass and logs any the idle skip would have skipped that did " +
                "real work; costs the passes the fix saves.",
                advanced: true);
        }

        // One full pass per second at the 30 Hz pass rate, for anything the hooks do not track.
        private const int HygieneInterval = 30;

        private static long _ringHash;
        private static long _createdVersion;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(SceneIdleSkipPatch));

        // The streamed ring the hash filter tests against; valid from the first pass on, and
        // aligned to the pass before it runs so a mid-pass change always registers.
        private static bool _ringValid;
        private static int _ringMinX;
        private static int _ringMaxX;
        private static int _ringMinY;
        private static int _ringMaxY;
        private static bool _ringAliased;

        private static ZNetScene _snapshotScene;
        private static Vector2s _snapshotZone;
        private static long _snapshotRingHash;
        private static long _snapshotCreatedVersion;
        private static SimulationDistance _snapshotSimulationDistance;
        private static bool _idle;
        private static int _skipsSinceFullPass;

        private static Vector2s _zoneAtPrefix;
        private static long _ringHashAtPrefix;
        private static long _createdAtPrefix;
        private static bool _ranFullPass;
        private static bool _wouldSkip;

        // A missing hook means a whole class of scene change goes uncounted and the skip would
        // hide real work, so the answer gates the fix entirely.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(SceneIdleSkipPatch),
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

        // AddToSector and RemoveFromSector are the only writers of the sector stores. The XOR makes an in-ring move,
        // or the sector bounce every ZDO-data packet does, cancel out, while an arrival, departure, spawn or despawn
        // changes the hash. The Created setter counts the changes that bypass the sector stores.

        // AddToSector receives the new sector and RemoveFromSector the old one, so each firing is
        // filtered by the sector it actually touched. Both hand over a packed sector index and the
        // ring test is in zone coordinates. Index 0 is not one zone but every zone outside the
        // +/-256 grid, so it counts exactly when the ring reaches past the grid, which a dedicated
        // server's always does.
        private static void OnSectorTouched(ZDO zdo, ZoneSystem.SectorIndex sectorIndex) {
            if (_ringValid) {
                if (sectorIndex == ZoneSystem.SectorZero) {
                    if (!_ringAliased) { return; }
                } else {
                    Vector2s sector = ZoneSystem.IndexToSector(sectorIndex.Sector);
                    if (sector.x < _ringMinX || sector.x > _ringMaxX || sector.y < _ringMinY || sector.y > _ringMaxY) {
                        return;
                    }
                }
            }

            _ringHash ^= Scatter(zdo.m_uid);
        }

        // The XOR of raw consecutive ids cancels (4 ^ 5 ^ 6 ^ 7 == 0), and the objects of one
        // generated zone are numbered consecutively, so a batch of them arriving between two
        // passes could leave the hash unchanged. Each id is spread over 64 bits first, with the
        // SplitMix64 finalizer.
        private static long Scatter(ZDOID id) {
            unchecked {
                ulong x = (ulong)id.UserID * 0x9E3779B97F4A7C15UL + id.ID;
                x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
                x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
                return (long)(x ^ (x >> 31));
            }
        }

        [HarmonyPatch(typeof(ZDOMan), "AddToSector")]
        internal static class AddToSectorHook {
            [HarmonyPostfix]
            private static void Postfix(ZDO zdo, ZoneSystem.SectorIndex sectorIndex) => OnSectorTouched(zdo, sectorIndex);
        }

        [HarmonyPatch(typeof(ZDOMan), "RemoveFromSector")]
        internal static class RemoveFromSectorHook {
            [HarmonyPostfix]
            private static void Postfix(ZDO zdo, ZoneSystem.SectorIndex sectorIndex) => OnSectorTouched(zdo, sectorIndex);
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Created), MethodType.Setter)]
        internal static class CreatedSetterHook {
            [HarmonyPostfix]
            private static void Postfix() => _createdVersion++;
        }

        // ---- the skip ------------------------------------------------------------------------

        // Keep this a bool prefix that skips or runs the whole pass, so RemoveObjectsNrePatch and the other
        // sweep prefixes see a full pass exactly as vanilla would.
        [HarmonyPrefix]
        [HarmonyPatch("CreateDestroyObjects")]
        private static bool CreateDestroyObjectsPrefix(ZNetScene __instance) {
            _ranFullPass = false;
            if (ApiSwitch.Off || !Hooks.Healthy) { return true; }

            ZoneSystem zoneSystem = ZoneSystem.instance;
            ZNet znet = ZNet.instance;
            if (ReferenceEquals(zoneSystem, null) || ReferenceEquals(znet, null)) { return true; }

            Vector2s zone = ZoneSystem.GetZone(znet.GetReferencePosition());
            SimulationDistance simulationDistance = znet.GetSyncedSimulationDistance();
            bool canSkip = _idle
                && ReferenceEquals(__instance, _snapshotScene)
                && zone == _snapshotZone
                && _ringHash == _snapshotRingHash
                && _createdVersion == _snapshotCreatedVersion
                && simulationDistance.Equals(_snapshotSimulationDistance)
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

            // A square of the full simulation radius. The streamed set is the inscribed disc, so
            // this over-covers, which is the safe direction: no in-ring change goes uncounted.
            int span = simulationDistance.TotalSimulationDistance;
            _ringMinX = zone.x - span;
            _ringMaxX = zone.x + span;
            _ringMinY = zone.y - span;
            _ringMaxY = zone.y + span;

            // A square that reaches past the grid, or onto the grid corner index 0 unpacks to,
            // has a corner the game files under index 0.
            _ringAliased = ZoneSystem.SectorToIndex(_ringMinX, _ringMinY) == ZoneSystem.SectorZero
                || ZoneSystem.SectorToIndex(_ringMinX, _ringMaxY) == ZoneSystem.SectorZero
                || ZoneSystem.SectorToIndex(_ringMaxX, _ringMinY) == ZoneSystem.SectorZero
                || ZoneSystem.SectorToIndex(_ringMaxX, _ringMaxY) == ZoneSystem.SectorZero;
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
            // world streams in was measured at whole seconds of login time, so only look when
            // the answer can matter, and only as far as the first one unless verify reports it.
            if (!versionQuiet && !predictedSkip) { return; }

            bool areaLoaded = ZoneSystem.instance != null && ZoneSystem.instance.IsActiveAreaLoaded();
            if (!areaLoaded && !predictedSkip) { return; }

            // The lists as FindSectorObjects filled them for this pass, never the spawn queue
            // in m_tempCurrentObjects2: SpawnQueueCachePatch keeps that across passes, so between
            // its rebuilds it does not hold a ZDO that has arrived since.
            int limit = predictedSkip ? int.MaxValue : 1;
            int pendingNear = CountPending(__instance, __instance.m_tempCurrentObjects, limit);
            int pendingDistant = CountPending(__instance, __instance.m_tempCurrentDistantObjects, limit);

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
            _snapshotSimulationDistance = ZNet.instance.GetSyncedSimulationDistance();
        }

        private static void LogVerifySummary(string kind) {
            Logger.LogInfo(
                $"Idle skip verify ({kind}): would have skipped {_verifyWouldSkip} of " +
                $"{_verifyPasses} passes, {_verifyDivergences} divergence(s). A large skip share " +
                "with zero divergences means the fix will engage in this area once Verify is off.");
        }

        // Uncreated candidates whose prefab resolves, counted up to limit. A permanently
        // unresolvable ZDO (a removed mod's object, which vanilla re-enumerates forever) must not
        // hold the scene out of idle.
        private static int CountPending(ZNetScene scene, List<ZDO> zdos, int limit) {
            int pending = 0;
            for (int i = 0; i < zdos.Count && pending < limit; i++) {
                ZDO zdo = zdos[i];
                if (zdo.Created) { continue; }
                if (scene.GetPrefab(zdo.GetPrefab()) == null) { continue; }

                pending++;
            }

            return pending;
        }
    }
}
