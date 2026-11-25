using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // All chests share one grid, reset only after an auto-close, so a chest opens with the last chest's layout.
    // Prefixes InventoryGui.Show to set m_firstContainerUpdate when the chest changes, so the grid runs ResetView.
    // A standard chest opened after a taller one is centered instead of top-aligned and scrolled.
    // Provenance: contributed by Dreamwraith (PR #19).
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(InventoryGui))]
    internal static class ContainerGridAlignmentPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ContainerGridAlignmentPatch),
                ValConfig.SectionCorrectness,
                "Fix Container Grid Alignment",
                true,
                "Resets the chest grid's layout when you open a different chest, so a standard chest opens " +
                "centered after a larger one.");
        }

        private static Container _lastContainer;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(InventoryGui.Show))]
        private static void ShowPrefix(InventoryGui __instance, Container container) {
            if (Enabled == null || !Enabled.Value) { return; }

            // Only on a change of chest, so reopening the same one keeps its scroll position.
            if (container != null && container != _lastContainer) {
                _lastContainer = container;
                __instance.m_firstContainerUpdate = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("CloseContainer")]
        private static void CloseContainerPostfix() {
            _lastContainer = null;
        }
    }
}
