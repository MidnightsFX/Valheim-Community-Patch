using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using SoftReferenceableAssets;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Location Model Placeholders: a location's or dungeon room's model is built from a copy of
    // its prefab without the networked objects it never uses, instead of from the whole prefab.
    //
    // A location or room model is the prefab's non-networked part: walls, rocks, decoration. Its
    // networked objects (chests, trees, creatures, ore) are created from the world's saved data,
    // so before cloning the model the game switches them off in the prefab and they are cloned
    // switched off, staying in the model as dead copies for as long as it is loaded. In the Deep
    // North they are most of the model: The Hole's is 33,587 objects of which 197 are its model,
    // the north village houses 99% dead copies, the halls two thirds. Cloning them, and destroying
    // them again on unload, is most of the frame a model arrives in.
    //
    // A transpiler on ZoneSystem.SpawnLocation, and Fix Background Dungeon Generation's room model
    // call in DungeonGenerator.PlaceRoom, build models through this fix. Once a prefab has been
    // used twice and has enough dead copies to be worth it, a template is cloned from it,
    // switched off under a hidden holder between frames, and its networked objects destroyed a
    // few at a time. A networked object something in the kept part refers to (a random-object
    // choice, a level-of-detail renderer, the location's dungeon generator) stays as a switched-
    // off placeholder with nothing under it, so every reference survives as in the game's model.
    // Each model is then cloned from the template, after copying onto it the parts of the prefab
    // the game changes just before cloning: what the random-spawn and random-object components
    // switched on or off, a custom interior's position, and the location's cached biome.
    //
    // A template holds a reference to its prefab, so a dungeon's room templates survive between
    // visits instead of being rebuilt each time its rooms unload, and is dropped after five
    // minutes unused and on logout. A reference is released only during play, or, for one still
    // held at logout, once the next world starts loading; never while the game shuts down, where
    // releasing starts an asynchronous asset bundle unload during the game's own quit, which
    // crashed it. Until a template is ready, and when another mod hooks either method, the game's
    // own clone is used. 'Verify Location Model Templates' builds both, uses the game's, and logs any
    // difference, plus, once a template is ready, any reference left pointing at a destroyed object.
    //
    // Client: models are built where a player sees them.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(ZoneSystem), "SpawnLocation")]
    internal static class ModelTemplatePatch {
        private const string FixName = "Fix Location Model Placeholders";

        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Location Model Templates",
                false,
                "Diagnostic. Builds every location and dungeon room model both the game's way and from " +
                "this fix's template, uses the game's, and logs any difference, plus, when a template is " +
                "built, any reference to the networked objects it leaves out. Costs the clone this fix " +
                "exists to avoid, so leave it off unless you are validating the templates.",
                advanced: true);
        }

        // A template is worth building for a prefab used at least this often, whose dead copies
        // are at least this many objects and this share of the model.
        private const int MinUses = 2;
        private const int MinDeadObjects = 200;
        private const float MinDeadShare = 0.25f;

        private const float IdleSeconds = 300f;
        private const int MaxTemplates = 64;
        private const double StripBudgetMs = 2.0;
        private const float QuietFrameSeconds = 1f / 30f;

        private static readonly TakeoverCheck LocationTakeover = new TakeoverCheck(
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnLocation"),
            HookKinds.BoolPrefixes | HookKinds.Postfixes | HookKinds.Transpilers,
            owners => $"Location spawning is hooked by {owners}, which may read the model, so '{FixName}' " +
                      "stands down for locations and the game clones them whole.");

        private static readonly TakeoverCheck RoomTakeover = new TakeoverCheck(
            AccessTools.Method(typeof(DungeonGenerator), "PlaceRoom",
                new[] { typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode) }),
            HookKinds.Any,
            owners => $"Dungeon room placement is hooked by {owners}, which may read the room models, so " +
                      $"'{FixName}' stands down for rooms and the game clones them whole.");

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(
                instructions,
                AccessTools.Method(typeof(SoftReferenceableAssets.Utils), nameof(SoftReferenceableAssets.Utils.Instantiate),
                    new[] { typeof(SoftReference<GameObject>), typeof(Vector3), typeof(Quaternion) }),
                AccessTools.DeclaredMethod(typeof(ModelTemplatePatch), nameof(LocationModel)),
                $"{FixName} (ZoneSystem.SpawnLocation)", expected: 1);

        private static GameObject LocationModel(SoftReference<GameObject> prefab, Vector3 position, Quaternion rotation) =>
            Model(prefab, position, rotation, null, LocationTakeover);

        /// <summary>A dungeon room's model, as SoftReferenceableAssets.Utils.Instantiate builds it.</summary>
        internal static GameObject RoomModel(SoftReference<GameObject> prefab, Vector3 position, Quaternion rotation, Transform parent) =>
            Model(prefab, position, rotation, parent, RoomTakeover);

        private static GameObject Vanilla(SoftReference<GameObject> prefab, Vector3 position, Quaternion rotation, Transform parent) =>
            parent == null
                ? SoftReferenceableAssets.Utils.Instantiate(prefab, position, rotation)
                : SoftReferenceableAssets.Utils.Instantiate(prefab, position, rotation, parent);

        private static GameObject Model(SoftReference<GameObject> prefab, Vector3 position, Quaternion rotation, Transform parent, TakeoverCheck takeover) {
            // The callers loaded the prefab just before.
            GameObject asset = prefab.IsLoaded ? prefab.Asset : null;
            if (asset == null || takeover.TakenOver) { return Vanilla(prefab, position, rotation, parent); }

            // Use keeps a template only for the very prefab object it was made from, so a template
            // whose prefab unloaded and loaded again is never cloned.
            Template template = Use(asset, prefab);
            if (!template.Ready || template.Root == null) { return Vanilla(prefab, position, rotation, parent); }

            if (Verify != null && Verify.Value) { return VerifiedVanilla(template, prefab, position, rotation, parent); }

            return CloneTemplate(template, prefab, position, rotation, parent);
        }

        // ---- templates ---------------------------------------------------------------------------

        internal sealed class Template {
            internal GameObject Asset;
            internal SoftReference<GameObject> Prefab;
            internal int Uses;
            internal float LastUsed;

            internal bool Analyzed, Worth;
            internal int TotalObjects, DeadObjects;

            internal GameObject Root;
            internal bool Held, Ready;
            internal List<GameObject> ToStrip;
            internal int StripNext;

            // Prefab side: the networked objects left out, and those of them kept as placeholders.
            internal HashSet<Transform> DeadInAsset, PlaceholdersInAsset;
            internal int Placeholders;

            // For the debug line: the clone and the planning, in Stopwatch ticks, and strip frames.
            internal long CloneTicks, PlanTicks;
            internal int StripFrames;

            // What the game changes on the prefab just before cloning it, prefab side and template side.
            internal GameObject[] ToggleSources, ToggleTargets;
            internal Transform InteriorSource, InteriorTarget;
            internal Location LocationSource, LocationTarget;
        }

        private static readonly Dictionary<int, Template> Templates = new Dictionary<int, Template>();
        private static Template _building;
        private static GameObject _holder;
        private static float _nextSweep;

        private static Template Use(GameObject asset, SoftReference<GameObject> prefab) {
            int id = asset.GetInstanceID();
            if (!Templates.TryGetValue(id, out Template template) || template.Asset != asset) {
                template = new Template { Asset = asset, Prefab = prefab };
                Templates[id] = template;
            }

            template.Uses++;
            template.LastUsed = Time.unscaledTime;
            return template;
        }

        // Internal so 'Log World Generation Timings' can time it.
        internal static GameObject CloneTemplate(Template template, SoftReference<GameObject> prefab, Vector3 position, Quaternion rotation, Transform parent) {
            // The reference the game's clone takes, handed to the model as it does.
            if (prefab.Load() != LoadResult.Succeeded) {
                prefab.Release();
                return Vanilla(prefab, position, rotation, parent);
            }

            Mirror(template);

            GameObject model = parent == null
                ? Object.Instantiate(template.Root, position, rotation)
                : Object.Instantiate(template.Root, position, rotation, parent);
            model.TransferReference(prefab);
            return model;
        }

        // The template is switched off under the holder, so none of this wakes anything.
        private static void Mirror(Template template) {
            for (int i = 0; i < template.ToggleSources.Length; i++) {
                bool active = template.ToggleSources[i].activeSelf;
                if (template.ToggleTargets[i].activeSelf != active) { template.ToggleTargets[i].SetActive(active); }
            }

            if (template.InteriorTarget != null) {
                template.InteriorTarget.localPosition = template.InteriorSource.localPosition;
                template.InteriorTarget.localRotation = template.InteriorSource.localRotation;
            }

            if (template.LocationTarget != null) { template.LocationTarget.m_biome = template.LocationSource.m_biome; }
        }

        // ---- building, between frames -------------------------------------------------------------

        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate")]
        internal static class DriverHook {
            [HarmonyPostfix]
            private static void Postfix() {
                if (Templates.Count == 0) { return; }

                if (_building != null) {
                    Strip(_building);
                    return;
                }

                float now = Time.unscaledTime;
                if (now >= _nextSweep) {
                    _nextSweep = now + 10f;
                    Sweep(now);
                }

                // A loading screen hides the clone; otherwise only after a frame with room to spare.
                if (!RunMode.InLoadingScreen() && Time.unscaledDeltaTime > QuietFrameSeconds) { return; }

                // One piece of work per frame. Chosen first, since building may evict a template.
                Template next = null;
                foreach (Template template in Templates.Values) {
                    if (template.Uses < MinUses || template.Root != null || (template.Analyzed && !template.Worth)) { continue; }

                    // Not while the prefab is unloading or gone: cloning it then is not safe.
                    if (template.Asset == null || !template.Prefab.IsLoaded) { continue; }

                    next = template;
                    break;
                }

                if (next == null) { return; }

                if (!next.Analyzed) {
                    Analyze(next);
                } else {
                    Build(next);
                }
            }
        }

        // How much of the prefab is networked objects, counted on the prefab without cloning it.
        private static void Analyze(Template template) {
            template.Analyzed = true;

            Transform root = template.Asset.transform;
            template.TotalObjects = template.Asset.GetComponentsInChildren<Transform>(true).Length;

            foreach (ZNetView view in template.Asset.GetComponentsInChildren<ZNetView>(true)) {
                if (view.transform != root && IsOutermostNetworked(view.transform, root)) {
                    template.DeadObjects += view.GetComponentsInChildren<Transform>(true).Length;
                }
            }

            template.Worth = template.DeadObjects >= MinDeadObjects && template.DeadObjects >= template.TotalObjects * MinDeadShare;
        }

        private static bool IsOutermostNetworked(Transform transform, Transform root) {
            for (Transform parent = transform.parent; parent != null && parent != root; parent = parent.parent) {
                if (parent.GetComponent<ZNetView>() != null) { return false; }
            }

            return true;
        }

        private static void Build(Template template) {
            EvictIfFull();

            if (_holder == null) {
                _holder = new GameObject("VCP location model templates");
                _holder.SetActive(false);
                Object.DontDestroyOnLoad(_holder);
            }

            template.Prefab.HoldReference();
            template.Held = true;

            long start = Stopwatch.GetTimestamp();
            template.Root = Object.Instantiate(template.Asset, _holder.transform);
            template.CloneTicks = Stopwatch.GetTimestamp() - start;
            template.Root.name = template.Asset.name;

            // The prefab and its copy have the same shape, so they are walked side by side. Every
            // object with a networked component, other than the root, is cloned switched off by the
            // game, or under a parent the random spawns switched off, and so never runs.
            Dictionary<Transform, Transform> kept = new Dictionary<Transform, Transform>();
            Dictionary<Transform, GameObject> deadRoots = new Dictionary<Transform, GameObject>();
            Walk(template.Asset.transform, template.Root.transform, kept, deadRoots);
            PlanStrip(template, deadRoots);

            List<GameObject> sources = new List<GameObject>();
            List<GameObject> targets = new List<GameObject>();
            HashSet<GameObject> seen = new HashSet<GameObject>();

            void Toggle(GameObject source) {
                if (source == null || !seen.Add(source) || !kept.TryGetValue(source.transform, out Transform target)) { return; }

                sources.Add(source);
                targets.Add(target.gameObject);
            }

            foreach (RandomSpawn spawn in template.Asset.GetComponentsInChildren<RandomSpawn>(true)) {
                Toggle(spawn.gameObject);
                Toggle(spawn.m_OffObject);
            }

            foreach (RandomObject random in template.Asset.GetComponentsInChildren<RandomObject>(true)) {
                Toggle(random.gameObject);
                if (random.m_objects == null) { continue; }

                foreach (RandomObject.ObjectEntry entry in random.m_objects) { Toggle(entry?.m_object); }
            }

            template.ToggleSources = sources.ToArray();
            template.ToggleTargets = targets.ToArray();

            Location location = template.Asset.GetComponent<Location>();
            if (location != null) {
                template.LocationSource = location;
                template.LocationTarget = template.Root.GetComponent<Location>();

                if (location.m_interiorTransform != null && kept.TryGetValue(location.m_interiorTransform, out Transform interior)) {
                    template.InteriorSource = location.m_interiorTransform;
                    template.InteriorTarget = interior;
                }
            }

            template.PlanTicks = Stopwatch.GetTimestamp() - start - template.CloneTicks;
            template.StripNext = 0;
            template.StripFrames = 0;
            _building = template;
        }

        private static void Walk(Transform source, Transform target, Dictionary<Transform, Transform> kept, Dictionary<Transform, GameObject> deadRoots) {
            kept[source] = target;

            for (int i = 0; i < source.childCount; i++) {
                Transform sourceChild = source.GetChild(i);
                Transform targetChild = target.GetChild(i);

                if (sourceChild.GetComponent<ZNetView>() != null) {
                    deadRoots[sourceChild] = targetChild.gameObject;
                    continue;
                }

                Walk(sourceChild, targetChild, kept, deadRoots);
            }
        }

        // Decides what goes. A networked object the kept part refers to stays, switched off, with
        // the chain down to any referenced object under it; the rest of its copy goes, and so does
        // every networked object nothing refers to.
        private static void PlanStrip(Template template, Dictionary<Transform, GameObject> deadRoots) {
            HashSet<GameObject> dead = new HashSet<GameObject>();
            foreach (GameObject root in deadRoots.Values) {
                foreach (Transform transform in root.GetComponentsInChildren<Transform>(true)) { dead.Add(transform.gameObject); }
            }

            HashSet<GameObject> keep = new HashSet<GameObject>();
            ScanReferences(template.Root, dead, (owner, field, target, go) => {
                for (Transform t = go != null && dead.Contains(go) ? go.transform : null; t != null && dead.Contains(t.gameObject); t = t.parent) {
                    if (!keep.Add(t.gameObject)) { break; }
                }
            });

            template.ToStrip = new List<GameObject>();
            template.DeadInAsset = new HashSet<Transform>(deadRoots.Keys);
            template.PlaceholdersInAsset = new HashSet<Transform>();
            template.Placeholders = keep.Count;

            foreach (KeyValuePair<Transform, GameObject> pair in deadRoots) {
                if (!keep.Contains(pair.Value)) {
                    template.ToStrip.Add(pair.Value);
                    continue;
                }

                // The game clones it switched off; kept that way, it can never wake in a model.
                template.PlaceholdersInAsset.Add(pair.Key);
                pair.Value.SetActive(false);
                CollectStrip(pair.Value.transform, keep, template.ToStrip);
            }
        }

        private static void CollectStrip(Transform placeholder, HashSet<GameObject> keep, List<GameObject> toStrip) {
            for (int i = 0; i < placeholder.childCount; i++) {
                Transform child = placeholder.GetChild(i);
                if (keep.Contains(child.gameObject)) {
                    CollectStrip(child, keep, toStrip);
                } else {
                    toStrip.Add(child.gameObject);
                }
            }
        }

        // The dead copies go a few at a time; never awoken, they run no OnDestroy.
        private static void Strip(Template template) {
            if (template.Asset == null) {
                Drop(template);
                return;
            }

            long budget = (long)(StripBudgetMs * Stopwatch.Frequency / 1000.0);
            long start = Stopwatch.GetTimestamp();
            template.StripFrames++;

            while (template.StripNext < template.ToStrip.Count && Stopwatch.GetTimestamp() - start < budget) {
                GameObject dead = template.ToStrip[template.StripNext++];
                if (dead != null) { Object.DestroyImmediate(dead); }
            }

            if (template.StripNext < template.ToStrip.Count) { return; }

            template.ToStrip = null;
            template.Ready = true;
            _building = null;

            Logger.LogDebug(
                $"{FixName}: template for {template.Asset.name} ready, {template.TotalObjects - template.DeadObjects + template.Placeholders} " +
                $"object(s) per model instead of {template.TotalObjects}, {template.Placeholders} of them networked placeholders " +
                $"kept because the model refers to them. Built with a {template.CloneTicks * 1000.0 / Stopwatch.Frequency:0.0} ms " +
                $"clone and {template.PlanTicks * 1000.0 / Stopwatch.Frequency:0.0} ms of planning, stripped over {template.StripFrames} frame(s).");

            if (Verify != null && Verify.Value) { ReportDangling(template); }
        }

        private static void EvictIfFull() {
            int built = 0;
            Template oldest = null;
            foreach (Template template in Templates.Values) {
                if (template.Root == null) { continue; }

                built++;
                if (oldest == null || template.LastUsed < oldest.LastUsed) { oldest = template; }
            }

            if (built >= MaxTemplates && oldest != null) { Drop(oldest); }
        }

        private static readonly List<Template> Expired = new List<Template>();

        private static void Sweep(float now) {
            foreach (Template template in Templates.Values) {
                if (template != _building && (template.Asset == null || now - template.LastUsed > IdleSeconds)) { Expired.Add(template); }
            }

            foreach (Template template in Expired) { Drop(template); }
            Expired.Clear();
        }

        // References held at logout, released once the next world starts loading. Never released
        // on the way out of the game.
        private static readonly List<SoftReference<GameObject>> ReleaseLater = new List<SoftReference<GameObject>>();

        private static void Drop(Template template, bool release = true) {
            if (_building == template) { _building = null; }
            if (template.Root != null) { Object.DestroyImmediate(template.Root); }

            if (template.Held) {
                if (release) {
                    template.Prefab.Release();
                } else {
                    ReleaseLater.Add(template.Prefab);
                }
            }

            template.Root = null;
            template.Held = false;
            template.Ready = false;

            foreach (KeyValuePair<int, Template> pair in Templates) {
                if (!ReferenceEquals(pair.Value, template)) { continue; }

                Templates.Remove(pair.Key);
                break;
            }
        }

        // Runs as the game shuts down, and so as it quits: the templates go, their references stay.
        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                foreach (Template template in new List<Template>(Templates.Values)) { Drop(template, release: false); }
                Templates.Clear();
                _building = null;
            }
        }

        // The next world is loading, behind its loading screen.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        internal static class NextWorldHook {
            [HarmonyPostfix]
            private static void Postfix() {
                foreach (SoftReference<GameObject> prefab in ReleaseLater) { prefab.Release(); }
                ReleaseLater.Clear();
            }
        }

        // ---- verification --------------------------------------------------------------------------

        private const int MaxDifferencesLogged = 5;

        private static GameObject VerifiedVanilla(Template template, SoftReference<GameObject> prefab, Vector3 position, Quaternion rotation, Transform parent) {
            Mirror(template);

            // Cloned under the switched-off holder, so it wakes nothing, then compared and dropped.
            GameObject check = Object.Instantiate(template.Root, _holder.transform);
            GameObject model = Vanilla(prefab, position, rotation, parent);

            if (model != null) {
                List<string> differences = new List<string>();
                Compare(template, template.Asset.transform, model.transform, check.transform, root: true, differences);

                if (differences.Count == 0) {
                    Logger.LogInfo($"{FixName}: {template.Asset.name}: template model verified against the game's, no differences.");
                } else {
                    Logger.LogWarning(
                        $"{FixName}: {template.Asset.name}: template model differs from the game's: " +
                        string.Join("; ", differences) + (differences.Count >= MaxDifferencesLogged ? "; ..." : "") + ".");
                }
            }

            Object.DestroyImmediate(check);
            return model;
        }

        // Children are matched through the prefab, whose shape the game's model shares; scripts
        // waking in the game's model may append children of their own, which are not compared.
        private static void Compare(Template template, Transform asset, Transform model, Transform check, bool root, List<string> differences) {
            if (differences.Count >= MaxDifferencesLogged) { return; }

            string path = asset.name;
            if (model.gameObject.activeSelf != check.gameObject.activeSelf) {
                differences.Add($"'{path}' is {(model.gameObject.activeSelf ? "on" : "off")} in the game's model");
            }

            if (!root && (Vector3.Distance(model.localPosition, check.localPosition) > 0.001f ||
                          Quaternion.Angle(model.localRotation, check.localRotation) > 0.01f)) {
                differences.Add($"'{path}' is placed differently");
            }

            if (Vector3.Distance(model.localScale, check.localScale) > 0.001f) { differences.Add($"'{path}' is scaled differently"); }

            Component[] modelComponents = model.GetComponents<Component>();
            Component[] checkComponents = check.GetComponents<Component>();
            for (int i = 0; i < checkComponents.Length; i++) {
                if (i >= modelComponents.Length || modelComponents[i] == null || checkComponents[i] == null ||
                    modelComponents[i].GetType() != checkComponents[i].GetType()) {
                    differences.Add($"'{path}' has different components");
                    break;
                }

                if (modelComponents[i] is Behaviour a && checkComponents[i] is Behaviour b && a.enabled != b.enabled) {
                    differences.Add($"'{path}' {a.GetType().Name} is {(a.enabled ? "enabled" : "disabled")} in the game's model");
                }
            }

            int k = 0;
            for (int j = 0; j < asset.childCount && differences.Count < MaxDifferencesLogged; j++) {
                Transform assetChild = asset.GetChild(j);
                Transform modelChild = j < model.childCount ? model.GetChild(j) : null;
                if (modelChild == null) {
                    differences.Add($"'{path}' has fewer children in the game's model");
                    return;
                }

                if (template.DeadInAsset.Contains(assetChild)) {
                    if (modelChild.gameObject.activeInHierarchy) {
                        differences.Add($"networked object '{assetChild.name}' is on in the game's model, so its copy is not dead");
                    }

                    // Kept as a switched-off placeholder: present in the template, dead in both.
                    if (template.PlaceholdersInAsset.Contains(assetChild)) {
                        if (k < check.childCount && check.GetChild(k).gameObject.activeSelf) {
                            differences.Add($"placeholder '{assetChild.name}' is on in the template");
                        }

                        k++;
                    }

                    continue;
                }

                if (k >= check.childCount) {
                    differences.Add($"'{path}' has fewer children in the template");
                    return;
                }

                Compare(template, assetChild, modelChild, check.GetChild(k++), root: false, differences);
            }
        }

        // After stripping, a reference from the live part of the template to a destroyed object
        // would be empty in every model built from it, where the game's model has the object.
        private static void ReportDangling(Template template) {
            int reported = 0;
            ScanReferences(template.Root, null, (owner, field, target, go) => {
                if (go != null || ReferenceEquals(target, null) || reported++ >= 10) { return; }

                Logger.LogWarning(
                    $"{FixName}: {template.Asset.name}: {owner.GetType().Name} on '{owner.name}' refers through {field} " +
                    "to an object the template left out.");
            });

            if (reported == 0) {
                Logger.LogInfo(
                    $"{FixName}: {template.Asset.name}: template ready, every reference in it intact " +
                    $"({template.Placeholders} networked placeholder(s) kept for them).");
            }
        }

        // Every object reference in the serialized fields of the template's live components, and in
        // its LOD groups, with the GameObject it points at (null when that object is destroyed).
        // Components inside networked objects are skipped: switched off for good, they never run.
        private static void ScanReferences(GameObject root, HashSet<GameObject> skip, Action<Component, string, Object, GameObject> found) {
            Transform top = root.transform;

            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true)) {
                if (behaviour == null || IsSkipped(behaviour.transform, top, skip)) { continue; }

                ScanFields(behaviour, behaviour, behaviour.GetType(), 0, found);
            }

            foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true)) {
                if (IsSkipped(group.transform, top, skip)) { continue; }

                foreach (LOD lod in group.GetLODs()) {
                    foreach (Renderer renderer in lod.renderers) {
                        if (!ReferenceEquals(renderer, null)) { found(group, "its LODs", renderer, renderer != null ? renderer.gameObject : null); }
                    }
                }
            }
        }

        private static bool IsSkipped(Transform transform, Transform top, HashSet<GameObject> skip) {
            if (skip != null) { return skip.Contains(transform.gameObject); }

            for (Transform t = transform; t != null && t != top; t = t.parent) {
                if (t.GetComponent<ZNetView>() != null) { return true; }
            }

            return false;
        }

        private static void ScanFields(Component owner, object instance, Type type, int depth, Action<Component, string, Object, GameObject> found) {
            for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType) {
                foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                    bool serialized = field.IsPublic ? !field.IsNotSerialized : field.IsDefined(typeof(SerializeField), false);
                    if (serialized) { ScanValue(owner, field.Name, field.GetValue(instance), depth, found); }
                }
            }
        }

        private static void ScanValue(Component owner, string field, object value, int depth, Action<Component, string, Object, GameObject> found) {
            switch (value) {
                case null:
                    return;
                case Object target:
                    // A destroyed object still has its managed wrapper, which compares equal to null.
                    GameObject go = target == null ? null : target is GameObject g ? g : target is Component c ? c.gameObject : null;
                    if (go != null || target == null) { found(owner, field, target, go); }
                    return;
                case string _:
                    return;
                case IList list:
                    foreach (object item in list) { ScanValue(owner, field, item, depth, found); }
                    return;
            }

            Type type = value.GetType();
            if (depth < 3 && !type.IsPrimitive && !type.IsEnum && type.IsDefined(typeof(SerializableAttribute), false)) {
                ScanFields(owner, value, type, depth + 1, found);
            }
        }
    }
}
