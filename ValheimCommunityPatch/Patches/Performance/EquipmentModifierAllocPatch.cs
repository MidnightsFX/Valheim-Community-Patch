using System.Reflection;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Equipment Modifier Allocation: the local player's equipment modifiers are totalled through
    // typed field reads instead of reflection that boxes every value, fifty times a second.
    //
    // Player.UpdateModifiers runs every physics step for the local player. For each of the eleven
    // modifier fields of an item's shared data (movement, heat resistance, the stamina modifiers,
    // max adrenaline) it reads all eight equipped slots with FieldInfo.GetValue, which boxes the
    // float: up to 88 small allocations per step.
    //
    // A prefix computes the same totals through FieldRefAccess readers built from vanilla's own
    // FieldInfo array, in vanilla's slot order (right, left, chest, legs, helmet, shoulder, utility,
    // trinket) and with the same float additions, so the stored values are bit for bit vanilla's.
    // Nothing is cached between steps: mods that change item stats while playing stay correct. It
    // stands down to vanilla whenever the inputs are not what vanilla's loop expects (a field that
    // is not an instance float, mismatched arrays, an equipped item without shared data, where
    // vanilla throws as before), and for the session when another mod rewrites the method. It runs
    // after other mods' prefixes and respects one that already replaced the call; the mods that add
    // their own slots' modifiers do it in postfixes, which still run.
    //
    // Client: only the local player's modifiers are totalled.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Player), "UpdateModifiers")]
    internal static class EquipmentModifierAllocPatch {
        private const string FixName = "Fix Equipment Modifier Allocation";

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(EquipmentModifierAllocPatch),
            AccessTools.DeclaredMethod(typeof(Player), "UpdateModifiers"),
            HookKinds.Transpilers,
            owners => $"Equipment modifier totals are changed by {owners}, so '{FixName}' stands down and " +
                      "that mod's version applies.");

        // Built for one FieldInfo array; vanilla fills that array once per process.
        private static FieldInfo[] _readersFor;
        private static AccessTools.FieldRef<ItemDrop.ItemData.SharedData, float>[] _readers;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(Player __instance, bool __runOriginal) {
            if (!__runOriginal) { return false; }

            FieldInfo[] fields = Player.s_equipmentModifierSourceFields;
            float[] values = __instance.m_equipmentModifierValues;
            if (fields == null || values == null || values.Length > fields.Length) { return true; }
            if (Takeover.TakenOver) { return true; }

            AccessTools.FieldRef<ItemDrop.ItemData.SharedData, float>[] readers = ReadersFor(fields);
            if (readers == null) { return true; }

            bool broken = false;
            ItemDrop.ItemData.SharedData right = SharedOf(__instance.m_rightItem, ref broken);
            ItemDrop.ItemData.SharedData left = SharedOf(__instance.m_leftItem, ref broken);
            ItemDrop.ItemData.SharedData chest = SharedOf(__instance.m_chestItem, ref broken);
            ItemDrop.ItemData.SharedData legs = SharedOf(__instance.m_legItem, ref broken);
            ItemDrop.ItemData.SharedData helmet = SharedOf(__instance.m_helmetItem, ref broken);
            ItemDrop.ItemData.SharedData shoulder = SharedOf(__instance.m_shoulderItem, ref broken);
            ItemDrop.ItemData.SharedData utility = SharedOf(__instance.m_utilityItem, ref broken);
            ItemDrop.ItemData.SharedData trinket = SharedOf(__instance.m_trinketItem, ref broken);
            if (broken) { return true; }

            for (int i = 0; i < values.Length; i++) {
                AccessTools.FieldRef<ItemDrop.ItemData.SharedData, float> read = readers[i];

                float total = 0f;
                if (right != null) { total += read(right); }
                if (left != null) { total += read(left); }
                if (chest != null) { total += read(chest); }
                if (legs != null) { total += read(legs); }
                if (helmet != null) { total += read(helmet); }
                if (shoulder != null) { total += read(shoulder); }
                if (utility != null) { total += read(utility); }
                if (trinket != null) { total += read(trinket); }
                values[i] = total;
            }

            return false;
        }

        // Null for an empty slot. An equipped item without shared data marks the call broken, so
        // vanilla runs and fails exactly as it always did.
        private static ItemDrop.ItemData.SharedData SharedOf(ItemDrop.ItemData item, ref bool broken) {
            if (item == null) { return null; }
            if (item.m_shared == null) { broken = true; }

            return item.m_shared;
        }

        // Null when any field is not one this fix can read the way vanilla's cast does.
        private static AccessTools.FieldRef<ItemDrop.ItemData.SharedData, float>[] ReadersFor(FieldInfo[] fields) {
            if (ReferenceEquals(fields, _readersFor)) { return _readers; }

            _readersFor = fields;
            _readers = null;

            AccessTools.FieldRef<ItemDrop.ItemData.SharedData, float>[] readers =
                new AccessTools.FieldRef<ItemDrop.ItemData.SharedData, float>[fields.Length];
            for (int i = 0; i < fields.Length; i++) {
                FieldInfo field = fields[i];
                if (field == null || field.IsStatic || field.FieldType != typeof(float)
                    || !field.DeclaringType.IsAssignableFrom(typeof(ItemDrop.ItemData.SharedData))) {
                    Logger.LogWarning(
                        $"{FixName}: an equipment modifier field is not an instance float of the item's " +
                        "shared data, so this fix is inactive and the game's own totals are used.");
                    return null;
                }

                readers[i] = AccessTools.FieldRefAccess<ItemDrop.ItemData.SharedData, float>(field);
            }

            _readers = readers;
            return readers;
        }
    }
}
