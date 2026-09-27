using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Fix Terrain Paint Doubling: an edit that reaches two zones paints the ground they share
    // once, so Deep North snow no longer piles up or digs out twice as fast along a zone border.
    //
    // A zone's border texels also exist in the neighbouring zone's paint data. When
    // TerrainComp.PaintCleared paints one, its local function spread copies the result into the
    // neighbour's compiler and pokes that heightmap. If the same edit also reaches the neighbour,
    // the neighbour's own PaintCleared then starts from the copied value, because a poked
    // heightmap is read from the compiler's array, and paints it again. Lerped paint only
    // saturates faster, but snow depth is added, so the border gets double the change and a corner
    // shared by four zones up to four times.
    //
    // A transpiler on spread records each texel it is about to overwrite, with the value
    // PaintCleared would have read there before the write. A transpiler on PaintCleared sends its
    // reads (the getMask local function) through ReadPaint, which returns the recorded value for
    // those texels and is getMask verbatim otherwise. Each zone then paints from the value before
    // the edit, and its own spread still syncs the neighbours, so every copy ends one application
    // deep. Records belong to one edit (same position, settings object and frame, each zone
    // visited once) and are dropped when the next begins. With either hook missing nothing is
    // recorded or read, and PaintCleared behaves as vanilla.
    //
    // Both: PaintCleared runs on whichever peer owns the zone's terrain compiler.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(TerrainComp))]
    internal static class PaintSpreadOncePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(PaintSpreadOncePatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Paint Doubling",
                true,
                "Applies a terrain edit once to the ground two zones share. When an edit reaches both " +
                "sides of a 64m zone border, vanilla applies it twice along the border (up to four " +
                "times at a corner). Normal paint barely shows it, but Deep North snow is added, so " +
                "piling or clearing snow leaves a ridge or trench along the zone line.");
        }

        private struct Record {
            internal TerrainComp Comp;
            internal int Index;
            internal Color Before;
        }

        // Texels spread into during the current edit: only border texels inside one edit's reach.
        private static readonly List<Record> Records = new List<Record>();

        // The zones the current edit has painted.
        private static readonly List<TerrainComp> Painted = new List<TerrainComp>();

        private static int _editFrame = -1;
        private static Vector3 _editPos;
        private static TerrainOp.Settings _editSettings;

        // Every zone an edit reaches is painted with the same position and the same settings
        // object (the prefab's, looked up by hash), back to back when one peer owns them all. A
        // zone painted twice under the same key means a second edit on the same spot.
        [HarmonyPrefix]
        [HarmonyPatch("PaintCleared")]
        private static void PaintClearedPrefix(TerrainComp __instance, Vector3 worldPos, TerrainOp.Settings settings) {
            int frame = Time.frameCount;
            bool sameEdit = frame == _editFrame
                && worldPos.Equals(_editPos)
                && ReferenceEquals(settings, _editSettings)
                && !Painted.Contains(__instance);

            if (!sameEdit) {
                Records.Clear();
                Painted.Clear();
                _editFrame = frame;
                _editPos = worldPos;
                _editSettings = settings;
            } else if (Logger.DebugEnabled) {
                // A loop, not a lambda: capturing __instance would allocate on every call.
                int mine = 0;
                for (int i = 0; i < Records.Count; i++) {
                    if (ReferenceEquals(Records[i].Comp, __instance)) { mine++; }
                }

                if (mine > 0) {
                    Logger.LogDebug(
                        $"Fix Terrain Paint Doubling: the zone at {__instance.transform.position} paints {mine} " +
                        "border texel(s) from their value before this edit instead of painting them twice.");
                }
            }

            Painted.Add(__instance);
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. All three reads are rerouted,
        // since ReadPaint is getMask for any texel without a record.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("PaintCleared")]
        private static IEnumerable<CodeInstruction> PaintClearedTranspiler(IEnumerable<CodeInstruction> instructions) {
            MethodInfo readPaint = AccessTools.Method(typeof(PaintSpreadOncePatch), nameof(ReadPaint));
            return PatchHelper.ReplaceCalls(instructions, FindGetMask(readPaint), readPaint, "TerrainComp.PaintCleared");
        }

        private static Color ReadPaint(Heightmap hmap, TerrainComp tc, int x, int y, int index) {
            for (int i = 0; i < Records.Count; i++) {
                if (Records[i].Index == index && ReferenceEquals(Records[i].Comp, tc)) { return Records[i].Before; }
            }

            return VanillaGetMask(hmap, tc, x, y, index);
        }

        // getMask: the compiler's array while a regeneration is queued, else the drawn texture.
        private static Color VanillaGetMask(Heightmap hmap, TerrainComp tc, int x, int y, int index) {
            if (hmap.m_doLateUpdate != 1 || tc == null) { return hmap.GetPaintMask(x, y); }

            return tc.m_paintMask[index];
        }

        // Called by spread just before it writes the neighbour's texel. Only the first write of an
        // edit is kept: at a corner, later zones spread over values this edit already produced.
        private static void RecordSpread(TerrainComp target, int index) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (target == null || target.m_hmap == null) { return; }

            for (int i = 0; i < Records.Count; i++) {
                if (Records[i].Index == index && ReferenceEquals(Records[i].Comp, target)) { return; }
            }

            int pitch = target.m_width + 1;
            Records.Add(new Record {
                Comp = target,
                Index = index,
                Before = VanillaGetMask(target.m_hmap, target, index % pitch, index / pitch, index),
            });
        }

        // The compiler names local functions "<PaintCleared>g__name|N_M"; the numbers change
        // between builds, the prefix does not.
        private static MethodInfo FindLocalFunction(string name) {
            string prefix = "<PaintCleared>g__" + name + "|";
            MethodInfo[] matches = AccessTools.GetDeclaredMethods(typeof(TerrainComp))
                .Where(m => m.Name.StartsWith(prefix))
                .ToArray();

            return matches.Length == 1 ? matches[0] : null;
        }

        // ReadPaint replaces getMask call for call, so the signatures must match exactly.
        private static MethodInfo FindGetMask(MethodInfo readPaint) {
            MethodInfo getMask = FindLocalFunction("getMask");
            if (getMask == null || !getMask.IsStatic || getMask.ReturnType != readPaint.ReturnType) { return null; }

            bool sameParameters = getMask.GetParameters().Select(p => p.ParameterType)
                .SequenceEqual(readPaint.GetParameters().Select(p => p.ParameterType));

            return sameParameters ? getMask : null;
        }

        [HarmonyPatch]
        internal static class SpreadHook {
            private static readonly FieldInfo ModifiedPaintField =
                AccessTools.Field(typeof(TerrainComp), nameof(TerrainComp.m_modifiedPaint));

            private static readonly MethodInfo RecordSpreadMethod =
                AccessTools.Method(typeof(PaintSpreadOncePatch), nameof(RecordSpread));

            [HarmonyPrepare]
            private static bool Prepare() {
                if (FindLocalFunction("spread") != null) { return true; }

                Logger.LogWarning(
                    "TerrainComp.PaintCleared no longer has exactly one 'spread' local function, so " +
                    "'Fix Terrain Paint Doubling' is inactive. A Valheim update has most likely changed " +
                    "how terrain paint reaches neighbouring zones.");
                return false;
            }

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod() => FindLocalFunction("spread");

            // Inserts RecordSpread(neighbour, index) ahead of `neighbour.m_modifiedPaint[index] = true`,
            // the first of spread's two writes, reusing the two locals that statement loads.
            [HarmonyTranspiler]
            [HarmonyPriority(Priority.Last)]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                List<CodeInstruction> codes = PatchHelper.Copy(instructions);

                int site = -1;
                int found = 0;
                for (int i = 1; i < codes.Count - 1; i++) {
                    if (!codes[i].LoadsField(ModifiedPaintField)) { continue; }

                    site = i;
                    found++;
                }

                if (found != 1 || !codes[site - 1].IsLdloc() || !codes[site + 1].IsLdloc()) {
                    Logger.LogWarning(
                        $"TerrainComp.PaintCleared spread: expected one neighbour paint write, found {found}, " +
                        "so 'Fix Terrain Paint Doubling' is inactive. Another mod has most likely already " +
                        "rewritten the method - if so, nothing is wrong.");
                    return instructions;
                }

                CodeInstruction loadNeighbour = codes[site - 1];
                CodeInstruction loadIndex = codes[site + 1];

                CodeInstruction first = new CodeInstruction(loadNeighbour.opcode, loadNeighbour.operand);
                loadNeighbour.MoveLabelsTo(first);

                codes.InsertRange(site - 1, new[] {
                    first,
                    new CodeInstruction(loadIndex.opcode, loadIndex.operand),
                    new CodeInstruction(OpCodes.Call, RecordSpreadMethod),
                });

                return codes;
            }
        }
    }
}
