using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Dungeon Spawn Hitch: a dungeon or camp loading in near the player places its rooms over a
    // few frames within a time budget instead of all in one frame.
    //
    // A DungeonGenerator that arrives from the network loads its room prefabs asynchronously and,
    // when the last one is in, OnRoomLoaded calls Spawn, which instantiates every room (often 15 to
    // 45, each a large hierarchy) and then releases the held prefab references, which also clears
    // the zone's "loading" flag. When the prefabs are already resident the callbacks fire at once,
    // inside the generator's Awake, so the whole dungeon lands in the frame that created it.
    //
    // A transpiler routes OnRoomLoaded's Spawn and ReleaseHeldReferences calls through this fix.
    // Outside a loading screen the rooms are placed against a per-frame budget shared by every
    // dungeon, the first room of a frame always going, so a small dungeon still finishes at once;
    // the rest are placed from a MonoUpdaters.LateUpdate postfix in arrival order. Until the last
    // room is in, the generator keeps its prefab references and its zone stays flagged as loading,
    // which holds back the objects inside it exactly as during vanilla's asynchronous load. Then
    // vanilla's own ending runs in vanilla's order: SnapToGround.SnappAll, the room list cleared, the
    // references released. Each room is placed exactly once with vanilla's PlaceRoom, which saves and
    // restores the global random state itself, so the layout is the same. Behind a loading screen,
    // with a budget of 0, or when another mod hooks OnRoomLoaded or Spawn, both calls go straight
    // to vanilla. This paces work rather than checking it, but repeats nothing: what remains is a
    // cursor into the generator's own room array, and with no dungeon pending the driver is a
    // count check.
    //
    // A pending generator can go away three ways, and each releases exactly once. Unloaded or
    // destroyed, its own OnDestroy releases, so the job is just dropped. Deleted outright (a
    // location reset), its ZDO goes back to the pool before OnDestroy runs, and vanilla would then
    // clear the loading flag of the wrong zone, so a ZNetScene.OnZDODestroyed postfix releases while
    // the ZDO is still intact and OnDestroy finds nothing left to release. World shutdown clears the
    // queue without releasing, since shutdown destroys the generators. A room that throws ends the
    // job early with its references released, where vanilla would have left the zone stuck.
    //
    // Client: a server generates its own dungeons through Generate, which this does not touch.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(DungeonGenerator), "OnRoomLoaded")]
    internal static class DungeonRoomSpreadPatch {
        private const string FixName = "Fix Dungeon Spawn Hitch";

        internal static ConfigEntry<int> BudgetMs;

        internal static void BindConfig() {
            BudgetMs = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Dungeon Room Budget",
                4,
                "Milliseconds per frame spent placing the rooms of dungeons and camps loading in near " +
                "you; the rest are placed over the next frames. The first room in a frame always goes, " +
                "and nothing is spread behind a loading screen. 0 restores vanilla, which places a whole " +
                "dungeon in one frame.",
                advanced: true,
                valMin: 0,
                valMax: 50);
        }

        private sealed class Job {
            public DungeonGenerator Gen;
            public DungeonGenerator.RoomPlacementData[] Rooms;
            public ZDO Zdo;
            public int Next;

            // Debug summary.
            public float Started;
            public int Frames;
            public int LastFrame = -1;
            public long WorstFrame;
        }

        private static readonly List<Job> Jobs = new List<Job>();

        // Room placement time in the frame _frame, in Stopwatch ticks, shared by every job.
        private static int _frame = -1;
        private static long _spent;
        private static int _placed;

        private static readonly MethodInfo SpawnMethod = AccessTools.DeclaredMethod(typeof(DungeonGenerator), "Spawn");
        private static readonly MethodInfo ReleaseMethod =
            AccessTools.DeclaredMethod(typeof(DungeonGenerator), "ReleaseHeldReferences");

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(DungeonRoomSpreadPatch));

        private static readonly TakeoverCheck LoadedTakeover = new TakeoverCheck(
            typeof(DungeonRoomSpreadPatch),
            AccessTools.DeclaredMethod(typeof(DungeonGenerator), "OnRoomLoaded"),
            HookKinds.Any,
            owners => $"Dungeon room placement is changed by {owners}, so '{FixName}' stands down and that " +
                      "mod's pace applies.");

        private static readonly TakeoverCheck SpawnTakeover = new TakeoverCheck(
            typeof(DungeonRoomSpreadPatch),
            SpawnMethod,
            HookKinds.Any,
            owners => $"Dungeon room placement is changed by {owners}, so '{FixName}' stands down and that " +
                      "mod's pace applies.");

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. Both rewrites or neither.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            IEnumerable<CodeInstruction> spawned = PatchHelper.ReplaceCalls(
                instructions, SpawnMethod, AccessTools.Method(typeof(DungeonRoomSpreadPatch), nameof(SpawnOrQueue)),
                "DungeonGenerator.OnRoomLoaded", expected: 1);
            if (ReferenceEquals(spawned, instructions)) { return instructions; }

            IEnumerable<CodeInstruction> released = PatchHelper.ReplaceCalls(
                spawned, ReleaseMethod, AccessTools.Method(typeof(DungeonRoomSpreadPatch), nameof(ReleaseUnlessQueued)),
                "DungeonGenerator.OnRoomLoaded", expected: 1);
            if (ReferenceEquals(released, spawned)) { return instructions; }

            return released;
        }

        private static void SpawnOrQueue(DungeonGenerator gen) {
            if (!ShouldPace()) {
                gen.Spawn();
                return;
            }

            // Vanilla calls Spawn once per generator; a second call would find no rooms anyway.
            if (FindJob(gen) != null) { return; }

            ZLog.Log("Spawning dungeon");

            Job job = new Job {
                Gen = gen,
                Rooms = gen.m_loadedRooms,
                Zdo = gen.m_zdoSetToBeLoadingInZone,
                Started = Time.unscaledTime,
            };
            Jobs.Add(job);

            // A throw here propagates as it would from vanilla's Spawn, after cleaning up the job.
            bool done;
            try {
                done = Place(job, budgeted: true);
            } catch {
                Jobs.Remove(job);
                throw;
            }

            if (done) {
                Jobs.Remove(job);
                Finish(job);
            }
        }

        // Vanilla's release, unless the rooms are still being placed: then the job releases them.
        private static void ReleaseUnlessQueued(DungeonGenerator gen) {
            if (FindJob(gen) != null) { return; }

            gen.ReleaseHeldReferences();
        }

        private static bool ShouldPace() {
            int budget = BudgetMs != null ? BudgetMs.Value : 0;
            return budget > 0
                && ZoneSystem.instance != null
                && !RunMode.InLoadingScreen()
                && !ApiSwitch.Off
                && !LoadedTakeover.TakenOver
                && !SpawnTakeover.TakenOver;
        }

        private static Job FindJob(DungeonGenerator gen) {
            for (int i = 0; i < Jobs.Count; i++) {
                if (ReferenceEquals(Jobs[i].Gen, gen)) { return Jobs[i]; }
            }

            return null;
        }

        /// <summary>
        /// Counts other world-building work done this frame against the room budget, and takes the
        /// frame's always-allowed first room, so rooms wait for the next frame instead of piling on.
        /// </summary>
        internal static void ChargeFrame(long ticks) {
            int frame = Time.frameCount;
            if (frame != _frame) {
                _frame = frame;
                _spent = 0;
                _placed = 0;
            }

            _spent += ticks;
            if (_placed == 0) { _placed = 1; }
        }

        // Places rooms until the job is done (true) or the frame's budget is spent (false). The first
        // room of a frame always goes.
        private static bool Place(Job job, bool budgeted) {
            long budget = (BudgetMs != null ? BudgetMs.Value : 0) * Stopwatch.Frequency / 1000;

            while (job.Next < job.Rooms.Length) {
                int frame = Time.frameCount;
                if (frame != _frame) {
                    _frame = frame;
                    _spent = 0;
                    _placed = 0;
                }

                if (budgeted && _placed > 0 && _spent >= budget) { return false; }

                DungeonGenerator.RoomPlacementData room = job.Rooms[job.Next];
                job.Next++;

                long start = Stopwatch.GetTimestamp();
                job.Gen.PlaceRoom(room.m_roomData, room.m_position, room.m_rotation, null, ZoneSystem.SpawnMode.Client);
                _spent += Stopwatch.GetTimestamp() - start;
                _placed++;

                if (job.LastFrame != frame) {
                    job.LastFrame = frame;
                    job.Frames++;
                }

                if (_spent > job.WorstFrame) { job.WorstFrame = _spent; }
            }

            return true;
        }

        // The end of vanilla's Spawn; the caller releases, as OnRoomLoaded does next.
        private static void Finish(Job job) {
            SnapToGround.SnappAll();
            job.Gen.m_loadedRooms = null;

            if (Logger.DebugEnabled) {
                Logger.LogDebug(
                    $"Dungeon spawn: {job.Rooms.Length} room(s) over {job.Frames} frame(s), worst frame " +
                    $"{job.WorstFrame * 1000.0 / Stopwatch.Frequency:0.0} ms, zone held " +
                    $"{Time.unscaledTime - job.Started:0.00} s.");
            }
        }

        // Drops a job whose generator is already being taken care of, or releases for it.
        private static void Drop(int index, bool release) {
            Job job = Jobs[index];
            Jobs.RemoveAt(index);
            if (release) { job.Gen.ReleaseHeldReferences(); }
        }

        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate")]
        internal static class DriverHook {
            [HarmonyPostfix]
            private static void Postfix() {
                if (Jobs.Count == 0) { return; }

                // Behind a loading screen the player is waiting for exactly this, and a budget or the
                // fix turned off mid-dungeon means vanilla's all at once; none of them is paced.
                bool budgeted = !RunMode.InLoadingScreen() && BudgetMs != null && BudgetMs.Value > 0 && !ApiSwitch.Off;

                int i = 0;
                while (i < Jobs.Count) {
                    Job job = Jobs[i];
                    DungeonGenerator gen = job.Gen;

                    // Destroyed: its OnDestroy released. Unloading: its OnDestroy will, with the
                    // persistent ZDO it flagged still valid.
                    if (gen == null || gen.m_nview == null || !gen.m_nview.IsValid()) {
                        Drop(i, release: false);
                        continue;
                    }

                    // Something else replaced or finished the room list.
                    if (!ReferenceEquals(gen.m_loadedRooms, job.Rooms)) {
                        Drop(i, release: true);
                        continue;
                    }

                    bool done;
                    try {
                        done = Place(job, budgeted);
                    } catch (Exception ex) {
                        Logger.LogWarning(
                            $"{FixName}: placing a dungeon room failed, so that dungeon is left unfinished and " +
                            $"its zone released. {ex.GetType().Name}: {ex.Message}");
                        gen.m_loadedRooms = null;
                        Drop(i, release: true);
                        continue;
                    }

                    // Arrival order: a job that ran out of budget keeps every later one waiting.
                    if (!done) { return; }

                    Jobs.RemoveAt(i);
                    Finish(job);
                    gen.ReleaseHeldReferences();
                }
            }
        }

        // Runs before ZDOMan returns the destroyed ZDO to its pool, so the zone it flagged is still
        // readable when the release clears that flag.
        [HarmonyPatch(typeof(ZNetScene), "OnZDODestroyed")]
        internal static class DeletedHook {
            [HarmonyPostfix]
            private static void Postfix(ZDO zdo) {
                for (int i = Jobs.Count - 1; i >= 0; i--) {
                    if (!ReferenceEquals(Jobs[i].Zdo, zdo)) { continue; }

                    if (Jobs[i].Gen == null) {
                        Jobs.RemoveAt(i);
                    } else {
                        Drop(i, release: true);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                Jobs.Clear();
                _frame = -1;
            }
        }
    }
}
