# Valheim Community Patch

Vanilla bug fixes and performance fixes. **No gameplay changes.**

This mod exists to fix things that are broken or pathologically slow in Valheim itself — crashes,
exceptions that silently kill a system, item loss, quadratic hot paths, per-frame allocations, log
spam. It deliberately ships no quality-of-life features, no balance changes, and no content, so you
can install it on a server without anyone having to agree about how the game should play.

Every fix has its own toggle in the config, so you can turn off any single one without a rebuild.
Toggles are admin-only and server-synced.

## Fixes in this release

### Performance

- **Cache Recipe Lookups** — `ObjectDB.GetRecipe` was an unindexed linear scan doing a string
  comparison per recipe. `InventoryGui` calls it for every worn item *every frame* the inventory is
  open at a crafting station, so standing at a workbench cost thousands of string comparisons per
  frame — tens of thousands once other mods add recipes. This is the workbench repair hitch. Now
  indexed by item name.
- **Fix Portal Connection Scan** *(server)* — the server re-pairs portals every five seconds by
  rescanning every portal for every unconnected portal. An unpaired portal never resolves, so that
  scan repeats forever and the cost grows with the square of how many portals the world has ever had.
  Now indexed by tag, with no per-tick allocation and no network round-trip.
- **Fix World Load Connection Scan** *(server)* — the three nested loops that pair portals, spawners
  and sync transforms (every ship and cart) with their targets during world load are now indexed. Each
  compared a hash one pair at a time, so the cost grew with the square of how many of each the world
  holds. On a long-lived world these were a multi-second stall on every server start, before anyone
  could join.
- **Fix Disconnect ZDO Sweep** *(server)* — every time a player disconnects, the server walks *every
  ZDO in the world* to find the handful of temporary objects that player left ownerless. On a large
  world that is millions of scattered heap reads to find a few dozen objects, so each logout is a
  main-thread freeze whose length grows with the size of the world. Non-persistent objects are now
  indexed by owner, so the sweep only looks at the departing player's own. An admin-only "Verify
  Orphan Index" toggle runs both the index and the old scan and reports any disagreement.
- **Fix Tar Pit Memory Leak** — tar pits (`LiquidVolume`) allocated their raycast buffers with
  Unity's 4-frame temporary allocator but held them for the object's entire lifetime, leaking native
  memory and spamming `JobTempAlloc has allocations that are more than 4 frames old` on every load.
  Now allocated persistently and disposed safely.
- **Fix Auto Pickup Allocation** — the auto-pickup check allocated a fresh array every frame for
  every player.
- **Speed Up Crafting Recipe List** — the crafting list throws away and re-creates every row in the
  list from scratch on each refresh, and a refresh happens every time you move an item in your
  inventory and after every craft — not just when the panel opens. With a lot of recipes available
  (a large content modpack, or `nocost`) that is a hard hitch every single time. Rows are now reused
  instead of destroyed, the per-row lookups and inventory counts are cached for the rebuild, and item
  names are translated once rather than once per sort comparison. Rows are also built quietly in the
  background during world load, so the first time you open the panel is fast too.
- **Cull Offscreen Recipe Rows** — every row in the crafting list stayed active in the UI canvas even
  when scrolled far out of view, so the whole list was re-batched whenever anything else on the
  screen changed. Rows outside the visible area are now deactivated.

### Terrain

- **Fix Terrain Seams** — Valheim shades each 64 m zone's terrain using only that zone's own
  triangles, so the ground on either side of a zone border is lit slightly differently. That shows up
  as a hard crease running through flat terrain, most obvious in the Plains and Meadows. Lighting
  normals are now computed across the boundary so both sides agree. Terrain *geometry* was never the
  problem — world generation already lines up exactly at zone borders.
