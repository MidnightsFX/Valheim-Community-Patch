using BepInEx;
using BepInEx.Configuration;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130
    internal class ValConfig {
        public static ConfigFile cfg;
        
        // Add Client sided config entries under here
        public static ConfigEntry<bool> EnableDebugMode;
        public static ConfigEntry<bool> PatchEverySide;

        // Add Server synced config entries under here
        public static ConfigEntry<float> ConfigApplyDelay;

        public const string cfgFolder = "ValheimCommunityPatch";

        // Config sections. Correctness and terrain fixes each get their own toggle so a single fix
        // can be switched off without a rebuild; the toggles live next to the patch that reads
        // them. Performance fixes are always on - the Performance section holds only their tuning
        // values (budgets, caps, distances, intervals).
        public const string SectionPerformance = "Fixes - Performance";
        public const string SectionCorrectness = "Fixes - Correctness";
        public const string SectionTerrain = "Fixes - Terrain";

        // Diagnostics only - the Verify toggles that run both an indexed path and vanilla's for
        // comparison. Kept out of the Fixes sections so a new user browsing the config does not
        // mistake them for fixes and turn them on; every one of them deliberately costs the work
        // its fix exists to avoid.
        public const string SectionDebug = "Debug";

        public ValConfig(ConfigFile cf) {
            // ensure all the config values are created
            cfg = cf;
            cfg.SaveOnConfigSet = true;
            CreateConfigValues(cf);
            Logger.SetDebugLogging(EnableDebugMode.Value);
        }

        public static void SaveOnSet(bool enabled) {
            cfg.SaveOnConfigSet = enabled;
            cfg.Save();
        }

        private void CreateConfigValues(ConfigFile Config) {
            // Debugmode
            EnableDebugMode = Config.Bind("Client config", "EnableDebugMode", false,
                new ConfigDescription("Enables Debug logging.",
                null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));
            EnableDebugMode.SettingChanged += Logger.EnableDebugLogging;
            Logger.CheckEnableDebugLogging();

            // Deliberately a plain client-side bind and not BindServerConfig: this is read while
            // patches are being applied, long before Jotunn could sync anything down, and it is a
            // property of this machine rather than of the world.
            PatchEverySide = Config.Bind("Client config", "Patch Every Side", false,
                new ConfigDescription(
                    "Applies every fix regardless of which side it is for. Normally the client-only " +
                    "fixes are not applied on a dedicated server, because nothing there could ever " +
                    "reach them. Turn this on only if this machine has a display but was detected as " +
                    "headless. Requires a game restart.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            // Instantiate server synced config entries here
            ConfigApplyDelay = BindServerConfig(SectionDebug, "Config Apply Delay", 1f, "Delay in seconds before a changed config entry is applied in-game. Coalesces a burst of rapid edits (typing, file reloads, server sync) into a single apply. Set to 0 to apply instantly.", true, 0f, 10f);

            BindFixToggles();
        }

        // Each fix binds its config here - correctness and terrain fixes their toggle, performance
        // fixes their tuning and Verify entries - so every entry exists before the single save
        // flush in ValheimCommunityPatch.Awake. Performance fixes with nothing to tune bind
        // nothing and do not appear.
        private static void BindFixToggles() {
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
        /// Binds a fix's on/off toggle, prefixing its description with the side the fix runs on.
        /// </summary>
        /// <remarks>
        /// The tag is read from the [PatchSide] attribute on the patch class itself, so the side
        /// shown in the config and the decision ApplyPatches makes come from the same declaration
        /// and cannot drift. The generated .cfg then doubles as a machine-produced list of every
        /// fix's side, which is what the README can be checked against.
        ///
        /// The side goes in the description and not the section name on purpose: renaming sections
        /// would change the config keys and orphan every existing .cfg.
        /// </remarks>
        public static ConfigEntry<bool> BindFixToggle(Type patchClass, string category, string key, bool value, string description, bool advanced = false) {
            return BindServerConfig(
                category, key, value,
                PatchSideAttribute.Tag(PatchSideAttribute.Of(patchClass)) + " " + description,
                null, advanced);
        }

        /// <summary>
        /// Helper to bind configs for float types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valMin"></param>
        /// <param name="valMax"></param>
        /// <returns></returns>
        public static ConfigEntry<float[]> BindServerConfig(string category, string key, float[] value, string description, bool advanced = false, float valMin = 0, float valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<float>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        ///  Helper to bind configs for bool types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="acceptableValues"></param>>
        /// <param name="advanced"></param>
        /// <returns></returns>
        public static ConfigEntry<bool> BindServerConfig(string category, string key, bool value, string description, AcceptableValueBase acceptableValues = null, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                    acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for int types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valMin"></param>
        /// <param name="valMax"></param>
        /// <returns></returns>
        public static ConfigEntry<int> BindServerConfig(string category, string key, int value, string description, bool advanced = false, int valMin = 0, int valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<int>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for float types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valMin"></param>
        /// <param name="valMax"></param>
        /// <returns></returns>
        public static ConfigEntry<float> BindServerConfig(string category, string key, float value, string description, bool advanced = false, float valMin = 0, float valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<float>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for strings
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <returns></returns>
        public static ConfigEntry<string> BindServerConfig(string category, string key, string value, string description, AcceptableValueList<string> acceptableValues = null, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(
                    description,
                    acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }
    }
}
