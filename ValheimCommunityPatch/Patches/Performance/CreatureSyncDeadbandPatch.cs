using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Idle Creature Sync: an idle creature's owner stops re-sending it to every player each
    // frame over physics jitter.
    //
    // The owner writes a creature's position and velocity (ZSyncTransform.OwnerSync), its body
    // velocity (Character.SyncVelocity) and its ground tilt (Character.UpdateGroundTilt) whenever
    // the value differs at all from the last one it saw. A character's rigidbody never sleeps, so
    // float noise changes all four every frame, each change bumps the ZDO's data revision, and a
    // changed ZDO is re-sent whole to every peer. A base of idle tamed animals fills its owner's
    // upload and every other player's download.
    //
    // Transpilers route those four writes through deadbands that compare against the value already
    // stored in the ZDO, so the error stays bounded and slow drift is still written once it crosses
    // the band: 2 cm of position, 0.05 m/s of velocity (below which velocity is written as zero)
    // and 1 degree of tilt. Vanilla's own change checks around each write are kept verbatim. Only
    // non-player characters are affected, and a moving creature crosses every band each frame.
    // Stands down when Network Performance System 1.9.1 or later is loaded, which applies the same
    // deadband.
    //
    // Both: the writes happen wherever the creature is owned, usually on a client.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class CreatureSyncDeadbandPatch {
        private const string FixName = "Fix Idle Creature Sync";

        // Also named by the plugin's soft dependency, which loads NPS first so Prepare can see it.
        internal const string NpsGuid = "MidnightsFX.NetworkPerformanceSystem";
        // System.Version: the game declares a global Version class of its own.
        private static readonly System.Version NpsDeadbandVersion = new System.Version(1, 9, 1);

        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(CreatureSyncDeadbandPatch),
                ValConfig.SectionPerformance,
                FixName,
                true,
                "Stops an idle creature being re-sent to every player many times a second over tiny " +
                "physics jitter. Vanilla re-sends a creature whenever its position, velocity or ground " +
                "tilt changes at all, and a standing creature's change every frame, so a base full of " +
                "tamed animals can fill its owner's upload and every other player's download. Changes " +
                "smaller than 2 cm, 0.05 m/s or 1 degree from what was last sent are no longer sent on " +
                "their own. Players, ships, carts and items are unaffected. Inactive when Network " +
                "Performance System 1.9.1 or later is installed, which does the same.");
        }

        private static readonly MethodInfo SetPositionMethod =
            AccessTools.Method(typeof(ZDO), nameof(ZDO.SetPosition), new[] { typeof(Vector3) });
        private static readonly MethodInfo SetVec3Method =
            AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(Vector3) });
        private static readonly MethodInfo SetQuaternionMethod =
            AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(Quaternion) });

        private static readonly FieldInfo VelocityKey = AccessTools.Field(typeof(ZDOVars), nameof(ZDOVars.s_velHash));
        private static readonly FieldInfo BodyVelocityKey = AccessTools.Field(typeof(ZDOVars), nameof(ZDOVars.s_bodyVelocity));
        private static readonly FieldInfo TiltKey = AccessTools.Field(typeof(ZDOVars), nameof(ZDOVars.s_tiltrot));

        private static readonly MethodInfo WritePositionMethod =
            AccessTools.Method(typeof(CreatureSyncDeadbandPatch), nameof(WritePosition));
        private static readonly MethodInfo WriteVelocityMethod =
            AccessTools.Method(typeof(CreatureSyncDeadbandPatch), nameof(WriteVelocity));
        private static readonly MethodInfo WriteBodyVelocityMethod =
            AccessTools.Method(typeof(CreatureSyncDeadbandPatch), nameof(WriteBodyVelocity));
        private static readonly MethodInfo WriteTiltMethod =
            AccessTools.Method(typeof(CreatureSyncDeadbandPatch), nameof(WriteTilt));

        private static bool _loggedStandDown;

        [HarmonyPrepare]
        private static bool Prepare() {
            if (!Chainloader.PluginInfos.TryGetValue(NpsGuid, out PluginInfo nps)) { return true; }

            System.Version version = nps?.Metadata?.Version;
            if (version == null || version < NpsDeadbandVersion) { return true; }

            if (!_loggedStandDown) {
                _loggedStandDown = true;
                Logger.LogInfo(
                    $"Network Performance System {version} is loaded and applies the same idle creature " +
                    $"deadband, so '{FixName}' stands down and leaves it to NPS.");
            }

            return false;
        }

        // Priority.Last on all three: see ValheimCommunityPatch.ApplyPatches. The first s_velHash
        // write is the world velocity; the second, under m_characterParentSync, is the velocity
        // relative to a parent and is left alone.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ZSyncTransform), "OwnerSync")]
        private static IEnumerable<CodeInstruction> OwnerSyncTranspiler(IEnumerable<CodeInstruction> instructions) {
            IEnumerable<CodeInstruction> codes = RedirectFirstWrite(
                instructions, SetPositionMethod, null, WritePositionMethod, "ZSyncTransform.OwnerSync", expected: 1, keyed: false);
            return RedirectFirstWrite(
                codes, SetVec3Method, VelocityKey, WriteVelocityMethod, "ZSyncTransform.OwnerSync", expected: 2);
        }

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Character), "SyncVelocity")]
        private static IEnumerable<CodeInstruction> SyncVelocityTranspiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectFirstWrite(instructions, SetVec3Method, BodyVelocityKey, WriteBodyVelocityMethod, "Character.SyncVelocity", expected: 1);

        // The first s_tiltrot write is the ground-tilt branch; the second is the wall-run branch,
        // which only players reach, and is left alone.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Character), "UpdateGroundTilt")]
        private static IEnumerable<CodeInstruction> UpdateGroundTiltTranspiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectFirstWrite(instructions, SetQuaternionMethod, TiltKey, WriteTiltMethod, "Character.UpdateGroundTilt", expected: 2);

        // Rewrites the first of exactly `expected` calls to `original` whose key argument is loaded
        // from `key` (every call, when not keyed) into a static call to `replacement`, which takes
        // the same stack plus the patched method's `this`. Same count-and-bail contract as
        // PatchHelper.ReplaceCalls: any other count leaves the method as it was, and says so.
        private static IEnumerable<CodeInstruction> RedirectFirstWrite(
            IEnumerable<CodeInstruction> instructions, MethodInfo original, FieldInfo key, MethodInfo replacement,
            string site, int expected, bool keyed = true) {
            if (original == null || replacement == null || (keyed && key == null)) {
                Logger.LogWarning($"{site}: a method or field '{FixName}' needs could not be resolved, so it is inactive here.");
                return instructions;
            }

            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int first = -1;
            int found = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(original)) { continue; }
                if (keyed && !KeyLoadedBefore(codes, i, key)) { continue; }

                if (found == 0) { first = i; }
                found++;
            }

            if (found != expected) {
                string what = keyed ? key.Name : original.Name;
                Logger.LogWarning(
                    $"{site}: expected {expected} {what} write(s), found {found}, so '{FixName}' leaves " +
                    "it to vanilla. Another mod has most likely already rewritten the method - if so, " +
                    "nothing is wrong.");
                return instructions;
            }

            // The inserted load takes over any branch that targeted the call.
            CodeInstruction loadThis = new CodeInstruction(OpCodes.Ldarg_0);
            codes[first].MoveLabelsTo(loadThis);
            codes[first].opcode = OpCodes.Call;
            codes[first].operand = replacement;
            codes.Insert(first, loadThis);

            return codes;
        }

        // The key is the call's second argument, loaded just before the value: one instruction
        // back for a local, two for a field.
        private static bool KeyLoadedBefore(List<CodeInstruction> codes, int call, FieldInfo key) {
            for (int i = call - 1; i >= 0 && i >= call - 3; i--) {
                if (codes[i].LoadsField(key)) { return true; }
            }

            return false;
        }

        // Read at call time rather than at patch time, so the toggle applies without a restart.
        private static bool Active => Enabled != null && Enabled.Value;

        // ReferenceEquals: a ZSyncTransform without a Character holds a real null, and Unity's ==
        // would add a native alive-check to a per-frame path.
        private static bool IsCreature(Character character) =>
            !ReferenceEquals(character, null) && !character.IsPlayer();

        // Stands in for OwnerSync's zdo.SetPosition(position).
        private static void WritePosition(ZDO zdo, Vector3 position, ZSyncTransform sync) {
            if (Active && IsCreature(sync.m_character) && Deadband.PositionSettled(position, zdo.GetPosition())) { return; }

            zdo.SetPosition(position);
        }

        // Stands in for OwnerSync's zdo.Set(ZDOVars.s_velHash, velocity).
        private static void WriteVelocity(ZDO zdo, int hash, Vector3 velocity, ZSyncTransform sync) {
            if (Active && IsCreature(sync.m_character) && !Deadband.VelocityChanged(ref velocity, zdo.GetVec3(hash, Vector3.zero))) { return; }

            zdo.Set(hash, velocity);
        }

        // Stands in for SyncVelocity's zdo.Set(ZDOVars.s_bodyVelocity, velocity).
        private static void WriteBodyVelocity(ZDO zdo, int hash, Vector3 velocity, Character character) {
            if (Active && !character.IsPlayer() && !Deadband.VelocityChanged(ref velocity, zdo.GetVec3(hash, Vector3.zero))) { return; }

            zdo.Set(hash, velocity);
        }

        // Stands in for UpdateGroundTilt's zdo.Set(ZDOVars.s_tiltrot, tilt). Identity is the default
        // viewers read when nothing is stored.
        private static void WriteTilt(ZDO zdo, int hash, Quaternion tilt, Character character) {
            if (Active && !character.IsPlayer() && Deadband.TiltSettled(tilt, zdo.GetQuaternion(hash, Quaternion.identity))) { return; }

            zdo.Set(hash, tilt);
        }

        // The comparisons, kept apart from the patch class and free of engine calls so they can be
        // exercised outside the game. Each skips a write only when the value is provably inside its
        // band, so NaN and the like are still written, as vanilla would.
        internal static class Deadband {
            internal const float PositionBand = 0.02f;
            internal const float VelocityBand = 0.05f;
            internal const float TiltBandDegrees = 1f;

            // Unit quaternions are under the band apart when |dot| exceeds the cosine of half of
            // it, which is the relation Quaternion.Angle inverts.
            private static readonly float CosHalfTiltBand = (float)Math.Cos(TiltBandDegrees * 0.5 * Math.PI / 180.0);

            internal static bool PositionSettled(Vector3 position, Vector3 stored) {
                float dx = position.x - stored.x;
                float dy = position.y - stored.y;
                float dz = position.z - stored.z;
                return dx * dx + dy * dy + dz * dz < PositionBand * PositionBand;
            }

            // Snaps a velocity inside the band to zero, then says whether to write it. A zero is
            // written whenever the stored value is not exactly zero, so a stopped creature never
            // keeps a small leftover velocity that viewers would extrapolate from.
            internal static bool VelocityChanged(ref Vector3 velocity, Vector3 stored) {
                const float bandSqr = VelocityBand * VelocityBand;

                if (velocity.x * velocity.x + velocity.y * velocity.y + velocity.z * velocity.z < bandSqr) {
                    velocity = default;
                    return stored.x != 0f || stored.y != 0f || stored.z != 0f;
                }

                float dx = velocity.x - stored.x;
                float dy = velocity.y - stored.y;
                float dz = velocity.z - stored.z;
                return !(dx * dx + dy * dy + dz * dz < bandSqr);
            }

            internal static bool TiltSettled(Quaternion tilt, Quaternion stored) {
                float dot = tilt.x * stored.x + tilt.y * stored.y + tilt.z * stored.z + tilt.w * stored.w;
                return Math.Abs(dot) > CosHalfTiltBand;
            }
        }
    }
}
