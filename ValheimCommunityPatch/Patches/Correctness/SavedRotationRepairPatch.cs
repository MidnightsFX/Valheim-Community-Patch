using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Saves made without Fix Saved Rotations left building pieces half a degree short of the game's 22.5 degree steps.
    // Fix Saved Rotations' ZDO.Load postfix calls RepairIfShifted to turn them back; LoadChunks logs the count.
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
                "Turns building pieces that a save without Fix Saved Rotations left half a degree off back when " +
                "the world loads, including any a 1 degree building mod placed at those angles (22, 67, 112 ... " +
                "degrees); the world load logs how many pieces sit there.");
        }

        // 22.5 degrees in the save's half-degree steps.
        private const int StepsPerPlacement = 45;
        private const int StepsPerTurn = 720;

        private static int _repaired;

        // Prefab hashes with a Piece component. Built on the main thread as a world loads and replaced
        // whole, so the save thread can read it.
        private static HashSet<int> _pieces = new HashSet<int>();

        // Pieces that loaded at such an angle with no exact rotation and were left as they are. Fix
        // Saved Rotations does not mark them as meant when it saves them, so turning the repair on later
        // still reaches them. Filled only while a world loads, read by the save.
        private static readonly HashSet<ZDOID> Unrepaired = new HashSet<ZDOID>();

        internal static bool IsPiece(int prefab) => _pieces.Contains(prefab);

        // An upright rotation exactly half a degree below a 22.5 degree step, as read from the save. A
        // building mod turning in 1 degree steps also places pieces there (22, 67, 112 ...), and an old
        // save cannot tell the two apart, which is why the repair is off by default.
        internal static bool LooksShifted(Vector3 saved) {
            if (saved.x != 0f || saved.z != 0f) { return false; }

            // A whole number of half degrees, so this is exact.
            int steps = (int)(saved.y * 2f);
            return steps >= 0 && steps < StepsPerTurn && saved.y * 2f == steps && (steps + 1) % StepsPerPlacement == 0;
        }

        internal static bool LoadedUnrepaired(ZDOID uid) => Unrepaired.Contains(uid);

        // Fix Saved Rotations skips this for an object carrying its own exact rotation, so pieces it saved
        // as meant are never turned. Pieces only: trees and rocks land on these angles by chance.
        internal static void RepairIfShifted(ZDO zdo) {
            Vector3 rotation = zdo.m_rotation;
            if (!LooksShifted(rotation) || !IsPiece(zdo.GetPrefab())) { return; }

            if (!Enabled.Value) {
                Unrepaired.Add(zdo.m_uid);
                return;
            }

            rotation.y = ((int)(rotation.y * 2f) + 1) % StepsPerTurn * 0.5f;
            zdo.m_rotation = rotation;
            Interlocked.Increment(ref _repaired);
        }

        private static void CollectPieces() {
            var pieces = new HashSet<int>();
            if (ZNetScene.instance != null) {
                foreach (KeyValuePair<int, GameObject> prefab in ZNetScene.instance.m_namedPrefabs) {
                    if (prefab.Value != null && prefab.Value.GetComponent<Piece>() != null) { pieces.Add(prefab.Key); }
                }
            }
            _pieces = pieces;
        }

        // A new world never loads, and its pieces still need marking when saved. Priority.Last: after
        // Jotunn and other mods have added their prefabs.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static void ZNetSceneAwakePostfix() => CollectPieces();

        // The world loads in ZNet.Start, after ZNetScene.Awake has registered every prefab, modded ones
        // included, so the piece set is complete here.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZDOMan.Load))]
        private static void LoadPrefix() {
            Unrepaired.Clear();
            CollectPieces();
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZDOMan.LoadChunks))]
        private static void LoadChunksPrefix() {
            Unrepaired.Clear();
            CollectPieces();
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZDOMan.LoadChunks))]
        private static void LoadChunksPostfix() {
            int repaired = Interlocked.Exchange(ref _repaired, 0);
            if (repaired > 0) {
                Logger.LogInfo($"Repair Saved Rotations: turned {repaired} building piece(s) back by the half degree an earlier save took off.");
            }

            if (Unrepaired.Count > 0) {
                Logger.LogInfo(
                    $"{Unrepaired.Count} building piece(s) sit exactly half a degree short of the game's rotation steps, as a " +
                    "save without Fix Saved Rotations leaves them. Turn on 'Repair Saved Rotations' to turn them back.");
            }
        }
    }
}
