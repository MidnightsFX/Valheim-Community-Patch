using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Smelter Catch-up Reads: an idle smelter catching up an hour re-reads fuel and ore 3,600 times in one frame.
    // Transpiles Smelter.UpdateSmelter's GetFuel and GetQueuedOre calls into reads memoised on the ZDO's data revision.
    // A base of idle smelters no longer spikes a frame on the tick after it loads, and smelting is unchanged.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(Smelter))]
    internal static class SmelterCatchupPatch {
        private const string Site = "Smelter.UpdateSmelter";

        private static readonly MethodInfo GetFuelMethod = AccessTools.DeclaredMethod(typeof(Smelter), "GetFuel");
        private static readonly MethodInfo GetQueuedOreMethod = AccessTools.DeclaredMethod(typeof(Smelter), "GetQueuedOre");
        private static readonly MethodInfo FuelMethod = AccessTools.DeclaredMethod(typeof(SmelterCatchupPatch), nameof(Fuel));
        private static readonly MethodInfo QueuedOreMethod = AccessTools.DeclaredMethod(typeof(SmelterCatchupPatch), nameof(QueuedOre));

        // Armed for the duration of one UpdateSmelter call and cleared afterwards, so a revision arriving
        // from a peer between calls can never alias a stale value.
        private static bool _armed;
        private static ZDO _zdo;
        private static uint _revision;
        private static bool _haveFuel;
        private static float _fuel;
        private static bool _haveOre;
        private static string _ore;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(SmelterCatchupPatch));

        [HarmonyPrefix]
        [HarmonyPatch("UpdateSmelter")]
        private static void UpdateSmelterPrefix() {
            // Left unarmed when turned off, so the rewritten reads go straight to vanilla's accessors.
            _armed = !ApiSwitch.Off;
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

        // GetFuel and GetQueuedOre run in the catch-up loop before its check for work, once per simulated
        // second. Two of each, pinned: a mod that already rewrote UpdateSmelter keeps it rather than both breaking.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("UpdateSmelter")]
        private static IEnumerable<CodeInstruction> UpdateSmelterTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(
                PatchHelper.ReplaceCalls(instructions, GetFuelMethod, FuelMethod, Site, expected: 2),
                GetQueuedOreMethod, QueuedOreMethod, Site, expected: 2);

        /// <summary>
        /// Drops both memos when the ZDO changed identity or revision. ZDO.Set raises the revision only
        /// when a stored value changes, so an unchanged revision proves the memos exact. Returns false
        /// when there is nothing safe to memoise against, in which case callers use the vanilla accessor.
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
