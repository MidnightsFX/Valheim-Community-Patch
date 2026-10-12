using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Container Grid Alignment: standard chests (4 or fewer rows) open with their slots
    // centered instead of pinned to the top of the window after a tall container was opened.
    //
    // InventoryGrid.ResetView pins the container grid to the top of the window (pivot 1.0) when
    // the grid is taller than the window, centers it (0.5) otherwise, and zeroes its scroll
    // offset. UpdateContainer only calls ResetView while m_firstContainerUpdate is true, and only
    // CloseContainer (walking out of range with the chest still open) sets it back; Hide never
    // does. All containers share a single UI grid, so a chest opened after one that was closed
    // normally skips ResetView and keeps the previous container's pivot and scroll offset.
    //
    // A prefix on InventoryGui.Show sets m_firstContainerUpdate back to true when the container
    // has changed, so the grid recalculates its pivot. Reopening the same container still keeps
    // its scroll position. A postfix on InventoryGui.CloseContainer clears the tracked container
    // on auto-close.
    //
    // Client: InventoryGui is client-only. Provenance: contributed by Dreamwraith (PR #19).
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
                "Resets the container grid pivot when switching chests, so standard chests open " +
                "centered instead of keeping the top-aligned layout of a larger container.");
        }

        private static Container _lastContainer;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(InventoryGui.Show))]
        private static void ShowPrefix(InventoryGui __instance, Container container) {
            if (Enabled == null || !Enabled.Value) { return; }

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
