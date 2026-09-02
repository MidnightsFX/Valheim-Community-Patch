using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Utils;

namespace ValheimCommunityPatch
{
    // Not EveryoneMustHaveMod: every fix here is safe one-sided. The mod adds no prefabs, recipes or
    // save data, and its one custom RPC is ignored by peers that do not know it, so a modded client
    // can join a vanilla server and a modded server can accept vanilla clients. Roughly half the
    // fixes only do anything on one side; the README tags each one.
    //
    // VersionCheckOnly still enforces VersionStrictness when both sides do have it, which is the case
    // that matters: a client and server on different versions of this mod can disagree about
    // behaviour, and that should be refused up front rather than debugged later.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.VersionCheckOnly, VersionStrictness.Minor)]
    internal class ValheimCommunityPatch : BaseUnityPlugin
    {
        public const string PluginGUID = "MidnightsFX.ValheimCommunityPatch";
        public const string PluginName = "ValheimCommunityPatch";
        public const string PluginVersion = "0.21.0";

        internal static ManualLogSource Log;
        internal ValConfig cfg;

        private readonly Harmony harmony = new Harmony(PluginGUID);

        public void Awake() {
            Log = this.Logger;
            cfg = new ValConfig(Config);

            // Engine-flag fixes with no Harmony patch and no config apply here.
            Patches.Performance.CollisionCallbackReusePatch.Apply();

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
        // Correctness and terrain fixes check their own config toggle at runtime, so a fix switched
        // off in the config is a cheap bool check, not an unpatched method. The exceptions are
        // transpilers, which read their toggle at patch time and therefore need a restart to change.
        // Performance fixes have no toggle - they are always on.
        //
        // Every transpiler in this mod is declared [HarmonyPriority(Priority.Last)], and that is a
        // compatibility rule rather than a preference. Harmony rebuilds a method from its original IL
        // each time a patch is added and applies all transpilers in descending priority order, so
        // priority - not BepInEx load order - decides who sees vanilla IL. Ours rewrite call operands
        // in place, which is exactly what a mod using CodeMatcher.ThrowIfNotMatch is matching on: if we
        // go first, their matcher misses, they throw, and HarmonyX drops the whole method - losing both
        // mods' fixes and anyone else's patches on it. Ours are the tolerant side, since every one of
        // them counts what it rewrote and returns the instructions untouched when the count is wrong,
        // so ours are the ones that yield. This is what lets us share Container.RPC_RequestOpen,
        // ZSteamSocket.SendQueuedPackages and Projectile.FixedUpdate with ComfyMods' BetterZeeLog.
        //
        // The one hole left: a mod that also uses Priority.Last and hard-matches IL puts us back on
        // registration order. Nothing to do about that from here, but it is where to look first if
        // this class of breakage ever shows up again.
        //
        // The one thing decided here rather than at runtime is the side. Every patch class declares a
        // [PatchSide], and the client-only ones are not applied at all on a dedicated server, where
        // nothing could ever reach them. Headless-ness is the only environment question answerable
        // this early - ZNet does not exist yet - and it is fixed for the life of the process, which is
        // what makes it safe to patch against. Server-side fixes are never skipped for exactly that
        // reason in reverse: this process may start hosting a world later in the same run.
        private void ApplyPatches() {
            int applied = 0, failed = 0, skipped = 0;
            int client = 0, server = 0, both = 0;

            // Read once. ValConfig is constructed before this runs so the entry exists, but the null
            // check matches how every fix reads its own toggle and keeps the ordering from being load
            // bearing.
            bool patchEverySide = ValConfig.PatchEverySide != null && ValConfig.PatchEverySide.Value;

            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes()) {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) { continue; }

                Side side = PatchSideAttribute.Of(type);

                // A fix is one top-level patch class. Some own nested hook classes, which reach this
                // loop as types in their own right and are patched normally, but counting them would
                // inflate the totals below past what the README lists.
                bool isFix = type.DeclaringType == null;

                if (side == Side.Client && RunMode.IsHeadless && !patchEverySide) {
                    if (isFix) { skipped++; }
                    Logger.LogDebug($"Skipped {type.Name}: client-only, and this process is headless.");
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
                    Logger.LogDebug($"Applied {type.Name} {PatchSideAttribute.Tag(side)}.");
                } catch (Exception ex) {
                    failed++;
                    Log.LogError($"Could not apply {type.Name}: {ex}");
                }
            }

            // Always break the count down by side: on a one-sided install this line is what tells an
            // admin what they actually got, without having to cross-reference the README.
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
