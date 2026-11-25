using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // A null entry in a spawner's creature table, left by an update or removed mod, throws and kills that spawner.
    // Postfixes SpawnArea.Awake to remove the null entries from m_prefabs once.
    // Spawners keep working with whatever creatures are still valid.
    // Provenance: the same fix as ComfyMods/LetMePlay (GPL-3.0, redseiko).
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(SpawnArea))]
    internal static class SpawnAreaNullPrefabPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SpawnAreaNullPrefabPatch),
                ValConfig.SectionCorrectness,
                "Fix Spawner Null Prefabs",
                true,
                "Removes missing creatures from spawner tables on load, so one left by a game update or a " +
                "removed creature mod no longer kills that spawner.");
        }

        private static readonly System.Predicate<SpawnArea.SpawnData> IsNullPrefab = data => data == null || data.m_prefab == null;

        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(SpawnArea __instance) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (__instance.m_prefabs == null) { return; }

            int removed = __instance.m_prefabs.RemoveAll(IsNullPrefab);
            if (removed > 0) {
                Logger.LogInfo($"Removed {removed} null spawn entries from {__instance.name} at {__instance.transform.position}.");
            }
        }
    }
}
