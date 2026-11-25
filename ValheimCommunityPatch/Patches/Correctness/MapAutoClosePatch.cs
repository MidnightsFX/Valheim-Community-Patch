using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Game.UpdateNoMap closes an open map on every world key change, so any boss woken or killed closes every map.
    // Transpiles UpdateNoMap's SetMapMode call to skip closing an open map unless a boss is in the configured range.
    // Players keep their map open through boss fights elsewhere on the server; it still closes for those near the boss.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Game))]
    internal static class MapAutoClosePatch {
        internal static FixToggle Enabled;
        internal static ConfigEntry<float> BossRange;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(MapAutoClosePatch),
                ValConfig.SectionCorrectness,
                "Fix Map Auto-Close",
                true,
                "Stops the map closing whenever a boss is woken or killed anywhere on the server; it still " +
                "closes for a player with a boss nearby.");

            BossRange = ValConfig.BindServerConfig(
                ValConfig.SectionCorrectness,
                "Map Auto-Close Boss Range",
                100f,
                "How close, in metres, a boss must be for a world key change to close your map as vanilla does. " +
                "The default is the boss health bar's range; 0 never closes it.",
                false, 0f, 1000f);
        }

        private static readonly MethodInfo SetMapModeMethod =
            AccessTools.Method(typeof(Minimap), nameof(Minimap.SetMapMode));
        private static readonly MethodInfo GuardedSetMapModeMethod =
            AccessTools.Method(typeof(MapAutoClosePatch), nameof(GuardedSetMapMode));

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(Game.UpdateNoMap))]
        private static IEnumerable<CodeInstruction> UpdateNoMapTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(instructions, SetMapModeMethod, GuardedSetMapModeMethod, "Game.UpdateNoMap", expected: 1);

        // Read at call time rather than at patch time, so the toggle applies without a restart.
        private static void GuardedSetMapMode(Minimap minimap, Minimap.MapMode mode) {
            bool closingOpenMap = mode == Minimap.MapMode.Small && minimap.m_mode == Minimap.MapMode.Large;
            if (closingOpenMap && Enabled != null && Enabled.Value && !BossNearby()) { return; }

            minimap.SetMapMode(mode);
        }

        // Any boss in range counts, alerted or not: the key resend reaches players who do not own the boss before its
        // alert state does.
        private static bool BossNearby() {
            Player localPlayer = Player.m_localPlayer;
            if (localPlayer == null) { return true; }

            float range = BossRange.Value;
            if (range <= 0f) { return false; }

            float rangeSqr = range * range;
            Vector3 position = localPlayer.transform.position;
            foreach (Character character in Character.GetAllCharacters()) {
                if (character == null || !character.IsBoss()) { continue; }
                if ((character.transform.position - position).sqrMagnitude <= rangeSqr) { return true; }
            }

            return false;
        }
    }
}
