using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Smelter Catch-up Reads: a smelter catching up on missed production time reads its fuel
    // and ore queue once per changed value instead of once per simulated second.
    //
    // Smelter.UpdateSmelter runs on a 1 Hz InvokeRepeating and settles the time a smelter was not
    // loaded with a catch-up loop, "while (accumulator >= 1f) { accumulator--; ... }", whose
    // accumulator is clamped to 3600. The first two statements in the loop body are GetFuel() and
    // GetQueuedOre() - a ZDO float read, and a ZDO int read plus a conditional string read - and
    // they run *before* the condition that decides whether there is any work to do. So a smelter
    // that is out of ore, or out of fuel, and has not ticked for an hour performs several
    // thousand ZDO lookups in a single frame and produces nothing from any of them. That lands as
    // a frame spike on the tick after a base loads, which is exactly when a spike is least
    // welcome.
    //
    // A transpiler redirects both accessors, inside UpdateSmelter only, to helpers that memoise
    // per call against the ZDO's own DataRevision. IncreaseDataRevision is raised by exactly the
    // writes that changed a stored value - ZDO.Set skips it when the new value compares equal to
    // the old - so an unchanged revision proves no field changed and a cached answer is exact.
    // The revision counts the whole ZDO rather than one field, which only ever costs an
    // unnecessary refresh; in the idle case that motivates this fix nothing is written at all, so
    // one read serves the whole loop. A prefix arms the memo and a postfix clears it, so it never
    // survives a call and a revision arriving from a peer between calls can never alias a stale
    // value. Anything reaching the helpers unarmed, or holding an invalid view, falls straight
    // through to the vanilla accessor.
    //
    // This changes no ZDO write, no ordering and no arithmetic, so the simulation and everything
    // it saves are untouched; it only stops re-reading values that provably did not change. Each
    // rewrite is counted and stands down on its own if the count is wrong, so a mod that has
    // already rewritten UpdateSmelter wins the method rather than both breaking it.
    //
    // Both: the simulation half is behind m_nview.IsOwner(), so the returning client, a listen
    // host and a dedicated server can each be the peer that runs it, and they must not disagree.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(Smelter))]
    internal static class SmelterCatchupPatch {
        private const string Site = "Smelter.UpdateSmelter";

        private static readonly MethodInfo GetFuelMethod = AccessTools.DeclaredMethod(typeof(Smelter), "GetFuel");
        private static readonly MethodInfo GetQueuedOreMethod = AccessTools.DeclaredMethod(typeof(Smelter), "GetQueuedOre");
        private static readonly MethodInfo FuelMethod = AccessTools.DeclaredMethod(typeof(SmelterCatchupPatch), nameof(Fuel));
        private static readonly MethodInfo QueuedOreMethod = AccessTools.DeclaredMethod(typeof(SmelterCatchupPatch), nameof(QueuedOre));

        // Armed for the duration of one UpdateSmelter call and cleared afterwards.
        private static bool _armed;
        private static ZDO _zdo;
        private static uint _revision;
        private static bool _haveFuel;
        private static float _fuel;
        private static bool _haveOre;
        private static string _ore;

        [HarmonyPrefix]
        [HarmonyPatch("UpdateSmelter")]
        private static void UpdateSmelterPrefix() {
            _armed = true;
            _zdo = null;
            _haveFuel = false;
            _haveOre = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch("UpdateSmelter")]
        private static void UpdateSmelterPostfix() {
            _armed = false;
            _zdo = null;
            _ore = null;
        }

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("UpdateSmelter")]
        private static IEnumerable<CodeInstruction> UpdateSmelterTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(
                PatchHelper.ReplaceCalls(instructions, GetFuelMethod, FuelMethod, Site, expected: 2),
                GetQueuedOreMethod, QueuedOreMethod, Site, expected: 2);

        /// <summary>
        /// Drops both memos when the ZDO changed identity or revision. Returns false when there is
        /// nothing safe to memoise against, in which case callers use the vanilla accessor.
        /// </summary>
        private static bool Sync(Smelter smelter) {
            if (!_armed) { return false; }

            ZNetView nview = smelter.m_nview;
            if (nview == null || !nview.IsValid()) { return false; }

            ZDO zdo = nview.GetZDO();
            if (zdo == null) { return false; }

            if (!ReferenceEquals(zdo, _zdo) || zdo.DataRevision != _revision) {
                _zdo = zdo;
                _revision = zdo.DataRevision;
                _haveFuel = false;
                _haveOre = false;
            }

            return true;
        }

        private static float Fuel(Smelter smelter) {
            if (!Sync(smelter)) { return smelter.GetFuel(); }

            if (!_haveFuel) {
                _fuel = smelter.GetFuel();
                _haveFuel = true;
            }

            return _fuel;
        }

        private static string QueuedOre(Smelter smelter) {
            if (!Sync(smelter)) { return smelter.GetQueuedOre(); }

            if (!_haveOre) {
                _ore = smelter.GetQueuedOre();
                _haveOre = true;
            }

            return _ore;
        }
    }
}
