using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Tolerate Duplicate ZDOs On Load: a save containing two ZDOs with the same id loads instead
    // of aborting.
    //
    // ZDOMan.Load and ZDOMan.LoadChunks - the single-file and chunked save formats - index every
    // ZDO with Dictionary.Add, which throws on a duplicate key. A save damaged by a crash
    // mid-write, a botched world merge or a mod minting its own ids then refuses to load at all.
    //
    // A transpiler swaps every such Add for an indexer write that keeps the later entry and logs
    // a warning. One of the two was already unreachable in vanilla's index, so recovering the
    // world is strictly better than refusing it. The expected counts are pinned per load path, so
    // a game update that adds or removes a branch stands the fix down with a log line rather than
    // silently covering less than it claims.
    //
    // Server: neither load path runs off the host. Provenance: ComfyMods/Atlas (GPL-3.0, redseiko).
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class ZdoLoadDuplicatePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ZdoLoadDuplicatePatch),
                ValConfig.SectionCorrectness,
                "Tolerate Duplicate ZDOs On Load",
                true,
                "Recovers a world whose save contains duplicate ZDO ids instead of aborting the load with " +
                "an exception. Changing this requires a game restart.");
        }

        private static readonly MethodInfo DictionaryAddMethod =
            AccessTools.Method(typeof(Dictionary<ZDOID, ZDO>), nameof(Dictionary<ZDOID, ZDO>.Add));
        private static readonly MethodInfo AddOrReplaceMethod =
            AccessTools.Method(typeof(ZdoLoadDuplicatePatch), nameof(AddOrReplace));

        // Same stack as the instance Add it replaces: (dictionary, key, value).
        private static void AddOrReplace(Dictionary<ZDOID, ZDO> objectsById, ZDOID uid, ZDO zdo) {
            if (objectsById.ContainsKey(uid)) {
                Logger.LogWarning(
                    $"Duplicate ZDO id {uid} in the save file; keeping the later one. " +
                    "This world was saved in a damaged state, but loading will continue.");
            }

            objectsById[uid] = zdo;
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        // Three sites: the pre-chunk format's two per-ZDO branches and the bulk index pass.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(ZDOMan.Load))]
        private static IEnumerable<CodeInstruction> LoadTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, DictionaryAddMethod, AddOrReplaceMethod, "ZDOMan.Load", expected: 3);
        }

        // The chunked save format. Two sites: the portal chunk and the bulk index pass.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(ZDOMan.LoadChunks))]
        private static IEnumerable<CodeInstruction> LoadChunksTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, DictionaryAddMethod, AddOrReplaceMethod, "ZDOMan.LoadChunks", expected: 2);
        }
    }
}
