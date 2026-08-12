using BepInEx;
using BepInEx.Logging;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;
using JotunnModStub.Common;

namespace JotunnModStub
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    //[NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    internal class JotunnModStub : BaseUnityPlugin
    {
        public const string PluginGUID = "AuthorName.JotunnModStub";
        public const string PluginName = "JotunnModStub";
        public const string PluginVersion = "0.0.1";

        internal static ManualLogSource Log;
        internal ValConfig cfg;
        public static CustomLocalization Localization = LocalizationManager.Instance.GetLocalization();
        public static AssetBundle EmbeddedResourceBundle;

        public void Awake() {
            Log = this.Logger;
            cfg = new ValConfig(Config);

            // All startup hooks should go after the config & Logger have been wired up
            
            EmbeddedResourceBundle = AssetUtils.LoadAssetBundleFromResources("JotunnModStub.Assets.embedded_bundle", typeof(JotunnModStub).Assembly);
            LocalizationLoader.AddLocalizations();

            // Configs are not written until after they are all wired up, they exist in memory before this.
            // Flushing all of the configs at once is a significant speedup in mod load time
            ValConfig.SaveOnSet(true);
        }
    }
}