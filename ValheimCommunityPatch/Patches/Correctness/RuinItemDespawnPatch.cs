using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Ruin Item Despawn: lets dropped items despawn in Ashlands ruins, as they do anywhere else
    // outside a player's base.
    //
    // A dropped item despawns after an hour unless ItemDrop.IsInsideBase finds a player base area at its
    // position. CharredRuins, CharredTowerRuins and the rooms of AshlandRuins carry such an area of their
    // own, with a radius of up to 40 m, as a plain child of the location or room. It keeps spawners out of
    // the ruin, and it also keeps everything dropped there in the world for good, so loot piles up.
    //
    // A transpiler points IsInsideBase's one EffectArea.IsPointInsideArea call at a lookup that passes over
    // areas sitting under a Location or Room. Pieces are spawned as objects of their own, so a campfire or
    // workbench never sits under one and a base built in a ruin still counts, even where the ruin's area is
    // found first. Spawning makes its own lookups and is unchanged, and so are the fire pits and guard stones
    // of traders and Dvergr outposts, which are pieces.
    //
    // Both: runs on whichever side owns the item.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ItemDrop))]
    internal static class RuinItemDespawnPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(RuinItemDespawnPatch),
                ValConfig.SectionCorrectness,
                "Fix Ruin Item Despawn",
                true,
                "Lets dropped items despawn in Ashlands ruins as they do anywhere else outside a player's " +
                "base. Several ruins count as a player base themselves, so everything dropped in them stayed " +
                "in the world for good. Items near a base a player built in a ruin still stay. Changing this " +
                "requires a game restart.");
        }

        private static readonly MethodInfo LookupMethod = AccessTools.Method(typeof(EffectArea), nameof(EffectArea.IsPointInsideArea));
        private static readonly MethodInfo FindBaseAreaMethod = AccessTools.Method(typeof(RuinItemDespawnPatch), nameof(FindBaseArea));

        private static bool IsPartOfRuin(EffectArea area) =>
            area.GetComponentInParent<Location>() != null || area.GetComponentInParent<Room>() != null;

        private static EffectArea FindBaseArea(Vector3 point, EffectArea.Type type, float radius) {
            EffectArea first = EffectArea.IsPointInsideArea(point, type, radius);
            if (first == null || !Enabled.Value || !IsPartOfRuin(first)) { return first; }

            // Vanilla stops at the first match, and a player's base may share this spot.
            int count = Physics.OverlapSphereNonAlloc(point, radius, EffectArea.m_tempColliders, EffectArea.s_characterMask);
            for (int i = 0; i < count; i++) {
                EffectArea area = EffectArea.m_tempColliders[i].GetComponent<EffectArea>();
                if (area != null && (area.m_type & type) != EffectArea.Type.None && !IsPartOfRuin(area)) { return area; }
            }

            return null;
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("IsInsideBase")]
        private static IEnumerable<CodeInstruction> IsInsideBaseTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, LookupMethod, FindBaseAreaMethod, "ItemDrop.IsInsideBase", expected: 1, patched: original);
        }
    }
}
