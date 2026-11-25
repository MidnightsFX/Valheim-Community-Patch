using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // A melee swing looks for a Pickable on each hit collider's own object, so it misses plants with child colliders.
    // Transpiles Attack.DoMeleeAttack's GetComponent<Pickable> into a lookup that falls back to the parent object.
    // The scythe cuts every plant marked harvestable, such as Magecaps, Jotun Puffs, mushrooms and thistles.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Attack))]
    internal static class ScytheHarvestPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ScytheHarvestPatch),
                ValConfig.SectionCorrectness,
                "Fix Scythe Harvest",
                true,
                "Lets the scythe cut every plant the game marks as harvestable, such as Magecaps, Jotun Puffs, " +
                "mushrooms, dandelions and thistles; turning it on needs a game restart.");
        }

        private static readonly MethodInfo GetPickableMethod =
            AccessTools.Method(typeof(GameObject), nameof(GameObject.GetComponent), Type.EmptyTypes, new[] { typeof(Pickable) });

        private static readonly MethodInfo FindPickableMethod =
            AccessTools.Method(typeof(ScytheHarvestPatch), nameof(FindPickable));

        private static readonly HashSet<Pickable> _cutThisFrame = new HashSet<Pickable>();
        private static int _cutFrame = -1;

        // Vanilla's harvestable and CanBePicked checks still run on the result, so unflagged plants such as
        // berry bushes stay out of reach.
        private static Pickable FindPickable(GameObject hit) {
            Pickable own = hit.GetComponent<Pickable>();
            if (own != null || !Enabled.Value) { return own; }

            Pickable plant = hit.GetComponentInParent<Pickable>();
            if (plant == null) { return null; }

            // Tracked per plant, so a swing still cuts everything in reach. Each of one plant's colliders
            // lands here, and a plant another player owns stays pickable until its owner answers, so only
            // the first may reach vanilla's Interact, which rolls the bonus yield and shows its text.
            if (_cutFrame != Time.frameCount) {
                _cutFrame = Time.frameCount;
                _cutThisFrame.Clear();
            }

            return _cutThisFrame.Add(plant) ? plant : null;
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("DoMeleeAttack")]
        private static IEnumerable<CodeInstruction> DoMeleeAttackTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, GetPickableMethod, FindPickableMethod, "Attack.DoMeleeAttack", expected: 1, patched: original);
        }
    }
}
