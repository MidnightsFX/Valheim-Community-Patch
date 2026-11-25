using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // VisEquipment reads body and armor textures the Deep North Shadow's material lacks, logging ~11 errors per Shadow.
    // Transpiles UpdateBaseModel, SetChestEquipped and SetLegEquipped so GetTexture reads a missing property as null.
    // Shadows load without error spam and look the same; each skipped read goes to the debug log.
    // Provenance: the defect was reported by nezuma's ShadowPersonMaterialFix, which swaps the Shadow's shader instead.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(VisEquipment))]
    internal static class EquipmentTextureErrorPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(EquipmentTextureErrorPatch),
                ValConfig.SectionCorrectness,
                "Fix Equipment Texture Errors",
                true,
                "Stops the 'doesn't have a texture property' errors each Deep North Shadow logs when it is " +
                "created; they still show with EnableDebugMode on; turning it on needs a game restart.");
        }

        private static readonly MethodInfo GetTextureMethod =
            AccessTools.Method(typeof(Material), nameof(Material.GetTexture), new[] { typeof(int) });
        private static readonly MethodInfo GetTextureIfPresentMethod =
            AccessTools.Method(typeof(EquipmentTextureErrorPatch), nameof(GetTextureIfPresent));

        // Null is what GetTexture returns after logging, so the look is unchanged. Vanilla already guards some of these
        // same reads with HasProperty.
        private static Texture GetTextureIfPresent(Material material, int nameID) {
            if (material.HasProperty(nameID) || !Enabled.Value) { return material.GetTexture(nameID); }

            if (Logger.DebugEnabled) {
                Logger.LogDebug(
                    $"VisEquipment: material '{material.name}' with shader '{(material.shader != null ? material.shader.name : "null")}' " +
                    $"has no texture property {SlotName(nameID)}, read as null.");
            }

            return null;
        }

        // Only for the debug line: the texture slots these methods read, by name.
        private static Dictionary<int, string> _slotNames;

        private static string SlotName(int nameID) {
            if (_slotNames == null) {
                _slotNames = new Dictionary<int, string>();
                foreach (string slot in new[] { "_MainTex", "_SkinBumpMap", "_ChestTex", "_ChestBumpMap", "_ChestMetal", "_LegsTex", "_LegsBumpMap", "_LegsMetal" }) {
                    _slotNames[Shader.PropertyToID(slot)] = slot;
                }
            }

            return _slotNames.TryGetValue(nameID, out string name) ? name : nameID.ToString();
        }

        // Any count is accepted: each swap stands alone, so a game update that adds or removes a
        // texture slot does not switch the fix off.
        private static IEnumerable<CodeInstruction> GuardReads(IEnumerable<CodeInstruction> instructions, string method) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, GetTextureMethod, GetTextureIfPresentMethod, "VisEquipment." + method);
        }

        // Priority.Last on all three: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("UpdateBaseModel")]
        private static IEnumerable<CodeInstruction> UpdateBaseModelTranspiler(IEnumerable<CodeInstruction> instructions) =>
            GuardReads(instructions, "UpdateBaseModel");

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("SetChestEquipped")]
        private static IEnumerable<CodeInstruction> SetChestEquippedTranspiler(IEnumerable<CodeInstruction> instructions) =>
            GuardReads(instructions, "SetChestEquipped");

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("SetLegEquipped")]
        private static IEnumerable<CodeInstruction> SetLegEquippedTranspiler(IEnumerable<CodeInstruction> instructions) =>
            GuardReads(instructions, "SetLegEquipped");
    }
}
