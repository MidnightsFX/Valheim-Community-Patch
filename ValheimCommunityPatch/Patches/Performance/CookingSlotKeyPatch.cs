using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Cooking Slot Keys: a cooking station reads and writes its slot data through precomputed
    // key hashes instead of rebuilding the key strings on every access.
    //
    // CookingStation.GetSlot and SetSlot build three of their four ZDO keys inline, as
    // "slot" + slot (twice each - once for the item name, once for the cooked time) and
    // "slotstatus" + slot. Each of those is a slot.ToString() plus a concatenation, so six string
    // allocations per call, every one immediately hashed and thrown away. UpdateCooking, on a
    // 1 Hz InvokeRepeating, calls GetSlot once per slot and SetSlot once per slot, then hands off
    // to UpdateVisual which calls GetSlot for the same slots again. A five-slot station with its
    // fire lit therefore produces roughly ninety string allocations a second, forever, purely as
    // garbage. The fourth key is already an int (ZDOVars.s_cheatedQueued + slot) and costs
    // nothing; it is reproduced here unchanged. So is the int key SetSlot gained in 1.0.21,
    // ZDOVars.s_playerID + slot, written only for a non-zero sender: SpawnItem reads it to credit
    // the cooked item to the player who added it, so leaving it out would undo the game's fix.
    //
    // Prefixes replace both methods with vanilla's body over two static tables of precomputed
    // stable hashes. This has to be a replacement rather than a transpiler: the allocation
    // happens building the argument, before the call, and GetString(string) and GetString(int,
    // string) do not share a stack shape for ReplaceCalls to rewrite. Equivalence is by
    // construction - every string overload in ZDO is defined as a call to its hash overload with
    // name.GetStableHashCode(), with the same defaults, so this is the same int reaching the same
    // method. Vanilla does exactly this already in ArmorStand.InitKeys/SetKeyHashes, for the same
    // problem on the same API. A negative slot index would need a key the tables cannot hold and
    // falls through to vanilla. IsEmpty and GetFreeSlot build the same keys inline and are left
    // alone deliberately: both are interaction-rate, reached from OnUseItem and the hover menu
    // rather than from a repeater. SetSlot has one hook class per signature (with sender from
    // 1.0.21, without before), and Prepare applies only the one the running game matches:
    // Harmony cannot build a prefix naming a parameter the method lacks, and a failed build stays
    // on the method and breaks every later patch on it, by any mod.
    //
    // Both: UpdateVisual runs wherever the station is loaded, UpdateCooking's owner branch runs
    // on whichever peer owns the ZDO, and a listen host runs both.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(CookingStation))]
    internal static class CookingSlotKeyPatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(CookingSlotKeyPatch));

        // ("slot" + i) and ("slotstatus" + i) stable hashes. Grown by publishing a whole new
        // array, never resized in place, so a reader mid-call keeps a consistent table.
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

        // SetSlot's parameters as of 1.0.21; builds before it stop after cheated.
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
