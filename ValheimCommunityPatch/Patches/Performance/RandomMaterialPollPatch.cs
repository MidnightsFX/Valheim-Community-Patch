using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Piece Material Polling: RandomMaterialValues polls five times by string invoke, rewriting the same values.
    // Prefixes Start to queue the piece on a ZNetScene.Update pump that stops polling once the values are set.
    // Pieces with material variation load with one shared ticker and no repeated allocations.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(RandomMaterialValues))]
    internal static class RandomMaterialPollPatch {
        // Vanilla's schedule: first poll immediately, then every 0.2 s, giving up after 5.
        private const float PollInterval = 0.2f;
        private const int MaxChecks = 5;

        private struct Entry {
            public RandomMaterialValues m_rmv;
            public float m_next;
        }

        private static readonly List<Entry> Queue = new List<Entry>();
        private static readonly Dictionary<string, int> PropertyIds = new Dictionary<string, int>();

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(RandomMaterialPollPatch));

        // A queued piece is polled only by the pump, so Start must not queue unless it attached.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(RandomMaterialPollPatch),
            "Piece material polling",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZNetScene), "Update"), typeof(PumpHook)));

        // Vanilla's Start with the InvokeRepeating replaced by a queue entry due immediately.
        [HarmonyPrefix]
        [HarmonyPatch("Start")]
        private static bool StartPrefix(RandomMaterialValues __instance) {
            // Turned off, the pump still finishes the pieces already queued.
            if (ApiSwitch.Off || !Hooks.Healthy) { return true; }

            __instance.m_nview = __instance.GetComponentInParent<ZNetView>();
            __instance.m_piece = __instance.GetComponentInParent<Piece>();
            if (!(bool)(Object)__instance.m_nview) {
                ZLog.LogError($"Missing nview on '{__instance.transform.gameObject.name}'");
            }

            Queue.Add(new Entry { m_rmv = __instance, m_next = Time.time });
            return false;
        }

        // Vanilla's CheckMaterial minus the invoke bookkeeping. Returns true when this piece is
        // finished polling. Stopping once m_isSet changes nothing: the values are a pure function of
        // the seed, which is written once, so vanilla's later polls rewrite the same values.
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

                    // Swap-remove; a tail entry landing on a visited slot polls next frame instead.
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
    }
}
