// Valheim Community Patch - mod API, file version 1.
//
// Copy this file into your mod as it is, or reference ValheimCommunityPatch.API.dll (merged into
// your plugin, never shipped loose). It has no compile-time dependency on Valheim Community Patch:
// it finds the mod at runtime, and every member does nothing and returns a default when the mod is
// not installed or is too old to have that member. Add a soft dependency so the mod loads first:
//
//   [BepInDependency("MidnightsFX.ValheimCommunityPatch", BepInDependency.DependencyFlags.SoftDependency)]
//
// Documentation: https://github.com/MidnightsFX/Valheim-Community-Patch/tree/master/ValheimCommunityPatch.API

using System;
using System.Collections.Generic;
using System.Reflection;

namespace ValheimCommunityPatch.API {

    /// <summary>
    /// Lets other mods cooperate with Valheim Community Patch where both change the same game code,
    /// instead of one of them standing down.
    /// </summary>
    public static class CommunityPatchAPI {
        /// <summary>The API version this file was written against.</summary>
        public const int FileApiVersion = 1;

        /// <summary>Valheim Community Patch's BepInEx GUID, for the soft dependency.</summary>
        public const string PluginGUID = "MidnightsFX.ValheimCommunityPatch";

        // Values GetFixState returns.
        /// <summary>No fix has this id.</summary>
        public const int FixStateUnknown = 0;
        /// <summary>Applied and running.</summary>
        public const int FixStateActive = 1;
        /// <summary>Applied, but switched off in the config.</summary>
        public const int FixStateDisabled = 2;
        /// <summary>
        /// Applied, but it has given way to another mod for this session: by itself, because a mod
        /// turned it off with <see cref="DisableFix"/>, or because its own hooks are missing.
        /// </summary>
        public const int FixStateStoodDown = 3;
        /// <summary>Never applied: not used on this side (a client fix on a dedicated server), or it stood down at startup for a known mod.</summary>
        public const int FixStateNotApplied = 4;
        /// <summary>Applying it failed, usually because a game update changed a method it patches.</summary>
        public const int FixStateFailed = 5;

        /// <summary>Ids of the fixes this API's hooks run through, for <see cref="GetFixState"/>.</summary>
        public static class FixIds {
            /// <summary>Fix Terrain Builder Throughput: runs the terrain build thread, and with it the terrain build hooks.</summary>
            public const string TerrainBuilderThroughput = "HeightmapBuilderThroughputPatch";
            /// <summary>Fix Background Zone Pacing: paces ghost-zone generation, and honours <see cref="SkipGhostZones"/>.</summary>
            public const string BackgroundZonePacing = "ZoneGenPacingPatch";
        }

        private const string ReceiverTypeName = "ValheimCommunityPatch.APIReceiver";
        private const string ReceiverAssemblyName = "ValheimCommunityPatch";

        private static readonly Type APIReceiver;
        private static readonly List<string> Unbound = new List<string>();

        private static readonly Func<int> getApiVersion;
        private static readonly Func<List<string>> getFixIds;
        private static readonly Func<string, int> getFixState;
        private static readonly Func<string, bool> canDisableFix;
        private static readonly Func<string, string, bool> disableFix;
        private static readonly Func<bool> isTerrainBuildLoopActive;
        private static readonly Func<int> getTerrainReadyCap;
        private static readonly Func<IList<Vector2s>, bool> setTerrainPriorityZones;
        private static readonly Action clearTerrainPriorityZones;
        private static readonly Action<Action<HeightmapBuilder.HMBuildData>> addTerrainBuildFinished;
        private static readonly Action<Action<HeightmapBuilder.HMBuildData>> removeTerrainBuildFinished;
        private static readonly Func<HeightmapBuilder.HMBuildData, bool> submitFinishedTerrainBuild;
        private static readonly Func<bool> getSkipGhostZones;
        private static readonly Action<bool> setSkipGhostZones;
        private static readonly Func<bool> isSkipGhostZonesSupported;

        /// <summary>
        /// True when Valheim Community Patch is installed. Decided the first time any member of
        /// this class is used, so do not touch it before the mod has loaded (see the soft
        /// dependency above).
        /// </summary>
        public static bool IsAvailable => APIReceiver != null;

        /// <summary>The API version the installed Valheim Community Patch provides, 0 when it is not installed.</summary>
        public static int ReceiverApiVersion => getApiVersion != null ? getApiVersion() : 0;

        /// <summary>
        /// Members of this file the installed Valheim Community Patch does not provide, usually
        /// because it is older than this file. Those members do nothing. Worth logging once.
        /// </summary>
        public static List<string> UnboundMembers => new List<string>(Unbound);

