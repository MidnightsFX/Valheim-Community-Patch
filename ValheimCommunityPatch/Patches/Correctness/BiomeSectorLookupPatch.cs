using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Biome Sector Lookup: the biome sector at a point always has the biome GetBiome reports
    // there, and the "GetBiome error" warning no longer fills the log.
    //
    // AltBiomeWorldData samples GetBiome on a 12 m grid and flood-fills the samples into
    // BiomeSectors, which carry the alt-biome modifiers. GetBiomeSector reads that grid, and it now
    // decides the player's biome, the weather, spawn level-ups and the terrain's corner biomes. Its
    // lookup, (int)((x - 6) / 12 + 1024), rounds down to the sample at or below the point, so up to
    // 12 m from a border it returns the neighbouring biome's sector. Player.UpdateBiome notices
    // every second and logs a warning. GetBiomeHeight also looks a sector up on every height
    // sample and never uses it (the alt-biome height hook behind it is compiled out).
    //
    // Prefixes on the two world-space GetBiomeSector overloads return the sector of the 2x2
    // samples around the point when all four agree, which is almost everywhere and costs no
    // GetBiome call. Near a border they call GetBiome and take the nearest sample of that biome
    // within two cells, or a shared sector of that biome with no alt biomes when the biome is a
    // sliver the grid never sampled. A sliver passing between four agreeing samples is still
    // missed. Location placement keeps vanilla's lookup, so a seed places locations exactly where
    // unmodded Valheim does, and distant terrain colors read the nearest sample without the
    // border check. A transpiler removes GetBiomeHeight's unused lookup, or points it at vanilla's
    // lookup if a future build reads the result, since heights must match vanilla clients.
    // Another points UpdateBiome's warning at the debug sink.
    //
    // The grid's size and spacing are not fixed: Expand World Size scales them with the world
    // radius and stretch by transpiling AltBiomeWorldData's conversions. Every lookup here reads
    // the grid's placement back through MapSpaceToWorldSpace and its bounds from the array, so it
    // follows whatever grid the game built rather than vanilla's 2048 samples at 12 m.
    //
    // Both: servers place vegetation and roll spawn levels through the same lookup.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(WorldGenerator))]
    internal static class BiomeSectorLookupPatch {
        private const string FixName = "Fix Biome Sector Lookup";

        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(BiomeSectorLookupPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Makes the biome sector lookup agree with the actual biome near biome borders. Vanilla " +
                "reads a 12 m grid rounded down, so within about 12 m of a border the player's biome, " +
                "weather, spawn levels, the map's biome name and terrain coloring follow the " +
                "neighbouring biome, and a 'GetBiome error' warning is logged every second. Location " +
                "placement keeps vanilla's lookup so seeds generate the same locations. The warning is " +
                "still visible with EnableDebugMode on. Changing this requires a game restart.");
        }

        // Set while location placement runs, which keeps vanilla's lookup. Thread-static because
        // HeightmapBuilder looks sectors up on its own thread at the same time.
        [ThreadStatic] private static bool _inLocationPlacement;

        private static readonly ConcurrentDictionary<Heightmap.Biome, BiomeSector> Fallbacks =
            new ConcurrentDictionary<Heightmap.Biome, BiomeSector>();

        // Without the location hook, placement would silently stop matching vanilla, so a missing
        // hook stands the whole fix down.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(BiomeSectorLookupPatch),
            FixName,
            () => PatchHelper.HasHook(LocationPlacementHook.Target, typeof(LocationPlacementHook)));

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(WorldGenerator.GetBiomeSector), typeof(float), typeof(float), typeof(bool))]
        private static bool GetBiomeSectorPrefix(
            WorldGenerator __instance, float wx, float wy, ref BiomeSector __result, bool __runOriginal) {
            if (!__runOriginal) { return false; }

            BiomeSector sector = CorrectedSector(__instance, wx, wy);
            if (sector == null) { return true; }

            __result = sector;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(WorldGenerator.GetBiomeSector), typeof(Vector3), typeof(bool))]
        private static bool GetBiomeSectorVectorPrefix(
            WorldGenerator __instance, Vector3 worldPos, ref BiomeSector __result, bool __runOriginal) {
            if (!__runOriginal) { return false; }

            BiomeSector sector = CorrectedSector(__instance, worldPos.x, worldPos.z);
            if (sector == null) { return true; }

            __result = sector;
            return false;
        }

        // Null means vanilla answers: the fix is off, location placement is running, or the grid
        // is missing or unfinished (vanilla returns its placeholder sectors then).
        private static BiomeSector CorrectedSector(WorldGenerator gen, float wx, float wz) {
            if (Enabled == null || !Enabled.Value || _inLocationPlacement) { return null; }

            AltBiomeWorldData data = gen.m_world?.m_biomeData;
            if (data == null || !data.IsReady || !Hooks.Healthy) { return null; }

            return Lookup(gen, data, wx, wz);
        }

        private static BiomeSector Lookup(WorldGenerator gen, AltBiomeWorldData data, float wx, float wz) {
            // The array's own bounds, not data.Size: Expand World Size grows the arrays in place
            // and sets Size after them, which the terrain builder thread can catch half done.
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

        // ---- GetBiomeHeight's unused lookup ---------------------------------------------------

        private static readonly MethodInfo GetBiomeSectorMethod = AccessTools.Method(
            typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector),
            new[] { typeof(float), typeof(float), typeof(bool) });

        private static readonly MethodInfo SkippedSectorMethod =
            AccessTools.Method(typeof(BiomeSectorLookupPatch), nameof(SkippedSector));

        private static readonly MethodInfo VanillaSectorMethod =
            AccessTools.Method(typeof(BiomeSectorLookupPatch), nameof(VanillaSector));

        private static BiomeSector SkippedSector(WorldGenerator gen, float wx, float wy, bool clamp) => null;

        // Vanilla's arithmetic through the grid-coordinate overload, which this fix leaves alone.
        private static BiomeSector VanillaSector(WorldGenerator gen, float wx, float wy, bool clamp) {
            return gen.GetBiomeSector(
                AltBiomeWorldData.WorldSpaceToMapSpace(wx), AltBiomeWorldData.WorldSpaceToMapSpace(wy), clamp);
        }

        // The nearest sample, unbiased but never exact, through the same overload (which clamps).
        private static BiomeSector NearestSector(WorldGenerator gen, float wx, float wy, bool clamp) {
            // Turned off, the call it replaced, which the prefixes above then leave to vanilla.
            if (!Enabled.Value) { return gen.GetBiomeSector(wx, wy, clamp); }
            if (!ToGrid(wx, wy, out float fx, out float fz)) { return VanillaSector(gen, wx, wy, clamp); }

            return gen.GetBiomeSector(Mathf.FloorToInt(fx + 0.5f), Mathf.FloorToInt(fz + 0.5f), clamp);
        }

        // Load-bearing: without it every height sample near a border would pay for a GetBiome call
        // through the prefix above.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(WorldGenerator.GetBiomeHeight))]
        private static IEnumerable<CodeInstruction> GetBiomeHeightTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int site = -1;
            int found = 0;
            for (int i = 0; i < codes.Count - 1; i++) {
                if (!codes[i].Calls(GetBiomeSectorMethod)) { continue; }

                site = i;
                found++;
            }

            if (found != 1 || !codes[site + 1].IsStloc()) {
                Logger.LogWarning(
                    $"WorldGenerator.GetBiomeHeight: expected one stored GetBiomeSector call, found {found}, " +
                    $"so '{FixName}' leaves it alone and height sampling near borders costs more. Another " +
                    "mod has most likely already rewritten the method.");
                return instructions;
            }

            int slot = LocalSlot(codes[site + 1]);
            bool read = false;
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].IsLdloc() && LocalSlot(codes[i]) == slot) { read = true; }
            }

            codes[site].opcode = OpCodes.Call;
            codes[site].operand = read ? VanillaSectorMethod : SkippedSectorMethod;
            return codes;
        }

        private static int LocalSlot(CodeInstruction code) {
            if (code.opcode == OpCodes.Ldloc_0 || code.opcode == OpCodes.Stloc_0) { return 0; }
            if (code.opcode == OpCodes.Ldloc_1 || code.opcode == OpCodes.Stloc_1) { return 1; }
            if (code.opcode == OpCodes.Ldloc_2 || code.opcode == OpCodes.Stloc_2) { return 2; }
            if (code.opcode == OpCodes.Ldloc_3 || code.opcode == OpCodes.Stloc_3) { return 3; }

            return code.operand is LocalVariableInfo local ? local.LocalIndex : Convert.ToInt32(code.operand);
        }

        // ---- hooks ----------------------------------------------------------------------------

        [HarmonyPatch(typeof(Player), "UpdateBiome")]
        internal static class UpdateBiomeWarningHook {
            private static readonly MethodInfo ZLogWarningMethod =
                AccessTools.Method(typeof(ZLog), nameof(ZLog.LogWarning), new[] { typeof(object) });

            private static readonly MethodInfo SinkMethod = AccessTools.Method(typeof(UpdateBiomeWarningHook), nameof(Sink));

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
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                if (Enabled == null || !Enabled.Value) { return instructions; }

                return PatchHelper.ReplaceCalls(instructions, ZLogWarningMethod, SinkMethod, "Player.UpdateBiome", expected: 1);
            }
        }

        // Distant terrain colors one vertex every 10 m across a 2.4 km ring on the main thread,
        // where a GetBiome call per border vertex would cost more than the exact color is worth.
        // A mod that builds the mesh on its own threads instead, as ValheimOptimized does, gets the
        // exact lookup through the prefixes above, where that cost stays out of the frame.
        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        internal static class DistantLodColorHook {
            private static readonly MethodInfo NearestSectorMethod =
                AccessTools.Method(typeof(BiomeSectorLookupPatch), nameof(NearestSector));

            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                if (Enabled == null || !Enabled.Value) { return instructions; }

                return PatchHelper.ReplaceCalls(
                    instructions, GetBiomeSectorMethod, NearestSectorMethod, "Heightmap.RebuildRenderMesh", expected: 1);
            }
        }

        // The coroutine's MoveNext returns at every yield, so the flag never outlives one step.
        [HarmonyPatch]
        internal static class LocationPlacementHook {
            // Null until Harmony resolves it, and still null if it could not, which HookHealth reads
            // as not attached.
            internal static MethodBase Target { get; private set; }

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod() {
                Target = AccessTools.EnumeratorMoveNext(AccessTools.Method(
                    typeof(ZoneSystem), "GenerateLocationsTimeSliced",
                    new[] { typeof(ZoneSystem.ZoneLocation), typeof(Stopwatch), typeof(ZPackage) }));
                return Target;
            }

            [HarmonyPrefix]
            private static void Prefix() => _inLocationPlacement = true;

            // Finalizer rather than postfix so an exception cannot leave the flag latched.
            [HarmonyFinalizer]
            private static void Finalizer() => _inLocationPlacement = false;
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

                // Settle the hook check here, on the main thread with every patch attached, rather
                // than on whichever thread looks a sector up first.
                if (Enabled != null && Enabled.Value) { _ = Hooks.Healthy; }
            }
        }
    }
}
