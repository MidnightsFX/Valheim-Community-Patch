using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using ValheimCommunityPatch.Patches.Performance;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// The receiving end of the mod API. Other mods never reference this class: they copy
    /// CommunityPatchAPI.cs (or reference ValheimCommunityPatch.API.dll), which finds this class
    /// by name and binds each method below by name and signature.
    /// </summary>
    /// <remarks>
    /// Every name and signature here is a published contract. Add methods freely; never rename,
    /// remove or change one, because a mod built against an older API file binds the old shape
    /// and silently loses that member if it is gone. Bump <see cref="ApiVersion"/> with every
    /// addition. Each method only checks its arguments and hands off to the fix that owns the
    /// behaviour.
    /// </remarks>
    public static class APIReceiver {
        /// <summary>Raised by one with every member added.</summary>
        internal const int ApiVersion = 1;

        public static int GetApiVersion() => ApiVersion;

        // ---- Fix status ----

        public static List<string> GetFixIds() => FixRegistry.GetFixIds();

        public static int GetFixState(string fixId) => FixRegistry.GetState(fixId);

        // ---- Turning fixes off ----

        public static bool CanDisableFix(string fixId) => FixRegistry.CanDisable(fixId);

        public static bool DisableFix(string fixId, string reason) => FixRegistry.Disable(fixId, reason, DescribeCaller());

        // ---- Terrain build queue (Fix Terrain Builder Throughput) ----

        public static bool IsTerrainBuildLoopActive() => HeightmapBuilderThroughputPatch.LoopRunning;

        public static int GetTerrainReadyCap() => HeightmapBuilderThroughputPatch.ReadyCapInForce;

        public static bool SetTerrainPriorityZones(IList<Vector2s> zones) {
            HeightmapBuilderThroughputPatch.SetPriorityZones(zones);
            return HeightmapBuilderThroughputPatch.LoopRunning;
        }

        public static void ClearTerrainPriorityZones() => HeightmapBuilderThroughputPatch.ClearPriorityZones();

        public static void AddTerrainBuildFinished(Action<HeightmapBuilder.HMBuildData> handler) =>
            HeightmapBuilderThroughputPatch.AddBuildFinishedHandler(handler);

        public static void RemoveTerrainBuildFinished(Action<HeightmapBuilder.HMBuildData> handler) =>
            HeightmapBuilderThroughputPatch.RemoveBuildFinishedHandler(handler);

        public static bool SubmitFinishedTerrainBuild(HeightmapBuilder.HMBuildData data) =>
            HeightmapBuilderThroughputPatch.SubmitFinishedBuild(data);

        // ---- Ghost zones (Fix Background Zone Pacing) ----

        public static bool GetSkipGhostZones() => ZoneGenPacingPatch.SkipGhostZones;

        public static void SetSkipGhostZones(bool skip) => ZoneGenPacingPatch.SkipGhostZones = skip;

        public static bool IsSkipGhostZonesSupported() => ZoneGenPacingPatch.SkipGhostZonesSupported;

        // ---- Callers ----

        // Other mods compile the API file into their own assembly, under this namespace.
        private const string ApiNamespace = "ValheimCommunityPatch.API";

        // The mod that called into the API, for the log: the first frame that is not this mod,
        // the API file or the runtime's own plumbing, named by the plugin that owns its assembly.
        // Worked out here rather than taken from the caller, which could name anyone.
        private static string DescribeCaller() {
            StackTrace trace = new StackTrace(1, false);
            for (int i = 0; i < trace.FrameCount; i++) {
                MethodBase method = trace.GetFrame(i)?.GetMethod();

                // Null for Harmony's patched copies of a method; the frames past it still say who.
                Type type = method?.DeclaringType;
                if (type == null) { continue; }

                Assembly assembly = type.Assembly;
                if (assembly == typeof(APIReceiver).Assembly || assembly == typeof(object).Assembly) { continue; }
                if (type.Namespace == ApiNamespace) { continue; }

                return $"{PluginOf(assembly)}, from {type.FullName}.{method.Name}";
            }

            return "an unknown mod";
        }

        // By file as well as by instance: BepInEx sets Instance only once the plugin's Awake has
        // returned, and Awake is where most mods will call.
        private static string PluginOf(Assembly assembly) {
            string location = assembly.Location;
            foreach (PluginInfo info in Chainloader.PluginInfos.Values) {
                bool sameFile = !string.IsNullOrEmpty(location)
                    && string.Equals(info.Location, location, StringComparison.OrdinalIgnoreCase);
                if (sameFile || (info.Instance != null && info.Instance.GetType().Assembly == assembly)) {
                    return $"{info.Metadata.Name} {info.Metadata.Version} ({info.Metadata.GUID})";
                }
            }

            return $"assembly {assembly.GetName().Name}";
        }
    }
}
