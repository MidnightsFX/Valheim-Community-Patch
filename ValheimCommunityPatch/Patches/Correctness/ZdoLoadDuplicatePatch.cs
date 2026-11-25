using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // ZDOMan indexes loaded ZDOs with Dictionary.Add, so a save holding two with the same id refuses to load at all.
    // Transpiles ZDOMan.Load and ZDOMan.LoadChunks to swap each Add for an indexer write that keeps the later ZDO.
    // A world damaged by a crash mid-write, a botched merge or a mod minting its own ids loads with a warning.
    // Provenance: ComfyMods/Atlas (GPL-3.0, redseiko).
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class ZdoLoadDuplicatePatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ZdoLoadDuplicatePatch),
                ValConfig.SectionCorrectness,
                "Tolerate Duplicate ZDOs On Load",
                true,
                "Loads a world whose save contains duplicate object ids, keeping the later copy, instead of " +
                "failing to load; turning it on needs a game restart.");
        }

        private static readonly MethodInfo DictionaryAddMethod =
            AccessTools.Method(typeof(Dictionary<ZDOID, ZDO>), nameof(Dictionary<ZDOID, ZDO>.Add));
        private static readonly MethodInfo AddOrReplaceMethod =
            AccessTools.Method(typeof(ZdoLoadDuplicatePatch), nameof(AddOrReplace));

        // Same stack as the instance Add it replaces: (dictionary, key, value). One of two duplicates was
        // already unreachable in vanilla's index, so keeping the later one beats refusing the world.
        private static void AddOrReplace(Dictionary<ZDOID, ZDO> objectsById, ZDOID uid, ZDO zdo) {
            if (!Enabled.Value) {
                objectsById.Add(uid, zdo);
                return;
            }

            if (objectsById.ContainsKey(uid)) {
                Logger.LogWarning(
                    $"Duplicate ZDO id {uid} in the save file; keeping the later one. " +
                    "This world was saved in a damaged state, but loading will continue.");
            }

            objectsById[uid] = zdo;
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. The expected counts are pinned, so a game
        // update that adds or drops a branch stands the fix down with a log line instead of covering less.
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
