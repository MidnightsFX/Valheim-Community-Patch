using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Map Auto-Close: keeps an open map open when a world key changes somewhere on the server,
    // unless a boss is near the player.
    //
    // Game.UpdateNoMap re-applies the nomap setting by calling Minimap.SetMapMode(Small), which also
    // closes an open map. It runs from Game.UpdateWorldRates on every global key change, and each key
    // change makes the server resend the whole key list to every client, which re-adds each key and
    // closes the map once per key. A boss sets the activeBosses key the first time it is alerted (a hit
    // is enough) and again when it dies, alongside its defeated_ key, so each boss fight anywhere on
    // the server closes every player's open map.
    //
    // A transpiler swaps the one SetMapMode call in UpdateNoMap for a guard that skips only an open
    // map being closed, and still lets it close when a boss is loaded within range, as vanilla would
    // for the players at the fight. The nomap computation is kept verbatim, as are opening the small
    // map on spawn and hiding it when nomap turns on. The guard does not check whether the boss is
    // alerted: the key resend reaches the players who do not own the boss before its alert state does.
    //
    // Client: the map only exists on a client, and UpdateNoMap runs there on every key change.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Game))]
    internal static class MapAutoClosePatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> BossRange;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(MapAutoClosePatch),
                ValConfig.SectionCorrectness,
                "Fix Map Auto-Close",
                true,
                "Stops the map closing whenever a boss is woken or killed anywhere on the server. Vanilla " +
                "closes every player's open map on any world key change, and each boss fight makes several. " +
                "The map still closes as before for a player with a boss nearby.");

            BossRange = ValConfig.BindServerConfig(
                ValConfig.SectionCorrectness,
                "Map Auto-Close Boss Range",
                100f,
                "How close a boss must be for a world key change to close your map, as vanilla does, in " +
                "metres. The default matches the range at which the boss health bar shows. 0 never closes it.",
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
