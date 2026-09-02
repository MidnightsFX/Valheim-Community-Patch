using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Spawner Null Prefabs: keeps a spawner working when its creature table has a null entry.
    //
    // SpawnArea.m_prefabs (tar pits, greydwarf nests, draugr piles and every other spawner) can
    // hold entries whose prefab is null after a content update or a removed creature mod. Nothing
    // filters them, so SelectWeightedPrefab or IsSpawnPrefab dereferences one and the exception
    // permanently kills that spawner.
    //
    // A postfix on SpawnArea.Awake removes the null entries once, so the spawner keeps working
    // with whatever is still valid.
    //
    // Both: spawners near world origin sit inside a dedicated server's own active area.
    // Provenance: the same fix as ComfyMods/LetMePlay (GPL-3.0, redseiko).
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(SpawnArea))]
    internal static class SpawnAreaNullPrefabPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SpawnAreaNullPrefabPatch),
                ValConfig.SectionCorrectness,
                "Fix Spawner Null Prefabs",
                true,
                "Removes null entries from spawner (SpawnArea) tables on load. Without it a single missing " +
                "creature prefab - common after a game update or a removed creature mod - throws during " +
                "spawn selection and silently kills that spawner.");
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