        static CommunityPatchAPI() {
            APIReceiver = FindReceiver();
            if (APIReceiver == null) { return; }

            getApiVersion = Bind<Func<int>>("GetApiVersion");
            getFixIds = Bind<Func<List<string>>>("GetFixIds");
            getFixState = Bind<Func<string, int>>("GetFixState");
            canDisableFix = Bind<Func<string, bool>>("CanDisableFix");
            disableFix = Bind<Func<string, string, bool>>("DisableFix");
            isTerrainBuildLoopActive = Bind<Func<bool>>("IsTerrainBuildLoopActive");
            getTerrainReadyCap = Bind<Func<int>>("GetTerrainReadyCap");
            setTerrainPriorityZones = Bind<Func<IList<Vector2s>, bool>>("SetTerrainPriorityZones");
            clearTerrainPriorityZones = Bind<Action>("ClearTerrainPriorityZones");
            addTerrainBuildFinished = Bind<Action<Action<HeightmapBuilder.HMBuildData>>>("AddTerrainBuildFinished");
            removeTerrainBuildFinished = Bind<Action<Action<HeightmapBuilder.HMBuildData>>>("RemoveTerrainBuildFinished");
            submitFinishedTerrainBuild = Bind<Func<HeightmapBuilder.HMBuildData, bool>>("SubmitFinishedTerrainBuild");
            getSkipGhostZones = Bind<Func<bool>>("GetSkipGhostZones");
            setSkipGhostZones = Bind<Action<bool>>("SetSkipGhostZones");
            isSkipGhostZonesSupported = Bind<Func<bool>>("IsSkipGhostZonesSupported");
        }

        private static Type FindReceiver() {
            Type type = null;
            try {
                type = Type.GetType(ReceiverTypeName + ", " + ReceiverAssemblyName, false);
            } catch (Exception) {
                // Some runtimes still throw while resolving the assembly; the scan below covers it.
            }

            if (type != null) { return type; }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                if (assembly.GetName().Name != ReceiverAssemblyName) { continue; }

                type = assembly.GetType(ReceiverTypeName, false);
                if (type != null) { return type; }
            }

            return null;
        }

        // Binds by name and by the delegate's exact signature, once. A member the receiver lacks,
        // or has with another shape, stays null and is listed in UnboundMembers.
        private static T Bind<T>(string name) where T : class {
            MethodInfo invoke = typeof(T).GetMethod("Invoke");
            ParameterInfo[] parameters = invoke.GetParameters();
            Type[] types = new Type[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) { types[i] = parameters[i].ParameterType; }

            MethodInfo method = APIReceiver.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, types, null);
            T bound = method != null && method.ReturnType == invoke.ReturnType
                ? Delegate.CreateDelegate(typeof(T), method, false) as T
                : null;

