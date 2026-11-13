using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// Mods that hide building pieces' renderers or draw pieces their own way, which Fix Building
    /// Renderer Overhead would fight.
    /// </summary>
    /// <remarks>
    /// Each one either merges pieces itself or sets <c>forceRenderingOff</c> on piece renderers
    /// (FarLands' distant buildings hide a zone's pieces while it draws its own copies), so only one
    /// of them may own a piece's renderers.
    /// </remarks>
    internal static class BuildingMergeCompat {
        /// <summary>The first such mod that is installed with its piece drawing on, or null.</summary>
        internal static string Other() {
            Dictionary<string, PluginInfo> plugins = Chainloader.PluginInfos;
            foreach (KeyValuePair<string, PluginInfo> p in plugins) {
                string id = p.Key;
                if (id == "com.chase.valheimenhancededition" || id == "com.chase.perfmod") {
                    if (Setting(p.Value, "Merged buildings", "Enabled", true)) { return "Valheim Enhanced Edition (merged buildings)"; }
                } else if (id == "com.pup.instancecombiner.safe") {
                    return "PUP FPS";
                } else if (id.StartsWith("com.Skarif.ValheimPerformanceOverhaul")) {
                    return "Valheim Performance Overhaul";
                } else if (id == "valheim.buildpieceprofiler") {
                    if (Setting(p.Value, "Renderer Batching", "EnableRendererBatching", false)) { return "Valheim Build Optimization (renderer batching)"; }
                } else if (id == "ru.ivest.farlands") {
                    if (Setting(p.Value, "Buildings", "Enabled", true)) { return "FarLands (distant buildings)"; }
                }
            }
            return null;
        }

        private static bool Setting(PluginInfo info, string section, string key, bool fallback) {
            ConfigFile config = info.Instance != null ? info.Instance.Config : null;
            if (config != null && config.TryGetEntry(section, key, out ConfigEntry<bool> entry)) { return entry.Value; }
            return fallback;
        }
    }
}
