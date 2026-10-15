using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Biome Sector Lookup: near biome borders, the player's biome, the weather, the map's biome
    // name and spawn level-ups follow the biome actually there, and the "GetBiome error" warning no
    // longer fills the log.
    //
    // AltBiomeWorldData samples GetBiome on a 12 m grid and flood-fills the samples into
    // BiomeSectors, which carry the alt-biome modifiers. GetBiomeSector reads that grid with
    // (int)((x - 6) / 12 + 1024), which rounds down to the sample at or below the point, so up to
    // 12 m from a border it returns the neighbouring biome's sector. Player.UpdateBiome notices
    // every second and logs a warning.
    //
    // Transpilers point those gameplay readers at a corrected lookup: the sector of the 2x2
    // samples around the point when all four agree, which is almost everywhere and costs no
    // GetBiome call. Near a border it calls GetBiome and takes the nearest sample of that biome
    // within two cells, or a shared sector of that biome with no alt biomes when the biome is a
    // sliver the grid never sampled. A sliver passing between four agreeing samples is still
    // missed. Another transpiler points UpdateBiome's warning at the debug sink.
    //
    // GetBiomeSector itself stays vanilla, so world generation and terrain keep vanilla's lookup.
    // Terrain paint and vegetation follow the sectors at each terrain patch's corners; vegetation
    // is saved when land generates but paint is worked out on every load, so a corrected corner
    // repaints existing land around its old vegetation and disagrees with peers without the fix.
    // Do not point terrain, vegetation or location placement at the corrected lookup.
    //
    // The grid's size and spacing are not fixed: Expand World Size scales them with the world
    // radius and stretch by transpiling AltBiomeWorldData's conversions. The lookup reads the
    // grid's placement back through MapSpaceToWorldSpace and its bounds from the array, so it
    // follows whatever grid the game built rather than vanilla's 2048 samples at 12 m.
    //
    // Both: servers roll spawn levels through the same lookup.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(Player), "UpdateBiome")]
    internal static class BiomeSectorLookupPatch {
        private const string FixName = "Fix Biome Sector Lookup";

        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(BiomeSectorLookupPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Makes the player's biome agree with the actual biome near biome borders. Vanilla " +
                "reads a 12 m grid rounded down, so within about 12 m of a border the player's biome, " +
                "weather, spawn levels and the map's biome name follow the neighbouring biome, and a " +
                "'GetBiome error' warning is logged every second. Terrain coloring, vegetation and " +
                "locations keep vanilla's lookup, so the world generates and looks as it does without " +
                "the fix. The warning is still visible with EnableDebugMode on. Changing this requires " +
                "a game restart.");
        }

        private static readonly MethodInfo GetBiomeSectorMethod = AccessTools.Method(
            typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), new[] { typeof(Vector3), typeof(bool) });

        private static readonly MethodInfo CorrectedSectorMethod =
            AccessTools.Method(typeof(BiomeSectorLookupPatch), nameof(CorrectedSector));

        private static readonly ConcurrentDictionary<Heightmap.Biome, BiomeSector> Fallbacks =
            new ConcurrentDictionary<Heightmap.Biome, BiomeSector>();

        // Takes the place of the instance call, so the generator arrives as the first argument.
        // Vanilla answers when the fix is off, or the grid is missing or unfinished (vanilla
        // returns its placeholder sectors then).
        private static BiomeSector CorrectedSector(WorldGenerator gen, Vector3 worldPos, bool clamp) {
            if (Enabled.Value) {
                AltBiomeWorldData data = gen?.m_world?.m_biomeData;
                BiomeSector sector = data != null && data.IsReady ? Lookup(gen, data, worldPos.x, worldPos.z) : null;
                if (sector != null) { return sector; }
            }

            return gen.GetBiomeSector(worldPos, clamp);
        }

        private static BiomeSector Lookup(WorldGenerator gen, AltBiomeWorldData data, float wx, float wz) {
            // The array's own bounds, not data.Size: Expand World Size grows the arrays in place
            // and sets Size after them.
            BiomeSector[,] sectors = data.PointSectors;
            int lastX = sectors.GetLength(0) - 1;
            int lastZ = sectors.GetLength(1) - 1;
            if (lastX < 1 || lastZ < 1 || !ToGrid(wx, wz, out float fx, out float fz)) { return null; }

            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, lastX - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, lastZ - 1);

            // Four samples of one sector around the point: a 2x2 block of one biome is always a
            // single sector, since the flood fill joins 4-connected samples.
            BiomeSector sector = sectors[x0, z0];
            if (sector != null && sectors[x0 + 1, z0] == sector && sectors[x0, z0 + 1] == sector
                && sectors[x0 + 1, z0 + 1] == sector) {
                return sector;
            }

            // Default arguments, as the grid was sampled with.
            Heightmap.Biome biome = gen.GetBiome(wx, wz);

            BiomeSector nearest = null;
            float nearestDistance = float.MaxValue;
            for (int z = Math.Max(z0 - 1, 0); z <= Math.Min(z0 + 2, lastZ); z++) {
                for (int x = Math.Max(x0 - 1, 0); x <= Math.Min(x0 + 2, lastX); x++) {
                    BiomeSector candidate = sectors[x, z];
                    if (candidate == null || candidate.Biome != biome) { continue; }

                    float dx = x - fx;
                    float dz = z - fz;
                    float distance = dx * dx + dz * dz;
                    if (distance >= nearestDistance) { continue; }

                    nearest = candidate;
                    nearestDistance = distance;
                }
            }

            return nearest ?? FallbackSector(biome);
        }

        // Vanilla places sample j at MapSpaceToWorldSpace(j), (j - 1024) * 12 + 6. Reading the
        // origin and spacing back through that method, which a mod that resizes the grid patches,
        // gives coordinates in which every sample sits on an integer. The spacing is measured
        // across the whole vanilla width so float rounding at large distances stays out of it.
        // A spacing that is not positive can only come from a broken patch; vanilla answers then.
        private const float SpacingSpan = AltBiomeWorldData.c_textureSize;

        private static bool ToGrid(float wx, float wz, out float fx, out float fz) {
            float origin = AltBiomeWorldData.MapSpaceToWorldSpace(0f);
            float spacing = (AltBiomeWorldData.MapSpaceToWorldSpace(SpacingSpan) - origin) / SpacingSpan;

            fx = (wx - origin) / spacing;
            fz = (wz - origin) / spacing;
            return spacing > 0f;
        }

        private static BiomeSector FallbackSector(Heightmap.Biome biome) {
            if (Fallbacks.TryGetValue(biome, out BiomeSector sector)) { return sector; }

            return Fallbacks.GetOrAdd(biome, CreateFallback);
        }

        private static BiomeSector CreateFallback(Heightmap.Biome biome) {
            Logger.LogDebug(
                $"{FixName}: a patch of {biome} narrower than the biome grid's spacing; it gets a {biome} " +
                "sector with no alt biomes.");
            return new BiomeSector(null, biome);
        }

        private static IEnumerable<CodeInstruction> CorrectLookups(IEnumerable<CodeInstruction> instructions, string site) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, GetBiomeSectorMethod, CorrectedSectorMethod, site, expected: 1);
        }

        // ---- the player's biome ----------------------------------------------------------------

        private static readonly MethodInfo ZLogWarningMethod =
            AccessTools.Method(typeof(ZLog), nameof(ZLog.LogWarning), new[] { typeof(object) });

        private static readonly MethodInfo SinkMethod =
            AccessTools.Method(typeof(BiomeSectorLookupPatch), nameof(Sink));

        // The debug log, or the game's warning again once the fix is turned off.
        private static void Sink(object message) {
            if (Enabled.Value) {
                Logger.DebugSink(message);
            } else {
                ZLog.LogWarning(message);
            }
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> UpdateBiomeTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            IEnumerable<CodeInstruction> codes = CorrectLookups(instructions, "Player.UpdateBiome");
            return PatchHelper.ReplaceCalls(codes, ZLogWarningMethod, SinkMethod, "Player.UpdateBiome", expected: 1);
        }

        // ---- other readers ---------------------------------------------------------------------

        [HarmonyPatch]
        internal static class EnvironmentHook {
            // The biome whose weather plays, and the alt-biome environment that overrides it.
            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            [HarmonyPatch(typeof(EnvMan), "GetBiome")]
            private static IEnumerable<CodeInstruction> Biome(IEnumerable<CodeInstruction> instructions) =>
                CorrectLookups(instructions, "EnvMan.GetBiome");

            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            [HarmonyPatch(typeof(EnvMan), "GetEnvironmentOverride")]
            private static IEnumerable<CodeInstruction> Override(IEnumerable<CodeInstruction> instructions) =>
                CorrectLookups(instructions, "EnvMan.GetEnvironmentOverride");
        }

        // The biome name shown on the map.
        [HarmonyPatch(typeof(Minimap), "UpdateBiome")]
        internal static class MapLabelHook {
            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                CorrectLookups(instructions, "Minimap.UpdateBiome");
        }

        // Alt biomes scale the chance of a spawned creature levelling up.
        [HarmonyPatch]
        internal static class SpawnLevelHook {
            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            [HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.GetLevelUpChance), typeof(Vector3), typeof(float))]
            private static IEnumerable<CodeInstruction> SpawnSystemChance(IEnumerable<CodeInstruction> instructions) =>
                CorrectLookups(instructions, "SpawnSystem.GetLevelUpChance");

            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            [HarmonyPatch(typeof(SpawnArea), nameof(SpawnArea.GetLevelUpChance))]
            private static IEnumerable<CodeInstruction> SpawnAreaChance(IEnumerable<CodeInstruction> instructions) =>
                CorrectLookups(instructions, "SpawnArea.GetLevelUpChance");
        }

        // Diagnostic only: vanilla rebuilds the grid from scratch on every world load and join.
        [HarmonyPatch(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.VerifyBiomeData))]
        internal static class GridBuildHook {
            [HarmonyPrefix]
            private static void Prefix(out Stopwatch __state) => __state = Stopwatch.StartNew();

            [HarmonyPostfix]
            private static void Postfix(World world, Stopwatch __state) {
                Logger.LogDebug(
                    $"Biome grid for '{world?.m_name}' built in {__state.ElapsedMilliseconds} ms, " +
                    $"{world?.m_biomeData?.Sectors.Count ?? 0} sectors.");
            }
        }
    }
}