            if (bound == null) { Unbound.Add(name); }
            return bound;
        }

        // ---- Fix Status ----

        /// <summary>
        /// Every fix's id: the name of its patch class, stable from release to release. An empty
        /// list when the mod is not installed.
        /// </summary>
        public static List<string> GetFixIds() {
            return getFixIds != null ? getFixIds() : new List<string>();
        }

        /// <summary>
        /// What a fix is doing this session, as one of the FixState constants above.
        /// Stand-downs are mostly decided at a fix's first use in a world, so ask when you need
        /// the answer rather than once at startup.
        /// </summary>
        /// <param name="fixId">A fix id, from <see cref="GetFixIds"/> or <see cref="FixIds"/>.</param>
        public static int GetFixState(string fixId) {
            return getFixState != null ? getFixState(fixId) : FixStateUnknown;
        }

        /// <summary>True when the fix is applied, switched on and has not stood down.</summary>
        public static bool IsFixActive(string fixId) {
            return GetFixState(fixId) == FixStateActive;
        }

        // ---- Turning Fixes Off ----

        /// <summary>
        /// True when <see cref="DisableFix"/> can turn this fix off while the game runs. A few
        /// cannot, mostly ones that rewrite the game's code as it starts and give the game's own
        /// results anyway; the API documentation lists them and why.
        /// </summary>
        /// <param name="fixId">A fix id, from <see cref="GetFixIds"/>.</param>
        public static bool CanDisableFix(string fixId) {
            return canDisableFix != null && canDisableFix(fixId);
        }

        /// <summary>
        /// Turns a fix off for the rest of the session, for a mod that does the same job itself.
        /// The game's own behaviour applies in its place, from this call on, and the fix reads
        /// <see cref="FixStateStoodDown"/>. It stays off until the game restarts; the server
        /// admin's config is not changed. Only this game process is affected: a client turning a
        /// fix off does not turn it off on the server, nor the other way round.
        /// Valheim Community Patch logs a warning naming your mod, and the reason if given, so
        /// call it only when you need to, and from the main thread.
        /// </summary>
        /// <param name="fixId">A fix id, from <see cref="GetFixIds"/>.</param>
        /// <param name="reason">A few words for the log on why, for example "MyMod paces ghost zones itself".</param>
        /// <returns>
        /// True when the fix is off for the rest of the session: turned off now or earlier, or not
        /// running this session anyway. False when no fix has that id, the fix cannot be turned off
        /// while the game runs (<see cref="CanDisableFix"/>), the call came from another thread,
        /// or the mod is not installed.
        /// </returns>
        public static bool DisableFix(string fixId, string reason = null) {
            return disableFix != null && disableFix(fixId, reason);
        }

        // ---- Terrain Build Queue ----

        /// <summary>
        /// True when Valheim Community Patch's loop is the one the terrain build thread runs, so
        /// the terrain members below take effect. When false, patch HeightmapBuilder yourself.
        /// The thread starts with the first terrain (the main menu's), so ask when you need it. To
        /// replace the loop itself, turn <see cref="FixIds.TerrainBuilderThroughput"/> off with
        /// <see cref="DisableFix"/> in your Awake: a BuildThread patch only reaches a thread
        /// started after it.
        /// </summary>
        public static bool TerrainBuildLoopActive {
            get { return isTerrainBuildLoopActive != null && isTerrainBuildLoopActive(); }
        }

        /// <summary>
        /// How many finished builds the build thread keeps before discarding the oldest. Stay
        /// under it when queueing terrain ahead of time, or early results are thrown away.
        /// 0 when the mod is not installed.
        /// </summary>
        public static int TerrainReadyCap {
            get { return getTerrainReadyCap != null ? getTerrainReadyCap() : 0; }
        }

        /// <summary>
        /// Has the build thread build these zones before anything else queued, in the order given
        /// (nearest first, for example), until you replace or clear the list. Zones that are
        /// requested after this call are covered too, so it can be set before the game asks for
        /// them. Distant-terrain tiles are never promoted. Main thread.
        /// </summary>
        /// <param name="zones">Zone ids, as ZoneSystem.GetZone returns them. Copied.</param>
        /// <returns>True when the priority takes effect (<see cref="TerrainBuildLoopActive"/>).</returns>
        public static bool SetTerrainPriorityZones(IList<Vector2s> zones) {
            return setTerrainPriorityZones != null && setTerrainPriorityZones(zones);
        }

        /// <summary>Returns the build thread to first-come, first-built.</summary>
        public static void ClearTerrainPriorityZones() {
            if (clearTerrainPriorityZones != null) { clearTerrainPriorityZones(); }
        }

        /// <summary>
        /// Raised for every terrain build the build thread finishes, with the finished data,
        /// which has just been added to the builder's ready list. Raised on the build thread:
        /// keep handlers short and do not call Unity. The data is shared with the game, which
        /// only reads it; do not change it. A handler that throws is removed. Only raised while
        /// <see cref="TerrainBuildLoopActive"/>.
        /// </summary>
        public static event Action<HeightmapBuilder.HMBuildData> TerrainBuildFinished {
            add { if (addTerrainBuildFinished != null) { addTerrainBuildFinished(value); } }
            remove { if (removeTerrainBuildFinished != null) { removeTerrainBuildFinished(value); } }
        }

        /// <summary>
        /// Hands a finished build back to the builder, as if it had just been built, so the next
        /// request for that terrain takes it instead of building it again; for example one kept
        /// from <see cref="TerrainBuildFinished"/>. Respects <see cref="TerrainReadyCap"/>. Any
        /// thread.
        /// </summary>
        /// <returns>
        /// False when there is no builder, the data was never built or was built for another
        /// world, or an equal build is already waiting.
        /// </returns>
        public static bool SubmitFinishedTerrainBuild(HeightmapBuilder.HMBuildData data) {
            return submitFinishedTerrainBuild != null && submitFinishedTerrainBuild(data);
        }

        // ---- Ghost Zones ----

        /// <summary>
        /// While true, the world owner generates no ghost zones: the ring of zones pre-generated
        /// around the host and every peer that nobody stands in yet. Zones players enter still
        /// load. Set it for a short, bounded time (a loading screen) and always clear it; a pause
        /// left on for over two minutes is ignored until it is cleared. Main thread.
        /// </summary>
        public static bool SkipGhostZones {
            get { return getSkipGhostZones != null && getSkipGhostZones(); }
            set { if (setSkipGhostZones != null) { setSkipGhostZones(value); } }
        }

        /// <summary>True when <see cref="SkipGhostZones"/> takes effect. When false, patch ZoneSystem.CreateGhostZones yourself.</summary>
        public static bool SkipGhostZonesSupported {
            get { return isSkipGhostZonesSupported != null && isSkipGhostZonesSupported(); }
        }
    }
}
