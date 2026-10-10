using System;
using System.Collections.Generic;
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
    }
}
