# Changelog

## 0.3.0

Terrain seams. Each entry names the vanilla method it fixes.

Note on what is *not* here: the base heightfield is seam-consistent by construction.
`HeightmapBuilder.Build` blends the four corner-biome heights with `SmoothStep`-warped coordinates,
and since `SmoothStep(0,1,0)==0`, `SmoothStep(0,1,1)==1`, and adjacent zones sample
`WorldGenerator.GetBiome` at the same world positions for their shared corners, both zones compute an
identical height at every shared vertex. World generation was ruled out before any of this was
written.

### New diagnostic

- **`vcp_terrainscan [radius]`** — read-only. Walks every adjacent pair of loaded heightmaps and
  compares their shared boundary vertices, reporting height, surface-normal and paint-mask deltas
  separately. A non-zero *height* delta means the geometry genuinely diverged; a zero height delta
  with a non-zero *normal* delta means the seam is purely lighting. Paint is broken out per channel
  (dirt / cultivated / paved / vegetation) since the cause differs by channel, and normals are
  bucketed by whether both zones had all four neighbours loaded — without that split, terrain at the
  edge of the loaded area drags the numbers down and makes a working fix look broken. Changes
  nothing — no `Poke`, no `Save`, no `TerrainComp` writes.

  First real-world run confirmed the height delta is **exactly zero** across 2600 shared vertices,
  which rules out the cross-zone divergence described under "known, documented, not yet fixed" below
  for that world, and confirms the seam is a shading artifact.

### Terrain

- `Heightmap.RebuildRenderMesh` — vanilla calls `RecalculateNormals()` on a mesh containing only one
  zone's 33×33 vertices, so a vertex on a zone boundary is shaded from the triangles on one side only
  while the neighbouring zone shades the same world position from the other. The result is a hard
  lighting crease along every 64 m zone border, most visible on flat bright terrain — Plains and
  Meadows. Normals are now computed analytically from the height field by central difference, taking
  samples across the boundary from the neighbouring heightmap. Loaded neighbours are refreshed too,
  since their edge normals depend on this zone's heights. When a neighbour is not loaded, vanilla's
  normals are left alone rather than half-applying the fix.

  The neighbour refresh runs whether or not the newly built zone could be fixed itself, and that
  ordering matters: zones are always built at the frontier of the loaded area, so a new zone rarely
  has all four of its own neighbours yet — what it does do is complete the neighbour set of the zone
  behind it. Skipping the refresh in that case left a permanent band of vanilla-normal terrain
  trailing the player.
- `Heightmap.SetPaintMask` / `Heightmap.UpdateTerrainAlpha` / `TerrainComp.UpdatePaintMask` — all
  three walk the paint mask with the wrong stride or bounds. The paint arrays are `(m_width + 1)²`,
  but two of them index with a stride of `m_width`, which slips a column further left on every row —
  a diagonal skew, not a uniform offset — and both stop one short of the last row and column.
  `SetPaintMask` separately rejects index `m_width`, which is exactly the row and column a zone shares
  with its neighbour, so paint could never be written to the seam at all.
- `TerrainComp.Update` — `Awake` gives up without initialising when it cannot find its zone's
  heightmap, but `Update` still calls `CheckLoad` every frame, which dereferences the null
  `m_modifiedHeight` and then the null `m_hmap`. Besides the exception spam, that zone stops accepting
  terrain edits permanently, because the compiler was never added to `s_instances` and nothing can
  find it. The frame is now skipped, and initialisation is retried once the heightmap appears.

### Known, documented, not yet fixed

`TerrainComp` stores terrain edits as deltas *relative to the current height* and routes them to
whichever peer owns each zone (`TerrainComp.ApplyOperation` → RPC → `m_levelDelta[i] += targetY -
GetHeight(x,y)`). When an edit straddles a zone boundary and the two zones are owned by different
players, each half resolves the same shared vertex against a differently-stale snapshot, which would
produce a real geometric step on the 64 m line. Whether this actually happens in practice is what
`vcp_terrainscan`'s height delta is there to answer. No fix is shipped for it yet, deliberately — the
candidate fix changes ownership behaviour and should not be written on a hunch.

## 0.2.0

Server performance and data integrity. Each entry names the vanilla method it fixes.

### Performance

- `Game.ConnectPortals` — replaced the quadratic portal-pairing scan the server runs every five
  seconds. Vanilla calls `FindRandomUnconnectedPortal` per unconnected portal, which allocates a list
  and rescans every portal doing a ZDO string lookup and comparison each; an unpaired portal never
  resolves, so it repeats that scan forever. Now three O(n) passes over a tag-keyed index with no
  steady-state allocation. Ownership is taken directly instead of round-tripping through
  `RPC_SetConnection`, and force-sends are batched per peer. Pairing is now deterministic rather than
  random, and all same-tag portals pair in one tick instead of one pair per tick.
