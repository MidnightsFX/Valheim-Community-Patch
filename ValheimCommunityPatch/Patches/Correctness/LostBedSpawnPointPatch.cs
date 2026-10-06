using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Lost Bed Spawn Point: a respawn keeps the bed spawn point while the server has not yet
    // sent the area around the bed, for up to 30 seconds.
    //
    // Game.FindSpawnPoint clears the custom spawn point when IsAreaReady passes at the bed but no
    // current Bed instance exists. On a client of a dedicated server, IsAreaReady passes for a zone
    // whose objects have not arrived yet, because it only checks the objects the client knows
    // about, and the bed's terrain is generated locally. Vanilla's fixed 8 s wait usually hides
    // this; a server that needs longer to stream the bed area, or a mod that shortens the wait,
    // sends the player to the world start instead of their bed.
    //
    // A transpiler swaps the one ClearCustomSpawnPoint call in FindSpawnPoint for a guard that
    // skips the clear while the client holds no object at all in the bed's zone, or while Fix
    // Loading Screen Wait is still waiting for the server to confirm it. Vanilla's own reset of
    // m_respawnWait right after the call then re-arms its wait and retries. After 30 s the clear
    // goes through as in vanilla. Vanilla's "Failed to find bed" line is written once per window
    // rather than on every retry.
    //
    // Client: the decision is the respawning player's, and a host holds every object itself.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Game))]
    internal static class LostBedSpawnPointPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(LostBedSpawnPointPatch),
                ValConfig.SectionCorrectness,
                "Fix Lost Bed Spawn Point",
                true,
                "Keeps your bed as the respawn point while the server has not yet sent the area around " +
                "it, instead of sending you to the world start. Vanilla decides the bed is gone as soon " +
                "as the ground there is loaded, even when the buildings have not arrived yet.");
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

        private static void GuardedClear(PlayerProfile profile) {
            if (ShouldDefer(profile)) { return; }

            _deferSince = -1f;
            profile.ClearCustomSpawnPoint();
        }

        private static bool ShouldDefer(PlayerProfile profile) {
            if (Enabled == null || !Enabled.Value) { return false; }

            ZNet net = ZNet.instance;
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

        // A generated zone always holds objects of its own (its zone controller, vegetation, the
        // bed itself), so none at all means the server has not sent it yet.
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
            if (_deferSince >= 0f) { return; }

            ZLog.Log(message);
        }
    }
}
