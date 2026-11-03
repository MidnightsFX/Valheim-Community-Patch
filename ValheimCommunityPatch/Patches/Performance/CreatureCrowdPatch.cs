using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Creature Crowd Slowdown: holds creature updates to a time budget per physics step, so a
    // crowd of hundreds no longer takes longer to simulate than the game has.
    //
    // Every owned creature runs Character.CustomFixedUpdate on each 20 ms physics step, at about
    // 12 microseconds a creature, and its AI every 50 ms. Around 700 of them that and the physics
    // take longer than the step they simulate, so the game runs its maximum of ten steps per
    // rendered frame and drops to single-digit FPS. A creature at rest spends nearly all of that
    // re-applying a zero velocity, which also keeps its rigidbody from ever sleeping. In a packed
    // crowd nearly every creature also counts as walking, because a wander target it cannot reach
    // keeps it pushing against the others until the 30 to 45 second wander timer ends.
    //
    // Hooks on the loop helpers time both loops. While they fit Creature Update Budget nothing else
    // changes. Once they have cost more for about a second, each step updates every character that
    // is not resting as usual and gives the resting ones what is left of the budget, in turns, with
    // their delta time scaled to how long a full turn took so their timers keep real time. No
    // creature waits longer than MaxPeriod steps, even if that goes over budget. Resting AI takes
    // turns the same way. A resting creature standing on the terrain is put to sleep after its
    // update, and the next update checks the ground is still there. Resting means the owner's AI
    // creature is not asked to move, is on the ground, is barely moving, and has no target, recent
    // hit, status effect, attack, rider or player within RestMinPlayerDistance; a creature stops
    // resting on the first step any of that changes. A prefix on BaseAI.RandomMovement ends a wander
    // that a crowd is holding still. Collision layers and the ZDO format are unchanged, so fences
    // hold and other players need nothing. Stands down when another mod can skip creature updates.
    //
    // Both: creatures are simulated wherever they are owned, usually on a client.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class CreatureCrowdPatch {
        private const string FixName = "Fix Creature Crowd Slowdown";

        // The longest a resting creature waits for its update: 200 ms, and 250 ms for its AI.
        private const int MaxPeriod = 10;
        private const int MaxAiPeriod = 5;

        // What one AI update may spend on resting AI, as a share of the budget per step. The loop
        // runs every 50 ms against 20 ms physics steps.
        private const float AiShareOfBudget = 0.3f;
        private const float AiUpdatesPerStep = 0.4f;

        private const float RestMaxSpeedSqr = 0.5f * 0.5f;
        private const float RestMinPlayerDistanceSqr = 10f * 10f;
        private const float RestAfterHurtSeconds = 10f;

        // Standing on anything but the terrain, such as a floor or another creature, keeps a body
        // awake: only the terrain can be checked from the update without the physics running.
        private const float SleepMaxSpeedSqr = 0.2f * 0.2f;
        private const float SleepMaxGroundGap = 0.05f;

        // A wander ends after about this long at under half the speed the creature is trying to go,
        // once it is trying for at least TriedSpeed: slower than that, it is still turning or setting
        // off. A random chance per AI update rather than a timer per creature, so there is no
        // per-creature state to keep or clean up.
        private const float GiveUpSeconds = 2f;
        private const float TriedSpeedSqr = 0.5f * 0.5f;
        private const float BlockedShareSqr = 0.5f * 0.5f;

        // Costs are averaged over about a second. The fix stops once the full updates would cost
        // under this share of the budget for this many steps; the gap keeps a load near the budget
        // from flipping it, since creatures cost more once they are walking again.
        private const float SmoothingPerStep = 0.02f;
        private const float SmoothingPerAiUpdate = 0.05f;
        private const float DisengageShare = 0.8f;

        // One slow update, such as the first after a load, counts for no more than this many budgets,
        // so only a sustained load can start the fix.
        private const float SpikeLimit = 2f;
        private const int DisengageSteps = 500;

        private static readonly float TicksPerMs = Stopwatch.Frequency / 1000f;

        internal static FixToggle Enabled;
        private static ConfigEntry<float> UpdateBudget;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(CreatureCrowdPatch),
                ValConfig.SectionPerformance,
                FixName,
                true,
                "Keeps the game playable with hundreds of creatures loaded, such as a huge breeding pen. " +
                "When creature updates take longer than Creature Update Budget, creatures standing around " +
                "take turns updating a few times a second instead of fifty and their bodies sleep, and one " +
                "a crowd has stopped gives up walking. Fights, falls, hits and anything within 10 m of a " +
                "player run in full, and collisions are unchanged. While they fit the budget nothing changes.");

            UpdateBudget = ValConfig.BindClientConfig(
                "Creature Update Budget",
                4f,
                "Milliseconds per physics step creature updates may take on this machine before Fix " +
                "Creature Crowd Slowdown starts, and what it then holds them to. Lower gives a higher " +
                "frame rate with a crowd, at the cost of resting creatures reacting a little later.",
                1f,
                20f);
        }

        private static readonly TakeoverCheck[] Takeovers = {
            SkipCheck(AccessTools.DeclaredMethod(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomFixedUpdate))),
            SkipCheck(AccessTools.DeclaredMethod(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.UpdateAI))),
            SkipCheck(AccessTools.DeclaredMethod(typeof(Character), nameof(Character.CustomFixedUpdate))),
            SkipCheck(AccessTools.DeclaredMethod(typeof(Humanoid), nameof(Humanoid.CustomFixedUpdate))),
            SkipCheck(AccessTools.DeclaredMethod(typeof(BaseAI), nameof(BaseAI.UpdateAI))),
            SkipCheck(AccessTools.DeclaredMethod(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))),
        };

        private static TakeoverCheck SkipCheck(System.Reflection.MethodBase target) => new TakeoverCheck(
            typeof(CreatureCrowdPatch),
            target,
            HookKinds.BoolPrefixes,
            owners => $"Creature updates can be skipped by {owners}, so '{FixName}' stands down and leaves them to that mod.");

        // Read by the wander prefix, which runs for every wandering creature at any load.
        private static bool _engaged;

        private static bool _timingCharacters;
        private static bool _timingAi;
        private static long _loopStart;

        // Averages, in milliseconds: the full-rate part of each loop, a resting creature's update,
        // and what resting AI spends per AI update.
        private static float _everyStepMs;
        private static float _everyAiMs;
        private static float _restingUpdateMs;
        private static float _restingAiUpdateMs;
        private static float _restingAiMs;
        private static int _quietSteps;

        // Rebuilt each update. Handed to the loop helpers in place of the game's lists; the helpers
        // copy them before updating anything, so a creature dying mid-loop cannot change them.
        private static readonly List<IMonoUpdater> EveryStep = new List<IMonoUpdater>();
        private static readonly List<IUpdateAI> EveryAi = new List<IUpdateAI>();
        private static readonly List<Character> Resting = new List<Character>();
        private static readonly List<int> RestingIndices = new List<int>();
        private static readonly List<IUpdateAI> RestingAi = new List<IUpdateAI>();
        private static readonly List<int> RestingAiIndices = new List<int>();
        private static readonly List<Vector3> PlayerPositions = new List<Vector3>();

        private static readonly Turns CharacterTurns = new Turns(MaxPeriod);
        private static readonly Turns AiTurns = new Turns(MaxAiPeriod);

        // Whose turn it is, kept as a position in the game's list so a creature joining or leaving
        // the resting ones does not cost another its turn.
        private sealed class Turns {
            private readonly int _maxPeriod;
            private int _next;
            private int _updates;

            // How many updates the last full turn took, which is what a creature's delta time covers.
            internal int Period = 1;

            internal Turns(int maxPeriod) {
                _maxPeriod = maxPeriod;
            }

            internal void Reset() {
                _next = 0;
                _updates = 0;
                Period = 1;
            }

            internal int First(List<int> indices) {
                int position = indices.BinarySearch(_next);
                return position < 0 ? ~position : position;
            }

            internal int Wrap(int position, int count) {
                if (position < count) { return position; }

                Period = Mathf.Clamp(_updates, 1, _maxPeriod);
                _updates = 0;
                return 0;
            }

            internal void Took(int index) => _next = index + 1;

            internal void EndUpdate() => _updates++;

            internal int Minimum(int count) => (count + _maxPeriod - 1) / _maxPeriod;
        }

        // Priority.Last and __runOriginal: see ValheimCommunityPatch.ApplyPatches. A mod that
        // replaces this helper has its own loop over the list, so this one leaves it alone.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomFixedUpdate))]
        private static void CustomFixedUpdatePrefix(ref List<IMonoUpdater> source, float deltaTime, bool __runOriginal) {
            if (!ReferenceEquals(source, Character.Instances)) { return; }

            if (!__runOriginal || Enabled == null || !Enabled.Value) {
                _timingCharacters = false;
                Disengage();
                return;
            }

            if (_engaged) { source = UpdateRestingCharacters(source, deltaTime); }

            _timingCharacters = true;
            _loopStart = Stopwatch.GetTimestamp();
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomFixedUpdate))]
        private static void CustomFixedUpdatePostfix() {
            if (!_timingCharacters) { return; }

            _timingCharacters = false;
            float spentMs = Mathf.Min(MsSince(_loopStart), UpdateBudget.Value * SpikeLimit);
            _everyStepMs += (spentMs - _everyStepMs) * SmoothingPerStep;
            UpdateEngaged();
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.UpdateAI))]
        private static void UpdateAIPrefix(ref List<IUpdateAI> source, float deltaTime, bool __runOriginal) {
            _timingAi = __runOriginal;
            if (!__runOriginal) { return; }

            if (_engaged) { source = UpdateRestingAi(source, deltaTime); }

            _loopStart = Stopwatch.GetTimestamp();
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.UpdateAI))]
        private static void UpdateAIPostfix() {
            if (!_timingAi) { return; }

            _timingAi = false;
            float spentMs = Mathf.Min(MsSince(_loopStart), UpdateBudget.Value * SpikeLimit / AiUpdatesPerStep);
            _everyAiMs += (spentMs - _everyAiMs) * SmoothingPerAiUpdate;
        }

        // Runs before RandomMovement decides, so a wander ended here stops the creature in the same
        // call, through the game's own "reached it" path.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseAI), "RandomMovement")]
        private static void RandomMovementPrefix(BaseAI __instance, float dt) {
            if (!_engaged || __instance.m_reachedRandomMoveTarget || __instance.m_randomMoveUpdateTimer <= 0f) { return; }

            Character character = __instance.m_character;
            Vector3 tried = character.m_currentVel;
            float triedSqr = tried.x * tried.x + tried.z * tried.z;
            if (triedSqr < TriedSpeedSqr || __instance.IsAlerted()) { return; }

            Vector3 velocity = character.m_body.linearVelocity;
            if (velocity.x * velocity.x + velocity.z * velocity.z >= triedSqr * BlockedShareSqr) { return; }

            if (Random.value < dt / GiveUpSeconds) { __instance.m_reachedRandomMoveTarget = true; }
        }

        private static void UpdateEngaged() {
            float budget = UpdateBudget.Value;

            if (!_engaged) {
                if (_everyStepMs + _everyAiMs * AiUpdatesPerStep <= budget || TakenOver()) { return; }

                _engaged = true;
                _quietSteps = 0;
                _everyStepMs = 0f;
                _everyAiMs = 0f;
                CharacterTurns.Reset();
                AiTurns.Reset();
                Logger.LogDebug($"{FixName}: creature updates are over the {budget:F1} ms budget, so resting creatures now take turns.");
                return;
            }

            float fullCost = _everyStepMs + Resting.Count * _restingUpdateMs
                + (_everyAiMs + RestingAi.Count * _restingAiUpdateMs) * AiUpdatesPerStep;
            _quietSteps = fullCost < budget * DisengageShare ? _quietSteps + 1 : 0;
            if (_quietSteps >= DisengageSteps) { Disengage(); }
        }

        private static void Disengage() {
            if (!_engaged) { return; }

            _engaged = false;
            Logger.LogDebug($"{FixName}: creature updates fit the budget again, so every creature updates every step.");
        }

        private static bool TakenOver() {
            foreach (TakeoverCheck takeover in Takeovers) {
                if (takeover.TakenOver) { return true; }
            }

            return false;
        }

        private static List<IMonoUpdater> UpdateRestingCharacters(List<IMonoUpdater> source, float deltaTime) {
            long start = Stopwatch.GetTimestamp();

            PlayerPositions.Clear();
            foreach (Player player in Player.GetAllPlayers()) { PlayerPositions.Add(player.transform.position); }

            EveryStep.Clear();
            Resting.Clear();
            RestingIndices.Clear();
            for (int i = 0; i < source.Count; i++) {
                IMonoUpdater updater = source[i];
                if (updater is Character creature && IsResting(creature)) {
                    Resting.Add(creature);
                    RestingIndices.Add(i);
                } else {
                    EveryStep.Add(updater);
                }
            }

            int count = Resting.Count;
            if (count == 0) { return EveryStep; }

            // The rest of the creature work has been costing this; resting creatures get what is left.
            float leftMs = UpdateBudget.Value - _everyStepMs - (_everyAiMs + _restingAiMs) * AiUpdatesPerStep;
            long deadline = start + (long)(Mathf.Max(leftMs, 0f) * TicksPerMs);
            int minimum = CharacterTurns.Minimum(count);
            int position = CharacterTurns.First(RestingIndices);

            long updatesStart = Stopwatch.GetTimestamp();
            int done = 0;
            while (done < count) {
                position = CharacterTurns.Wrap(position, count);
                UpdateResting(Resting[position], deltaTime * CharacterTurns.Period);
                CharacterTurns.Took(RestingIndices[position]);
                position++;
                done++;
                if (done >= minimum && Stopwatch.GetTimestamp() >= deadline) { break; }
            }

            CharacterTurns.EndUpdate();
            _restingUpdateMs += (MsSince(updatesStart) / done - _restingUpdateMs) * SmoothingPerStep;
            return EveryStep;
        }

        private static void UpdateResting(Character creature, float delta) {
            // A sleeping body reports no ground contact, and this update wakes it for the rest of the
            // update, where the game would then read it as having left the ground.
            if (creature.m_body.IsSleeping()) { creature.m_lastGroundTouch = 0f; }

            creature.CustomFixedUpdate(delta);

            if (creature.m_body.linearVelocity.sqrMagnitude < SleepMaxSpeedSqr && IsOnTerrain(creature)) {
                creature.m_body.Sleep();
            }
        }

        // m_lastGroundHeight is the terrain under the creature, re-read by the update, so ground dug
        // away from under a sleeping creature leaves it awake, to fall. The slope allowance is how
        // far a capsule standing on a slope sits above the terrain under its centre.
        private static bool IsOnTerrain(Character creature) {
            float upright = Mathf.Max(creature.m_lastGroundNormal.y, 0.5f);
            float allowed = SleepMaxGroundGap + creature.GetRadius() * (1f / upright - 1f);
            return Mathf.Abs(creature.transform.position.y - creature.m_lastGroundHeight.y) < allowed;
        }

        private static List<IUpdateAI> UpdateRestingAi(List<IUpdateAI> source, float deltaTime) {
            long start = Stopwatch.GetTimestamp();

            EveryAi.Clear();
            RestingAi.Clear();
            RestingAiIndices.Clear();
            for (int i = 0; i < source.Count; i++) {
                if (source[i] is BaseAI ai && ai.m_character is Character creature && IsResting(creature)) {
                    RestingAi.Add(source[i]);
                    RestingAiIndices.Add(i);
                } else {
                    EveryAi.Add(source[i]);
                }
            }

            int count = RestingAi.Count;
            if (count == 0) {
                _restingAiMs -= _restingAiMs * SmoothingPerAiUpdate;
                return EveryAi;
            }

            long deadline = start + (long)(UpdateBudget.Value * AiShareOfBudget * TicksPerMs);
            int minimum = AiTurns.Minimum(count);
            int position = AiTurns.First(RestingAiIndices);

            long updatesStart = Stopwatch.GetTimestamp();
            int done = 0;
            while (done < count) {
                position = AiTurns.Wrap(position, count);
                RestingAi[position].UpdateAI(deltaTime * AiTurns.Period);
                AiTurns.Took(RestingAiIndices[position]);
                position++;
                done++;
                if (done >= minimum && Stopwatch.GetTimestamp() >= deadline) { break; }
            }

            AiTurns.EndUpdate();
            float spentMs = MsSince(updatesStart);
            _restingAiMs += (spentMs - _restingAiMs) * SmoothingPerAiUpdate;
            _restingAiUpdateMs += (spentMs / done - _restingAiUpdateMs) * SmoothingPerAiUpdate;
            return EveryAi;
        }

        // The cheap managed tests come first; most creatures that fail do so on the first few.
        private static bool IsResting(Character creature) {
            if (creature.m_moveDir.sqrMagnitude >= 0.01f) { return false; }
            if (!creature.m_groundContact && !creature.m_body.IsSleeping()) { return false; }

            BaseAI ai = creature.m_baseAI;
            if (ReferenceEquals(ai, null)) { return false; }

            if (creature.m_isDead || creature.m_flying || creature.m_standUp > 0f
                || creature.m_staggerDamage > 0f || creature.m_pushForce.sqrMagnitude > 0f
                || creature.m_cashedInLiquidDepth > 0f || creature.m_lavaHeatLevel > 0f
                || !ReferenceEquals(creature.m_lastGroundBody, null)
                || creature.m_seman.m_statusEffects.Count > 0) {
                return false;
            }

            if (ai.m_alerted || ai.m_timeSinceHurt < RestAfterHurtSeconds || ai.HaveRider()) { return false; }
            if (ai is MonsterAI monster
                && (!ReferenceEquals(monster.m_targetCreature, null) || !ReferenceEquals(monster.m_targetStatic, null))) {
                return false;
            }

            if (creature is Humanoid humanoid && humanoid.m_currentAttack != null) { return false; }

            ZDO zdo = creature.m_nview.GetZDO();
            if (zdo == null || !zdo.IsOwner()) { return false; }

            Vector3 position = zdo.GetPosition();
            for (int i = 0; i < PlayerPositions.Count; i++) {
                Vector3 player = PlayerPositions[i];
                float dx = position.x - player.x;
                float dz = position.z - player.z;
                if (dx * dx + dz * dz < RestMinPlayerDistanceSqr) { return false; }
            }

            return creature.m_body.linearVelocity.sqrMagnitude < RestMaxSpeedSqr;
        }

        private static float MsSince(long start) => (Stopwatch.GetTimestamp() - start) / TicksPerMs;
    }
}
