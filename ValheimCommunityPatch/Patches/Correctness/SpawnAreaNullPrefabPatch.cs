using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: SpawnArea.m_prefabs (used by tar pits, greydwarf nests, draugr piles and every
    // other spawner) can contain entries whose m_prefab is null - typically after a content update or
    // when another mod removes a creature. Nothing filters them, so SelectWeightedPrefab can hand a
    // null prefab to SpawnOne, and IsSpawnPrefab dereferences prefab.m_prefab.name while counting
    // existing instances. Either throws, and the exception aborts that spawner permanently.
    //
    // Fix: drop null entries once, on Awake. A spawner with a partially broken table then keeps working
    // with the entries that are still valid instead of dying outright.
    //
    // Provenance: same fix as ComfyMods/LetMePlay (GPL-3.0, redseiko).
    [HarmonyPatch(typeof(SpawnArea))]
    internal static class SpawnAreaNullPrefabPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                ValConfig.SectionCorrectness,
                "Fix Spawner Null Prefabs",
                true,
                "Removes null entries from spawner (SpawnArea) tables on load. Without it a single missing " +
                "creature prefab - common after a game update or a removed creature mod - throws during " +
                "spawn selection and silently kills that spawner.");
        }

        // Cached so the removal does not allocate a delegate per spawner.
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
