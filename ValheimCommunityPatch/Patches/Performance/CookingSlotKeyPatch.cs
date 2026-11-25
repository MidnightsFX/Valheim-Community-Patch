using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Cooking Slot Keys: GetSlot and SetSlot build key strings on every call, dozens a second per lit station.
    // Prefixes replace both methods with vanilla's body over tables of precomputed key hashes.
    // Cooking stations stop producing string garbage every second they cook.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(CookingStation))]
    internal static class CookingSlotKeyPatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(CookingSlotKeyPatch));

        // ("slot" + i) and ("slotstatus" + i) stable hashes. Grown by publishing a whole new
        // array, never resized in place, so a reader mid-call keeps a consistent table. Equivalent
        // by construction: every ZDO string-key overload calls its int overload with
        // GetStableHashCode() and the same defaults, as ArmorStand.InitKeys already relies on.
        private static int[] SlotKeys = new int[0];
        private static int[] StatusKeys = new int[0];

        private static void EnsureKeys(int count) {
            if (SlotKeys.Length >= count) { return; }

            int[] slots = new int[count];
            int[] statuses = new int[count];
            for (int i = 0; i < count; i++) {
                slots[i] = ("slot" + i).GetStableHashCode();
                statuses[i] = ("slotstatus" + i).GetStableHashCode();
            }

            SlotKeys = slots;
            StatusKeys = statuses;
        }

        // Both prefixes need a table entry for this slot; a negative index has no representable
        // key, so vanilla handles it, as it does everything once the fix is turned off.
        private static bool TryPrepare(CookingStation station, int slot) {
            if (slot < 0 || ApiSwitch.Off) { return false; }

            Transform[] slots = station.m_slots;
            EnsureKeys(Mathf.Max(slot + 1, slots == null ? 0 : slots.Length));
            return true;
        }

        // A replacement, not a transpiler: the string is built as the argument, before the call, and
        // the string and int overloads do not share a stack shape. IsEmpty and GetFreeSlot build the
        // same keys but are left alone: they run on interaction, not every second.
        [HarmonyPrefix]
        [HarmonyPatch("GetSlot")]
        private static bool GetSlotPrefix(
            CookingStation __instance,
            int slot,
            ref string itemName,
            ref float cookedTime,
            ref CookingStation.Status status,
            ref bool cheated) {
            if (!TryPrepare(__instance, slot)) { return true; }

            if (!__instance.m_nview.IsValid()) {
                itemName = "";
                status = CookingStation.Status.NotDone;
                cookedTime = 0f;
                cheated = false;
                return false;
            }

            ZDO zdo = __instance.m_nview.GetZDO();
            itemName = zdo.GetString(SlotKeys[slot]);
            cookedTime = zdo.GetFloat(SlotKeys[slot]);
            status = (CookingStation.Status)zdo.GetInt(StatusKeys[slot]);
            cheated = zdo.GetBool(ZDOVars.s_cheatedQueued + slot);
            return false;
        }

        // SetSlot's parameters as of 1.0.21; builds before it stop after cheated. One hook class per
        // signature, applied only to the one the game has: Harmony cannot build a prefix naming a
        // missing parameter, and a failed build breaks every later patch on the method, by any mod.
        private static readonly string[] SetSlotNames = { "slot", "itemName", "cookedTime", "status", "cheated", "sender" };
        private static readonly Type[] SetSlotTypes = {
            typeof(int), typeof(string), typeof(float), typeof(CookingStation.Status), typeof(bool), typeof(long)
        };

        private static int _setSlotArity = -1;

        // 6 from 1.0.21, 5 before it, 0 for any other parameter list. Prepare can run more than once
        // per class, so the answer, and the warning, come once.
        private static int SetSlotArity() {
            if (_setSlotArity >= 0) { return _setSlotArity; }

            MethodInfo[] candidates = AccessTools.GetDeclaredMethods(typeof(CookingStation))
                .Where(m => m.Name == "SetSlot")
                .ToArray();
            ParameterInfo[] parameters = candidates.Length == 1 ? candidates[0].GetParameters() : null;

            _setSlotArity = 0;
            if (parameters != null && parameters.Length >= 5 && parameters.Length <= SetSlotNames.Length) {
                _setSlotArity = parameters.Length;
                for (int i = 0; i < parameters.Length; i++) {
                    if (parameters[i].Name != SetSlotNames[i] || parameters[i].ParameterType != SetSlotTypes[i]) {
                        _setSlotArity = 0;
                        break;
                    }
                }
            }

            if (_setSlotArity == 0) {
                Logger.LogWarning(
                    "CookingStation.SetSlot no longer has the parameters 'Fix Cooking Slot Keys' expects, so " +
                    "cooking stations write their slots the vanilla way. A Valheim update has most likely " +
                    "changed the method.");
            }

            return _setSlotArity;
        }

        private static bool WriteSlot(
            CookingStation station, int slot, string itemName, float cookedTime,
            CookingStation.Status status, bool cheated, long sender) {
            if (!TryPrepare(station, slot)) { return true; }
            if (!station.m_nview.IsValid()) { return false; }

            ZDO zdo = station.m_nview.GetZDO();
            zdo.Set(SlotKeys[slot], itemName);
            zdo.Set(SlotKeys[slot], cookedTime);
            zdo.Set(StatusKeys[slot], (int)status);
            zdo.Set(ZDOVars.s_cheatedQueued + slot, cheated);
            // SpawnItem reads this to credit the cooked item to whoever added it (1.0.21).
            if (sender != 0L) { zdo.Set(ZDOVars.s_playerID + slot, sender); }
            return false;
        }

        [HarmonyPatch(typeof(CookingStation), "SetSlot")]
        internal static class SetSlotHook {
            [HarmonyPrepare]
            private static bool Prepare() => SetSlotArity() == 6;

            [HarmonyPrefix]
            private static bool Prefix(
                CookingStation __instance,
                int slot,
                string itemName,
                float cookedTime,
                CookingStation.Status status,
                bool cheated,
                long sender) =>
                WriteSlot(__instance, slot, itemName, cookedTime, status, cheated, sender);
        }

        // Up to 1.0.20, when nothing recorded who added an item.
        [HarmonyPatch(typeof(CookingStation), "SetSlot")]
        internal static class SetSlotBefore1021Hook {
            [HarmonyPrepare]
            private static bool Prepare() => SetSlotArity() == 5;

            [HarmonyPrefix]
            private static bool Prefix(
                CookingStation __instance,
                int slot,
                string itemName,
                float cookedTime,
                CookingStation.Status status,
                bool cheated) =>
                WriteSlot(__instance, slot, itemName, cookedTime, status, cheated, 0L);
        }
    }
}
