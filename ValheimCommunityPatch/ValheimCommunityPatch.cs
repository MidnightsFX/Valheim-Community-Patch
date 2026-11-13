using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Utils;

namespace ValheimCommunityPatch
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    // All dependencies here are required to support automatically disabling fixes that would conflict.
    [BepInDependency(Patches.Performance.CreatureSyncDeadbandPatch.NpsGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Patches.Correctness.StationRefundPatch.EternalFireGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Patches.Correctness.StationRefundPatch.AutomaticFuelGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(HearthBelowCompat.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Patches.Correctness.RareDropPityPatch.SaveYourLuckGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.VersionCheckOnly, VersionStrictness.Minor)]
    internal class ValheimCommunityPatch : BaseUnityPlugin
    {
        public const string PluginGUID = "MidnightsFX.ValheimCommunityPatch";
        public const string PluginName = "ValheimCommunityPatch";
        public const string PluginVersion = "0.46.0";

        internal static ManualLogSource Log;

        /// <summary>Unity's main thread, for API calls that must come from it.</summary>
        internal static int MainThreadId;

        private readonly Harmony harmony = new Harmony(PluginGUID);

        // No unpatch on exit
        private static bool _quitting;

        public void Awake() {
            Log = Logger;
            MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            UnityEngine.Application.quitting += OnQuitting;
            ValConfig.Bind(Config);

            // An engine flag with no Harmony patch behind it.
            Patches.Performance.CollisionCallbackReusePatch.Apply();
            FixRegistry.RecordPatched(typeof(Patches.Performance.CollisionCallbackReusePatch), patchedAnything: true);
            ApplyPatches();
        }

        // Each patch class is applied on its own rather than through Harmony.PatchAllg.
        //
        // Three rules every patch class follows:
        //
        //  - Transpilers run at Priority.Last. Harmony applies transpilers in priority order, so
        //    this ensures others changes take priority and the VCP feature is disabled.
        //
        //  - A prefix that replaces a method other mods steer through its arguments runs at
        //    Priority.Last and honors __runOriginal, let others control the method instead if they do
        //
        //  - Client-only fixes (see PatchSide) are not applied on a dedicated server. Server fixes
        //    are applied on a client, due to integrated servers
        private void ApplyPatches() {
            int applied = 0, failed = 0, skipped = 0;
            int client = 0, server = 0, both = 0;

            bool patchEverySide = ValConfig.PatchEverySide != null && ValConfig.PatchEverySide.Value;

            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes()) {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) { continue; }

                Side side = PatchSideAttribute.Of(type);

                // A fix is one top-level patch class. Nested hook classes are patched too, but
                // counting them would inflate the totals past what the README lists.
                bool isFix = type.DeclaringType == null;

                if (side == Side.Client && RunMode.IsHeadless && !patchEverySide) {
                    if (isFix) { skipped++; }
                    FixRegistry.RecordSkipped(type);
                    Log.LogDebug($"Skipped {type.Name}: client-only, and this process is headless.");
                    continue;
                }

                try {
                    // Empty when the class's Prepare declined, which is how fixes stand down at
                    // startup for a mod they know (HearthBelow, NPS).
                    List<MethodInfo> patched = harmony.CreateClassProcessor(type).Patch();
                    FixRegistry.RecordPatched(type, patched != null && patched.Count > 0);
                    if (isFix) {
                        applied++;
                        switch (side) {
                            case Side.Client: client++; break;
                            case Side.Server: server++; break;
                            default: both++; break;
                        }
                    }
                    Log.LogDebug($"Applied {type.Name} {PatchSideAttribute.Tag(side)}.");
                } catch (Exception ex) {
                    failed++;
                    FixRegistry.RecordFailed(type);
                    Log.LogError($"Could not apply {type.Name}: {ex}");
                }
            }

            // Broken down by side so a one-sided install can see what it actually got.
            Log.LogInfo($"{applied} fix(es) applied: {server} server, {both} both, {client} client.");

            if (skipped > 0) {
                Log.LogInfo(
                    $"{skipped} client-only fix(es) not applied: this process has no graphics device, " +
                    "so it is a dedicated server and nothing would ever reach them. If that is wrong, " +
                    "set 'Patch Every Side' in the config.");
            }

            if (failed > 0) {
                Log.LogWarning(
                    $"{failed} fix(es) failed. The failures above are usually caused by a Valheim " +
                    "update changing a patched method; the remaining fixes are unaffected.");
            }
        }

        private static void OnQuitting() => _quitting = true;

        public void OnDestroy() {
            // Nothing to restore or unpatch for a process that is exiting.
            if (_quitting) { return; }

            UnityEngine.Application.quitting -= OnQuitting;

            // Fixes that removed entries from a vanilla collection have to put them back before
            // the patches that would have restored them go away.
            Patches.Performance.ZsfxIdleDormancyPatch.RestoreAll();
            harmony?.UnpatchSelf();
        }
    }
}
