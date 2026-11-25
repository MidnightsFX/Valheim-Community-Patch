using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using SoftReferenceableAssets;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Background Dungeon Generation: dungeons generated ahead of players build each room model only to destroy it.
    // Transpiles DungeonGenerator.PlaceRoom to build ghost-mode rooms as stand-ins of just Room and its connections.
    // Dungeons and camps in zones nobody has reached generate with a far smaller hitch and the same saved result.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(DungeonGenerator), "PlaceRoom",
        typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode))]
    internal static class GhostDungeonStandInPatch {
        private const string FixName = "Fix Background Dungeon Generation";

        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Background Dungeon Stand-ins",
                false,
                "Diagnostic. Builds both the stand-in and the full model for every room generated ahead of the " +
                "players, uses the model, and logs any difference; costs what the fix saves.",
                advanced: true);
        }

        private static readonly MethodBase PlaceRoomTarget = AccessTools.Method(typeof(DungeonGenerator), "PlaceRoom",
            new[] { typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode) });

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(GhostDungeonStandInPatch),
            PlaceRoomTarget,
            HookKinds.Any,
            owners => $"Dungeon room placement is hooked by {owners}, which may read the room models, so " +
                      $"'{FixName}' stands down and builds them.");

        // Above zero while a ghost-mode PlaceRoom runs.
        private static int _ghostDepth;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(GhostDungeonStandInPatch));

        [HarmonyPrefix]
        private static void Prefix(ZoneSystem.SpawnMode mode, out bool __state) {
            __state = mode == ZoneSystem.SpawnMode.Ghost;
            if (__state) { _ghostDepth++; }
        }

        [HarmonyFinalizer]
        private static void Finalizer(bool __state) {
            if (__state) { _ghostDepth--; }
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(
                instructions,
                AccessTools.Method(typeof(SoftReferenceableAssets.Utils), nameof(SoftReferenceableAssets.Utils.Instantiate),
                    new[] { typeof(SoftReference<GameObject>), typeof(Vector3), typeof(Quaternion), typeof(Transform) }),
                AccessTools.DeclaredMethod(typeof(GhostDungeonStandInPatch), nameof(RoomModel)),
                $"{FixName} (DungeonGenerator.PlaceRoom)", expected: 1);

        private static GameObject RoomModel(SoftReference<GameObject> prefab, Vector3 position, Quaternion rotation, Transform parent) {
            // A model that stays (or one the layout needs in full) is built by Fix Location Model
            // Placeholders, which falls back to the game's own clone.
            if (_ghostDepth <= 0) { return ModelTemplatePatch.RoomModel(prefab, position, rotation, parent); }

            // PlaceRoom loaded the prefab before calling here.
            GameObject asset = prefab.Asset;
            if (asset == null || ApiSwitch.Off || !CanStandIn(asset) || Takeover.TakenOver) {
                return ModelTemplatePatch.RoomModel(prefab, position, rotation, parent);
            }

            if (Verify == null || !Verify.Value) { return BuildStandIn(asset, position, rotation, parent); }

            GameObject model = SoftReferenceableAssets.Utils.Instantiate(prefab, position, rotation, parent);
            GameObject standIn = BuildStandIn(asset, position, rotation, parent);
            Compare(asset.name, model, standIn);
            Object.DestroyImmediate(standIn);
            return model;
        }

        // ---- eligibility -----------------------------------------------------------------------

        private static readonly Dictionary<int, bool> Eligible = new Dictionary<int, bool>();
        private static int _terrainLayer = -2;

        // A terrain-layer collider is the one part of a room model the generation can see besides
        // the layout data: the camp layouts probe the ground on that layer between placements.
        private static bool CanStandIn(GameObject asset) {
            int id = asset.GetInstanceID();
            if (Eligible.TryGetValue(id, out bool eligible)) { return eligible; }

            if (_terrainLayer == -2) { _terrainLayer = LayerMask.NameToLayer("terrain"); }

            eligible = asset.GetComponent<Room>() != null;
            if (eligible && _terrainLayer >= 0) {
                foreach (Collider collider in asset.GetComponentsInChildren<Collider>(true)) {
                    if (collider.gameObject.layer != _terrainLayer) { continue; }

                    eligible = false;
                    Logger.LogDebug($"{FixName}: room {asset.name} has a terrain-layer collider, so it is always built in full.");
                    break;
                }
            }

            Eligible[id] = eligible;
            return eligible;
        }

        // ---- the stand-in ----------------------------------------------------------------------

        private static readonly Dictionary<Transform, Transform> Mirrored = new Dictionary<Transform, Transform>();

        // Everything the layout, the door and end-cap passes and the saved room list read from a placed room.
        // Internal so 'Log World Generation Timings' can time it.
        internal static GameObject BuildStandIn(GameObject asset, Vector3 position, Quaternion rotation, Transform parent) {
            Transform source = asset.transform;

            // Instantiate(original, position, rotation, parent) places the copy in world space and
            // keeps the original's local scale.
            GameObject root = new GameObject(asset.name);
            Transform rootTransform = root.transform;
            rootTransform.SetParent(parent, false);
            rootTransform.SetPositionAndRotation(position, rotation);
            rootTransform.localScale = source.localScale;

            // Room.Awake runs here, before the copy, so it spawns no music volume.
            CopySerialized(asset.GetComponent<Room>(), root.AddComponent<Room>());

            // In hierarchy order, which is the order GetComponentsInChildren returns them in, so
            // the mirror lists the active ones in the same order as the model would.
            Mirrored.Clear();
            Mirrored[source] = rootTransform;
            foreach (RoomConnection connection in asset.GetComponentsInChildren<RoomConnection>(true)) {
                Transform mirror = Mirror(connection.transform);
                RoomConnection copy = mirror.gameObject.AddComponent<RoomConnection>();
                CopySerialized(connection, copy);
                copy.enabled = connection.enabled;
            }

            Mirrored.Clear();
            return root;
        }

        // The mirror of one object under the room, creating its ancestors first. New objects are
        // appended after their already-mirrored siblings, so sibling order follows the original.
        private static Transform Mirror(Transform source) {
            if (Mirrored.TryGetValue(source, out Transform mirror)) { return mirror; }

            Transform parent = Mirror(source.parent);

            GameObject copy = new GameObject(source.name);
            mirror = copy.transform;
            mirror.SetParent(parent, false);
            mirror.localPosition = source.localPosition;
            mirror.localRotation = source.localRotation;
            mirror.localScale = source.localScale;
            copy.SetActive(source.gameObject.activeSelf);

            Mirrored[source] = mirror;
            return mirror;
        }

        private static readonly Dictionary<Type, FieldInfo[]> SerializedFields = new Dictionary<Type, FieldInfo[]>();

        // The fields Instantiate copies: public ones not marked NonSerialized, and SerializeField
        // ones. Everything else keeps its initializer, as on an instantiated copy.
        private static void CopySerialized<T>(T from, T to) where T : MonoBehaviour {
            if (!SerializedFields.TryGetValue(typeof(T), out FieldInfo[] fields)) {
                List<FieldInfo> found = new List<FieldInfo>();
                for (Type type = typeof(T); type != null && type != typeof(MonoBehaviour); type = type.BaseType) {
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                        bool serialized = field.IsPublic
                            ? !field.IsNotSerialized
                            : field.IsDefined(typeof(SerializeField), false);
                        if (serialized) { found.Add(field); }
                    }
                }

                fields = found.ToArray();
                SerializedFields[typeof(T)] = fields;
            }

            foreach (FieldInfo field in fields) { field.SetValue(to, field.GetValue(from)); }
        }

        // ---- verification ----------------------------------------------------------------------

        // Rooms compared in the generation running now, and in all generations so far.
        private static int _verified, _differed;
        private static int _totalVerified, _totalDiffered;

        // One line per verified dungeon, at the end of its generation.
        [HarmonyPatch(typeof(DungeonGenerator), nameof(DungeonGenerator.Generate), typeof(int), typeof(ZoneSystem.SpawnMode))]
        internal static class GenerateHook {
            [HarmonyPrefix]
            private static void Prefix() {
                _verified = 0;
                _differed = 0;
            }

            [HarmonyFinalizer]
            private static void Finalizer(DungeonGenerator __instance) {
                if (_verified == 0) { return; }

                _totalVerified += _verified;
                _totalDiffered += _differed;
                Logger.LogInfo(
                    $"{FixName}: {__instance.name}: {_verified} room stand-in(s) verified against their models, " +
                    $"{_differed} differed ({_totalVerified} verified, {_totalDiffered} differed this session).");

                _verified = 0;
                _differed = 0;
            }
        }

        private static void Compare(string name, GameObject model, GameObject standIn) {
            _verified++;

            RoomConnection[] expected = model.GetComponent<Room>().GetConnections();
            RoomConnection[] actual = standIn.GetComponent<Room>().GetConnections();

            // PlaceRoom deactivates the room's networked objects before instantiating the model and
            // creates them itself. One still active in the model saved itself during the copy,
            // which a stand-in would not do.
            int networked = model.GetComponentsInChildren<ZNetView>(false).Length;

            string difference = null;
            if (networked > 0) {
                difference = $"the model created {networked} networked object(s) of its own";
            } else if (expected.Length != actual.Length) {
                difference = $"{expected.Length} connection(s) on the model, {actual.Length} on the stand-in";
            } else {
                for (int i = 0; i < expected.Length && difference == null; i++) {
                    RoomConnection a = expected[i], b = actual[i];
                    if (a.name != b.name || a.m_type != b.m_type || a.m_entrance != b.m_entrance ||
                        a.m_allowDoor != b.m_allowDoor || a.m_doorOnlyIfOtherAlsoAllowsDoor != b.m_doorOnlyIfOtherAlsoAllowsDoor) {
                        difference = $"connection {i} ('{a.name}') differs in name, type or door settings";
                    } else if (Vector3.Distance(a.transform.position, b.transform.position) > 0.001f ||
                               Quaternion.Angle(a.transform.rotation, b.transform.rotation) > 0.01f) {
                        difference = $"connection {i} ('{a.name}') is placed differently";
                    }
                }
            }

            if (difference != null) {
                _differed++;
                Logger.LogWarning($"{FixName}: the stand-in for room {name} differs from its model: {difference}.");
            }
        }
    }
}
