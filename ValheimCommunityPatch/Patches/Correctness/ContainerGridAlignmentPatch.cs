using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Container Grid Alignment: standard chests (4 or fewer rows) open with their slots
    // centered instead of pinned to the top of the window after a tall container was opened.
    //
    // InventoryGrid.ResetView sets the container grid's vertical pivot to 1.0 (top-aligned) for
    // containers with 5+ rows and 0.5 (centered) for 4 or fewer. UpdateContainer only calls
    // ResetView when m_firstContainerUpdate is true.
    //
    // When the player switches containers without moving away (> 4 m auto-close distance),
    // InventoryGui.Hide leaves m_firstContainerUpdate false so the scroll position is kept for
    // the active container. All containers share a single UI grid, so opening a different one
    // skips ResetView and the new container keeps the previous container's top-aligned pivot.
    //
    // A prefix on InventoryGui.Show sets m_firstContainerUpdate back to true when the container
    // has changed, so the grid recalculates its pivot. Reopening the same container still keeps
    // its scroll position. A postfix on InventoryGui.CloseContainer clears the tracked container
    // on auto-close.
    //
    // Client: InventoryGui is client-only.
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
