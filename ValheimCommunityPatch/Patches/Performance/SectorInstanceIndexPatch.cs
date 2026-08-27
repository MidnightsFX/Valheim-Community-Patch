using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: ZNetScene.HaveInstanceInSector answers "does any non-distant object stand in
    // this zone" by iterating the ENTIRE instance dictionary - with a native alive-check and a
    // native transform.position read per entry (ZNetScene.cs:329-337) - until it finds one, or
    // has visited all ~90k entries to say no. Its one caller is ZoneSystem.UpdateTTL
    // (ZoneSystem.cs:1779), which asks it for every loaded zone whose TTL has expired before
    // unloading that zone's terrain. Measured while a large base streams: ~29 ms of every second.
    //
    // Fix: a per-zone count of non-distant instances, answered in O(1). Maintained on the
    // complete mutation surface of m_instances, which was verified to be exactly one write site:
    //  - ZNetScene.AddInstance is the only place an entry is added (ZNetScene.cs:65);
    //  - every removal path (RemoveObjects, OnZDODestroyed, Shutdown) destroys the view's
    //    GameObject, so ZNetView.OnDestroy is the removal signal - and because removal paths call
    //    ResetZDO first, the bookkeeping is keyed by the ZNetView, not the ZDO;
    //  - ZDOMan.AddToSector is the single place a ZDO's sector changes land (SetSector's
    //    remove-then-add always ends there), which keeps moving creatures counted in the right
    //    zone. The sector-invalidation bounce (remove to sentinel and back inside one handler)
    //    passes through as two harmless moves that cancel.
    //
    // Equivalence: vanilla keys presence on the zone of the instance's live transform; the index
    // keys it on the ZDO's sector. For the static majority these never differ. For movers they
    // differ by at most the position-sync latency - and the error direction is safe on the side
    // that matters: a remote mover's transform lags its ZDO, so the index reports the NEW zone
    // occupied before the transform arrives there, keeping it loaded early rather than late. An
    // owned mover's ZDO sector trails its transform by under a frame. Against m_zoneTTL, a
    // sub-second disagreement on when a zone became empty is immaterial. A destroyed-this-frame
    // view is counted until its deferred OnDestroy where vanilla's alive-check skips it a few
    // frames sooner - again the safe direction (a zone lives marginally longer).
    //
    // The index is maintained unconditionally (standing rule); the toggle gates only the read,
    // and the read stands down to vanilla's walk if any maintenance hook failed to attach.
    //
    // Both: a dedicated server runs UpdateTTL over its own loaded zones at the same rate.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class SectorInstanceIndexPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SectorInstanceIndexPatch),
                ValConfig.SectionPerformance,
                "Fix Zone Occupancy Scan",
                true,
                "Answers \"does anything stand in this zone\" from a per-zone tally instead of " +
                "walking every loaded object with two engine calls each. The game asks this for " +
                "every zone it considers unloading, which at large object counts is a steady " +
                "share of frame time.");

            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Zone Occupancy",
                false,
                "Diagnostic. Answers every zone-occupancy check both from the tally and " +
                "vanilla's full walk, acts on vanilla's answer, and logs disagreements. Costs " +
                "the walk this fix exists to avoid, so leave it off unless you are validating " +
                "the tally. Transient one-off disagreements on zones holding a moving creature " +
                "are expected and harmless; persistent ones are not.",
                advanced: true);
        }

        // Zones with a positive count only - Bump removes emptied keys, so presence in the
        // dictionary IS the answer. CountedAt remembers which zone each view is tallied under,
        // keyed by view because removal paths null the ZDO before OnDestroy runs.
        private static readonly Dictionary<Vector2i, int> NonDistantCount = new Dictionary<Vector2i, int>();
        private static readonly Dictionary<ZNetView, Vector2i> CountedAt = new Dictionary<ZNetView, Vector2i>();

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        // UpdateTTL asks a few times per second at most, so the first session with verify on
        // never reached a 2000-comparison threshold before ending - summaries must land within
        // minutes at this call rate.
        private const int VerifyReportInterval = 250;
        private static bool _verifyActive;
        private static long _verifyComparisons;
        private static long _verifyDivergences;
        private static int _comparisonsSinceReport;

        // ---- index maintenance (unconditional; the toggle gates only the read) ---------------

        private static void Bump(Vector2i sector, int delta) {
            NonDistantCount.TryGetValue(sector, out int count);
            count += delta;
            if (count > 0) { NonDistantCount[sector] = count; } else { NonDistantCount.Remove(sector); }
        }

        [HarmonyPostfix]
        [HarmonyPatch("AddInstance")]
        private static void AddInstancePostfix(ZDO zdo, ZNetView nview) {
            if (ReferenceEquals(nview, null) || nview.m_distant) { return; }

            Vector2i sector = zdo.GetSector();

            // A re-added view (should not happen, but m_instances[zdo] = nview tolerates it)
            // must not be double-counted.
            if (CountedAt.TryGetValue(nview, out Vector2i previous)) { Bump(previous, -1); }

            CountedAt[nview] = sector;
            Bump(sector, 1);
        }

        [HarmonyPatch(typeof(ZNetView))]
        internal static class ViewDestroyHook {
            [HarmonyPostfix]
            [HarmonyPatch("OnDestroy")]
            private static void OnDestroyPostfix(ZNetView __instance) {
                if (!CountedAt.TryGetValue(__instance, out Vector2i sector)) { return; }

                CountedAt.Remove(__instance);
                Bump(sector, -1);
            }
        }

        [HarmonyPatch(typeof(ZDOMan))]
        internal static class SectorMoveHooks {
            [HarmonyPostfix]
            [HarmonyPatch("AddToSector")]
            private static void AddToSectorPostfix(ZDO zdo, Vector2i sector) {
                ZNetScene scene = ZNetScene.instance;
                if (ReferenceEquals(scene, null)) { return; }
                if (!scene.m_instances.TryGetValue(zdo, out ZNetView view)) { return; }
                if (!CountedAt.TryGetValue(view, out Vector2i previous) || previous == sector) { return; }

                Bump(previous, -1);
                Bump(sector, 1);
                CountedAt[view] = sector;
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                NonDistantCount.Clear();
                CountedAt.Clear();
            }
        }

        // ---- the read ------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch("HaveInstanceInSector")]
        private static bool HaveInstanceInSectorPrefix(ZNetScene __instance, Vector2i sector, ref bool __result) {
            if (Enabled == null || !Enabled.Value || !HooksHealthy()) { return true; }

            bool indexed = NonDistantCount.ContainsKey(sector);

            if (Verify != null && Verify.Value) {
                _verifyActive = true;
                _verifyComparisons++;

                bool walked = WalkedAnswer(__instance, sector);
                if (walked != indexed) {
                    _verifyDivergences++;
                    Logger.LogWarning(
                        $"Zone occupancy verify: DIVERGED on zone {sector} (tally: {indexed}, " +
                        $"walk: {walked}). Vanilla's answer was used. One-off disagreements on " +
                        "zones holding a moving creature are expected; report this if it " +
                        "repeats for the same zone.");
                }

                if (++_comparisonsSinceReport >= VerifyReportInterval) {
                    _comparisonsSinceReport = 0;
                    LogVerifySummary("periodic");
                }

                __result = walked;
                return false;
            }

            if (_verifyActive) {
                _verifyActive = false;
                LogVerifySummary("final");
                _verifyComparisons = 0;
                _verifyDivergences = 0;
                _comparisonsSinceReport = 0;
            }

            __result = indexed;
            return false;
        }

        // Vanilla's walk verbatim (ZNetScene.cs:329-337), for the verify path only.
        private static bool WalkedAnswer(ZNetScene scene, Vector2i sector) {
            foreach (KeyValuePair<ZDO, ZNetView> instance in scene.m_instances) {
                if ((bool)(Object)instance.Value && !instance.Value.m_distant
                    && ZoneSystem.GetZone(instance.Value.transform.position) == sector) {
                    return true;
                }
            }

            return false;
        }

        private static void LogVerifySummary(string kind) {
            Logger.LogInfo(
                $"Zone occupancy verify ({kind}): {_verifyComparisons} comparison(s), " +
                $"{_verifyDivergences} divergence(s).");
        }

        // ---- hook health ---------------------------------------------------------------------

        /// Without all three maintenance hooks the tally silently drifts, so the read stands
        /// down to vanilla's walk when any is missing.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            _hooksHealthy =
                HasOurPostfix(AccessTools.DeclaredMethod(typeof(ZNetScene), "AddInstance"), typeof(SectorInstanceIndexPatch))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(ZNetView), "OnDestroy"), typeof(ViewDestroyHook))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(ZDOMan), "AddToSector"), typeof(SectorMoveHooks));

            if (!_hooksHealthy) {
                Logger.LogError(
                    "Zone occupancy: a maintenance hook is not attached, so the tally cannot be " +
                    "trusted and occupancy checks have fallen back to vanilla's full walk for " +
                    "this session. This usually means a Valheim update changed those methods - " +
                    "look for the patch failure logged at startup.");
            }

            return _hooksHealthy;
        }

        private static bool HasOurPostfix(MethodBase target, System.Type hookClass) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            foreach (Patch patch in info.Postfixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookClass) { continue; }
                return true;
            }

            return false;
        }
    }
}
