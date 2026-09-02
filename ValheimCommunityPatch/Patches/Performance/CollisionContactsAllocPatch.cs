using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Collision Contact Allocation: reading a collision's contact points fills a reused
    // buffer instead of allocating an array per read.
    //
    // Collision.contacts builds a new ContactPoint[] on every read, and the game reads it inside
    // physics callbacks that fire every fixed step: Character.OnCollisionStay once per contacting
    // collider for every owned character, ImpactEffect.OnCollisionEnter twice for one collision,
    // FloatingTerrain.OnCollisionStay once. Each array is read once and dropped.
    //
    // Transpilers route those reads through a buffer sized to the real contact count and filled
    // by Collision.GetContacts. Sizing to the exact count is what keeps this a single edit: the
    // compiled foreach and ImpactEffect's .Length read see the same length they always did. None
    // of the three call sites holds the array across anything that could ask for contacts again.
    // Above 64 contacts the property is used unchanged.
    //
    // Both.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class CollisionContactsAllocPatch {
        private const int MaxCachedContacts = 64;

        private static readonly ContactPoint[][] Buffers = new ContactPoint[MaxCachedContacts + 1][];
        private static readonly ContactPoint[] Empty = new ContactPoint[0];

        private static readonly MethodInfo ContactsGetter =
            AccessTools.PropertyGetter(typeof(Collision), nameof(Collision.contacts));
        private static readonly MethodInfo ReusedContactsMethod =
            AccessTools.Method(typeof(CollisionContactsAllocPatch), nameof(ReusedContacts));

        // Same values, same order, same Length as Collision.contacts; only the lifetime differs.
        private static ContactPoint[] ReusedContacts(Collision collision) {
            if (collision == null) { return Empty; }

            int count = collision.contactCount;
            if (count <= 0) { return Empty; }
            if (count > MaxCachedContacts) { return collision.contacts; }

            ContactPoint[] buffer = Buffers[count];
            if (buffer == null) {
                buffer = new ContactPoint[count];
                Buffers[count] = buffer;
            }

            collision.GetContacts(buffer);
            return buffer;
        }

        // Priority.Last on all three: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Character), "OnCollisionStay")]
        private static IEnumerable<CodeInstruction> CharacterOnCollisionStayTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(instructions, ContactsGetter, ReusedContactsMethod, "Character.OnCollisionStay", expected: 1);

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ImpactEffect), nameof(ImpactEffect.OnCollisionEnter))]
        private static IEnumerable<CodeInstruction> ImpactEffectOnCollisionEnterTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(instructions, ContactsGetter, ReusedContactsMethod, "ImpactEffect.OnCollisionEnter", expected: 2);

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(FloatingTerrain), "OnCollisionStay")]
        private static IEnumerable<CodeInstruction> FloatingTerrainOnCollisionStayTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(instructions, ContactsGetter, ReusedContactsMethod, "FloatingTerrain.OnCollisionStay", expected: 1);
    }
}
