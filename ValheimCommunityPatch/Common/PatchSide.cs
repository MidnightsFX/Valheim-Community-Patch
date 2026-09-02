using System;
using UnityEngine;
using UnityEngine.Rendering;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// Which side a fix does anything on. It decides exactly one thing: Client fixes are not
    /// applied on a dedicated server.
    /// </summary>
    /// <remarks>
    /// A dedicated server is not a simulation host. It only instantiates objects inside its own
    /// active area, which never leaves world origin because nothing there moves the reference
    /// position. So component and behaviour fixes are client value, and the data and network
    /// layer (ZDOMan, ZNetScene bookkeeping, ZPackage, sockets) is where a server gains. Where a
    /// fix is borderline, prefer Both: an unused patch costs one trampoline, a wrongly skipped
    /// one costs a bug on somebody else's machine.
    /// </remarks>
    internal enum Side {
        /// <summary>Needs a local player, input, UI or rendering. Not patched on a dedicated server.</summary>
        Client,

        /// <summary>
        /// Only the machine that owns the world. Declarative only: always patched, because a client
        /// process can start hosting later in the same run. Every server-only fix already sits
        /// behind a vanilla IsServer check at its call site.
        /// </summary>
        Server,

        /// <summary>Reachable and useful on both.</summary>
        Both,
    }

    /// <summary>
    /// Declares which side a Harmony patch class is worth applying on. Read by ApplyPatches to
    /// gate the patch and by ValConfig.BindFixToggle to tag the config description, so the two
    /// cannot disagree.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class PatchSideAttribute : Attribute {
        internal PatchSideAttribute(Side side) { Side = side; }

        internal Side Side { get; }

        /// <summary>The side declared on <paramref name="type"/> or on the nearest type it is nested in.</summary>
        internal static Side Of(Type type) {
            // Nested hook classes reach ApplyPatches as types of their own, and attribute
            // inheritance walks base types rather than declaring types, hence the manual walk.
            for (Type t = type; t != null; t = t.DeclaringType) {
                object[] found = t.GetCustomAttributes(typeof(PatchSideAttribute), false);
                if (found.Length > 0) { return ((PatchSideAttribute)found[0]).Side; }
            }

            // Undeclared means "apply everywhere", so a forgotten attribute is never a disabled fix.
            return Side.Both;
        }

        /// <summary>The tag used in config descriptions and the README.</summary>
        internal static string Tag(Side side) {
            switch (side) {
                case Side.Client: return "(client)";
                case Side.Server: return "(server)";
                default: return "(both)";
            }
        }
    }

    /// <summary>Where this process is running: headless or not, and hosting or not.</summary>
    /// <remarks>
    /// Two different questions. Headless-ness is fixed for the life of the process and known at
    /// patch time, so it decides whether a Client patch is applied at all. The network role is
    /// not known until ZNet exists and changes when a player hosts, quits and joins a friend, so
    /// it is resolved at runtime and cached against the ZNet instance that answered.
    /// </remarks>
    internal static class RunMode {
        // The same test Jotunn's GUIManager.IsHeadless makes. Deliberately not
        // "|| Application.isBatchMode", which would also catch a real client launched headless
        // for tooling and silently drop every client fix.
        private static readonly bool _headless =
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        internal static bool IsHeadless => _headless;

        // Cached against the ZNet instance rather than cleared by a shutdown hook: ZNet.Awake
        // assigns a fresh instance per session, so object identity is the invalidation.
        private static ZNet _resolvedFor;
        private static bool _isServer;
        private static bool _isDedicated;

        /// <summary>True on a dedicated server or a listen host: the machine that owns the world.</summary>
        internal static bool IsServer { get { Resolve(); return _isServer; } }

        /// <summary>True only on a headless server. False on a listen host, which draws its own game.</summary>
        internal static bool IsDedicated { get { Resolve(); return _isDedicated; } }

        private static void Resolve() {
            ZNet znet = ZNet.instance;

            // ReferenceEquals rather than Unity's ==, which does a native alive-check: this runs
            // on every ZDO ownership change. IsServer and IsDedicated read statics, so a destroyed
            // but still referenced ZNet answers correctly anyway.
            if (ReferenceEquals(znet, _resolvedFor)) { return; }

            if (ReferenceEquals(znet, null)) {
                // Between sessions. Reset rather than cache, since the next session may be hosted.
                _resolvedFor = null;
                _isServer = false;
                _isDedicated = false;
                return;
            }

            _isServer = znet.IsServer();
            _isDedicated = znet.IsDedicated();
            _resolvedFor = znet;
        }
    }
}
