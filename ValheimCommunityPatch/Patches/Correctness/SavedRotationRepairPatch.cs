using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Saves before 1.0.30 rounded rotations down, leaving building pieces half a degree short of the game's 22.5 degree steps.
    // A ZDO.Load postfix turns them back and drops the exact rotations older versions of this mod saved; LoadChunks logs the count.
    // With the admin's opt-in, old buildings return to the angles they were placed at on the next world load.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class SavedRotationRepairPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SavedRotationRepairPatch),
                ValConfig.SectionCorrectness,
                "Repair Saved Rotations",
                false,
                "Turns building pieces that a save before Valheim 1.0.30 left half a degree off back when the world " +
                "loads, including any a 1 degree building mod placed at those angles (22, 67, 112 ... degrees); the " +
                "world load logs how many pieces sit there.");
        }

        // Older versions of this mod saved an object's exact rotation under this key, and marked pieces
        // deliberately placed at a repairable angle with it.
        private static readonly int ExactRotationKey = "VCP_ExactRotation".GetStableHashCode();

        // 22.5 degrees in the save's half-degree steps.
        private const int StepsPerPlacement = 45;
        private const int StepsPerTurn = 720;

        private static int _repaired, _unrepaired;

        // Prefab hashes with a Piece component, collected as a world starts loading.
        private static HashSet<int> _pieces = new HashSet<int>();

        // An upright rotation exactly half a degree below a 22.5 degree step, as read from the save. A
        // building mod turning in 1 degree steps also places pieces there (22, 67, 112 ...), and a save
        // cannot tell the two apart, which is why the repair is off by default.
        private static bool LooksShifted(Vector3 saved) {
            if (saved.x != 0f || saved.z != 0f) { return false; }

            // A whole number of half degrees, so this is exact.
            int steps = (int)(saved.y * 2f);
            return steps >= 0 && steps < StepsPerTurn && saved.y * 2f == steps && (steps + 1) % StepsPerPlacement == 0;
        }

        // Only the chunked format saves half degrees; the older ones kept rotations whole. The exact
        // rotation goes whether or not the repair is on, since the game has no use for it.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Load))]
        private static void ZdoLoadPostfix(ZDO __instance, Version.World version) {
            if (version < Version.World.ChunkedSave) { return; }

            // An object that carried one was saved as placed.
            if (ZDOExtraData.RemoveVec3(__instance.m_uid, ExactRotationKey)) { return; }

            // Pieces only: trees and rocks land on these angles by chance.
            Vector3 rotation = __instance.m_rotation;
            if (!LooksShifted(rotation) || !_pieces.Contains(__instance.GetPrefab())) { return; }

            if (!Enabled.Value) {
                _unrepaired++;
                return;
            }

            rotation.y = ((int)(rotation.y * 2f) + 1) % StepsPerTurn * 0.5f;
            __instance.m_rotation = rotation;
            _repaired++;
        }

        // The world loads in ZNet.Start, after ZNetScene.Awake has registered every prefab, modded ones
        // included, so the piece set is complete here.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZDOMan.LoadChunks))]
        private static void LoadChunksPrefix() {
            _repaired = 0;
            _unrepaired = 0;

            var pieces = new HashSet<int>();
            if (ZNetScene.instance != null) {
                foreach (KeyValuePair<int, GameObject> prefab in ZNetScene.instance.m_namedPrefabs) {
                    if (prefab.Value != null && prefab.Value.GetComponent<Piece>() != null) { pieces.Add(prefab.Key); }
                }
            }
            _pieces = pieces;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZDOMan.LoadChunks))]
        private static void LoadChunksPostfix() {
            if (_repaired > 0) {
                Logger.LogInfo($"Repair Saved Rotations: turned {_repaired} building piece(s) back by the half degree an earlier save took off.");
            }

            if (_unrepaired > 0) {
                Logger.LogInfo(
                    $"{_unrepaired} building piece(s) sit exactly half a degree short of the game's rotation steps, as saves " +
                    "before Valheim 1.0.30 left them. Turn on 'Repair Saved Rotations' to turn them back.");
            }
        }
    }
}
