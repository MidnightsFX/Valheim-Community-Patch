using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla counts rare drops down per star level and starts at half the listed chance, so dry streaks run long.
    // Transpiles CharacterDrop.GenerateDropList to one countdown per item; a SetupCharacterPreview prefix clears them.
    // Every star level counts toward one countdown per item, paying out at the listed chance from the first kill.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class RareDropPityPatch {
        internal const string SaveYourLuckGuid = "vapok.mods.SaveYourLuckBruh";

        private const string FixName = "Fix Rare Drop Pity";

        // The game's highest chance that counts down rather than rolls.
        private const float CountdownLimit = 0.3f;

        // What Roll answers in place of the random value the game compares with the chance.
        private const float Hit = 0f;
        private const float Miss = float.PositiveInfinity;

        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(RareDropPityPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Makes every star level of a creature count toward one rare drop countdown per item that " +
                "pays out at the listed chance from the first kill, and stops character browsing mixing countdowns.");
        }

        private static readonly FieldInfo ChanceField = AccessTools.Field(typeof(CharacterDrop.Drop), nameof(CharacterDrop.Drop.m_chance));
        private static readonly MethodInfo RandomValueGetter = AccessTools.PropertyGetter(typeof(Random), nameof(Random.value));
        private static readonly MethodInfo LimitForMethod = AccessTools.Method(typeof(RareDropPityPatch), nameof(LimitFor));
        private static readonly MethodInfo ClaimCountdownMethod = AccessTools.Method(typeof(RareDropPityPatch), nameof(ClaimCountdown));
        private static readonly MethodInfo RollMethod = AccessTools.Method(typeof(RareDropPityPatch), nameof(Roll));

        // Whether Roll decides the drop GenerateDropList is on.
        private static bool _rollsThisDrop;

        private static bool _loggedStandDown;

        [HarmonyPrepare]
        private static bool Prepare() {
            if (!Chainloader.PluginInfos.ContainsKey(SaveYourLuckGuid)) { return true; }

            if (!_loggedStandDown) {
                _loggedStandDown = true;
                Logger.LogInfo($"SaveYourLuckBruh is loaded and rewrites the same rare drop countdowns, so '{FixName}' stands down.");
            }

            return false;
        }

        private static bool OnSharedCountdown(CharacterDrop.Drop drop) =>
            Enabled.Value && drop.m_chance > 0f && drop.m_chance <= CountdownLimit;

        // Replaces the limit in the game's "chance <= 0.3", so every star level reaches the checks behind it,
        // such as the No Pseudo Drops world modifier.
        private static float LimitFor(CharacterDrop.Drop drop) =>
            OnSharedCountdown(drop) ? float.PositiveInfinity : CountdownLimit;

        // Filters the game's decision to use its countdown. False skips the countdown and reaches the
        // plain roll, where Roll stands in.
        private static bool ClaimCountdown(bool countsDown, CharacterDrop.Drop drop) {
            _rollsThisDrop = countsDown && OnSharedCountdown(drop);
            return countsDown && !_rollsThisDrop;
        }

        // Stands in for the Random.value of the game's plain roll.
        private static float Roll(CharacterDrop.Drop drop, float chance) {
            if (!_rollsThisDrop) { return Random.value; }

            // The key the game gives the unstarred drop, so saves carry over and an unstarred kill on a
            // running countdown does exactly what vanilla does.
            float ownChance = drop.m_chance;
            int key = drop.m_prefab.name.GetStableHashCode() + (int)(ownChance * 1000f);
            int range = (int)(1f / ownChance * 2f);
            int spent = WholeKills(chance / ownChance);

            // The lower of two rolls, where a running countdown would stand at a random moment. The
            // game's first kill on a new countdown does not spend, hence the added kill.
            if (!CharacterDrop.s_pseudoCounter.TryGetValue(key, out int left)) {
                left = Mathf.Min(Random.Range(0, range), Random.Range(0, range)) + 1;
            }

            left -= spent;

            // Only the game's plain roll drops here, so these star levels stay at vanilla's rate; a miss
            // still spends and a hit restarts the countdown.
            if (chance > CountdownLimit) {
                bool hit = Random.value <= chance;
                CharacterDrop.s_pseudoCounter[key] = hit ? Random.Range(0, range + 2) : Mathf.Max(left, 1);
                return hit ? Hit : Miss;
            }

            if (left > 0) {
                CharacterDrop.s_pseudoCounter[key] = left;
                return Miss;
            }

            // At most one kill's worth carries, so a countdown cannot build up a run of drops.
            CharacterDrop.s_pseudoCounter[key] = Random.Range(0, range + 2) + Mathf.Max(left, 1 - Mathf.Max(spent, 1));
            return Hit;
        }

        // A mod can scale a star level's chance by a fraction; rounding up with the chance of that
        // fraction keeps its average, as the counter holds whole kills.
        private static int WholeKills(float kills) {
            int whole = (int)kills;
            return Random.value < kills - whole ? whole + 1 : whole;
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. Every edit is read at run time, so
        // the toggle takes effect at once.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
        private static IEnumerable<CodeInstruction> GenerateDropListTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int ownChance = codes.FindIndex(code => code.LoadsField(ChanceField));
            int limit = codes.FindIndex(code => code.opcode == OpCodes.Ldc_R4 && code.operand is float value && value == CountdownLimit);
            int store = limit < 0 ? -1 : codes.FindIndex(limit, code => code.IsStloc());
            int roll = codes.FindIndex(code => code.Calls(RandomValueGetter));

            // In the game's order, with the locals loaded where the game loads them.
            if (ownChance < 1 || limit < ownChance || store < 0 || roll < store
                || !codes[ownChance - 1].IsLdloc() || !codes[limit - 1].IsLdloc()) {
                PatchHelper.WarnOnce(
                    "CharacterDrop.GenerateDropList: the rare drop roll is not where this fix expects it, so " +
                    "it is inactive here. Another mod has most likely already rewritten the method - if so, " +
                    "nothing is wrong.");
                return instructions;
            }

            CodeInstruction loadDrop = codes[ownChance - 1];
            CodeInstruction loadChance = codes[limit - 1];

            // Back to front, so the earlier indexes stay valid. An edit in place keeps its labels.
            codes[roll].opcode = loadDrop.opcode;
            codes[roll].operand = loadDrop.operand;
            codes.InsertRange(roll + 1, new[] {
                new CodeInstruction(loadChance.opcode, loadChance.operand),
                new CodeInstruction(OpCodes.Call, RollMethod),
            });

            // The game's two branches join on this store, so what goes ahead of it takes the label.
            codes.InsertRange(store, new[] {
                new CodeInstruction(loadDrop.opcode, loadDrop.operand).MoveLabelsFrom(codes[store]),
                new CodeInstruction(OpCodes.Call, ClaimCountdownMethod),
            });

            codes[limit].opcode = loadDrop.opcode;
            codes[limit].operand = loadDrop.operand;
            codes.Insert(limit + 1, new CodeInstruction(OpCodes.Call, LimitForMethod));

            return codes;
        }

        // Player.Load merges a character's countdowns into this static dictionary without clearing it,
        // so each preview would add to the last and a new character would save them.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.SetupCharacterPreview))]
        private static void SetupCharacterPreviewPrefix() {
            if (Enabled.Value) { CharacterDrop.s_pseudoCounter.Clear(); }
        }
    }
}
