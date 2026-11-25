using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Loading Zone Cadence: CreateLocalZones installs one zone every 0.1 s, so loading screens wait 0.1 s per zone.
    // During a loading screen, a prefix pokes the same ring nearest first through PokeLocalZone, up to a time budget.
    // Portal, respawn and login loading screens no longer wait on a fixed one-zone-per-tick pace.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZoneSystem))]
    internal static class LoadingZoneCadencePatch {
        internal const string FastLoadingGuid = "touzki.valheim.fastloading";

        internal static ConfigEntry<int> BudgetMs;

        internal static void BindConfig() {
            BudgetMs = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Loading Zone Budget",
                30,
                "Milliseconds per zone tick (ten a second) spent installing the zones around you behind a " +
                "loading screen, at least one zone each; 0 is vanilla's one zone per tick.",
                advanced: true,
                valMin: 0,
                valMax: 200);
        }

        private static readonly List<Vector2s> Ring = new List<Vector2s>();
        private static Vector2s _ringCenter;
        private static int _ringNear = -1;
        private static bool _ringClassic;
        private static Vector2s _sortCenter;
        private static readonly Comparison<Vector2s> NearestFirst = CompareNearestFirst;

        private static ZoneSystem _checkedFor;
        private static bool _standDown;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(LoadingZoneCadencePatch));

        // Debug summary of one loading screen.
        private static bool _inEpisode;
        private static bool _episodeReported;
        private static float _episodeStart;
        private static int _episodeInstalled;
        private static int _episodeTicks;
        private static double _episodeWorstMs;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("CreateLocalZones")]
        private static bool CreateLocalZonesPrefix(ZoneSystem __instance, Vector3 refPoint, ref bool __result, bool __runOriginal) {
            if (!__runOriginal) { return false; }

            if (!ShouldPace(__instance, refPoint, out Vector2s center, out SimulationDistance distance)) {
                _inEpisode = false;
                return true;
            }

            BuildRing(__instance, center, distance);

            long budget = (long)BudgetMs.Value * Stopwatch.Frequency / 1000;
            long start = Stopwatch.GetTimestamp();
            bool spent = false;
            int installed = 0;

            for (int i = 0; i < Ring.Count; i++) {
                Vector2s zone = Ring[i];

                // An installed zone only has its ttl reset; that one is never skipped, so none ages
                // out mid-load.
                bool loaded = __instance.m_zones.ContainsKey(zone);
                if (!loaded && spent) { continue; }

                // Vanilla's PokeLocalZone, so spawn mode, the location-prefab gate and other mods'
                // hooks on it are unchanged.
                if (__instance.PokeLocalZone(zone)) { installed++; }
                if (!loaded) { spent = Stopwatch.GetTimestamp() - start >= budget; }
            }

            // Vanilla's meaning: true when a zone was installed, which skips the host's ghost zones that tick.
            __result = installed > 0;

            if (Logger.DebugEnabled) { RecordEpisode(__instance, installed, Stopwatch.GetTimestamp() - start); }

            return false;
        }

        private static bool ShouldPace(ZoneSystem zoneSystem, Vector3 refPoint, out Vector2s center, out SimulationDistance distance) {
            center = default;
            distance = default;

            if (BudgetMs == null || BudgetMs.Value <= 0 || ApiSwitch.Off) { return false; }
            if (StandDown(zoneSystem)) { return false; }

            // A dedicated server has no local player, so it would always look like a loading screen.
            ZNet net = ZNet.instance;
            if (net == null || RunMode.IsDedicated) { return false; }
            if (!InLoadingScreen()) { return false; }

            // Before the spawn point is chosen the reference is still the origin; vanilla's walk
            // there is wasted work either way, and no faster version of it is wanted.
            Vector3 reference = net.GetReferencePosition();
            if (reference.x == 0f && reference.y == 0f && reference.z == 0f) { return false; }

            // Some networking mods call this for other players' positions; only the local walk is paced.
            center = ZoneSystem.GetZone(refPoint);
            if (center != ZoneSystem.GetZone(reference)) { return false; }

            distance = zoneSystem.m_simulationDistance;
            return distance.NearSimulationDistance > 0;
        }

        // ZNetScene.InLoadingScreen is a private instance method; replicated as in ZoneDiffRemovalPatch.
        private static bool InLoadingScreen() =>
            Player.m_localPlayer == null || Player.m_localPlayer.IsTeleporting();

        private static void BuildRing(ZoneSystem zoneSystem, Vector2s center, SimulationDistance distance) {
            int near = distance.NearSimulationDistance;
            bool classic = distance.IsClassic;
            if (Ring.Count > 0 && near == _ringNear && classic == _ringClassic && center == _ringCenter) { return; }

            FillNearRing(zoneSystem, center, distance, Ring);

            _ringCenter = center;
            _ringNear = near;
            _ringClassic = classic;
        }

        // The zones CreateLocalZones walks around center, by vanilla's own membership test, nearest
        // first. Fix Destination Terrain Delay prewarms the same set.
        internal static void FillNearRing(ZoneSystem zoneSystem, Vector2s center, SimulationDistance distance, List<Vector2s> ring) {
            int near = distance.NearSimulationDistance;
            bool classic = distance.IsClassic;

            ring.Clear();
            for (int y = center.y - near; y <= center.y + near; y++) {
                for (int x = center.x - near; x <= center.x + near; x++) {
                    Vector2s zone = new Vector2s(x, y);
                    if (classic || zoneSystem.ZonesWithinRadius(center, zone, near)) { ring.Add(zone); }
                }
            }

            _sortCenter = center;
            ring.Sort(NearestFirst);
        }

        // Nearest first; ties keep vanilla's row-major order.
        private static int CompareNearestFirst(Vector2s a, Vector2s b) {
            int adx = a.x - _sortCenter.x, ady = a.y - _sortCenter.y;
            int bdx = b.x - _sortCenter.x, bdy = b.y - _sortCenter.y;
            int byDistance = (adx * adx + ady * ady).CompareTo(bdx * bdx + bdy * bdy);
            if (byDistance != 0) { return byDistance; }

            int byRow = a.y.CompareTo(b.y);
            return byRow != 0 ? byRow : a.x.CompareTo(b.x);
        }

        // Asked once per ZoneSystem: every mod has patched by then. Prefixes and postfixes on the
        // walk still run around this replacement, so only transpilers count, plus Fast Loading,
        // which installs zones from a ZoneSystem.Update postfix with a budget of its own.
        private static bool StandDown(ZoneSystem zoneSystem) {
            if (ReferenceEquals(zoneSystem, _checkedFor)) { return _standDown; }
            _checkedFor = zoneSystem;

            SortedSet<string> owners = new SortedSet<string>(StringComparer.Ordinal);
            if (Chainloader.PluginInfos.ContainsKey(FastLoadingGuid)) { owners.Add(FastLoadingGuid); }

            // Fully qualified: HarmonyLib.Patches collides with this mod's Patches namespace.
            HarmonyLib.Patches info = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(ZoneSystem), "CreateLocalZones"));
            if (info != null) {
                foreach (Patch patch in info.Transpilers) {
                    if (patch.owner != ValheimCommunityPatch.PluginGUID) { owners.Add(patch.owner); }
                }
            }

            _standDown = owners.Count > 0;
            if (_standDown) {
                FixRegistry.MarkStoodDown(typeof(LoadingZoneCadencePatch));
                Logger.LogInfo(
                    $"Zone loading is changed by {string.Join(", ", owners)}, so 'Fix Loading Zone Cadence' " +
                    "stands down and that mod's pace applies.");
            }

            return _standDown;
        }

        private static void RecordEpisode(ZoneSystem zoneSystem, int installed, long elapsedTicks) {
            if (!_inEpisode) {
                _inEpisode = true;
                _episodeReported = false;
                _episodeStart = Time.unscaledTime;
                _episodeInstalled = 0;
                _episodeTicks = 0;
                _episodeWorstMs = 0;
            }

            _episodeTicks++;
            _episodeInstalled += installed;
            double ms = elapsedTicks * 1000.0 / Stopwatch.Frequency;
            if (ms > _episodeWorstMs) { _episodeWorstMs = ms; }

            if (_episodeReported || !zoneSystem.IsActiveAreaLoaded()) { return; }

            _episodeReported = true;
            Logger.LogDebug(
                $"Loading screen zones: {_episodeInstalled} installed over {_episodeTicks} tick(s), worst tick " +
                $"{_episodeWorstMs:0.0} ms, active area complete after {Time.unscaledTime - _episodeStart:0.0} s.");
        }
    }
}
