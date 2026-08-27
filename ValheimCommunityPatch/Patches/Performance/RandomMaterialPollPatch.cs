using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: every piece with material variation (RandomMaterialValues) schedules
    // itself five polls through Unity's string-based invoke machinery the moment it spawns -
    // InvokeRepeating("CheckMaterial", 0f, 0.2f) in Start, CancelInvoke(nameof(CheckMaterial))
    // after the fifth poll (RandomMaterialValues.cs:23-54). The polls exist only to wait for the
    // piece's random seed to arrive over the network; once it has (usually on the first poll),
    // the remaining polls re-derive and re-write the exact same deterministic values, allocating
    // a System.Random per property and re-hashing every shader property name through
    // Shader.PropertyToID each time. Streaming a large base runs this machinery for thousands
    // of pieces at once - measured at ~35 ms of every streaming second across the component,
    // its CancelInvoke calls and the MaterialMan writes.
    //
    // Fix: one central queue pumped from ZNetScene.Update replaces the per-piece invoke
    // scheduling, and the poll body is replicated with two value-preserving cuts:
    //  - polling STOPS once the values are applied (m_isSet), instead of re-applying identical
    //    values on the remaining polls. Identical is provable: the values are pure functions of
    //    the seed (System.Random(seed) per property), the seed is written once, and MaterialMan
    //    stores per-object keyed values that nothing clears between polls - so the re-writes
    //    vanilla performs are state-level no-ops bought at full price;
    //  - Shader.PropertyToID results are cached by name.
    // Everything else is verbatim: the same not-yet-seeded retry (five polls, 0.2 s apart,
    // first one immediate, then the piece gives up exactly like vanilla), the same owner-side
    // seed write - that one is real ZDO state and is preserved on the same schedule - the same
    // placement-ghost condition, the same m_checks bookkeeping on the component.
    //
    // A queued piece destroyed before finishing simply drops out on its next due poll (vanilla's
    // own alive-checks inside the body make a dead piece's poll a no-op anyway). The queue is
    // pumped unconditionally, so pieces routed into it stay serviced if the toggle is flipped
    // off mid-session, while new pieces revert to vanilla scheduling - both mechanisms coexist,
    // like WearCacheEventPatch. Start stands down to vanilla if the pump hook failed to attach:
    // a piece must never end up scheduled by neither.
    //
    // Both: a dedicated server runs the same polling for every piece it instantiates, including
    // the owner-side seed assignment.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(RandomMaterialValues))]
    internal static class RandomMaterialPollPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(RandomMaterialPollPatch),
                ValConfig.SectionPerformance,
                "Fix Piece Material Polling",
                true,
                "Runs the material-variation seed polling for newly spawned pieces through one " +
                "shared ticker instead of five string-based engine invokes per piece, and stops " +
                "polling a piece as soon as its values are applied instead of re-applying the " +
                "same values four more times. The variation itself is unchanged - the values " +
                "are derived from the same seed by the same math.");
        }

        // Vanilla's schedule: first poll immediately, then every 0.2 s, giving up after 5.
        private const float PollInterval = 0.2f;
        private const int MaxChecks = 5;

        private struct Entry {
            public RandomMaterialValues m_rmv;
            public float m_next;
        }

        private static readonly List<Entry> Queue = new List<Entry>();
        private static readonly Dictionary<string, int> PropertyIds = new Dictionary<string, int>();

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        // Vanilla's Start verbatim (RandomMaterialValues.cs:23-30) with the InvokeRepeating
        // replaced by a queue entry due immediately.
        [HarmonyPrefix]
        [HarmonyPatch("Start")]
        private static bool StartPrefix(RandomMaterialValues __instance) {
            if (Enabled == null || !Enabled.Value || !HooksHealthy()) { return true; }

            __instance.m_nview = __instance.GetComponentInParent<ZNetView>();
            __instance.m_piece = __instance.GetComponentInParent<Piece>();
            if (!(bool)(Object)__instance.m_nview) {
                ZLog.LogError($"Missing nview on '{__instance.transform.gameObject.name}'");
            }

            Queue.Add(new Entry { m_rmv = __instance, m_next = Time.time });
            return false;
        }

        // Vanilla's CheckMaterial verbatim (RandomMaterialValues.cs:32-54) minus the invoke
        // bookkeeping, plus the two cuts the header argues for. Returns true when this piece is
        // finished polling.
        private static bool Poll(RandomMaterialValues rmv) {
            if ((!rmv.m_isSet && rmv.m_randomSeed < 0
                    || rmv.m_isSet && (!(bool)(Object)rmv.m_piece || !Player.IsPlacementGhost(rmv.m_piece.gameObject)))
                && (bool)(Object)rmv.m_nview && rmv.m_nview.GetZDO() != null) {
                rmv.m_randomSeed = rmv.m_nview.GetZDO().GetInt(RandomMaterialValues.s_randSeedString, -1);
                if (rmv.m_randomSeed < 0 && rmv.m_nview.IsOwner()) {
                    rmv.m_nview.GetZDO().Set(RandomMaterialValues.s_randSeedString, Random.Range(0, 12345));
                }

                if (rmv.m_randomSeed >= 0) {
                    for (int index = 0; index < rmv.m_vectorProperties.Count; ++index) {
                        RandomMaterialValues.VectorVariationProperty property = rmv.m_vectorProperties[index];
                        foreach (string propertyName in property.m_propertyNames) {
                            MaterialMan.instance.SetValue<Vector4>(
                                rmv.gameObject, PropertyId(propertyName), property.GetValue(rmv.m_randomSeed + index));
                        }
                    }

                    rmv.m_isSet = true;
                }
            }

            ++rmv.m_checks;
            return rmv.m_isSet || rmv.m_checks >= MaxChecks;
        }

        private static int PropertyId(string name) {
            if (!PropertyIds.TryGetValue(name, out int id)) {
                id = Shader.PropertyToID(name);
                PropertyIds.Add(name, id);
            }

            return id;
        }

        [HarmonyPatch(typeof(ZNetScene))]
        internal static class PumpHook {
            [HarmonyPostfix]
            [HarmonyPatch("Update")]
            private static void Postfix() {
                if (Queue.Count == 0) { return; }

                float now = Time.time;
                for (int i = Queue.Count - 1; i >= 0; i--) {
                    Entry entry = Queue[i];
                    if (now < entry.m_next) { continue; }

                    // A swap-removed tail entry lands on an already-visited slot and polls next
                    // frame instead - a one-frame slip against a 0.2 s cadence.
                    if (entry.m_rmv == null || Poll(entry.m_rmv)) {
                        Queue[i] = Queue[Queue.Count - 1];
                        Queue.RemoveAt(Queue.Count - 1);
                    } else {
                        entry.m_next = now + PollInterval;
                        Queue[i] = entry;
                    }
                }
            }

            [HarmonyPostfix]
            [HarmonyPatch("Shutdown")]
            private static void ShutdownPostfix() => Queue.Clear();
        }

        // ---- hook health ---------------------------------------------------------------------

        /// A queued piece is polled ONLY by the pump, so Start must not route pieces into the
        /// queue unless it attached.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            _hooksHealthy = HasOurPostfix(AccessTools.DeclaredMethod(typeof(ZNetScene), "Update"), typeof(PumpHook));

            if (!_hooksHealthy) {
                Logger.LogError(
                    "Piece material polling: the pump hook is not attached, so pieces are " +
                    "polling through vanilla's invoke scheduling for this session. This usually " +
                    "means a Valheim update changed ZNetScene.Update - look for the patch " +
                    "failure logged at startup.");
            }

            return _hooksHealthy;
        }

        private static bool HasOurPostfix(MethodBase target, System.Type hookClass) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            foreach (Patch patch in info.Postfixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookClass) { continue; }
                return true;
            }

            return false;
        }
    }
}