- **Fix Terrain Paint Seams** — terrain paint could end up applied on only one side of a zone border,
  drawing a hard straight line of dirt across the ground along the 64 m grid. Boundary paint is now
  reconciled between neighbouring zones. It only ever removes paint from the boundary itself and
  never adds it, so ground you painted normally across a border is untouched.
- **Fix Terrain Paint Mask Indexing** — three places walk the terrain paint data with the wrong
  stride, skewing it diagonally, and refuse to write the row and column each zone shares with its
  neighbour.
- **Fix Terrain Compiler Init Race** — if a zone's terrain compiler loads before its heightmap
  exists, vanilla throws a `NullReferenceException` every frame from then on *and* that zone silently
  stops accepting terrain edits. It now recovers once the heightmap appears.

### Diagnostics

- **`vcp_terrainscan [radius]`** — read-only console command. Compares the shared boundary vertices
  of every adjacent loaded zone and reports height, lighting-normal and paint deltas separately, so a
  visible seam can be attributed to geometry, lighting or paint rather than guessed at. Changes
  nothing.
- **`vcp_recipebench [iterations] [vanilla]`** — read-only console command. Times the crafting list
  rebuild with the inventory open, broken out into requirement checks, sorting and row building, and
  reports how many rows were reused rather than created. Passing `vanilla` measures the unpatched
  path in the same session, so a before/after is compared against the same world, inventory and mod
  list rather than across two restarts. Changes nothing.

### Correctness

- **Fix Object Unload Crash** — `ZNetScene.RemoveObjects` dereferenced every instance's ZDO with no
  validity check. A single orphaned entry aborted the whole unload pass and then threw again every
  frame, so nothing despawned and memory climbed.
- **Tolerate Duplicate ZDOs On Load** — a save containing a duplicate ZDO id made the world refuse to
  load at all. It now recovers and logs a warning.
- **Fix Effect Areas** — two fixes. The fixed 128-collider buffer behind fire warmth and wetness
  checks silently truncated, so in a dense base the area that mattered could be missed entirely. And
  a character that died inside an effect area stayed in its list forever, throwing every physics step.
- **Fix Fuel And Ore Loss** — adding fuel or ore removes the item from your inventory and *then* sends
  a network message that is silently dropped if the owning player is lagging or has disconnected. The
  item is simply destroyed. Ownership is now taken first.
- **Share Boss Defeat Keys** — the per-player boss defeat key was recorded only on whichever client
  happened to own the boss, so in a group everyone else got no credit. Most visible with Hildir's
  quest bosses.
- **Fix Recipe Amount Crash** — `Recipe.GetAmount` dereferenced a null item, so opening the crafting
  or upgrade panel for a "requires any one of these" recipe while carrying none of the ingredients
  threw and left the panel broken.
- **Fix Spawner Null Prefabs** — a single null entry in a spawner's creature table (common after a
  game update or a removed creature mod) threw during spawn selection and permanently killed that
  spawner. Null entries are now dropped on load.
- **Require Lit Fire** — the burning-area check tested only a fireplace's bounds, never whether it
  was lit, so an unlit or burnt-out fire still counted as a heat source and kept cooking.
- **Fix Run Attack Stamina Drain** — holding sprint while attacking stopped your movement but kept
  charging run stamina for the whole swing.
- **Fix Projectile Rotation Spam** — every projectile whose velocity reached exactly zero made Unity
  log `Look rotation viewing vector is zero` on every physics step.

## Installation

Install with a mod manager, or drop `ValheimCommunityPatch.dll` into `BepInEx/plugins`.

Requires [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) and
[Jotunn](https://valheim.thunderstore.io/package/ValheimModding/Jotunn/).

This mod must be installed on the server **and** on every client.

## Reporting a bug

Issues go to [GitHub](https://github.com/MidnightsFX/Valheim-Community-Patch). Please include your
`LogOutput.log` and the list of other mods you are running.

To decide whether a fix belongs here, the test is: *is the current behaviour something Iron Gate
would call a bug?* If it is a matter of taste, it belongs in a different mod.
