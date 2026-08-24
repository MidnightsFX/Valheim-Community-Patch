using System;
using UnityEngine;
using UnityEngine.Rendering;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// Which side of a session a fix does anything on.
    /// </summary>
    /// <remarks>
    /// The question this answers is narrow, and deliberately so: does a dedicated server gain
    /// anything at all from having this fix installed? That is the only question with a
    /// consequence, because a listen host is never headless, so Client has exactly one effect -
    /// the patch is not applied on a dedicated server.
    ///
    /// The reason so many fixes come out Client is that a dedicated server is not a simulation
    /// host. ZNetScene.CreateDestroyObjects (ZNetScene.cs:286) keys off a single
    /// ZNet.GetReferencePosition, not one per peer, and every caller of SetReferencePosition is a
    /// local-player path (Player.cs:330, Player.cs:1001, Game.cs:315/342/365). On a headless
    /// server it never moves off Vector3.zero, so the only GameObjects that exist there are the
    /// ones in its own active area around world origin. Peers get CreateGhostZones, which
    /// generates ZDO data and never instantiates anything.
    ///
    /// So component and behaviour fixes are client value, and the data and network layer - ZDOMan,
    /// ZNetScene bookkeeping, ZPackage, ZSteamSocket, Game.ConnectPortals - is where a server
    /// gains. Where a fix is borderline, prefer Both: the cost of patching something the server
    /// never reaches is one unused trampoline, and the cost of skipping something it does reach is
    /// a bug that only shows up on somebody else's machine.
    /// </remarks>
    internal enum Side {
        /// <summary>
        /// Needs a local player, input, UI or rendering, or produces output nothing headless ever
        /// looks at. Not patched at all on a dedicated server.
        /// </summary>
        Client,

        /// <summary>
        /// Only the machine that owns the world - a dedicated server or a listen host.
        /// Declarative only: these are always patched, because a client process can start hosting
        /// later in the same run and ZNet does not exist yet when patches are applied. Every
        /// server-only fix here already sits behind a vanilla IsServer check at its call site, so
        /// none of them needs a runtime gate of its own.
        /// </summary>
        Server,

        /// <summary>Reachable and useful on both.</summary>
        Both,
    }

    /// <summary>
    /// Declares which side a Harmony patch class is worth applying on. Read by
    /// <see cref="ValheimCommunityPatch.ApplyPatches"/> and by the fix's config description, so the
    /// gate and the documentation come from one declaration and cannot drift apart.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class PatchSideAttribute : Attribute {
        internal PatchSideAttribute(Side side) { Side = side; }

        internal Side Side { get; }

        /// <summary>
        /// The side declared on <paramref name="type"/>, or on the nearest type it is nested in.
        /// </summary>
        /// <remarks>
        /// The walk out through DeclaringType is the point of this method. Nested patch classes -
        /// OrphanZdoIndexPatch's five hooks - are separate entries in Assembly.GetTypes() and reach
        /// ApplyPatches individually, but attribute inheritance walks *base* types, not declaring
        /// ones, so Inherited = true cannot reach the outer declaration. Without the walk they
        /// would silently fall back to Both.
        /// </remarks>
        internal static Side Of(Type type) {
            for (Type t = type; t != null; t = t.DeclaringType) {
                object[] found = t.GetCustomAttributes(typeof(PatchSideAttribute), false);
                if (found.Length > 0) { return ((PatchSideAttribute)found[0]).Side; }
            }

            // Undeclared means "apply everywhere", which is what this mod did before the attribute
            // existed. Forgetting one is then a no-op rather than a silently disabled fix.
            return Side.Both;
        }

        /// <summary>The tag used in config descriptions and in the README. One spelling, one place.</summary>
        internal static string Tag(Side side) {
            switch (side) {
                case Side.Client: return "(client)";
                case Side.Server: return "(server)";
                default: return "(both)";
            }
        }
    }

    /// <summary>
    /// Where this process is running. Two different questions, deliberately kept apart.
    /// </summary>
    /// <remarks>
    /// Headless-ness is a property of the build and is fixed for the life of the process, so it can
    /// be answered while patches are being applied. The network role cannot: ZNet does not exist
    /// yet at that point, and it genuinely changes within one process when somebody hosts a world,
    /// quits to the menu and joins a friend.
    ///
    /// That split is why the gating is asymmetric, which otherwise looks inconsistent. The
    /// patch-time IsHeadless check is early but heuristic, so it is an optimisation - it decides
    /// whether to bother patching. The runtime IsDedicated check is late but authoritative, since
    /// IsDedicated is a per-build constant (the client assembly hardcodes false at ZNet.cs:1562),
    /// so it is the correctness check for the two client fixes whose target method genuinely runs
    /// and does real work on a dedicated server. Everywhere else a client fix on a server is
    /// naturally inert - the body never runs without a local player - and a guard would be noise.
    /// </remarks>
    internal static class RunMode {
        // The only environment question answerable at plugin Awake, since ZNet.instance does not
        // exist yet. A headless process has no graphics device for its whole life and the dedicated
        // server is the only Valheim build that runs that way, so this is stable enough to patch
        // against. It is the same test Jotunn's GUIManager.IsHeadless() makes, and the same one
        // vanilla itself makes at RandEventSystem.cs:333; done locally rather than through Jotunn
        // so that applying patches does not depend on a Jotunn manager being usable this early.
        //
        // Deliberately not "|| Application.isBatchMode": that would also catch a real client
        // launched headless for tooling, and every client fix would silently vanish.
        private static readonly bool _headless =
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        internal static bool IsHeadless => _headless;

        // Cached against the ZNet instance that answered, rather than behind a flag that some
        // shutdown hook has to remember to clear. ZNet.Awake assigns a fresh m_instance per session
        // and ZNet.OnDestroy nulls it, so object identity *is* the invalidation: no Harmony hook, no
        // ordering assumption, nothing to forget. That also closes a hole in the hook-based version
        // this replaced - ZNet.StopAll skips ZDOMan.ShutDown entirely when suspending (ZNet.cs:397).
        private static ZNet _resolvedFor;
        private static bool _isServer;
        private static bool _isDedicated;

        /// <summary>True on a dedicated server or a listen host: the machine that owns the world.</summary>
        internal static bool IsServer { get { Resolve(); return _isServer; } }

        /// <summary>True only on a headless server. False on a listen host, which draws its own game.</summary>
        internal static bool IsDedicated { get { Resolve(); return _isDedicated; } }

        private static void Resolve() {
            ZNet znet = ZNet.instance;

            // ReferenceEquals throughout, never Unity's ==: that operator does a native alive-check
            // on every call, and this runs on every ZDO ownership change, which is hot on a busy
            // server. A destroyed-but-still-referenced ZNet answers correctly anyway - IsServer and
            // IsDedicated read statics only (ZNet.cs:1558, :1562).
            if (ReferenceEquals(znet, _resolvedFor)) { return; }

            if (ReferenceEquals(znet, null)) {
                // Between sessions. Reset rather than cache: freezing the answer at "not a server"
                // would survive into the next session, and that one may be hosted.
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
