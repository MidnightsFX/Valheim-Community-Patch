using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Ashlands ruins carry their own player-base area, so items dropped in them never despawn and loot piles up.
    // Transpiles ItemDrop.IsInsideBase's EffectArea.IsPointInsideArea call to skip areas under a Location or Room.
    // Dropped items despawn in ruins as anywhere else, while a base a player built in a ruin still keeps its items.
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
                "Lets dropped items despawn in Ashlands ruins as they do anywhere else, while items near a base " +
                "built in a ruin still stay; turning it on needs a game restart.");
        }

        private static readonly MethodInfo LookupMethod = AccessTools.Method(typeof(EffectArea), nameof(EffectArea.IsPointInsideArea));
        private static readonly MethodInfo FindBaseAreaMethod = AccessTools.Method(typeof(RuinItemDespawnPatch), nameof(FindBaseArea));

        // CharredRuins, CharredTowerRuins and AshlandRuins' rooms hold the area as a plain child, to keep spawners
        // out; spawning makes its own lookups and still sees it. Pieces, traders' and Dvergr outposts' fire pits and
        // guard stones included, are objects of their own and never sit under one, so a base in a ruin still counts.
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
