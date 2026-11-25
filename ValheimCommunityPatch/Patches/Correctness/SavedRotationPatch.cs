using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // The chunked save rounds rotations down to half degrees, so pieces load slightly turned and walls open gaps.
    // Prefixes WriteSmallRotation to round to nearest; GetSaveData and ZDO.Load hooks save and restore exact angles.
    // Building pieces load exactly where they were placed, and saves stay readable without the mod.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class SavedRotationPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SavedRotationPatch),
                ValConfig.SectionCorrectness,
                "Fix Saved Rotations",
                true,
                "Saves every object's rotation exactly, so building pieces no longer come back slightly turned " +
                "after a load; saves stay readable without the mod.");
        }

        // The extra value's key. Disk only: taken out again as the object loads. Vanilla keeps it as unknown extra
        // data, so a world saved with the fix loads without it to the nearest half degree.
        internal static readonly int ExactRotationKey = "VCP_ExactRotation".GetStableHashCode();

        // Far below anything visible: a 4 m wall's end moves by 0.03 mm.
        private const float Tolerance = 0.001f;

        [ThreadStatic] private static ZDO _saving;

        // Half-degree steps, wrapped into 0..719 so the vanilla cast never sees a negative or 360.
        private static float ToHalfDegrees(float angle) {
            double steps = Math.Round(angle * 2.0, MidpointRounding.AwayFromZero) % 720.0;
            if (steps < 0.0) { steps += 720.0; }

            return (float)steps * 0.5f;
        }

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        // What a save with the rounding below keeps of a rotation: ZDO.Save drops one under half a degree
        // on every axis, and WriteSmallRotation drops x and z when both are within a step of zero.
        internal static Vector3 SavedForm(Vector3 rotation) {
            if ((int)Math.Abs(rotation.x * 2f) < 1 && (int)Math.Abs(rotation.y * 2f) < 1 && (int)Math.Abs(rotation.z * 2f) < 1) {
                return Vector3.zero;
            }

            float x = ToHalfDegrees(rotation.x), y = ToHalfDegrees(rotation.y), z = ToHalfDegrees(rotation.z);
            int stepsX = (int)(x * 2f), stepsZ = (int)(z * 2f);
            if ((stepsX <= 1 || stepsX >= 719) && (stepsZ <= 1 || stepsZ >= 719)) {
                return new Vector3(0f, y, 0f);
            }

            return new Vector3(x, y, z);
        }

        internal static bool SameRotation(Vector3 a, Vector3 b) =>
            Math.Abs(Mathf.DeltaAngle(a.x, b.x)) <= Tolerance &&
            Math.Abs(Mathf.DeltaAngle(a.y, b.y)) <= Tolerance &&
            Math.Abs(Mathf.DeltaAngle(a.z, b.z)) <= Tolerance;

        // Nearest rather than vanilla's cast down: Unity hands back some of the game's 22.5 degree steps a hair short
        // (225 as 224.99998), which the cast saves half a degree off.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZPackage), nameof(ZPackage.WriteSmallRotation))]
        private static void WriteSmallRotationPrefix(ref Vector3 v3) {
            if (!Enabled.Value || !IsFinite(v3)) { return; }

            v3 = new Vector3(ToHalfDegrees(v3.x), ToHalfDegrees(v3.y), ToHalfDegrees(v3.z));
        }

        // ZDO.Save reads its extra data through GetSaveData before it writes anything; the postfix below
        // needs to know whose.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Save))]
        private static void SavePrefix(ZDO __instance) => _saving = __instance;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Save))]
        private static void SavePostfix() => _saving = null;

        // vec3s is a scratch list refilled for every object, so adding to it touches no live data.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZDOExtraData), nameof(ZDOExtraData.GetSaveData))]
        private static void GetSaveDataPostfix(ZDOID zid, ref List<KeyValuePair<int, Vector3>> vec3s) {
            ZDO zdo = _saving;
            if (zdo == null || zdo.m_uid != zid || !Enabled.Value) { return; }

            // Left in memory only when the fix was off as the world loaded.
            for (int i = vec3s.Count - 1; i >= 0; i--) {
                if (vec3s[i].Key == ExactRotationKey) { vec3s.RemoveAt(i); }
            }

            Vector3 rotation = zdo.m_rotation;
            if (!IsFinite(rotation)) { return; }

            // Marked as meant at a repairable angle, unless it only loaded there from an older save.
            Vector3 saved = SavedForm(rotation);
            bool markAsMeant = SavedRotationRepairPatch.LooksShifted(saved) &&
                SavedRotationRepairPatch.IsPiece(zdo.GetPrefab()) && !SavedRotationRepairPatch.LoadedUnrepaired(zid);
            if (SameRotation(saved, rotation) && !markAsMeant) { return; }

            vec3s.Add(new KeyValuePair<int, Vector3>(ExactRotationKey, rotation));
        }

        // Only the chunked format saves half degrees; the older ones kept rotations whole. The exact value is taken out
        // before the object is used, so it never reaches the network, and is used only while the saved rotation still
        // matches it: a piece a game without the fix turned since keeps that turn.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Load))]
        private static void LoadPostfix(ZDO __instance, Version.World version) {
            if (version < Version.World.ChunkedSave) { return; }

            ZDOID uid = __instance.m_uid;
            if (ZDOExtraData.GetVec3(uid, ExactRotationKey, out Vector3 exact)) {
                // Off: keep it as vanilla would, and leave the rotation alone.
                if (!Enabled.Value) { return; }

                ZDOExtraData.RemoveVec3(uid, ExactRotationKey);
                if (SameRotation(SavedForm(exact), __instance.m_rotation)) {
                    __instance.m_rotation = exact;
                    return;
                }
            }

            SavedRotationRepairPatch.RepairIfShifted(__instance);
        }
    }
}
