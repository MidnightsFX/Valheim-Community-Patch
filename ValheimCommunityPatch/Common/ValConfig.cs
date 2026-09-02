using BepInEx.Configuration;
using System;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>Binds every config entry and holds the shared bind helpers.</summary>
    internal static class ValConfig {
        public static ConfigFile cfg;

        public static ConfigEntry<bool> EnableDebugMode;
        public static ConfigEntry<bool> PatchEverySide;

        // Correctness and terrain fixes each have an on/off toggle. Performance fixes are always
        // on; the Performance section holds only their tuning values.
        public const string SectionPerformance = "Fixes - Performance";
        public const string SectionCorrectness = "Fixes - Correctness";
        public const string SectionTerrain = "Fixes - Terrain";

        // Verify toggles and other diagnostics. Kept apart from the fixes so nobody mistakes them
        // for one: each deliberately costs the work its fix exists to avoid.
        public const string SectionDebug = "Debug";

        /// <summary>Binds every entry, then writes the file once.</summary>
        internal static void Bind(ConfigFile file) {
            cfg = file;

            // One file write at the end instead of BepInEx's default of one per entry.
            cfg.SaveOnConfigSet = false;

            EnableDebugMode = cfg.Bind("Client config", "EnableDebugMode", false,
                new ConfigDescription("Enables Debug logging.", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true }));
            Logger.SetDebug(EnableDebugMode.Value);
            EnableDebugMode.SettingChanged += (sender, args) => Logger.SetDebug(EnableDebugMode.Value);

            // A plain client-side entry, not server-synced: it is read while patches are applied,
            // long before anything could be synced down, and it describes this machine.
            PatchEverySide = cfg.Bind("Client config", "Patch Every Side", false,
                new ConfigDescription(
                    "Applies every fix regardless of which side it is for. Normally the client-only " +
                    "fixes are not applied on a dedicated server, because nothing there could ever " +
                    "reach them. Turn this on only if this machine has a display but was detected as " +
                    "headless. Requires a game restart.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            BindFixConfig();

            cfg.SaveOnConfigSet = true;
            cfg.Save();
        }

        // Each fix binds its own entries here: correctness and terrain fixes their toggle,
        // performance fixes their tuning and Verify entries. Fixes with nothing to configure
        // bind nothing.
        private static void BindFixConfig() {
            TeardownHooks.BindConfig();
            Patches.Performance.OrphanZdoIndexPatch.BindConfig();
            Patches.Performance.ZdoPrefabIndexPatch.BindConfig();
            Patches.Performance.HeightmapLookupPatch.BindConfig();
            Patches.Performance.StaticPhysicsCachePatch.BindConfig();
            Patches.Performance.ClutterRebuildCapPatch.BindConfig();
            Patches.Performance.HeightmapBuilderThroughputPatch.BindConfig();
            Patches.Performance.ZoneGenPacingPatch.BindConfig();
            Patches.Performance.TerrainLodSpreadPatch.BindConfig();
            Patches.Performance.WearSupportLookupPatch.BindConfig();
            Patches.Performance.SceneIdleSkipPatch.BindConfig();
            Patches.Performance.LightCostPatch.BindConfig();
            Patches.Performance.RemoveSweepPacingPatch.BindConfig();
            Patches.Performance.SpawnQueueCachePatch.BindConfig();
            Patches.Performance.SectorInstanceIndexPatch.BindConfig();
            Patches.Performance.SupportSleepPatch.BindConfig();
            Patches.Performance.ZoneDiffRemovalPatch.BindConfig();
            Patches.Performance.ReflectionSlicePatch.BindConfig();
            Patches.Performance.PhysicsCatchupPatch.BindConfig();
            Patches.Performance.SpawnEventQueuePatch.BindConfig();
            Patches.Performance.LocationBiomeAreaCachePatch.BindConfig();
            Patches.Correctness.RecipeGetAmountNrePatch.BindConfig();
            Patches.Correctness.ProjectileZeroVelocityPatch.BindConfig();
            Patches.Correctness.SpawnAreaNullPrefabPatch.BindConfig();
            Patches.Correctness.RunAttackStaminaPatch.BindConfig();
            Patches.Correctness.UnlitFireCookingPatch.BindConfig();
            Patches.Correctness.ZdoLoadDuplicatePatch.BindConfig();
            Patches.Correctness.RemoveObjectsNrePatch.BindConfig();
            Patches.Correctness.EffectAreaPatch.BindConfig();
            Patches.Correctness.FuelLossPatch.BindConfig();
            Patches.Correctness.BossKeySharePatch.BindConfig();
            Patches.Correctness.ItemIconVariantPatch.BindConfig();
            Patches.Correctness.SendFailureLogSpamPatch.BindConfig();
            Patches.Correctness.ContainerLogSpamPatch.BindConfig();
            Patches.Correctness.NegativeStaminaPatch.BindConfig();
            Patches.Correctness.DungeonZoneLoadPinPatch.BindConfig();
            Patches.Terrain.SeamlessNormalsPatch.BindConfig();
            Patches.Terrain.PaintSeamReconcilePatch.BindConfig();
            Patches.Terrain.TerrainOpPaintFanoutPatch.BindConfig();
            Patches.Terrain.PaintMaskStridePatch.BindConfig();
            Patches.Terrain.TerrainCompNullHmapPatch.BindConfig();
        }

        /// <summary>
        /// Binds a fix's on/off toggle. The description is prefixed with the side the fix runs on,
        /// read from the class's [PatchSide] attribute so the config and the patch gate cannot
        /// disagree. The side goes in the description rather than the section name so existing
        /// config files keep their keys.
        /// </summary>
        public static ConfigEntry<bool> BindFixToggle(Type patchClass, string category, string key, bool value, string description, bool advanced = false) {
            return BindServerConfig(
                category, key, value,
                PatchSideAttribute.Tag(PatchSideAttribute.Of(patchClass)) + " " + description,
                null, advanced);
        }

        // Server-synced, admin-only entries.

        public static ConfigEntry<bool> BindServerConfig(string category, string key, bool value, string description, AcceptableValueBase acceptableValues = null, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description, acceptableValues,
                    new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced }));
        }

        public static ConfigEntry<int> BindServerConfig(string category, string key, int value, string description, bool advanced = false, int valMin = 0, int valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description, new AcceptableValueRange<int>(valMin, valMax),
                    new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced }));
        }

        public static ConfigEntry<float> BindServerConfig(string category, string key, float value, string description, bool advanced = false, float valMin = 0, float valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description, new AcceptableValueRange<float>(valMin, valMax),
                    new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced }));
        }
    }
}
