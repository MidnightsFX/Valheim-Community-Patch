# Changelog

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
