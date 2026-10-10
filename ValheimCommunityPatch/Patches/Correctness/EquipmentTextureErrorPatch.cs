using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Equipment Texture Errors: stops the "doesn't have a texture property" errors the Deep North
    // Shadow logs each time one is created.
    //
    // VisEquipment copies the body and armour textures (_SkinBumpMap, _ChestTex, _LegsTex and their
    // bump and metal maps) from a model's base material and from each armour item's material with
    // unguarded Material.GetTexture calls. The Shadow (ShadowPerson) uses the player rig, but its base
    // material and all of its armour materials are one translucent Standard-shader material with none
    // of those properties. Unity logs an error with a stack trace for each read, about eleven per
    // Shadow, and a village creates several at once.
    //
    // Transpilers swap every GetTexture call in UpdateBaseModel, SetChestEquipped and SetLegEquipped
    // for one that returns null when the material lacks the property, which is what GetTexture
    // returns after logging. The HasProperty test is the guard vanilla already puts on some of these
    // same reads. The Shadow keeps its vanilla look, and each skipped read is written to the debug
    // log instead.
    //
    // Client: Shadows are created and dressed on the client that loads them. Provenance: the defect
    // was reported by nezuma's ShadowPersonMaterialFix, which swaps the Shadow's shader instead.
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
                "Stops the 'doesn't have a texture property' errors logged for each Deep North Shadow when " +
                "it is created, about eleven per Shadow. Its body and armour textures are read from a " +
                "material that has none of them. The reads are still visible with EnableDebugMode on. " +
                "Changing this requires a game restart.");
        }

        private static readonly MethodInfo GetTextureMethod =
            AccessTools.Method(typeof(Material), nameof(Material.GetTexture), new[] { typeof(int) });
        private static readonly MethodInfo GetTextureIfPresentMethod =
            AccessTools.Method(typeof(EquipmentTextureErrorPatch), nameof(GetTextureIfPresent));

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
