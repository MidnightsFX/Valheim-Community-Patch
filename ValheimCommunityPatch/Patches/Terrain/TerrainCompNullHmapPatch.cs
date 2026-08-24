using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Vanilla defect: TerrainComp.Awake gives up when it cannot find its heightmap, but Update carries
    // on regardless.
    //
    //   private void Awake() {
    //     this.m_nview = this.GetComponent<ZNetView>();
    //     this.m_hmap = Heightmap.FindHeightmap(this.transform.position);
    //     if (this.m_hmap == null) { ZLog.LogWarning("Terrain compiler could not find hmap"); }
    //     else { ...register RPC, Initialize(), CheckLoad()... }
    //   }
    //
    //   private void Update() { if (!this.m_nview.IsValid()) return; this.CheckLoad(); }
    //
    // When the heightmap is missing, Awake never runs Initialize, never registers the ApplyOperation
    // RPC, and never adds the compiler to s_instances. Update then calls CheckLoad every frame, which
    // dereferences the null m_modifiedHeight in Load and then the null m_hmap in Poke. The player sees
    // NullReferenceException spam, and that zone's terrain edits are permanently inert because nothing
    // can find or drive the compiler any more.
    //
    // This happens when a TerrainComp instantiates before the zone's heightmap has registered - a
    // race that gets more likely on a loaded server or a slow disk.
    //
    // Fix: skip the frame when we are not in a usable state, and retry the initialisation Awake
    // skipped once the heightmap does show up. Recovering the zone is the point; silencing the
    // exception alone would leave the terrain data just as dead.
    //
    // The recovery has to reproduce Awake's *whole* branch, deduplication included, and that is the
    // subtle part. Heightmap.GetAndCreateTerrainCompiler (Heightmap.cs:992) is vanilla's only guard
    // against a second compiler for one zone, and it searches s_instances - which a compiler that
    // lost the race is not in. So the next terrain op happily instantiates a second compiler, which
    // awakes normally and takes over. Unpatched, the stranded one stays invisible and inert forever:
    // wasteful, but harmless, because the survivor is the single source of truth.
    //
    // Recovering it without deduplicating would end that. Initialize() sets m_size, which is what
    // makes FindTerrainCompiler able to match a compiler at all, so the moment the stranded one is
    // recovered both are live for the same zone. Heightmap.ApplyModifiers (Heightmap.cs:372) resolves
    // through FindTerrainCompiler, which returns the *first* list match - so one compiler's saved
    // s_TCData silently wins and the other's terrain and paint are discarded. New edits route to the
    // same first match while the loser keeps diverging data that can win back after a zone reload,
    // since list order follows ZNetScene.CreateObject order and is not stable. That is terrain
    // flip-flopping, which is the exact failure the other terrain fixes here exist to remove.
    //
    // So TryRecover does what Awake does, in Awake's order: find the rival, destroy it, then
    // register. The alternative - abandon the recovery and leave the incumbent alone - was rejected
    // because it leaves this compiler's ZDO alive with stale data that can still win a later reload,
    // and re-checks on every frame forever. Vanilla's answer to two compilers in one zone is to
    // resolve it immediately, and a recovery that is indistinguishable from a clean Awake is the one
    // with no new behaviour to reason about.
    //
    // Both: TerrainComp.Update runs wherever the component exists, and the recovery re-registers the
    // ApplyOperation RPC, without which that zone can never accept or save an edit. Reachable on a
    // dedicated server for its own zones, and the race is more likely on a loaded one.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(TerrainComp))]
    internal static class TerrainCompNullHmapPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TerrainCompNullHmapPatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Compiler Init Race",
                true,
                "Recovers a terrain compiler that loaded before its zone's heightmap existed. In vanilla " +
                "it throws a NullReferenceException every frame from then on and that zone stops " +
                "accepting terrain edits entirely.");
        }

        // Compilers whose recovery threw. Without this the failure repeats on the next Update and
        // every Update after it, so one broken compiler writes a stack trace to the log sixty times
        // a second. A throw here means the state is not recoverable by retrying - a duplicate RPC
        // registration or a failed allocation - so the compiler is abandoned instead. It stays inert
        // rather than half-live: ApplyToHeightmap returns early on !m_initialized (TerrainComp.cs:216)
        // and FindTerrainCompiler cannot match m_size == 0, so nothing downstream can see it.
        private static readonly HashSet<TerrainComp> RecoveryFailed = new HashSet<TerrainComp>();

        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static bool UpdatePrefix(TerrainComp __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }
            if (__instance.m_hmap != null && __instance.m_initialized) { return true; }

            if (RecoveryFailed.Count > 0 && RecoveryFailed.Contains(__instance)) { return false; }

            if (!TryRecover(__instance)) { return false; }

            // Recovered this frame; CheckLoad already ran as part of re-initialisation.
            return false;
        }

        private static bool TryRecover(TerrainComp comp) {
            Heightmap hmap = comp.m_hmap != null
                ? comp.m_hmap
                : Heightmap.FindHeightmap(comp.transform.position);

            // Still no heightmap for this zone - nothing to do but stay quiet until one appears.
            if (hmap == null) { return false; }

            if (comp.m_nview == null || !comp.m_nview.IsValid()) { return false; }

            comp.m_hmap = hmap;

            try {
                // Mirrors the branch Awake skipped, in Awake's own order (TerrainComp.cs:41-50).
                //
                // Only when the compiler was never initialised, because that is exactly the case
                // where Awake's deduplication never ran. If m_initialized is already set, Awake did
                // dedupe and this is the far milder path of re-attaching a heightmap after the zone
                // reloaded - there is no rival to resolve and no RPC to register a second time.
                if (!comp.m_initialized) {
                    // Before Initialize, deliberately: FindTerrainCompiler matches on m_size, which
                    // is still 0 here, so it cannot return `comp` and can only return a real rival.
                    TerrainComp other = TerrainComp.FindTerrainCompiler(comp.transform.position);
                    if (other != null && other != comp && ZNetScene.instance != null) {
                        Logger.LogWarning(
                            $"Found another terrain compiler at {comp.transform.position}, removing it. " +
                            "Two compilers in one zone means one of their saved terrain edits would be " +
                            "discarded at random, so this resolves it the way TerrainComp.Awake does.");
                        ZNetScene.instance.Destroy(other.gameObject);
                    }

                    // s_instances is what FindTerrainCompiler and Heightmap.ApplyModifiers search, so
                    // without this the compiler stays invisible.
                    if (!TerrainComp.s_instances.Contains(comp)) { TerrainComp.s_instances.Add(comp); }

                    comp.m_nview.Register<ZPackage>(
                        "ApplyOperation", new Action<long, ZPackage>(comp.RPC_ApplyOperation));
                    comp.Initialize();
                } else if (!TerrainComp.s_instances.Contains(comp)) {
                    TerrainComp.s_instances.Add(comp);
                }

                comp.CheckLoad();
            } catch (Exception ex) {
                PruneFailed();
                RecoveryFailed.Add(comp);
                Logger.LogError(
                    $"Failed to recover terrain compiler at {comp.transform.position}, and it will not " +
                    $"be retried: {ex}");
                return false;
            }

            Logger.LogInfo($"Recovered terrain compiler at {comp.transform.position} after its heightmap loaded.");
            return true;
        }

        /// Drops compilers Unity has since destroyed, so the latch cannot grow across a session.
        /// Realistically this set holds nothing at all, so the sweep is only ever paid on the throw.
        private static void PruneFailed() {
            if (RecoveryFailed.Count == 0) { return; }

            RecoveryFailed.RemoveWhere(comp => comp == null);
        }
    }
}
