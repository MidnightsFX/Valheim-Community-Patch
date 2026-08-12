using BepInEx.Configuration;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Piece;

namespace JotunnModStub.Common {
    public static class JotunnPiece {

        public class LoadedGameObjects {
            public GameObject Prefab {
                get; set;
            }
            public Sprite Sprite {
                get; set;
            }
            public GameObject ScenePrefab {
                get; set;
            }
            // The resolved prefab's Piece component. Everything applied at runtime goes through this.
            public Piece ScenePiece {
                get; set;
            }
        }

        public class PieceCost {
            public string Prefab {
                get; set;
            }
            public int Amount {
                get; set;
            }
            public bool Refundable { get; set; } = true;
        }

        public class PieceConfigs {
            public ConfigEntry<bool> Enabled {
                get; set;
            }
            public ConfigEntry<bool> RequiresWorkbench {
                get; set;
            }
            public ConfigEntry<string> Workbench {
                get; set;
            }
            public ConfigEntry<string> PieceCategory {
                get; set;
            }
            public ConfigEntry<string> PieceCost {
                get; set;
            }
            public List<PieceCost> UpdatedCost { get; set; } = new List<PieceCost>();
        }

        public class JotunnBuildPiece {
            public string Name {
                get; set;
            }
            public bool Enabled { get; set; } = true;
            public string Prefab {
                get; set;
            }
            public string Sprite {
                get; set;
            }
            public string Category { get; set; } = "Misc";
            public string Workbench { get; set; } = "piece_workbench";
            public List<PieceCost> PieceCost { get; set; } = new List<PieceCost>();

            // Populated by in-game related runtime objects
            public LoadedGameObjects Objs {
                get; set;
            }
            public PieceConfigs Cfgs {
                get; set;
            }
        }

        static List<JotunnBuildPiece> BuildPieces = new List<JotunnBuildPiece>();
        static bool PiecesReady = false;

        public static void RegisterJotunnPiece(JotunnBuildPiece jbuildpiece) {

            LoadedGameObjects LGos = new LoadedGameObjects();

            // Set asset references
            LGos.Prefab = JotunnModStub.EmbeddedResourceBundle.LoadAsset<GameObject>($"{jbuildpiece.Prefab}.prefab");
            LGos.Sprite = JotunnModStub.EmbeddedResourceBundle.LoadAsset<Sprite>($"{jbuildpiece.Sprite}.png");
            jbuildpiece.Objs = LGos;
            jbuildpiece.Cfgs = new PieceConfigs();

            InitialPieceSetup(jbuildpiece);

            BuildPieces.Add(jbuildpiece);


            void ResolveAndApplyScenePrefab() {
                GameObject scenePrefab = ResolveScenePrefab(jbuildpiece.Prefab);
                if (scenePrefab == null) {
                    Logger.LogWarning($"Could not find a scene prefab named '{jbuildpiece.Prefab}' with a Piece component after prefab registration; skipping in-place setup for {jbuildpiece.Name}.");
                    return;
                }
                jbuildpiece.Objs.ScenePrefab = scenePrefab;
                jbuildpiece.Objs.ScenePiece = scenePrefab.GetComponent<Piece>();
                PiecesReady = true;
                // Bring the current config (default or server-synced) into effect a single time now that
                // every mod prefab is resolvable. This also covers values that arrived early via config sync.
                ApplyWorkbench(jbuildpiece);
                ApplyCategory(jbuildpiece);
                ApplyRecipe(jbuildpiece);
            }
            PrefabManager.OnPrefabsRegistered += ResolveAndApplyScenePrefab;
        }

