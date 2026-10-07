using BepInEx.Bootstrap;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// Azumatt's HearthBelow, which lets players dig into the ground.
    /// </summary>
    /// <remarks>
    /// A dug-out zone keeps its heightmap data at the surface before digging, switches the
    /// heightmap's collider off and adds HearthBelow's own collision meshes to the same GameObject.
    /// The game's terrain raycasts, and the methods HearthBelow patches, see the dug ground;
    /// heightmap data does not. Fixes that answer ground queries from heightmap data, or replace a
    /// method HearthBelow patches, stand down while it is loaded.
    /// </remarks>
    internal static class HearthBelowCompat {
        internal const string Guid = "Azumatt.HearthBelow";

        /// <summary>True when HearthBelow is loaded. The soft dependency makes it load before this mod.</summary>
        internal static bool Loaded => Chainloader.PluginInfos.ContainsKey(Guid);
    }
}
