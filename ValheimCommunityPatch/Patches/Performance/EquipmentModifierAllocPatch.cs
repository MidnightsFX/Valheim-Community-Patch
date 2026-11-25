using System.Reflection;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Equipment Modifier Allocation: Player.UpdateModifiers boxes every modifier read, up to 88 a physics step.
    // A prefix totals them with FieldRefAccess readers in vanilla's slot order, bit for bit vanilla's sums.
    // The local player's equipment modifiers stop allocating fifty times a second, with identical results.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Player), "UpdateModifiers")]
    internal static class EquipmentModifierAllocPatch {
        private const string FixName = "Fix Equipment Modifier Allocation";

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(EquipmentModifierAllocPatch));

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(EquipmentModifierAllocPatch),
            AccessTools.DeclaredMethod(typeof(Player), "UpdateModifiers"),
            HookKinds.Transpilers,
            owners => $"Equipment modifier totals are changed by {owners}, so '{FixName}' stands down and " +
                      "that mod's version applies.");

        // Built for one FieldInfo array; vanilla fills that array once per process.
        private static FieldInfo[] _readersFor;
        private static AccessTools.FieldRef<ItemDrop.ItemData.SharedData, float>[] _readers;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. Mods that add their own slots' modifiers
        // do it in postfixes, which still run. Nothing is cached between steps, so mods that change item
        // stats while playing stay correct.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(Player __instance, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (ApiSwitch.Off) { return true; }

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
