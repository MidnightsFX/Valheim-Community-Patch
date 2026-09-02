using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Utils;

namespace ValheimCommunityPatch
{
    // Not EveryoneMustHaveMod: every fix is safe one-sided. The mod adds no prefabs, recipes or
    // save data, and its one custom RPC is ignored by peers without it. VersionCheckOnly still
    // refuses a client and server on different versions of this mod, which is the one case where
    // the two sides could disagree about behaviour.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.VersionCheckOnly, VersionStrictness.Minor)]
    internal class ValheimCommunityPatch : BaseUnityPlugin
    {
        public const string PluginGUID = "MidnightsFX.ValheimCommunityPatch";
        public const string PluginName = "ValheimCommunityPatch";
        public const string PluginVersion = "0.21.0";

        internal static ManualLogSource Log;

        private readonly Harmony harmony = new Harmony(PluginGUID);

        public void Awake() {
            Log = Logger;
            ValConfig.Bind(Config);

            // An engine flag with no Harmony patch behind it.
            Patches.Performance.CollisionCallbackReusePatch.Apply();

            ApplyPatches();
        }

        // Each patch class is applied on its own rather than through Harmony.PatchAll, so a game
        // update that breaks one target logs one error and leaves every other fix working.
        //
        // Two rules every patch class follows:
        //
        //  - Transpilers run at Priority.Last. Harmony applies transpilers in priority order, so
        //    ours see IL that other mods have already rewritten. Each of ours counts what it
        //    changed and stands down when the count is wrong, so where another mod has fixed the
        //    same defect its version wins instead of both mods breaking the method.
        //
        //  - Client-only fixes (see PatchSide) are not applied on a dedicated server. Whether the
        //    process is headless is the only environment fact known this early, and it never
        //    changes, so it is safe to decide at patch time. Server fixes are always applied
        //    because this process may start hosting a world later.
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
                    Log.LogDebug($"Skipped {type.Name}: client-only, and this process is headless.");
                    continue;
                }

                try {
                    harmony.CreateClassProcessor(type).Patch();
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

        public void OnDestroy() {
            harmony?.UnpatchSelf();
        }
    }
}
