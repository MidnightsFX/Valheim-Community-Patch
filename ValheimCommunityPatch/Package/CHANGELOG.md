# Changelog

## 0.4.0

Crafting UI performance. Each entry names the vanilla method it fixes.

### Performance

- `InventoryGui.UpdateRecipeList` — destroys every row `GameObject` and re-`Instantiate`s the whole
  list on each call, and each row costs four `transform.Find` lookups, a `Localization.Localize`, two
  TMP text assignments and a fresh closure for its `onClick` listener. It is not an on-open cost:
  `UpdateCraftingPanel` is reached from seven call sites, including `OnSelectedItem` — every
  successful item drag-drop in the inventory or a container — and `DoCrafting` after every craft. With
  `nocost` set (which makes `GetAvailableRecipes` return every recipe in `ObjectDB`) or a large
  content modpack, that is a full teardown and rebuild of thousands of UI objects every time you move
  an item.

  Rows are now pooled and reused, with each row's child component references cached at creation. The
  row *data* is sorted before it is bound to a row, so `anchoredPosition` is written once when a row
  is created and vanilla's O(n) reposition pass after the sort disappears. Surplus rows are
  deactivated rather than destroyed, since destroying them is the cost being removed.

  Vanilla's `HaveRequirements(recipe, false, 1) | globalKey` uses the non-short-circuiting `|`, so the
  requirement scan runs even when `NoCraftCost` already decided the answer; that is now `||`. The
  upgrade-tab expression at the equivalent site is left semantically exact: `|` binds tighter than
  `&&` there, so a max-quality item is *not* upgradeable even under `NoCraftCost`, and hoisting the
  global key would have changed behaviour.

- `Inventory.CountItems` — a full linear scan of the inventory with a string comparison per slot,
  called by `HaveRequirementItems` once per requirement per quality tier per recipe with no
  memoization, even though the inventory cannot change during a rebuild. A scoped memo is now armed
  only around the craftability pass of a rebuild, guarded by a `try/finally`, a frame stamp, a world
  level stamp and an `Inventory.Changed` postfix. It is a prefix/postfix pair rather than a
  reimplementation, so the `matchWorldLevel`, null-name and negative-quality semantics are preserved
  by construction and other mods' patches still run and get memoized.

- `InventoryGui.UpdateRecipeList` sort comparators — `byName` called `Localization.Localize` *inside*
  the comparison, so a rebuild made `2 × O(n log n)` calls against `Localization`'s
  `LRUCache<string>(100)`. Past a hundred recipes that cache thrashes to a roughly 0% hit rate and
  every call re-runs the full translate path. Names are now translated once each and cached across
  rebuilds, invalidated when the selected language changes. The comparison stays culture-sensitive
  `string.CompareTo`, matching vanilla — an ordinal comparison would reorder non-ASCII names in every
  non-English locale.

- `InventoryGui.SetRecipe` — walked every row calling `transform.Find("selected")` on each, plus a
  `ZLog.Log` with a string concatenation, on every click, every gamepad D-pad tick, and at the tail of
  every rebuild. The one highlighted row is now tracked directly, so two `SetActive` calls replace the
  full scan, and the log line is gone.

- `InventoryGui.UpdateRecipeList` (canvas cost) — every row stayed active regardless of scroll
  position, so any canvas dirty re-batched the entire list. Rows outside the viewport are now
  deactivated, applied as a delta against the previous window so a scroll event touches only the rows
  that crossed the boundary. Behind its own toggle, since an inactive row is invisible to a
  `GetComponentInChildren` call that does not pass `includeInactive`.

- Row pre-warming — pooling only helps from the *second* rebuild onward, so the pool is now built by a
  time-budgeted coroutine during world load, where the cost is hidden by the loading screen. Capped by
  `Prewarm Recipe Rows` (default 1024) and by the actual recipe count. The row prefab is inactive, so
  pre-built rows are born inactive and cost memory only — roughly 5-15 KB each — until they are bound.

### New diagnostic

