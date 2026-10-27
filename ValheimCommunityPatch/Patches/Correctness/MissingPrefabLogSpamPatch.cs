using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Missing Prefab Log Spam: objects whose prefab no longer exists are reported once per
    // prefab and summed up at logout, instead of one warning per object per spawn pass.
    //
    // ZNetScene.CreateObject warns "Missing prefab hash" for every ZDO whose prefab is not
    // registered, which is what a world looks like after a mod that added objects to it is removed.
    // The host destroys each such ZDO after one warning and logs that too; a client cannot destroy
    // them, so it warns again on every spawn pass for as long as they are in range. Entering an area
    // such a mod left behind writes hundreds of lines in a second, and the console writer alone then
    // costs a frame.
    //
    // Transpilers point the warning in CreateObject and the "Destroyed invalid prefab ZDO" lines in
    // CreateObjectsSorted and CreateDistantObjects at sinks here; SpawnQueueCachePatch's replay of
    // CreateObjectsSorted calls the destroyed sink directly. The first sighting of a hash logs one
    // warning naming it, every later line goes to the debug sink, and ZNetScene.Shutdown logs one
    // summary with a count per hash. The string building stays; the log write and its stack trace
    // are the cost. CreateObject only runs on the main thread, so the counters are unguarded.
    //
    // Both: hosts and dedicated servers destroy the objects, clients keep warning about them.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class MissingPrefabLogSpamPatch {
        private const string FixName = "Fix Missing Prefab Log Spam";
        private const string MissingPrefix = "Missing prefab hash: ";

        // After this many distinct hashes the first-sighting warning stops too: a removed content
        // mod can leave hundreds, and the summary at logout carries them all.
        private const int FirstSightingLimit = 25;

        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(MissingPrefabLogSpamPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Reports an object whose prefab no longer exists once per prefab, with a summary at logout, " +
                "instead of warning about every such object on every spawn pass. Vanilla writes hundreds of " +
                "lines in a second when entering an area that a removed mod left objects in, and a client " +
                "keeps writing them for as long as those objects are in range. The lines are still visible " +
                "with EnableDebugMode on. Changing this requires a game restart.");
        }

        private static readonly MethodInfo LogMethod = AccessTools.Method(typeof(ZLog), nameof(ZLog.Log));
        private static readonly MethodInfo LogWarningMethod = AccessTools.Method(typeof(ZLog), nameof(ZLog.LogWarning));
        private static readonly MethodInfo MissingSinkMethod = AccessTools.Method(typeof(MissingPrefabLogSpamPatch), nameof(MissingSink));
        private static readonly MethodInfo DestroyedSinkMethod = AccessTools.Method(typeof(MissingPrefabLogSpamPatch), nameof(DestroyedSink));

        // Lines seen per distinct message, which carries the hash. On the host one line is one
        // object; on a client the same object warns on every pass.
        private static readonly Dictionary<string, int> Sightings = new Dictionary<string, int>();
        private static int _destroyed;

        private static bool On => Enabled != null && Enabled.Value;

        // Every ZLog.LogWarning in CreateObject lands here; only the missing-prefab one is counted,
        // the rest go to the game's log as before.
        private static void MissingSink(object message) {
            string text = message as string;
            if (!On || text == null || !text.StartsWith(MissingPrefix, StringComparison.Ordinal)) {
                ZLog.LogWarning(message);
                return;
            }

            if (Sightings.TryGetValue(text, out int seen)) {
                Sightings[text] = seen + 1;
                Logger.DebugSink(message);
                return;
            }

            Sightings[text] = 1;
            if (Sightings.Count > FirstSightingLimit) {
                Logger.DebugSink(message);
                return;
            }

            Logger.LogWarning(
                $"{text} - no loaded mod registers this prefab, most likely because the mod that added these " +
                "objects is no longer installed. The host removes them. Further objects with this prefab go " +
                "to debug logging, and a summary is logged at logout.");
        }

        // The game's "Destroyed invalid prefab ZDO" line, and SpawnQueueCachePatch's copy of it.
        internal static void DestroyedSink(object message) {
            if (!On) {
                ZLog.Log(message);
                return;
            }

            _destroyed++;
            Logger.DebugSink(message);
        }

        // Priority.Last on all three: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("CreateObject")]
        private static IEnumerable<CodeInstruction> CreateObjectTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (!On) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, LogWarningMethod, MissingSinkMethod, "ZNetScene.CreateObject");
        }

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("CreateObjectsSorted")]
        private static IEnumerable<CodeInstruction> CreateObjectsSortedTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (!On) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, LogMethod, DestroyedSinkMethod, "ZNetScene.CreateObjectsSorted", 1);
        }

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("CreateDistantObjects")]
        private static IEnumerable<CodeInstruction> CreateDistantObjectsTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (!On) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, LogMethod, DestroyedSinkMethod, "ZNetScene.CreateDistantObjects", 1);
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                if (Sightings.Count == 0 && _destroyed == 0) { return; }

                StringBuilder summary = new StringBuilder("Missing prefabs this session: ");
                bool first = true;
                foreach (KeyValuePair<string, int> sighting in Sightings) {
                    if (!first) { summary.Append(", "); }
                    first = false;
                    summary.Append(sighting.Key.Substring(MissingPrefix.Length)).Append(" (").Append(sighting.Value).Append(sighting.Value == 1 ? " line)" : " lines)");
                }

                summary.Append("; ").Append(_destroyed).Append(_destroyed == 1 ? " object removed here." : " objects removed here.");
                Logger.LogInfo(summary.ToString());

                Sightings.Clear();
                _destroyed = 0;
            }
        }
    }
}
