using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Treasure Chest Discovery: counts each treasure chest once per player in their treasure
    // stats, and only the chests meant to count.
    //
    // Container.Interact counts a container with a discover stat toward the player's stat the first
    // time each player opens it, and asks the container's owner to set a "Discovered_<player>" flag
    // that marks it as counted. It sends that request as "discovered", but Container.Awake registers
    // the handler as "RPC_Discovered", so the owner logs "Failed to find rpc method", the flag is
    // never set and every open counts again. Separately, the containers meant to count nothing (the
    // plain meadows and heath chests, shipwreck chests, the player-built Grausten chest, wardrobe and
    // sled, and others) still hold the number None had before the stat list grew. It now reads as
    // TreeBirch or BuildClusterSeasonal, so opening one counts a felled birch tree and sends the
    // request too.
    //
    // A transpiler on Interact treats any discover stat other than the three treasure stats as
    // none, and sends the request under the registered name, which a vanilla owner handles too. A
    // postfix on Awake also registers the sent name, so a request from a player without the mod is
    // recorded (or ignored, for a container meant to count nothing) when the owner has the mod.
    // Renaming and registering stay off when the game's two names agree.
    //
    // Both: the player who opens the container sends the request, and its owner handles it.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(Container))]
    internal static class TreasureChestDiscoveryPatch {
        internal static FixToggle Enabled;

        private const string SentName = "discovered";
        private const string RegisteredName = "RPC_Discovered";

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TreasureChestDiscoveryPatch),
                ValConfig.SectionCorrectness,
                "Fix Treasure Chest Discovery",
                true,
                "Counts each treasure chest once in your treasure stats instead of on every open, and stops " +
                "chests meant to count nothing, such as the plain meadows chest, the Grausten chest and the " +
                "wardrobe, from counting a felled birch tree each time they are opened. Also stops the " +
                "'Failed to find rpc method' warning logged on those opens. Players without the mod are " +
                "covered on chests owned by a machine that has it.");
        }

        private static readonly FieldInfo DiscoverStatField =
            AccessTools.Field(typeof(Container), nameof(Container.m_discoverStat));
        private static readonly MethodInfo DiscoverStatMethod =
            AccessTools.Method(typeof(TreasureChestDiscoveryPatch), nameof(DiscoverStat));
        private static readonly MethodInfo SentNameMethod =
            AccessTools.Method(typeof(TreasureChestDiscoveryPatch), nameof(DiscoveredRpcName));

        private static bool? _namesDiffer;

        // Whether the game still sends the request under a name it does not register. Read from the
        // game's own IL, before any mod's transpiler.
        private static bool NamesDiffer() {
            if (_namesDiffer == null) {
                _namesDiffer =
                    LoadsString(AccessTools.Method(typeof(Container), "Awake"), RegisteredName) &&
                    LoadsString(AccessTools.Method(typeof(Container), nameof(Container.Interact)), SentName);

                if (!_namesDiffer.Value) {
                    Logger.LogInfo("Fix Treasure Chest Discovery: this game version sends the chest discovery " +
                                   "request under the name it registers, so only the stat check is applied.");
                }
            }

            return _namesDiffer.Value;
        }

        private static bool LoadsString(MethodBase method, string value) {
            if (method == null) { return false; }

            try {
                foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method)) {
                    if (instruction.opcode == OpCodes.Ldstr && instruction.operand as string == value) { return true; }
                }
            } catch (Exception ex) {
                Logger.LogWarning($"Fix Treasure Chest Discovery: could not read {method.Name}: {ex.Message}");
            }

            return false;
        }

        // The stats a container is meant to count. Any other value on a container is a stale None.
        private static bool IsTreasureStat(PlayerStatType stat) =>
            stat == PlayerStatType.TreasureBuriedFound ||
            stat == PlayerStatType.TreasureDungeonFound ||
            stat == PlayerStatType.TreasureLocationFound;

        // Interact's reads of m_discoverStat.
        private static PlayerStatType DiscoverStat(Container container) {
            PlayerStatType stat = container.m_discoverStat;
            if (Enabled == null || !Enabled.Value || IsTreasureStat(stat)) { return stat; }

            return PlayerStatType.None;
        }

        // The name Interact sends: the registered one, or the game's own again while the fix is off.
        private static string DiscoveredRpcName() => Enabled != null && Enabled.Value ? RegisteredName : SentName;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. All or nothing: the stat check without
        // the renaming would leave real treasure chests counting on every open.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(Container.Interact))]
        private static IEnumerable<CodeInstruction> InteractTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (DiscoverStatField == null || DiscoverStatMethod == null || SentNameMethod == null) {
                PatchHelper.WarnOnce("Container.Interact: a member this fix needs could not be resolved, so it is inactive here.");
                return instructions;
            }

            IEnumerable<CodeInstruction> checkedStat = PatchHelper.ReplaceAllMatches(
                instructions,
                new[] { new CodeMatch(OpCodes.Ldfld, DiscoverStatField) },
                new[] { new CodeInstruction(OpCodes.Call, DiscoverStatMethod) },
                "Container.Interact",
                expected: 2);
            if (ReferenceEquals(checkedStat, instructions) || !NamesDiffer()) { return checkedStat; }

            IEnumerable<CodeInstruction> renamed = PatchHelper.ReplaceAllMatches(
                checkedStat,
                new[] { new CodeMatch(OpCodes.Ldstr, SentName) },
                new[] { new CodeInstruction(OpCodes.Call, SentNameMethod) },
                "Container.Interact",
                expected: 1);
            return ReferenceEquals(renamed, checkedStat) ? instructions : renamed;
        }

        // Every container with a discover stat, stale or not: a player without the mod sends the
        // request from all of them. Registered even while the fix is off, so turning it on later
        // covers containers that are already loaded.
        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(Container __instance) {
            if (__instance.m_discoverStat == PlayerStatType.None) { return; }

            ZNetView nview = __instance.m_nview;
            if (nview == null || nview.GetZDO() == null) { return; }

            // Register throws on a name the view already has.
            if (nview.m_functions.ContainsKey(SentName.GetStableHashCode()) || !NamesDiffer()) { return; }

            nview.Register<int>(SentName, (sender, keyHash) => {
                if (Enabled != null && Enabled.Value && IsTreasureStat(__instance.m_discoverStat)) {
                    __instance.RPC_Discovered(sender, keyHash);
                }
            });
        }
    }
}