        // Finds the registered prefab to apply config to. Jotunn's PrefabManager is authoritative, so ask it
        // first; the Resources scan is only a fallback for prefabs injected into ZNetScene by other means.
        //
        // The scan must filter on the Piece component: several loaded GameObjects can share a prefab's name
        // (the bundle asset, Jotunn's registered clone, child objects), Resources.FindObjectsOfTypeAll returns
        // them in no defined order, and picking one without a Piece made every Apply* below throw an NRE.
        private static GameObject ResolveScenePrefab(string prefabName) {
            GameObject registered = PrefabManager.Instance.GetPrefab(prefabName);
            if (registered != null && registered.GetComponent<Piece>() != null) {
                return registered;
            }

            List<GameObject> candidates = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(obj => obj.name == prefabName && obj.GetComponent<Piece>() != null)
                .ToList();
            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"Found {candidates.Count} scene object(s) named '{prefabName}' with a Piece component.");
            }
            return candidates.FirstOrDefault();
        }

        private static void InitialPieceSetup(JotunnBuildPiece jbuildpiece) {
            // Set where the recipe can be crafted. Gated on PiecesReady so an early config sync / file
            // reload (both fire before ZNetScene.Awake) can't run before the scene prefab is resolved.
            void RequiredBench_SettingChanged(object sender, EventArgs e) {
                if (!PiecesReady || jbuildpiece.Objs.ScenePiece == null) { return; }
                ConfigChangeDebouncer.Schedule(jbuildpiece.Cfgs.Workbench, () => ApplyWorkbench(jbuildpiece));
            }
            jbuildpiece.Cfgs.Workbench = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Workbench", jbuildpiece.Workbench, $"The table required to allow building this piece, eg: 'forge', 'piece_workbench', 'blackforge', 'piece_artisanstation'.");
            jbuildpiece.Cfgs.Workbench.SettingChanged += RequiredBench_SettingChanged;

            // Crafting cost change
            void BuildRecipeChanged_SettingChanged(object sender, EventArgs e) {
                if (!PiecesReady || jbuildpiece.Objs.ScenePiece == null) { return; }
                ConfigChangeDebouncer.Schedule(sender, () => {
                    if (sender.GetType() == typeof(ConfigEntry<string>)) {
                        ConfigEntry<string> sendEntry = (ConfigEntry<string>)sender;
                        if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Recieved new piece config {sendEntry.Value}"); }
                        // return if its an invalid change
                        if (PieceRecipeConfigUpdater(jbuildpiece, sendEntry.Value) == false) { return; }
                    }
                    ApplyRecipe(jbuildpiece);
                });
            }

            // Setup enable/disable
            jbuildpiece.Cfgs.Enabled = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Enabled", jbuildpiece.Enabled, $"Enable/Disable the {jbuildpiece.Name}.");
            jbuildpiece.Cfgs.Enabled.SettingChanged += BuildRecipeChanged_SettingChanged;
            // Setup piece category
            jbuildpiece.Cfgs.PieceCategory = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Piece Category", jbuildpiece.Category, "Piece category for building.", PieceCategories.GetAcceptableValueList());
            void CraftingCategory_SettingChanged(object sender, EventArgs e) {
                if (!PiecesReady || jbuildpiece.Objs.ScenePiece == null) { return; }
                ConfigChangeDebouncer.Schedule(jbuildpiece.Cfgs.PieceCategory, () => ApplyCategory(jbuildpiece));
            }
            jbuildpiece.Cfgs.PieceCategory.SettingChanged += CraftingCategory_SettingChanged;

            // Build out the internal default recipe
            List<string> raw_recipe_default = new List<string>();
            foreach (var entry in jbuildpiece.PieceCost) { raw_recipe_default.Add($"{entry.Prefab},{entry.Amount},{entry.Refundable}"); }
            string recipe_cfg_default = string.Join("|", raw_recipe_default);
            // Wire up the config and on-change for piece costs
            jbuildpiece.Cfgs.PieceCost = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Building Cost", recipe_cfg_default, $"Cost to build. Find item ids: https://valheim.fandom.com/wiki/Item_IDs Format: resouce_id,amount,refund eg: Wood,8,true|LeatherScraps,4,false", advanced: true);
            if (PieceRecipeConfigUpdater(jbuildpiece, jbuildpiece.Cfgs.PieceCost.Value, false) == false) {
                Logger.LogWarning($"{jbuildpiece.Name} has an invalid piece cost. The default will be used instead.");
                PieceRecipeConfigUpdater(jbuildpiece, recipe_cfg_default, false);
            }


            jbuildpiece.Cfgs.PieceCost.SettingChanged += BuildRecipeChanged_SettingChanged;

            // Collapse this piece's entries into a single grouped custom drawer to keep the in-game
            // Configuration Manager responsive (one visible row per piece under "Building Pieces").
            PieceConfigDrawer.Attach(jbuildpiece);

            List<RequirementConfig> recipe = new List<RequirementConfig>();
            foreach (var entry in jbuildpiece.Cfgs.UpdatedCost) {
                recipe.Add(new RequirementConfig { Item = entry.Prefab, Amount = entry.Amount, Recover = entry.Refundable });
            }

            // Build the jotunn piece definition
            PieceConfig piececfg = new PieceConfig() {
                CraftingStation = jbuildpiece.Cfgs.Workbench.Value,
                PieceTable = PieceTables.Hammer,
                Category = jbuildpiece.Cfgs.PieceCategory.Value,
                Icon = jbuildpiece.Objs.Sprite,
                Requirements = recipe.ToArray()
            };
            // Add the updated piece to the piece manager
            PieceManager.Instance.AddPiece(new CustomPiece(jbuildpiece.Objs.Prefab, fixReference: true, piececfg));
        }

        // Applies the configured crafting station to the in-scene piece. Callers must ensure the scene
        // prefab is resolved (PiecesReady) before invoking this.
        private static void ApplyWorkbench(JotunnBuildPiece jbuildpiece) {
            Piece piece = jbuildpiece.Objs.ScenePiece;
            if (piece == null) { return; }

            // RequiresWorkbench is currently never bound; treat a missing entry as "workbench required".
            bool requiresWorkbench = jbuildpiece.Cfgs.RequiresWorkbench?.Value ?? true;
            if (requiresWorkbench == false || string.IsNullOrEmpty(jbuildpiece.Cfgs.Workbench.Value) || jbuildpiece.Cfgs.Workbench.Value.ToLower() == "none") {
                Logger.LogInfo("Setting required crafting station to none.");
                piece.m_craftingStation = null;
                return;
            }

            CraftingStation craftable_at = PrefabManager.Instance.GetPrefab(jbuildpiece.Cfgs.Workbench.Value)?.GetComponent<CraftingStation>();
            if (craftable_at == null) {
                Logger.LogWarning($"Required crafting station does not exist or does not have a crafting station component, check your prefab name ({jbuildpiece.Cfgs.Workbench.Value}).");
                return;
            }

            if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Setting crafting station to {jbuildpiece.Cfgs.Workbench.Value}."); }
            piece.m_craftingStation = craftable_at;
        }

        // Applies the configured build category to the in-scene piece.
        private static void ApplyCategory(JotunnBuildPiece jbuildpiece) {
            Piece piece = jbuildpiece.Objs.ScenePiece;
            if (piece == null) { return; }

            Piece.PieceCategory? category = PieceManager.Instance.GetPieceCategory(jbuildpiece.Cfgs.PieceCategory.Value);
            if (category == null) {
                category = PieceManager.Instance.AddPieceCategory(jbuildpiece.Cfgs.PieceCategory.Value);
            }
            piece.m_category = (PieceCategory)category;
        }

        // Resolves the recipe in UpdatedCost against the live prefab database and applies it to the
        // in-scene piece. Bails out (leaving the existing recipe intact) if any requirement prefab is not
        // yet resolvable, so it is safe even if a dependency mod registered its items late.
        private static void ApplyRecipe(JotunnBuildPiece jbuildpiece) {
            Piece piece = jbuildpiece.Objs.ScenePiece;
            if (piece == null) { return; }

            List<RequirementConfig> recipe = new List<RequirementConfig>();
            if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo("Validating and building requirementsConfig"); }
            foreach (var entry in jbuildpiece.Cfgs.UpdatedCost) {
                if (PrefabManager.Instance.GetPrefab(entry.Prefab) == null) {
                    if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"{entry.Prefab} is not a valid prefab, skipping recipe update."); }
                    return;
                }
                if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Checking entry {entry.Prefab} amount:{entry.Amount} refund?:{entry.Refundable}"); }
                recipe.Add(new RequirementConfig { Item = entry.Prefab, Amount = entry.Amount, Recover = entry.Refundable });
            }
            if (jbuildpiece.Cfgs.Enabled.Value) {
                if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo("Updating Piece."); }
                List<Piece.Requirement> newRequirements = new List<Piece.Requirement>();
                foreach (var recipe_entry in recipe) {
                    Piece.Requirement piece_req = new Piece.Requirement();
                    piece_req.m_resItem = PrefabManager.Instance.GetPrefab(recipe_entry.Item.Replace("JVLmock_", ""))?.GetComponent<ItemDrop>();
                    piece_req.m_amount = recipe_entry.Amount;
                    piece_req.m_recover = recipe_entry.Recover;
                    newRequirements.Add(piece_req);
                }
                if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Fixed mock requirements {newRequirements.Count}."); }
                piece.m_resources = newRequirements.ToArray();
                if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"New requirements set {piece.m_resources}."); }
            } else {
                // Set this piece not craftable
                piece.m_enabled = false;
            }
        }

        private static bool PieceRecipeConfigUpdater(JotunnBuildPiece jBuildPiece, string rawRecipe, bool during_runtime = true) {
            String[] RawRecipeEntries = rawRecipe.Split('|');
            // Logger.LogInfo($"{RawRecipeEntries.Length} {string.Join(", ", RawRecipeEntries)}");
            List<PieceCost> updated_pieceRecipe = new List<PieceCost>();
            // we only clear out the default recipe if there is recipe data provided, otherwise we will continue to use the default recipe
            // TODO: Add a sanity check to ensure that recipe formatting is correct
            if (RawRecipeEntries.Length >= 1) {
                foreach (String recipe_entry in RawRecipeEntries) {
                    //Logger.LogInfo($"{recipe_entry}");
                    String[] recipe_segments = recipe_entry.Split(',');
                    if (recipe_segments.Length != 3) {
                        Logger.LogWarning($"{recipe_entry} is invalid, it does not have enough segments. Proper format is: PREFABNAME,COST,REFUND_BOOL eg: Wood,8,false");
                        return false;
                    }
                    if (ValConfig.EnableDebugMode.Value == true) {
                        String split_segments = "";
                        foreach (String segment in recipe_segments) {
                            split_segments += $" {segment}";
                        }
                        //Logger.LogInfo($"recipe segments: {split_segments} from {recipe_entry}");
                    }
                    // Add a sanity check to ensure the prefab we are trying to use exists
                    // This can only happen during runtime after pieces are available otherwise it will cause errors
                    if (during_runtime) {
                        if (PrefabManager.Instance.GetPrefab(recipe_segments[0]) == null) {
                            Logger.LogWarning($"{recipe_segments[0]} is an invalid prefab and does not exist.");
                            return false;
                        }
                    }
                    if (recipe_segments[0].Length == 0 || recipe_segments[1].Length == 0 || recipe_segments[2].Length == 0) {
                        Logger.LogWarning($"{recipe_entry} is invalid, one segment does not have enough data. Proper format is: PREFABNAME,CRAFT_COST,REFUND_BOOL eg: Wood,8,false");
                        return false;
                    }
                    bool refund_flag_parse;
                    if (bool.TryParse(recipe_segments[2], out refund_flag_parse) == false) {
                        Logger.LogWarning($"{recipe_entry} is invalid, the REFUND_BOOL could not be parsed to (true/false). Proper format is: PREFABNAME,CRAFT_COST,REFUND_BOOL eg: Wood,8,false");
                        return false;
                    }

                    if (ValConfig.EnableDebugMode.Value == true) {
                        Logger.LogInfo($"prefab: {recipe_segments[0]} c:{recipe_segments[1]} u:{recipe_segments[2]}");
                    }
                    updated_pieceRecipe.Add(new PieceCost() { Prefab = recipe_segments[0], Amount = Int32.Parse(recipe_segments[1]), Refundable = refund_flag_parse });
                }
                //Logger.LogInfo("Done parsing recipe");
                jBuildPiece.Cfgs.UpdatedCost.Clear();
                foreach (var entry in updated_pieceRecipe) { jBuildPiece.Cfgs.UpdatedCost.Add(entry); }
                //Logger.LogInfo("Set UpdatedRecipe");
                if (ValConfig.EnableDebugMode.Value == true) {
                    String recipe_string = "";
                    foreach (var entry in updated_pieceRecipe) {
                        recipe_string += $" {entry.Prefab} c:{entry.Amount} r:{entry.Refundable}";
                    }
                    Logger.LogInfo($"Updated recipe:{recipe_string}");
                }
                return true;
            } else {
                Logger.LogWarning($"Invalid recipe: {rawRecipe}. defaults will be used. Check your prefab names.");
                jBuildPiece.Cfgs.UpdatedCost = jBuildPiece.PieceCost;

            }
            return false;
        }
    }
}
