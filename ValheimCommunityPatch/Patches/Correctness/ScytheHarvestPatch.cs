using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Scythe Harvest: lets the scythe cut every plant the game marks as harvestable, such as
    // Magecaps, Jotun Puffs, mushrooms, dandelions and thistles.
    //
    // Attack.DoMeleeAttack gathers the colliders a swing reaches and looks for a Pickable on the object
    // each one sits on. Farmed crops keep their collider on that object, but these plants keep the
    // Pickable on the root and their colliders on child objects, so the swing finds nothing even though
    // m_harvestable is set.
    //
    // A transpiler replaces that one GetComponent<Pickable> with a lookup that falls back to the parent.
    // Vanilla's harvestable and CanBePicked checks and its Interact call still run on what the lookup
    // returns, so plants without the flag, such as berry bushes, stay out of reach.
    //
    // Client: the harvest only runs for the local player's swing.
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
                "Lets the scythe cut every plant the game marks as harvestable, including Magecaps, Jotun " +
                "Puffs, mushrooms, dandelions, thistles, fiddleheads and smoke puffs. Vanilla looks for the " +
                "plant on the object its collider sits on, and these keep their colliders on child objects, " +
                "so a swing missed them. Changing this requires a game restart.");
        }

        private static readonly MethodInfo GetPickableMethod =
            AccessTools.Method(typeof(GameObject), nameof(GameObject.GetComponent), Type.EmptyTypes, new[] { typeof(Pickable) });

        private static readonly MethodInfo FindPickableMethod =
            AccessTools.Method(typeof(ScytheHarvestPatch), nameof(FindPickable));

        private static readonly HashSet<Pickable> _cutThisFrame = new HashSet<Pickable>();
        private static int _cutFrame = -1;

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
