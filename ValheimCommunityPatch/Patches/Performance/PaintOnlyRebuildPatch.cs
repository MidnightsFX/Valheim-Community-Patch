using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Paint-Only Terrain Rebuilds: PokeHeightmaps asks for a full tile rebuild even for paint-only modifiers.
    // Transpiles that Poke to a paint-only rebuild; a Regenerate prefix/postfix runs the full one if the heights moved.
    // Rocks and ore deposits loading and unloading around the player stop rebuilding terrain collision and meshes.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(TerrainModifier), "PokeHeightmaps")]
    internal static class PaintOnlyRebuildPatch {
        private const string FixName = "Fix Paint-Only Terrain Rebuilds";

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(PaintOnlyRebuildPatch));

        private static readonly MethodInfo PokeMethod =
            AccessTools.Method(typeof(Heightmap), nameof(Heightmap.Poke), new[] { typeof(int), typeof(bool) });
        private static readonly MethodInfo PokeForMethod =
            AccessTools.Method(typeof(PaintOnlyRebuildPatch), nameof(PokeFor));

        // The height guard is what keeps a paint-only rebuild honest when a tile's terrain edits
        // unload first, so without it every modifier keeps vanilla's full rebuild.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(PaintOnlyRebuildPatch),
            FixName,
            () => PatchHelper.HasHook(
                AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.Regenerate)), typeof(HeightGuardHook)));

        // Replaces `hmap.Poke(delayed, false)` with `PokeFor(hmap, delayed, false, this)`.
        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            if (PokeMethod == null || PokeForMethod == null) {
                PatchHelper.WarnOnce("TerrainModifier.PokeHeightmaps: a method this fix needs could not be resolved, so it is inactive here.");
                return instructions;
            }

            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replaced = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(PokeMethod)) { continue; }

                // The callvirt becomes ldarg.0 (keeping any labels on it) and the static call
                // follows: stack [hmap, delayed, paintOnly] -> [.., this] -> PokeFor(...).
                codes[i].opcode = OpCodes.Ldarg_0;
                codes[i].operand = null;
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, PokeForMethod));
                replaced++;
                i++;
            }

            if (replaced != 1) {
                PatchHelper.WarnOnce(
                    $"TerrainModifier.PokeHeightmaps: expected 1 Heightmap.Poke call, found {replaced}, so this " +
                    "fix is inactive. Another mod has most likely already rewritten the method - if so, nothing " +
                    "is wrong.");
                return instructions;
            }

            return codes;
        }

        // Vanilla's Poke, asking for the paint-only rebuild when the modifier cannot change heights.
        // Vanilla's request merge keeps a rebuild full when anything else asked for a full one, and a
        // tile's first build is always full. Turned off, the height guard stays on for tiles already
        // asked for one.
        private static void PokeFor(Heightmap hmap, int delayed, bool paintOnly, TerrainModifier modifier) {
            hmap.Poke(delayed, paintOnly || (!modifier.m_level && !modifier.m_smooth && !ApiSwitch.Off && Hooks.Healthy));
        }

        // Every paint-only rebuild, vanilla's paint ops included: the meshes are kept only when the
        // heights they were built from came out the same.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
        internal static class HeightGuardHook {
            private sealed class Before {
                public float[] Heights = new float[0];
                public int Count;
                public HeightmapBuilder.HMBuildData BuildData;
            }

            // Reused between rebuilds; a rebuild nested inside another takes a fresh one.
            private static Before _spare;

            // Priority.Last: the snapshot is the last thing taken before vanilla runs, and a mod that
            // replaced the rebuild is left to it.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Heightmap __instance, bool __runOriginal, out Before __state) {
                __state = null;
                if (!__runOriginal || __instance.m_regenRequest != Heightmap.RegenRequest.PaintOnly) { return; }

                List<float> heights = __instance.m_heights;
                Before before = _spare ?? new Before();
                _spare = null;

                if (before.Heights.Length < heights.Count) { before.Heights = new float[heights.Count]; }

                heights.CopyTo(before.Heights);
                before.Count = heights.Count;
                before.BuildData = __instance.m_buildData;
                __state = before;
            }

            // Priority.Last: every other mod's postfix is done with this rebuild before a full one
            // starts inside it.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Heightmap __instance, Before __state) {
                if (__state == null) { return; }

                int moved = CountMoved(__instance, __state);
                __state.BuildData = null;
                _spare = __state;

                if (moved == 0) { return; }

                Logger.LogDebug(
                    $"Paint-only terrain rebuild at {__instance.transform.position}: " +
                    (moved < 0 ? "the terrain data was replaced" : $"{moved} heights changed") +
                    ", so the full rebuild was run.");

                // Poke without paintOnly turns the request full and rebuilds now, as vanilla would have.
                __instance.Poke();
            }

            // The number of heights that changed, or -1 when the grid itself was replaced.
            private static int CountMoved(Heightmap hmap, Before before) {
                List<float> heights = hmap.m_heights;
                if (heights.Count != before.Count || !ReferenceEquals(hmap.m_buildData, before.BuildData)) { return -1; }

                float[] old = before.Heights;
                int moved = 0;
                for (int i = 0; i < before.Count; i++) {
                    if (heights[i] != old[i]) { moved++; }
                }

                return moved;
            }
        }
    }
}
