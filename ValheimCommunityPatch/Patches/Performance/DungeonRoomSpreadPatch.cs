using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Dungeon Spawn Hitch: a dungeon or camp loading in near the player instantiates all its rooms in one frame.
    // Transpiles OnRoomLoaded to queue the rooms and place them under a per-frame budget from MonoUpdaters.LateUpdate.
    // Dungeons arrive over a few frames with the same layout, and their zone is always released when they finish.
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
                "Milliseconds per frame spent placing the rooms of dungeons and camps loading in near you; " +
                "the rest wait for the next frames. 0 is vanilla, which places a whole dungeon in one frame.",
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

        // Every job must end with its references released: a zone left flagged as loading fails
        // ZNetScene.IsAreaReady, which Player.UpdateTeleport waits on with no timeout, so a door, portal or
        // respawn into it would hang on the loading screen for good.
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

        // Without the driver, the rooms left over from a dungeon's first frame would never be placed.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(DungeonRoomSpreadPatch),
            FixName,
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(MonoUpdaters), "LateUpdate"), typeof(DriverHook)));

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

            // Nothing thrown here leaves this call: OnRoomLoaded releases right after it, and finds no
            // job left to wait for.
            bool done;
            try {
                done = Place(job, budgeted: true);
            } catch (Exception ex) {
                Jobs.Remove(job);
                Abandon(job, ex);
                return;
            }

            if (done) {
                Jobs.Remove(job);
                FinishOrWarn(job);
            }
        }

        // Vanilla's release, unless the rooms are still being placed: then the job releases them. Until then the zone
        // stays flagged as loading, holding back the objects inside it as during vanilla's asynchronous load.
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
                && !SpawnTakeover.TakenOver
                && Hooks.Healthy;
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

                // PlaceRoom saves and restores the global random state itself, so the layout matches vanilla's.
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

        // Every caller releases next, so a throw is reported here instead of passed on.
        private static void FinishOrWarn(Job job) {
            try {
                Finish(job);
            } catch (Exception ex) {
                job.Gen.m_loadedRooms = null;
                Logger.LogWarning(
                    $"{FixName}: finishing a dungeon failed, so its zone is released as it is. " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // A room that threw: the rest of the dungeon is given up and the caller releases its zone.
        private static void Abandon(Job job, Exception ex) {
            job.Gen.m_loadedRooms = null;
            Logger.LogWarning(
                $"{FixName}: placing a dungeon room failed, so that dungeon is left unfinished and " +
                $"its zone released. {ex.GetType().Name}: {ex.Message}");
        }

        // Drops a job whose generator is already being taken care of, or releases for it.
        private static void Drop(int index, bool release) {
            Job job = Jobs[index];
            Jobs.RemoveAt(index);
            if (release) { Release(job.Gen); }
        }

        // Vanilla's release. It throws only when the game itself has lost the zone flag, and the
        // driver must not stop over that with other jobs still queued.
        private static void Release(DungeonGenerator gen) {
            try {
                gen.ReleaseHeldReferences();
            } catch (Exception ex) {
                Logger.LogWarning($"{FixName}: releasing a dungeon failed. {ex.GetType().Name}: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate")]
        internal static class DriverHook {
            // A finalizer, so it runs on frames where an object's late update throws, which skip
            // postfixes. It leaves that exception as it was.
            //
            // After Valheim AVX's batched collider bakes: tiles rebuilt in this LateUpdate keep their
            // old collider until its finalizer assigns the new ones, and the snap at a dungeon's end
            // raycasts the terrain.
            [HarmonyFinalizer]
            [HarmonyAfter("com.kewb.valheimavx.terrain")]
            private static void Finalizer() {
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
                        Abandon(job, ex);
                        Drop(i, release: true);
                        continue;
                    }

                    // Arrival order: a job that ran out of budget keeps every later one waiting.
                    if (!done) { return; }

                    Jobs.RemoveAt(i);
                    FinishOrWarn(job);
                    Release(gen);
                }
            }
        }

        // A generator deleted outright (a location reset) has its ZDO pooled before its OnDestroy runs, and vanilla's
        // release there would clear the wrong zone's flag. This runs before ZDOMan returns the destroyed ZDO to its
        // pool, so the zone it flagged is still readable, and OnDestroy then finds nothing left to release.
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

        // Cleared without releasing: shutdown destroys the generators, and each releases in its OnDestroy.
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
