using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Jotunn empties its lookup of loaded objects as a world loads, so the first mocked location in play rebuilds it in one frame.
    // Postfixes Game.SpawnPlayer to rebuild Jotunn's GameObject lookup there, while the loading screen still covers it.
    // The first location or dungeon from a Jotunn mod such as More World Locations no longer freezes the game when it loads.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Game), "SpawnPlayer")]
    internal static class JotunnCachePrewarmPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.cfg.Bind(
                "Client config",
                "Build Jotunn Cache While Loading",
                false,
                new ConfigDescription(
                    "Builds Jotunn's lookup of loaded objects on the loading screen as you spawn instead of when the " +
                    "first location from a Jotunn mod loads in play, trading that freeze for a little loading time."));
            FixRegistry.RecordToggle(typeof(JotunnCachePrewarmPatch), Enabled);
        }

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(JotunnCachePrewarmPatch));

        // Not earlier, such as ZoneSystem.Start: some mocks name a part of a vanilla location (More World Locations' Vines,
        // from the meadows houses), which the lookup only holds while such a location is loaded, and at spawn the ones
        // around the player are, as they are for the rebuild in play.
        // Only GameObject: other types rebuild in a few milliseconds, and Transform, which only some More World Locations
        // dungeon rooms look up, would cost as much again on every load.
        [HarmonyPostfix]
        private static void Postfix() {
            if (ApiSwitch.Off || Enabled == null || !Enabled.Value) { return; }

            Stopwatch watch = Stopwatch.StartNew();
            int count;
            try {
                count = Build();
            } catch (Exception ex) {
                // Jotunn keeps no lookup after a failed build, so its next lookup scans again, as without this fix.
                Logger.LogWarning($"Could not build Jotunn's GameObject cache while loading: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // A respawn finds the lookup already built: no line.
            if (watch.ElapsedMilliseconds >= 1) {
                Logger.LogInfo($"Built Jotunn's GameObject cache while loading: {count} names in {watch.ElapsedMilliseconds} ms.");
            }
        }

        // A separate method, so a Jotunn without this API throws here, inside the catch, rather than when Postfix compiles.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Build() => PrefabManager.Cache.GetPrefabs(typeof(GameObject)).Count;
    }
}
