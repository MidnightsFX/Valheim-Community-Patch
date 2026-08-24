using System;
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

        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static bool UpdatePrefix(TerrainComp __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }
            if (__instance.m_hmap != null && __instance.m_initialized) { return true; }

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
                // Mirrors the branch Awake skipped. s_instances is what FindTerrainCompiler and
                // Heightmap.ApplyModifiers search, so without this the compiler stays invisible.
                if (!TerrainComp.s_instances.Contains(comp)) { TerrainComp.s_instances.Add(comp); }

                if (!comp.m_initialized) {
                    comp.m_nview.Register<ZPackage>(
                        "ApplyOperation", new Action<long, ZPackage>(comp.RPC_ApplyOperation));
                    comp.Initialize();
                }

                comp.CheckLoad();
            } catch (Exception ex) {
                Logger.LogError($"Failed to recover terrain compiler at {comp.transform.position}: {ex}");
                return false;
            }

            Logger.LogInfo($"Recovered terrain compiler at {comp.transform.position} after its heightmap loaded.");
            return true;
        }
    }
}
