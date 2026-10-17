using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Love Potion Spawns: makes the Love Potion attract starred trolls again, which the game
    // stopped doing in the 2026-09-09 update.
    //
    // The potion's status effect (Potion_TrollPheromones) names the Troll prefab and carries a spawn
    // chance, a creature cap, a minimum level and a level-up multiplier. SpawnSystem.UpdateSpawnList
    // applied them to troll spawners near a drinker until the Deep North rewrite of that method
    // dropped the code; nothing reads those fields now, and only the hearts above trolls remain.
    // The old code also replaced the spawn chance rather than raising it, cutting troll raid spawns
    // to a quarter; its cap never let a second troll spawn, because the group size still read the
    // spawner's own cap; and it only saw a drinker whose own machine ran the zone's spawning.
    //
    // A prefix on UpdateSpawnList raises the fields of each spawner the potion targets for that one
    // call and a finalizer restores them: spawn chance and cap to at least the potion's, minimum
    // level to the potion's, and maximum level to one above that, so raid trolls (maximum level 1)
    // can reach two stars. A postfix on GetLevelUpChance applies the multiplier. The potion counts
    // in the zone the drinker stands in. Each client writes its own active pheromone effect to its
    // player ZDO, so whichever client runs the zone's spawning sees a drinker on another machine.
    //
    // Client: spawning only runs on a machine with a local player, and only the drinker's own client
    // knows its status effects. A drinker without the mod counts only where their machine spawns.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(SpawnSystem))]
    internal static class LovePotionSpawnPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(LovePotionSpawnPatch),
                ValConfig.SectionCorrectness,
                "Fix Love Potion Spawns",
                true,
                "Makes the Love Potion attract trolls again: while it lasts, trolls spawn more often in " +
                "the zone you stand in, a second one can join the first, and they come with at least " +
                "one star. The game stopped applying it in the Deep North update. Never lowers a troll " +
                "raid's spawn chance, lets raid trolls reach two stars, and works for every player in " +
                "multiplayer, not only the one whose machine runs the zone's spawning.");
        }

        // On a player's ZDO: the name hash of their active spawn-changing pheromone effect, or 0.
        private static readonly int PheromoneKey = "VCP_Pheromone".GetStableHashCode();

        private struct Raised {
            internal SpawnSystem.SpawnData Spawner;
            internal float SpawnChance;
            internal int MaxSpawned;
            internal int MinLevel;
            internal int MaxLevel;
            internal float LevelUpMultiplier;
        }

        private static readonly List<SE_Stats> Active = new List<SE_Stats>();

        // Spawners raised for the UpdateSpawnList call in progress, with their own values.
        private static readonly List<Raised> RaisedSpawners = new List<Raised>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SpawnSystem.UpdateSpawnList))]
        private static void UpdateSpawnListPrefix(List<SpawnSystem.SpawnData> spawners) {
            if (Enabled == null || !Enabled.Value || spawners == null) { return; }

            // UpdateSpawning has just filled this with the players inside the zone.
            CollectActive(SpawnSystem.m_tempNearPlayers);
            if (Active.Count == 0) { return; }

            foreach (SpawnSystem.SpawnData spawner in spawners) { Raise(spawner); }

            Active.Clear();
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(SpawnSystem.UpdateSpawnList))]
        private static void UpdateSpawnListFinalizer() => Restore();

        // Spawn is the only caller of this overload, and only runs inside UpdateSpawnList.
        [HarmonyPostfix]
        [HarmonyPatch(nameof(SpawnSystem.GetLevelUpChance), typeof(Vector3), typeof(SpawnSystem.SpawnData))]
        private static void GetLevelUpChancePostfix(SpawnSystem.SpawnData creature, ref float __result) {
            for (int i = 0; i < RaisedSpawners.Count; i++) {
                if (ReferenceEquals(RaisedSpawners[i].Spawner, creature)) {
                    __result *= RaisedSpawners[i].LevelUpMultiplier;
                    return;
                }
            }
        }

        // The game publishes its status effect attribute bits from here the same way. Runs for every
        // character this machine owns, so anything but the local player leaves at the first check.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.Update))]
        private static void SEManUpdatePostfix(SEMan __instance, ZDO zdo) {
            Player localPlayer = Player.m_localPlayer;
            if (ReferenceEquals(localPlayer, null) || !ReferenceEquals(__instance.m_character, localPlayer)) { return; }
            if (zdo == null) { return; }

            // Cleared while the fix is off, so other machines stop counting this player.
            int hash = Enabled != null && Enabled.Value ? SpawnPheromoneHash(__instance) : 0;
            if (zdo.GetInt(PheromoneKey) != hash) { zdo.Set(PheromoneKey, hash); }
        }

        private static bool ChangesSpawns(SE_Stats stats) =>
            stats != null && stats.m_pheromoneTarget != null &&
            (stats.m_pheromoneSpawnChanceOverride > 0f || stats.m_pheromoneMaxInstanceOverride > 0 ||
             stats.m_pheromoneSpawnMinLevel > 0 || stats.m_pheromoneLevelUpMultiplier > 1f);

        // One effect per player: vanilla has a single such potion. Bug Repellent targets Deathsquitos
        // but changes no spawns, so it is skipped rather than hiding a Love Potion.
        private static int SpawnPheromoneHash(SEMan seman) {
            List<StatusEffect> effects = seman.GetStatusEffects();
            for (int i = 0; i < effects.Count; i++) {
                if (effects[i] is SE_Stats stats && ChangesSpawns(stats)) { return stats.NameHash(); }
            }

            return 0;
        }

        private static void CollectActive(List<Player> players) {
            Active.Clear();

            foreach (Player player in players) {
                if (player == null) { continue; }

                // The local player's own effects are exact; anyone else's come through their ZDO.
                if (ReferenceEquals(player, Player.m_localPlayer)) {
                    foreach (StatusEffect effect in player.GetSEMan().GetStatusEffects()) { Add(effect as SE_Stats); }
                    continue;
                }

                ZNetView nview = player.m_nview;
                if (nview == null || !nview.IsValid()) { continue; }

                int hash = nview.GetZDO().GetInt(PheromoneKey);
                if (hash != 0 && ObjectDB.instance != null) { Add(ObjectDB.instance.GetStatusEffect(hash) as SE_Stats); }
            }
        }

        private static void Add(SE_Stats stats) {
            if (ChangesSpawns(stats) && !Active.Contains(stats)) { Active.Add(stats); }
        }

        private static void Raise(SpawnSystem.SpawnData spawner) {
            if (spawner == null || spawner.m_prefab == null) { return; }

            // A spawner listed twice is raised once, so Restore puts back its own values.
            for (int i = 0; i < RaisedSpawners.Count; i++) {
                if (ReferenceEquals(RaisedSpawners[i].Spawner, spawner)) { return; }
            }

            float chance = spawner.m_spawnChance;
            int cap = spawner.m_maxSpawned;
            int minLevel = spawner.m_minLevel;
            float levelUp = 1f;
            bool targeted = false;

            // Several drinkers in one zone give the strongest of each value, not a product.
            foreach (SE_Stats stats in Active) {
                if (!ReferenceEquals(stats.m_pheromoneTarget, spawner.m_prefab)) { continue; }

                targeted = true;
                chance = Mathf.Max(chance, stats.m_pheromoneSpawnChanceOverride);

                // 0 is no cap, which the potion must not turn into one. The cap also bounds how many
                // missed spawn rounds a zone catches up on, so that rises with it.
                if (cap > 0) { cap = Mathf.Max(cap, stats.m_pheromoneMaxInstanceOverride); }

                minLevel = Mathf.Max(minLevel, stats.m_pheromoneSpawnMinLevel);
                levelUp = Mathf.Max(levelUp, stats.m_pheromoneLevelUpMultiplier);
            }

            if (!targeted) { return; }

            RaisedSpawners.Add(new Raised {
                Spawner = spawner,
                SpawnChance = spawner.m_spawnChance,
                MaxSpawned = spawner.m_maxSpawned,
                MinLevel = spawner.m_minLevel,
                MaxLevel = spawner.m_maxLevel,
                LevelUpMultiplier = levelUp,
            });

            spawner.m_spawnChance = chance;
            spawner.m_maxSpawned = cap;
            if (minLevel > spawner.m_minLevel) {
                spawner.m_minLevel = minLevel;
                spawner.m_maxLevel = Mathf.Max(spawner.m_maxLevel, minLevel + 1);
            }
        }

        private static void Restore() {
            foreach (Raised raised in RaisedSpawners) {
                raised.Spawner.m_spawnChance = raised.SpawnChance;
                raised.Spawner.m_maxSpawned = raised.MaxSpawned;
                raised.Spawner.m_minLevel = raised.MinLevel;
                raised.Spawner.m_maxLevel = raised.MaxLevel;
            }

            RaisedSpawners.Clear();
        }
    }
}
