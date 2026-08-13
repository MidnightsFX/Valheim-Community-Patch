using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Utils;

namespace ValheimCommunityPatch
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    internal class ValheimCommunityPatch : BaseUnityPlugin
    {
        public const string PluginGUID = "MidnightsFX.ValheimCommunityPatch";
        public const string PluginName = "ValheimCommunityPatch";
        public const string PluginVersion = "0.3.0";

        internal static ManualLogSource Log;
        internal ValConfig cfg;

        private readonly Harmony harmony = new Harmony(PluginGUID);

        public void Awake() {
            Log = this.Logger;
            cfg = new ValConfig(Config);

            // All startup hooks should go after the config & Logger have been wired up.
            ApplyPatches();

            // Configs are not written until after they are all wired up, they exist in memory before this.
            // Flushing all of the configs at once is a significant speedup in mod load time
            ValConfig.SaveOnSet(true);
        }

        // Deliberately not Harmony.PatchAll: this mod ships many independent fixes, and PatchAll aborts
        // the whole batch the moment one target fails to resolve. After a game update that renames or
        // removes a single vanilla method, we want the other fixes to keep working and one clear error
        // in the log - not a mod that silently does nothing.
        //
        // Each fix checks its own config toggle at runtime, so patching is unconditional here: a fix
        // switched off in the config is a cheap bool check, not an unpatched method. The exceptions are
        // transpilers, which read their toggle at patch time and therefore need a restart to change.
        private void ApplyPatches() {
            int applied = 0, failed = 0;

            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes()) {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) { continue; }

                try {
                    harmony.CreateClassProcessor(type).Patch();
                    applied++;
                    Logger.LogDebug($"Applied {type.Name}.");
                } catch (Exception ex) {
                    failed++;
                    Log.LogError($"Could not apply {type.Name}: {ex}");
                }
            }

            if (failed > 0) {
                Log.LogWarning(
                    $"{applied} fix(es) applied, {failed} failed. The failures above are usually caused by a " +
                    "Valheim update changing a patched method; the remaining fixes are unaffected.");
            } else {
                Log.LogInfo($"{applied} fix(es) applied.");
            }
        }

        public void OnDestroy() {
            harmony?.UnpatchSelf();
        }
    }
}
