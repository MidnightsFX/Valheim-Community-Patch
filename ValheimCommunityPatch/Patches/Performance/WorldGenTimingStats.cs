using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using SoftReferenceableAssets;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // World generation timings: the 'Log World Generation Timings' diagnostic, a per-phase
    // breakdown of each slow zone generation and of the locations and dungeons built inside it.
    //
    // A zone generated for the first time places its locations, its vegetation and every dungeon
    // inside a location in one frame. A sampling profiler cannot split that frame: the work is
    // spread across instantiation, component Awakes, asset loads and terrain, and the game's own
    // "Placed location ... duration" line gives only one total per location.
    //
    // Prefix and finalizer pairs time ZoneSystem.SpawnZone, PlaceLocations, PlaceVegetation,
    // PlaceZoneCtrl and SpawnLocation; DungeonGenerator.Generate, GenerateRooms, PlaceRoom and
    // Save; SnapToGround.SnappAll, Heightmap.ForceGenerateAll, HeightmapBuilder.RequestTerrainSync,
    // synchronous asset loads, room model instantiation and Fix Background Dungeon Generation's
    // stand-ins. Transpilers route the networked-object instantiation in SpawnLocation and
    // PlaceRoom and Generate's DestroyImmediate calls through timed wrappers that do exactly the
    // same call. A zone's lines are written at
    // the start of the next frame together with the whole frame's length, so cost landing after
    // the generation returns (end-of-frame destruction of ghost objects, the first render) shows
    // as the gap. A long wait on the terrain build thread gets a line of its own.
    //
    // Both: a dedicated server generates ghost zones the same way. Diagnostic; nothing here
    // changes behaviour, and with the entry off every hook is a single flag test.
    [PatchSide(Side.Both)]
    internal static class WorldGenTimingStats {
        internal static ConfigEntry<bool> LogTimings;

        internal static void BindConfig() {
            LogTimings = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Log World Generation Timings",
                false,
                "Diagnostic. Logs where the time goes each time new land is generated and takes 10 ms " +
                "or more, or places a location: the locations, dungeons, vegetation and terrain waits " +
                "inside it, phase by phase, and the length of the frame it happened in. Also logs " +
                "every wait of 5 ms or more on the terrain build thread. Leave it off unless you are " +
                "measuring.",
                advanced: true);
        }

        private const double ZoneFloorMs = 10.0;
        private const double TerrainWaitFloorMs = 5.0;

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        private sealed class ZoneTiming {
            internal string Zone;
            internal ZoneSystem.SpawnMode Mode;
            internal long Start, Total, Locations, Vegetation, ZoneCtrl, TerrainWait;
            internal int ZdosAtStart, Zdos, VegetationZdos, TerrainWaits;
            internal readonly List<LocationTiming> Placed = new List<LocationTiming>();
        }

        private sealed class LocationTiming {
            internal string Name;
            internal long Start, Total, Loads, Objects, Dungeons;
            internal int ZdosAtStart, Zdos, ObjectCount;
            internal readonly List<DungeonTiming> Generated = new List<DungeonTiming>();
        }

        private sealed class DungeonTiming {
            internal string Name;
            internal ZoneSystem.SpawnMode Mode;
            internal long Start, Total, PrefabLoads, GenerateRooms, PlaceRooms, RoomObjects, RoomModels, StandIns;
            internal long Save, Snap, ForcedTerrain, Teardown, TerrainWait;
            internal int ZdosAtStart, Zdos, Rooms, RoomObjectCount, RoomModelCount, StandInCount, ForcedMaps, TerrainWaits;
        }

        private struct Mark {
            internal long Ticks;
            internal int Count;
        }

        // Read once a frame so the hooks test a bool, not a ConfigEntry.
        private static bool _on;
        private static int _mainThread = -1;
        private static long _frameStart;

        // What is being generated right now. World generation is synchronous on the main thread,
        // so one of each is open at most.
        private static ZoneTiming _zone;
        private static LocationTiming _location;
        private static DungeonTiming _dungeon;
        private static bool _inGenerateRooms;
        private static int _forceDepth;

        // Finished this frame, written next frame with the frame's length.
        private static readonly List<object> Pending = new List<object>();

        private static bool Recording => _on && Thread.CurrentThread.ManagedThreadId == _mainThread;

        private static long Now => Stopwatch.GetTimestamp();

        private static int ZdoCount => ZDOMan.instance != null ? ZDOMan.instance.m_objectsByID.Count : 0;

        private static double Ms(long ticks) => ticks * MsPerTick;

        // ---- frame boundary --------------------------------------------------------------------

        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        internal static class FrameHook {
            [HarmonyPrefix]
            private static void Prefix() {
                long now = Now;
                if (Pending.Count > 0) {
                    if (_on) { Flush(now - _frameStart); }
                    Pending.Clear();
                }

                _on = LogTimings != null && LogTimings.Value;
                _mainThread = Thread.CurrentThread.ManagedThreadId;
                _frameStart = now;
            }
        }

        // ---- zone ------------------------------------------------------------------------------

        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        internal static class ZoneHook {
            [HarmonyPrefix]
            private static void Prefix(Vector2s zoneID, ZoneSystem.SpawnMode mode, out ZoneTiming __state) {
                __state = null;
                if (!Recording || _zone != null) { return; }

                __state = new ZoneTiming {
                    Zone = $"{zoneID.x},{zoneID.y}",
                    Mode = mode,
                    ZdosAtStart = ZdoCount,
                    Start = Now,
                };
                _zone = __state;
            }

            [HarmonyFinalizer]
            private static void Finalizer(bool __result, ZoneTiming __state) {
                if (__state == null) { return; }

                __state.Total = Now - __state.Start;
                __state.Zdos = ZdoCount - __state.ZdosAtStart;
                _zone = null;

                if (__result && (Ms(__state.Total) >= ZoneFloorMs || __state.Placed.Count > 0)) {
                    Pending.Add(__state);
                }
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
        internal static class PlaceLocationsHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) => __state = _zone != null ? Now : 0;

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                if (__state != 0 && _zone != null) { _zone.Locations += Now - __state; }
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation")]
        internal static class PlaceVegetationHook {
            [HarmonyPrefix]
            private static void Prefix(out Mark __state) {
                __state = _zone != null ? new Mark { Ticks = Now, Count = ZdoCount } : default;
            }

            [HarmonyFinalizer]
            private static void Finalizer(Mark __state) {
                if (__state.Ticks == 0 || _zone == null) { return; }

                _zone.Vegetation += Now - __state.Ticks;
                _zone.VegetationZdos += ZdoCount - __state.Count;
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceZoneCtrl")]
        internal static class PlaceZoneCtrlHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) => __state = _zone != null ? Now : 0;

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                if (__state != 0 && _zone != null) { _zone.ZoneCtrl += Now - __state; }
            }
        }

        // ---- waiting to be installed ----------------------------------------------------------
        //
        // A nearby zone is installed (its ground appears) only when PokeLocalZone succeeds. Until
        // then each tick it is asked again and fails for one of three reasons: its terrain is not
        // built yet, its location is still loading, or it was just generated in the background and
        // loads next tick. A zone that took a quarter second or more gets a line once installed.

        private sealed class InstallWait {
            internal float Since, LastAsked;
            internal int TerrainTicks, LocationTicks, BackgroundTicks;
        }

        private static readonly Dictionary<Vector2s, InstallWait> InstallWaits = new Dictionary<Vector2s, InstallWait>();
        private static bool _locationBlocked;

        [HarmonyPatch(typeof(ZoneSystem), "PokeLocalZone")]
        internal static class InstallWaitHook {
            [HarmonyPrefix]
            private static void Prefix(ZoneSystem __instance, Vector2s zoneID, out bool __state) {
                __state = Recording && !__instance.m_zones.ContainsKey(zoneID);
                _locationBlocked = false;
            }

            [HarmonyFinalizer]
            private static void Finalizer(ZoneSystem __instance, Vector2s zoneID, bool __result, bool __state) {
                if (!__state) { return; }

                float now = Time.realtimeSinceStartup;
                InstallWaits.TryGetValue(zoneID, out InstallWait wait);

                if (__instance.m_zones.ContainsKey(zoneID)) {
                    if (wait == null) { return; }

                    InstallWaits.Remove(zoneID);
                    float waited = now - wait.Since;
                    if (waited < 0.25f) { return; }

                    string location = __instance.m_locationInstances.TryGetValue(zoneID, out ZoneSystem.LocationInstance instance)
                        ? $" ({instance.m_location?.m_prefabName})" : "";
                    Logger.LogInfo(
                        $"World gen timing: zone {zoneID.x},{zoneID.y}{location} installed {waited:0.0} s after it was " +
                        $"first needed | ticks waiting for terrain {wait.TerrainTicks}, for its location to load " +
                        $"{wait.LocationTicks}, generated in the background {wait.BackgroundTicks}");
                    return;
                }

                // A zone the player walked away from starts over.
                if (wait == null || now - wait.LastAsked > 2f) {
                    wait = new InstallWait { Since = now };
                    InstallWaits[zoneID] = wait;
                }

                wait.LastAsked = now;
                if (__result) {
                    wait.BackgroundTicks++;
                } else if (_locationBlocked) {
                    wait.LocationTicks++;
                } else {
                    wait.TerrainTicks++;
                }

                if (InstallWaits.Count > 256) { InstallWaits.Clear(); }
            }
        }

        // SpawnZone asks only once the zone's terrain is ready, so a "no" here is the location.
        // Priority.Last: after Fix Location Room Preload has given its answer.
        [HarmonyPatch(typeof(ZoneSystem), "PokeCanSpawnLocation")]
        internal static class LocationGateHook {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(bool isFirstSpawn, bool __result) {
                if (isFirstSpawn && !__result) { _locationBlocked = true; }
            }
        }

        // ---- location --------------------------------------------------------------------------

        private static readonly MethodInfo InstantiateAt = FindInstantiateAt();

        // Object.Instantiate<GameObject>(GameObject, Vector3, Quaternion).
        private static MethodInfo FindInstantiateAt() {
            foreach (MethodInfo method in typeof(Object).GetMethods(BindingFlags.Public | BindingFlags.Static)) {
                if (method.Name != nameof(Object.Instantiate) || !method.IsGenericMethodDefinition) { continue; }

                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 3 && p[1].ParameterType == typeof(Vector3) && p[2].ParameterType == typeof(Quaternion)) {
                    return method.MakeGenericMethod(typeof(GameObject));
                }
            }

            return null;
        }

        [HarmonyPatch(typeof(ZoneSystem), "SpawnLocation")]
        internal static class SpawnLocationHook {
            [HarmonyPrefix]
            private static void Prefix(ZoneSystem.ZoneLocation location, out LocationTiming __state) {
                __state = null;
                if (!Recording || _location != null) { return; }

                __state = new LocationTiming {
                    Name = location?.m_prefabName ?? "?",
                    ZdosAtStart = ZdoCount,
                    Start = Now,
                };
                _location = __state;
            }

            [HarmonyFinalizer]
            private static void Finalizer(LocationTiming __state) {
                if (__state == null) { return; }

                __state.Total = Now - __state.Start;
                __state.Zdos = ZdoCount - __state.ZdosAtStart;
                _location = null;

                if (_zone != null) {
                    _zone.Placed.Add(__state);
                } else if (Ms(__state.Total) >= ZoneFloorMs) {
                    Pending.Add(__state);
                }
            }

            // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                PatchHelper.ReplaceCalls(
                    instructions, InstantiateAt,
                    AccessTools.DeclaredMethod(typeof(WorldGenTimingStats), nameof(LocationObject)),
                    "Log World Generation Timings (ZoneSystem.SpawnLocation)", expected: 1);
        }

        private static GameObject LocationObject(GameObject original, Vector3 position, Quaternion rotation) {
            LocationTiming location = _location;
            if (location == null) { return Object.Instantiate(original, position, rotation); }

            long start = Now;
            GameObject made = Object.Instantiate(original, position, rotation);
            location.Objects += Now - start;
            location.ObjectCount++;
            return made;
        }

        // ---- dungeon ---------------------------------------------------------------------------

        [HarmonyPatch(typeof(DungeonGenerator), nameof(DungeonGenerator.Generate), typeof(int), typeof(ZoneSystem.SpawnMode))]
        internal static class GenerateHook {
            [HarmonyPrefix]
            private static void Prefix(DungeonGenerator __instance, ZoneSystem.SpawnMode mode, out DungeonTiming __state) {
                __state = null;
                if (!Recording || _dungeon != null) { return; }

                __state = new DungeonTiming {
                    Name = __instance.name,
                    Mode = mode,
                    ZdosAtStart = ZdoCount,
                    Start = Now,
                };
                _dungeon = __state;
            }

            [HarmonyFinalizer]
            private static void Finalizer(DungeonTiming __state) {
                if (__state == null) { return; }

                __state.Total = Now - __state.Start;
                __state.Zdos = ZdoCount - __state.ZdosAtStart;
                _dungeon = null;
                _inGenerateRooms = false;

                if (_location != null) {
                    _location.Generated.Add(__state);
                    _location.Dungeons += __state.Total;
                } else if (Ms(__state.Total) >= ZoneFloorMs) {
                    Pending.Add(__state);
                }
            }

            // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                PatchHelper.ReplaceCalls(
                    instructions,
                    AccessTools.Method(typeof(Object), nameof(Object.DestroyImmediate), new[] { typeof(Object) }),
                    AccessTools.DeclaredMethod(typeof(WorldGenTimingStats), nameof(DungeonTeardown)),
                    "Log World Generation Timings (DungeonGenerator.Generate)", expected: 3);
        }

        private static void DungeonTeardown(Object target) {
            DungeonTiming dungeon = _dungeon;
            if (dungeon == null) {
                Object.DestroyImmediate(target);
                return;
            }

            long start = Now;
            Object.DestroyImmediate(target);
            dungeon.Teardown += Now - start;
        }

        [HarmonyPatch(typeof(DungeonGenerator), "GenerateRooms")]
        internal static class GenerateRoomsHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) {
                __state = _dungeon != null ? Now : 0;
                if (__state != 0) { _inGenerateRooms = true; }
            }

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                if (__state == 0 || _dungeon == null) { return; }

                _dungeon.GenerateRooms += Now - __state;
                _inGenerateRooms = false;
            }
        }

        // Only rooms placed by the generation being timed. A synchronous room prefab load can
        // complete another dungeon's asynchronous one, and that dungeon then places its rooms
        // from inside the load, before GenerateRooms starts.
        private static DungeonTiming Placing => _inGenerateRooms ? _dungeon : null;

        [HarmonyPatch(typeof(DungeonGenerator), "PlaceRoom",
            typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode))]
        internal static class PlaceRoomHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) => __state = Placing != null ? Now : 0;

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                if (__state == 0 || _dungeon == null) { return; }

                _dungeon.PlaceRooms += Now - __state;
                _dungeon.Rooms++;
            }

            // Priority.Last: see ValheimCommunityPatch.ApplyPatches. The room model call is left to
            // Fix Background Dungeon Generation, which rewrites it; the hooks below time it.
            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                PatchHelper.ReplaceCalls(
                    instructions, InstantiateAt,
                    AccessTools.DeclaredMethod(typeof(WorldGenTimingStats), nameof(RoomObject)),
                    "Log World Generation Timings (DungeonGenerator.PlaceRoom)", expected: 1);
        }

        // Set while a room's networked object is instantiated, so a room model instantiated from
        // inside one of its Awakes is not counted twice.
        private static bool _inRoomObject;

        private static GameObject RoomObject(GameObject original, Vector3 position, Quaternion rotation) {
            DungeonTiming dungeon = Placing;
            if (dungeon == null) { return Object.Instantiate(original, position, rotation); }

            long start = Now;
            _inRoomObject = true;
            try {
                return Object.Instantiate(original, position, rotation);
            } finally {
                _inRoomObject = false;
                dungeon.RoomObjects += Now - start;
                dungeon.RoomObjectCount++;
            }
        }

        [HarmonyPatch(typeof(SoftReferenceableAssets.Utils), nameof(SoftReferenceableAssets.Utils.Instantiate),
            typeof(SoftReference<GameObject>), typeof(Vector3), typeof(Quaternion), typeof(Transform))]
        internal static class RoomModelHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) => __state = Placing != null && !_inRoomObject ? Now : 0;

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                if (__state == 0 || _dungeon == null) { return; }

                _dungeon.RoomModels += Now - __state;
                _dungeon.RoomModelCount++;
            }
        }

        // A room model built from Fix Location Model Placeholders' template counts as a room model.
        [HarmonyPatch(typeof(ModelTemplatePatch), nameof(ModelTemplatePatch.CloneTemplate))]
        internal static class TemplateModelHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) => __state = Placing != null && !_inRoomObject ? Now : 0;

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                if (__state == 0 || _dungeon == null) { return; }

                _dungeon.RoomModels += Now - __state;
                _dungeon.RoomModelCount++;
            }
        }

        [HarmonyPatch(typeof(GhostDungeonStandInPatch), nameof(GhostDungeonStandInPatch.BuildStandIn))]
        internal static class StandInHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) => __state = Placing != null ? Now : 0;

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                if (__state == 0 || _dungeon == null) { return; }

                _dungeon.StandIns += Now - __state;
                _dungeon.StandInCount++;
            }
        }

        [HarmonyPatch(typeof(DungeonGenerator), "Save")]
        internal static class SaveHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) => __state = _dungeon != null ? Now : 0;

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                if (__state != 0 && _dungeon != null) { _dungeon.Save += Now - __state; }
            }
        }

        // ---- terrain ---------------------------------------------------------------------------

        [HarmonyPatch(typeof(SnapToGround), nameof(SnapToGround.SnappAll))]
        internal static class SnappAllHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) => __state = _dungeon != null ? Now : 0;

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                if (__state != 0 && _dungeon != null) { _dungeon.Snap += Now - __state; }
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.ForceGenerateAll))]
        internal static class ForceGenerateAllHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) {
                __state = _dungeon != null ? Now : 0;
                _forceDepth++;
            }

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                _forceDepth--;
                if (__state != 0 && _dungeon != null) { _dungeon.ForcedTerrain += Now - __state; }
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
        internal static class RegenerateHook {
            [HarmonyPrefix]
            private static void Prefix() {
                if (_forceDepth > 0 && _dungeon != null) { _dungeon.ForcedMaps++; }
            }
        }

        [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.RequestTerrainSync))]
        internal static class TerrainWaitHook {
            [HarmonyPrefix]
            private static void Prefix(HeightmapBuilder __instance, out Mark __state) {
                __state = Recording ? new Mark { Ticks = Now, Count = __instance.m_toBuild.Count } : default;
            }

            [HarmonyFinalizer]
            private static void Finalizer(Vector3 center, Mark __state) {
                if (__state.Ticks == 0) { return; }

                long waited = Now - __state.Ticks;
                if (_dungeon != null) {
                    _dungeon.TerrainWait += waited;
                    _dungeon.TerrainWaits++;
                }

                if (_zone != null) {
                    _zone.TerrainWait += waited;
                    _zone.TerrainWaits++;
                }

                if (Ms(waited) < TerrainWaitFloorMs) { return; }

                string during = _dungeon != null ? $"while generating {_dungeon.Name}"
                    : _location != null ? $"while placing {_location.Name}"
                    : _zone != null ? $"while generating zone {_zone.Zone}"
                    : "outside world generation";

                Logger.LogInfo(
                    $"World gen timing: waited {Ms(waited):0.0} ms for terrain at ({center.x:0}, {center.z:0}) " +
                    $"{during}, with {__state.Count} build(s) queued ahead of it.");
            }
        }

        // ---- asset loads -----------------------------------------------------------------------

        [HarmonyPatch]
        internal static class AssetLoadHook {
            [HarmonyTargetMethod]
            private static MethodBase Target() =>
                AccessTools.Method(AccessTools.TypeByName("SoftReferenceableAssets.AssetBundleLoader"), "Load", new[] { typeof(AssetID) });

            [HarmonyPrefix]
            private static void Prefix(out long __state) {
                // Room prefab loads are the ones before layout; inside it the rooms are already held.
                bool timed = (_dungeon != null && !_inGenerateRooms) || (_dungeon == null && _location != null);
                __state = timed && Recording ? Now : 0;
            }

            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                if (__state == 0) { return; }

                long elapsed = Now - __state;
                if (_dungeon != null) {
                    _dungeon.PrefabLoads += elapsed;
                } else if (_location != null) {
                    _location.Loads += elapsed;
                }
            }
        }

        // ---- report ----------------------------------------------------------------------------

        private static void Flush(long frameTicks) {
            StringBuilder line = new StringBuilder(256);

            foreach (object item in Pending) {
                switch (item) {
                    case ZoneTiming zone:
                        WriteZone(line, zone, frameTicks);
                        foreach (LocationTiming location in zone.Placed) {
                            WriteLocation(line, location, "  ");
                            foreach (DungeonTiming dungeon in location.Generated) { WriteDungeon(line, dungeon, "    "); }
                        }
                        break;
                    case LocationTiming location:
                        WriteLocation(line, location, "");
                        foreach (DungeonTiming dungeon in location.Generated) { WriteDungeon(line, dungeon, "  "); }
                        break;
                    case DungeonTiming dungeon:
                        WriteDungeon(line, dungeon, "");
                        break;
                }
            }
        }

        private static void WriteZone(StringBuilder line, ZoneTiming z, long frameTicks) {
            long other = z.Total - z.Locations - z.Vegetation - z.ZoneCtrl;

            line.Length = 0;
            line.Append("World gen timing: zone ").Append(z.Zone).Append(" (").Append(z.Mode).Append(") ")
                .Append(Ms(z.Total).ToString("0.0")).Append(" ms in a ").Append(Ms(frameTicks).ToString("0.0"))
                .Append(" ms frame, ").Append(z.Zdos).Append(" new objects | locations ")
                .Append(Ms(z.Locations).ToString("0.0")).Append(" ms | vegetation ").Append(Ms(z.Vegetation).ToString("0.0"))
                .Append(" ms (").Append(z.VegetationZdos).Append(" objects) | zone control ")
                .Append(Ms(z.ZoneCtrl).ToString("0.0")).Append(" ms | terrain and other ").Append(Ms(other).ToString("0.0"))
                .Append(" ms");

            if (z.TerrainWaits > 0) {
                line.Append(" | terrain waits ").Append(Ms(z.TerrainWait).ToString("0.0")).Append(" ms over ")
                    .Append(z.TerrainWaits).Append(" (included above)");
            }

            Logger.LogInfo(line.ToString());
        }

        private static void WriteLocation(StringBuilder line, LocationTiming l, string indent) {
            long other = l.Total - l.Loads - l.Objects - l.Dungeons;

            line.Length = 0;
            line.Append("World gen timing: ").Append(indent).Append("location ").Append(l.Name).Append(' ')
                .Append(Ms(l.Total).ToString("0.0")).Append(" ms, ").Append(l.Zdos).Append(" new objects | prefab load ")
                .Append(Ms(l.Loads).ToString("0.0")).Append(" ms | objects ").Append(Ms(l.Objects).ToString("0.0"))
                .Append(" ms (").Append(l.ObjectCount).Append(") | dungeons ").Append(Ms(l.Dungeons).ToString("0.0"))
                .Append(" ms | other ").Append(Ms(other).ToString("0.0")).Append(" ms");

            Logger.LogInfo(line.ToString());
        }

        private static void WriteDungeon(StringBuilder line, DungeonTiming d, string indent) {
            long layout = d.GenerateRooms - d.PlaceRooms;
            long roomOther = d.PlaceRooms - d.RoomObjects - d.RoomModels - d.StandIns;
            long snapOther = d.Snap - d.ForcedTerrain;
            long other = d.Total - d.PrefabLoads - d.GenerateRooms - d.Save - d.Snap - d.Teardown;

            line.Length = 0;
            line.Append("World gen timing: ").Append(indent).Append("dungeon ").Append(d.Name).Append(" (").Append(d.Mode)
                .Append(") ").Append(Ms(d.Total).ToString("0.0")).Append(" ms, ").Append(d.Rooms).Append(" rooms, ")
                .Append(d.Zdos).Append(" new objects | room prefab loads ").Append(Ms(d.PrefabLoads).ToString("0.0"))
                .Append(" ms | layout ").Append(Ms(layout).ToString("0.0")).Append(" ms | rooms ")
                .Append(Ms(d.PlaceRooms).ToString("0.0")).Append(" ms (objects ").Append(Ms(d.RoomObjects).ToString("0.0"))
                .Append(" ms for ").Append(d.RoomObjectCount).Append(", room models ").Append(Ms(d.RoomModels).ToString("0.0"))
                .Append(" ms for ").Append(d.RoomModelCount).Append(", stand-ins ").Append(Ms(d.StandIns).ToString("0.0"))
                .Append(" ms for ").Append(d.StandInCount).Append(", other ").Append(Ms(roomOther).ToString("0.0")).Append(" ms) | save ")
                .Append(Ms(d.Save).ToString("0.0")).Append(" ms | snap to ground ").Append(Ms(snapOther).ToString("0.0"))
                .Append(" ms | forced terrain ").Append(Ms(d.ForcedTerrain).ToString("0.0")).Append(" ms (")
                .Append(d.ForcedMaps).Append(" maps) | teardown ").Append(Ms(d.Teardown).ToString("0.0"))
                .Append(" ms | other ").Append(Ms(other).ToString("0.0")).Append(" ms");

            if (d.TerrainWaits > 0) {
                line.Append(" | terrain waits ").Append(Ms(d.TerrainWait).ToString("0.0")).Append(" ms over ")
                    .Append(d.TerrainWaits).Append(" (included above)");
            }

            Logger.LogInfo(line.ToString());
        }
    }
}
