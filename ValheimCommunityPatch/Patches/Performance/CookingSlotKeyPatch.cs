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
    // nothing; it is reproduced here unchanged.
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
    // rather than from a repeater.
    //
    // Both: UpdateVisual runs wherever the station is loaded, UpdateCooking's owner branch runs
    // on whichever peer owns the ZDO, and a listen host runs both.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(CookingStation))]
    internal static class CookingSlotKeyPatch {
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
        // key, so vanilla handles it.
        private static bool TryPrepare(CookingStation station, int slot) {
            if (slot < 0) { return false; }

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

        [HarmonyPrefix]
        [HarmonyPatch("SetSlot")]
        private static bool SetSlotPrefix(
            CookingStation __instance,
            int slot,
            string itemName,
            float cookedTime,
            CookingStation.Status status,
            bool cheated) {
            if (!TryPrepare(__instance, slot)) { return true; }
            if (!__instance.m_nview.IsValid()) { return false; }

            ZDO zdo = __instance.m_nview.GetZDO();
            zdo.Set(SlotKeys[slot], itemName);
            zdo.Set(SlotKeys[slot], cookedTime);
            zdo.Set(StatusKeys[slot], (int)status);
            zdo.Set(ZDOVars.s_cheatedQueued + slot, cheated);
            return false;
        }
    }
}
