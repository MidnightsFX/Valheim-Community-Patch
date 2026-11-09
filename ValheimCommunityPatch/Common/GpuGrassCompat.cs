using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// Detects mods that build and draw the game's grass themselves on the graphics card, and takes
    /// this mod's grass-building hooks out of their way.
    /// </summary>
    /// <remarks>
    /// FarLands and SteadyFrame share a grass module that places blades on a worker thread and draws
    /// them with compute shaders. Once per ClutterSystem it hands patch building back to the game if any
    /// other mod patches ClutterSystem.GenerateVegPatch or either InstanceRenderer.AddInstance overload.
    /// Both load after this mod, so at patch time they are not known yet; by the first
    /// ClutterSystem.Awake every plugin has loaded, and the fixes that hook those methods remove their
    /// hooks there, before the module first looks. Without compute shaders, or with FarLands turned off
    /// in its config, the module leaves the grass to the game and the fixes stay.
    /// </remarks>
    internal static class GpuGrassCompat {
        internal const string FarLandsGuid = "ru.ivest.farlands";
        internal const string SteadyFrameGuid = "ru.ivest.steadyframe";

        /// <summary>True when a loaded mod builds the grass on the GPU on this machine. Ask no earlier than ClutterSystem.Awake.</summary>
        internal static bool BuildsGrass {
            get {
                if (!SystemInfo.supportsComputeShaders) { return false; }
                if (Chainloader.PluginInfos.ContainsKey(SteadyFrameGuid)) { return true; }
                if (!Chainloader.PluginInfos.TryGetValue(FarLandsGuid, out PluginInfo farLands)) { return false; }

                // FarLands offers its grass module only while its General.Enabled is on.
                ConfigFile config = farLands.Instance != null ? farLands.Instance.Config : null;
                ConfigEntry<bool> enabled = null;
                return config == null || !config.TryGetEntry("General", "Enabled", out enabled) || enabled.Value;
            }
        }

        /// <summary>The loaded mod's name, for log lines.</summary>
        internal static string Name =>
            Chainloader.PluginInfos.ContainsKey(FarLandsGuid) ? "FarLands" : "SteadyFrame";

        /// <summary>
        /// Removes this mod's hooks declared in <paramref name="hookClass"/> from <paramref name="original"/>.
        /// </summary>
        internal static void Unhook(MethodBase original, Type hookClass) {
            if (original == null) { return; }

            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            if (info == null) { return; }

            List<MethodInfo> mine = new List<MethodInfo>();
            foreach (IReadOnlyList<Patch> patches in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers }) {
                foreach (Patch patch in patches) {
                    if (patch.owner == ValheimCommunityPatch.PluginGUID && patch.PatchMethod?.DeclaringType == hookClass) {
                        mine.Add(patch.PatchMethod);
                    }
                }
            }

            Harmony harmony = new Harmony(ValheimCommunityPatch.PluginGUID);
            foreach (MethodInfo patch in mine) { harmony.Unpatch(original, patch); }
        }
    }
}