- `ZDOMan.ConnectPortals` / `ZDOMan.ConnectSpawners` — both matched a source list against a target
  list with nested loops at world load. Now hash-indexed. Pairing decisions are unchanged, including
  ConnectPortals consuming each target once and ConnectSpawners letting several spawners share one.
- `Player.AutoPickup` — used the allocating `Physics.OverlapSphere` overload every frame per player.
  Now `OverlapSphereNonAlloc` against a reused buffer, with the loop bound rewritten to the hit count
  so it does not read past the live results.

### Correctness

- `ZNetScene.RemoveObjects` — dereferenced every instance's ZDO with no validity check. One orphaned
  entry threw and aborted the entire unload pass, then threw again every frame, so nothing despawned
  and memory climbed. Orphans are now dropped from the instance table instead.
- `ZDOMan.Load` — used `Dictionary.Add` for the ZDO index, so a save containing a duplicate ZDOID
  threw and made the world unloadable. Now keeps the later entry and logs a warning.
- `EffectArea.IsPointInsideArea` / `EffectArea.GetBaseValue` — `m_tempColliders` is a fixed
  128-element buffer that `OverlapSphereNonAlloc` silently truncates, so in dense builds warmth,
  wetness and burning checks could miss the area that mattered. The buffer now grows.
- `EffectArea.CustomFixedUpdate` — iterated `m_collidedWithCharacter` with no validity check. Unity
  never fires `OnTriggerExit` for a collider destroyed inside a trigger, so a character that died in
  the area stayed in the list and threw every physics step. Dead entries are stripped, and
  `Character.OnDestroy` now removes the character from every area it was standing in.
- `Smelter.OnAddFuel` / `Smelter.OnAddOre` / `Fireplace.Interact` / `Fireplace.UseItem` — these remove
  the item from your inventory and *then* send an RPC the owning peer may never process, destroying
  the fuel or ore. Ownership is now taken first so the call resolves locally.
- `Character.OnDeath` — the per-player boss defeat key was only recorded on whichever client owned the
  boss, because `CheckDeath` runs owner-only. The owner now broadcasts it and every player within
  range (default 300 m, configurable) is credited. Most visible with Hildir's quest bosses.

## 0.1.0

First release. Each entry names the vanilla method it fixes.

### Performance

- `ObjectDB.GetRecipe` — replaced the unindexed linear scan (a string comparison per recipe, run for
  every worn item on every frame the inventory is open at a crafting station) with a name-indexed
  lookup. Fixes the workbench repair hitch and the sustained FPS drop that grows with the number of
  installed recipes. First-recipe-wins semantics are preserved.
- `LiquidVolume.Awake` / `LiquidVolume.OnDestroy` — tar pit raycast buffers were allocated with
  `Allocator.TempJob` (a 4-frame allocator) but kept for the object's whole lifetime, and disposed
  without an `IsCreated` guard. Now `Allocator.Persistent` with guarded disposal. Stops the
  `JobTempAlloc has allocations that are more than 4 frames old` log spam and the associated native
  memory growth.

### Correctness

- `Recipe.GetAmount` — guarded the unchecked dereference of `GetFirstRequiredItem`, which returns
  null when the player holds none of the accepted ingredients. Was a `NullReferenceException` that
  broke the crafting/upgrade panel for "requires any one of these" recipes.
- `SpawnArea.Awake` — null entries in a spawner's prefab table are now removed instead of throwing
  during spawn selection and killing the spawner permanently.
- `EffectArea.IsPointPlus025InsideBurningArea` / `EffectArea.GetBurningAreaPointPlus025` — these
  tested only the cached bounds, never whether the area was active. `EffectArea` registers itself on
  `Awake` and is only unregistered on `OnDestroy`, so putting a fire out left its entry in the list
  forever and unlit fires kept cooking.
- `Character.CheckRun` — run stamina no longer drains while mid-attack. Patched on the base virtual
  because `Player.CheckRun` spends the stamina before returning. Narrowed to players so creature
  movement is unaffected.
- `Projectile.FixedUpdate` — `Quaternion.LookRotation` is no longer called with a zero vector, which
  made Unity log `Look rotation viewing vector is zero` every physics step per projectile.

### Notes

- Fixes are applied individually rather than through `Harmony.PatchAll`, so a Valheim update that
  breaks one patch target leaves the other fixes working and logs a single clear error.
- Toggles for transpiler-based fixes (tar pit leak, recipe crash, projectile spam) are read at patch
  time and need a game restart to take effect. The rest apply live.
