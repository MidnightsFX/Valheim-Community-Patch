# Changelog

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