- **`vcp_recipebench [iterations] [vanilla]`** — read-only. Times `InventoryGui.UpdateCraftingPanel`,
  reporting the first (cold) build separately from the steady-state rebuild, and breaking the rebuild
  into requirement checks, sorting and row binding, with `CountItems` memo hit rate and created /
  reused / idle row counts. Passing `vanilla` suppresses the fast path for the duration of the run, so
  both arms are measured in one session against the same world, inventory and `ObjectDB` — a
  before/after taken across two restarts compares none of those. Also reports whether the scroll
  viewport uses a stencil `Mask` or a `RectMask2D`, which decides how much the culling is worth.
  `UpdateCraftingPanel` is idempotent, so repeating it changes nothing.

### Compatibility

Replacing `UpdateRecipeList` means `AddRecipeToList` is never called, and it has no other caller — so
a mod that patches it would silently lose its feature with no exception and no log. Both, plus
`SetRecipe`, are probed with `Harmony.GetPatchInfo` on every `InventoryGui.Awake`; if any carries a
foreign patch the affected part falls back to vanilla and the owning plugin GUID is logged.
`SetRecipe` is probed independently so a mod patching it does not cost the pooling win.

A structural probe additionally requires the vanilla row prefab shape (`icon`, `name`, `Durability`,
`QualityLevel`, `selected`, a `Button`) and no `LayoutGroup` or `ContentSizeFitter` on the list root,
which is a hard requirement for sorting before binding. Anything else disables the fast path with a
logged reason rather than guessing. Vanilla would throw a `NullReferenceException` on a missing child,
so bailing out is strictly safer than vanilla rather than a divergence from it.

### Not done, deliberately

- **Skipping `HaveRequirements` under `NoCostCheat`.** `nocost` sets `Player.m_noPlacementCost`, which
  `GetAvailableRecipes` reads — that is *why* every recipe appears — but which `UpdateRecipeList` never
  consults. And `canCraft` drives the icon alpha, the name colour and `byCraftable`, the primary sort
  key, so skipping it would turn every row white and reorder the list. The `CountItems` memo is what
  makes `nocost` fast while keeping `canCraft` exact.
- **Indexing `Player.GetAvailableRecipes`.** It looks like an O(n) offender, but `m_knownRecipes` is
  already a `HashSet<string>` and `m_currentSeason` is null outside events, leaving it a rounding error
  next to the rebuild it feeds. Its one real defect — `ToLower()` allocations and a `Localize` per
  recipe per term in the `s_FilterCraft` path — only triggers when a filter is typed, and belongs in
  its own fix.
- **Full virtualization.** Recycling a small fixed pool would remove the first-open cost entirely, but
  `m_availableRecipes[i].InterfaceElement` would be null for off-screen rows, which can throw in any
  mod that walks that list. Every row keeps a real `GameObject`.

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
- `Heightmap.ApplyModifiers` — terrain paint could disagree between the two sides of a zone boundary,
  leaving painted ground (usually dirt, from levelling and raising terrain) stopping dead in a hard
  straight line along the 64 m grid. Measured on a live world: 556 of 2600 shared vertices disagreed
  on the dirt channel with a maximum delta of 1.0 — fully painted on one side, unpainted on the other
  — while cultivated, paved and vegetation matched exactly.

  World generation never writes dirt at all (it only fills the mask's alpha), so every bit of it comes
  from a terrain operation. Unlike terrain height, which `TerrainComp` stores as a delta, paint is
  stored as an *absolute colour snapshot* seeded from whatever the heightmap texture held when the
  operation was recorded. Every distance and rounding calculation involved is symmetric between the
  two zones, so the divergence is state rather than arithmetic — if the zones' textures differed at
  that instant, both compilers permanently record different colours for the same ground and replay
  them on every regeneration.

  Shared boundary texels are now reconciled by taking the per-channel minimum. Minimum specifically:
  it can only remove paint, never invent it; paint applied legitimately across a boundary reaches both
  zones symmetrically and so is already equal and unaffected; and both zones compute the same value
  from the same inputs, so the result is continuous no matter which side is processed first. Alpha is
  left alone — it is world-generated, drives lava rendering in the Ashlands, and was measured as
  already consistent.
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
