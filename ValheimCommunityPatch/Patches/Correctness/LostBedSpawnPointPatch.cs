using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Game.FindSpawnPoint clears the bed spawn point once the bed's ground loads, before the server has sent the bed.
    // A transpiler replaces its ClearCustomSpawnPoint call with a guard that waits up to 30 s for the bed's zone.
    // Respawning players return to their bed instead of the world start when the server is slow to send it.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Game))]
    internal static class LostBedSpawnPointPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(LostBedSpawnPointPatch),
                ValConfig.SectionCorrectness,
                "Fix Lost Bed Spawn Point",
                true,
                "Keeps your bed as the respawn point while the server has not yet sent the area around " +
                "it, instead of sending you to the world start.");
        }

        private const float MaxDeferral = 30f;
        private const string BedMissingMessage = "Failed to find bed at custom spawn point, using original";

        private static readonly MethodInfo ClearCustomSpawnPointMethod =
            AccessTools.Method(typeof(PlayerProfile), nameof(PlayerProfile.ClearCustomSpawnPoint));
        private static readonly MethodInfo GuardedClearMethod =
            AccessTools.Method(typeof(LostBedSpawnPointPatch), nameof(GuardedClear));
        private static readonly MethodInfo ZLogLogMethod =
            AccessTools.Method(typeof(ZLog), nameof(ZLog.Log), new[] { typeof(object) });
        private static readonly MethodInfo BedMissingLogMethod =
            AccessTools.Method(typeof(LostBedSpawnPointPatch), nameof(BedMissingLog));

        // Start of the current deferral window, or negative when none is open.
        private static float _deferSince = -1f;
        private static Game _deferFor;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("FindSpawnPoint")]
        private static IEnumerable<CodeInstruction> FindSpawnPointTranspiler(IEnumerable<CodeInstruction> instructions) {
            IEnumerable<CodeInstruction> swapped = PatchHelper.ReplaceCalls(
                instructions, ClearCustomSpawnPointMethod, GuardedClearMethod, "Game.FindSpawnPoint", expected: 1);
            if (ReferenceEquals(swapped, instructions)) { return instructions; }

            // Vanilla logs the failure on every retry; with a short respawn wait that is every
            // physics tick. Only the log right after the message is rerouted.
            List<CodeInstruction> codes = PatchHelper.Copy(swapped);
            bool quieted = false;
            for (int i = 0; i + 1 < codes.Count; i++) {
                if (codes[i].opcode != OpCodes.Ldstr || !(codes[i].operand is string text) || text != BedMissingMessage) {
                    continue;
                }
                if (!codes[i + 1].Calls(ZLogLogMethod)) { continue; }

                codes[i + 1].opcode = OpCodes.Call;
                codes[i + 1].operand = BedMissingLogMethod;
                quieted = true;
                break;
            }

            if (!quieted) {
                Logger.LogDebug("Game.FindSpawnPoint: the missing-bed log line was not found; it repeats while a respawn waits.");
            }

            return codes;
        }

        // Each respawn opens its own window.
        [HarmonyPostfix]
        [HarmonyPatch("_RequestRespawn")]
        private static void RequestRespawnPostfix() => _deferSince = -1f;

        // Skipping the clear is enough: vanilla resets m_respawnWait right after the call, so it
        // waits and retries.
        private static void GuardedClear(PlayerProfile profile) {
            if (ShouldDefer(profile)) { return; }

            _deferSince = -1f;
            profile.ClearCustomSpawnPoint();
        }

        private static bool ShouldDefer(PlayerProfile profile) {
            if (Enabled == null || !Enabled.Value) { return false; }

            ZNet net = ZNet.instance;
            // A host holds every object itself.
            if (net == null || net.IsServer() || profile == null || !profile.HaveCustomSpawnPoint()) { return false; }

            if (!ReferenceEquals(Game.instance, _deferFor)) {
                _deferFor = Game.instance;
                _deferSince = -1f;
            }

            Vector3 bed = profile.GetCustomSpawnPoint();
            bool notArrived = !KnowsAnyObject(ZoneSystem.GetZone(bed)) || LoadingWaitPatch.Client.AwaitingConfirmation(bed);
            if (!notArrived) { return false; }

            float now = Time.unscaledTime;
            if (_deferSince < 0f) {
                _deferSince = now;
                Logger.LogInfo("The area around your bed has not arrived from the server yet, so the bed stays your spawn point while it loads.");
                return true;
            }

            if (now - _deferSince < MaxDeferral) { return true; }

            Logger.LogWarning(
                $"The area around your bed still had not arrived from the server after {MaxDeferral:0} s, so " +
                "the bed spawn point is cleared as the game would.");
            return false;
        }

        // IsAreaReady passes before the server's objects arrive: it checks only objects the client
        // knows, and the terrain is generated locally. A generated zone always holds objects of its
        // own (its zone controller, vegetation, the bed itself), so none at all means the server has
        // not sent it yet.
        private static bool KnowsAnyObject(Vector2s zone) {
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) { return true; }

            ZoneSystem.SectorIndex index = ZoneSystem.SectorToIndex(zone);

            // Sector 0 aliases every zone outside the grid, so it cannot answer for this one.
            if (index.Sector == 0) { return true; }

            List<ZDO>[] bySector = zdoMan.m_objectsBySector;
            if (bySector != null && index.Sector < bySector.Length) {
                List<ZDO> objects = bySector[index.Sector];
                if (objects != null && objects.Count > 0) { return true; }
            }

            // Portals are kept out of the sector array.
            return zdoMan.m_portalObjects.TryGetValue(index, out List<ZDO> portals) && portals.Count > 0;
        }

        private static void BedMissingLog(object message) {
            if (_deferSince >= 0f && Enabled.Value) { return; }

            ZLog.Log(message);
        }
    }
}
