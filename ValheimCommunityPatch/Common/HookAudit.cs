using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// Notices this mod's hooks that another mod removed after they attached, and turns off the
    /// fixes they belong to where that can be done cleanly.
    /// </summary>
    /// <remarks>
    /// A fix with some hooks gone can run half applied: a cache filled by one hook and no longer
    /// cleared by another. Valheim AVX removes other mods' hooks from the methods it optimizes when
    /// its Compatibility.OverrideOtherMods is on or its ForceAvxMods names this mod. It does that in
    /// its Start, after this mod patched, and Harmony then no longer lists them.
    /// </remarks>
    internal static class HookAudit {
        private const string ValheimAvxGuid = "com.kewb.valheimavx";

        private static readonly object Gate = new object();
        private static readonly List<(MethodBase Original, MethodInfo Patch)> Hooks = new List<(MethodBase, MethodInfo)>();
        private static readonly HashSet<(MethodBase, MethodInfo)> Reported = new HashSet<(MethodBase, MethodInfo)>();

        /// <summary>True when another mod has removed any of this mod's hooks.</summary>
        internal static bool AnyRemoved => Missing().Count > 0;

        /// <summary>Records every hook this mod has attached. Call once, after patching.</summary>
        internal static void Snapshot(Harmony harmony) {
            lock (Gate) {
                foreach (MethodBase original in harmony.GetPatchedMethods()) {
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                    if (info == null) { continue; }

                    foreach (Patch patch in Mine(info)) { Hooks.Add((original, patch.PatchMethod)); }
                }
            }
        }

        private static List<(MethodBase Original, MethodInfo Patch)> Missing() {
            List<(MethodBase, MethodInfo)> missing = new List<(MethodBase, MethodInfo)>();
            lock (Gate) {
                foreach ((MethodBase original, MethodInfo patch) in Hooks) {
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                    if (info == null || !Mine(info).Any(p => p.PatchMethod == patch)) { missing.Add((original, patch)); }
                }
            }

            return missing;
        }

        private static IEnumerable<Patch> Mine(HarmonyLib.Patches info) =>
            info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                .Where(p => p.owner == ValheimCommunityPatch.PluginGUID && p.PatchMethod != null);

        private static void Report() {
            List<(MethodBase Original, MethodInfo Patch)> missing = Missing();
            lock (Gate) { missing.RemoveAll(h => !Reported.Add(h)); }
            if (missing.Count == 0) { return; }

            SortedSet<string> turnedOff = new SortedSet<string>(StringComparer.Ordinal);
            SortedSet<string> partial = new SortedSet<string>(StringComparer.Ordinal);
            foreach ((MethodBase _, MethodInfo patch) in missing) {
                Type fix = FixRegistry.FixOf(patch.DeclaringType);
                if (fix == null) {
                    partial.Add(patch.DeclaringType?.Name ?? "?");
                } else if (FixRegistry.TurnOffForMissingHooks(fix)) {
                    turnedOff.Add(FixRegistry.DisplayName(fix));
                } else {
                    partial.Add(FixRegistry.DisplayName(fix));
                }
            }

            bool avx = Chainloader.PluginInfos.TryGetValue(ValheimAvxGuid, out PluginInfo avxInfo);
            Logger.LogWarning(
                $"{(avx && AvxRemovesHooks(avxInfo) ? "Valheim AVX's Compatibility settings" : "Another mod")} removed " +
                $"{missing.Count} of this mod's hooks after they attached." +
                (turnedOff.Count > 0 ? $" Turned off for this session, so the game's own behaviour applies: {string.Join(", ", turnedOff)}." : "") +
                (partial.Count > 0 ? $" Running without some of their hooks: {string.Join(", ", partial)}." : "") +
                (avx ? $" To keep them, add {ValheimCommunityPatch.PluginGUID} to Compatibility.CooperativeMods in Valheim AVX's config." : ""));
        }

        private static bool AvxRemovesHooks(PluginInfo avx) {
            ConfigFile config = avx.Instance != null ? avx.Instance.Config : null;
            if (config == null) { return false; }

            return (config.TryGetEntry("Compatibility", "OverrideOtherMods", out ConfigEntry<bool> all) && all.Value)
                || (config.TryGetEntry("Compatibility", "ForceAvxMods", out ConfigEntry<string> named)
                    && !string.IsNullOrWhiteSpace(named.Value));
        }

        // Every world start: a mod can remove hooks whenever it looks at a method again.
        [HarmonyPatch(typeof(Game), "Start")]
        internal static class WorldStartHook {
            [HarmonyPostfix]
            private static void Postfix() {
                try {
                    Report();
                } catch (Exception ex) {
                    Logger.LogWarning($"Could not check this mod's hooks: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
