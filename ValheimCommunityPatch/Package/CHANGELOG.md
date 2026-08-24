# Changelog

Each entry names the vanilla method it fixes, and is tagged with the side that fix is worth
installing on — *(server)*, *(client)* or *(both)*. See the README for what a one-sided install gets
you.

## 0.4.0

Install-side gating, ZDO and socket work, log spam, and the terrain paint seam.

### Install sides

- Every fix now declares which side it runs on, and the client-only ones are **no longer applied at
  all on a dedicated server** — a headless process has no local player, so nothing there could ever
  reach them. Of the 26 fixes, 12 are client, 4 are server and 10 are both; the startup log prints
  that breakdown, so a one-sided install is self-describing. Each toggle's description in the config
  carries the same tag, generated from the same declaration, so the two cannot drift apart.

  A dedicated server is identified by having no graphics device, which is the test Valheim itself
  uses (`RandEventSystem.RefreshPlayerEventData`) and the same one Jotunn's `GUIManager.IsHeadless`
  makes. Server-side fixes are deliberately *not* gated the same way: a client process can start
  hosting a world later in the same run, and the network role is not knowable when patches are
  applied. `Patch Every Side` in the config overrides the whole thing if the detection is ever wrong.

  The two terrain seam fixes keep their existing runtime `IsDedicated` check as well. They are the
  only client fixes whose target method genuinely runs and does real work on a dedicated server —
  it builds terrain meshes and paint textures for its colliders — so being unpatched there is a
  saving rather than a no-op.

### Performance

- `ZDOMan.RemoveOrphanNonPersistentZDOS` *(server)* — runs on every disconnect and walks *every ZDO
  in the world* to find the handful a departing player left ownerless. `Persistent` and `HasOwner`
  are cheap bit tests, but reaching them dereferences a separate heap object per entry, so on a
  multi-million-ZDO world that is essentially all cache misses across a multi-gigabyte heap, plus a
  linear peer scan and a dictionary lookup per candidate. Hundreds of milliseconds of frozen main
  thread every time somebody logs out, to find a few dozen objects.

  Non-persistent ZDOs are now bucketed by owner as ownership changes, so the sweep visits only the
  buckets whose owner is gone — O(orphans) instead of O(world). The index is maintained even when the
  fix is toggled off, because an index that missed every change before the switch would leak orphans
  forever; only the sweep reads the toggle. An admin-only `Verify Orphan Index` runs both the index
  and vanilla's full scan, acts on vanilla's answer and logs any divergence, so the index can be
  proven on a live server before it is trusted. The hooks are also checked at first use, and the
  whole fix stands down to vanilla if any of them failed to attach.

- `ZDOMan.ConnectSyncTransforms` *(server)* — the third of the world-load pairing loops, joining
  0.2.0's `ConnectPortals` and `ConnectSpawners`. Same nested-loop shape, same hash-indexed
  replacement, and it covers every ship and cart the world has ever held.

- `ZPackage.Write(ZPackage)` *(both)* — copied the source package's entire buffer onto the heap via
  `GetArray()` before writing it. That is the innermost step of ZDO synchronisation, so the whole
  payload was duplicated once per peer per send tick, and again on every routed RPC and every
  receive. Now written straight from the flushed stream buffer. The bytes on the wire are identical.

- `ObjectDB.GetRecipe` *(client)* — **removed.** 0.1.0 shipped a name-indexed lookup replacing the
  linear scan. It has been withdrawn: the index had to be invalidated on every `ObjectDB` mutation to
  stay correct, and no mod-compatible way to catch all of them was found. This is a regression back
  to vanilla behaviour for anyone upgrading from 0.1.0-0.3.0.

### Correctness

- `Player.AddStamina` *(client)* — bounds only the top of the range: `m_stamina += v` followed by a
  clamp against `m_maxStamina` and nothing else. Vanilla never passes a negative `v`, so this only
  opens up under mods, but any mod that reduces stamina with `AddStamina(-x)` — rather than
  `UseStamina`, whose RPC does floor at zero — takes the field arbitrarily negative. Every
  `HaveStamina` gate is a strict `>`, so the player then cannot attack, run, dodge, sneak or build,
  and cannot move at all while encumbered.

  A negative on its own is largely self-correcting — `UpdateStats` → `UpdateFood` → `SetMaxStamina`
  clamps to `[0, m_maxStamina]` once a second, and `Player.Load` clamps the same way — so the fix
  closes a sub-second window in which the gates read false, the value is published to the ZDO for
  other peers, and the HUD bar draws negative.

  `NaN` is the case nothing recovers from. Unity's `Mathf.Clamp` is a pair of comparisons, and every
  comparison against `NaN` is false, so all three of vanilla's clamps return it unchanged. The regen
  gate `m_stamina < maxStamina` is false as well, so regen never runs, and `Player.Save` writes the
  `NaN` back to the character file unvalidated — the player stays broken across every relog.
  `Player.RPC_UseStamina` is the network-facing route: it is a registered RPC, so the float arrives
  from whichever peer sent it and the handler checks only `v == 0`. The public `UseStamina` wrapper
  does screen `NaN`, but its guard sits one line above `v *= Game.m_staminaRate` — and
  `Game.m_staminaRate` is a public static filled from the `StaminaRate` world key by
  `Game.trySetScalarKey`, which parses with `float.TryParse(s, NumberStyles.Any, InvariantCulture,
  …)`. That accepts the literal `"NaN"`, so one malformed global key turns every *local* `UseStamina`
  call into a screened value multiplied straight back into `NaN`.

  The field is now floored at zero after `Player.AddStamina`, and after `Player.Load` so a character
  already saved in a bad state is repaired as it enters the world. `Player.RPC_UseStamina` is handled
  the other way round — a call whose `v` is not finite is dropped rather than applied and then
  cleaned up, because repairing after the fact would mean a peer sending garbage silently resets the
  victim's stamina to zero. For any finite `v` vanilla's own floor is already correct, so nothing
  else about that path changes.

  Floor only: `RPC_UseStamina` with a negative `v` can push stamina above `m_maxStamina`, but
  `SetMaxStamina`'s per-second clamp already pulls that back, and a ceiling here would fight mods
  that grant over-max stamina deliberately. One warning per player per episode, where an episode ends
  only once stamina is genuinely above zero — the patch's own write of `0f` does not count, so a mod
  breaching the floor every frame logs once rather than once a frame. The rejected-RPC warning is
  tracked separately and is once per player, since dropping a call leaves stamina untouched and there
  is no recovery event to end an episode on.

- `ItemDrop.ItemData.GetIcon` *(client)* — indexes `m_shared.m_icons` with the item's stored
  `m_variant` and no bounds check, so an item saved with a variant its prefab no longer has — after a
  game update or a removed item mod — throws from every inventory, tooltip and crafting panel that
  tries to draw it. The index is now checked, falling back to the first icon, and the item name is
  logged once rather than once per draw.

- `ZSteamSocket.SendQueuedPackages` *(both)* — logs `Failed to send data` through `ZLog.Log` on every
  frame a socket's send queue is backed up, which is exactly when the machine can least afford to be
  formatting lines, capturing stack traces and writing to disk. A server holds one socket per
  connected peer where a client holds one. Repointed to this mod's debug log; the `break` semantics
  of the surrounding loop are unchanged.

- `Container.RPC_RequestOpen` / `RPC_RequestStack` / `RPC_RequestTakeAll` *(both)* — four log lines
  per chest operation, including a leading one built unconditionally out of `gameObject.name`,
  marshalled fresh out of native Unity. These land on the container's owner — `ZNetView.InvokeRPC`
  addresses `m_zdo.GetOwner()`, and a relaying server never invokes the handler — so a player sorting
  their base logs their own chests and a server logs the ones it owns. Same redirect as above.

  Both log fixes redirect rather than delete: turning on `EnableDebugMode` brings the messages back.

- `DungeonGenerator.OnRoomLoaded` / `ZoneSystem.UnsetLoadingInZone` *(both)* — a dungeon claims its
  zone with `SetLoadingInZone` before it starts loading its rooms asynchronously, and only gives the
  claim back once `m_roomsToLoad` reaches zero. `OnRoomLoaded` returns early on any result other than
  `Succeeded` — *before* the decrement — so a single room whose asset fails to load means the counter
  never completes. `Spawn` never runs, so the dungeon interior never appears, and the only call to
  `UnsetLoadingInZone` never runs either, so the zone stays claimed until the generator itself
  unloads. Which it will not do while a player is standing in it.

  A zone stuck that way is not cosmetic. `IsZoneReadyForType` returns false, so `ZNetScene` stops
  creating that zone's objects; `IsAreaReady` returns false, so respawn completion and teleport
  completion never finish — anyone who spawns into or teleports to that zone sits on the loading
  screen indefinitely.

  Failed rooms are now accounted for like successful ones and dropped before `Spawn` walks them,
  since `PlaceRoom` opens with `roomData.m_prefab.Asset.GetComponent<Room>()` and would throw on an
  unloaded asset — landing back at a claimed zone by another route. A dungeon missing a room is a bad
  outcome; a zone the server can never finish loading is a worse one.

  `UnsetLoadingInZone` is guarded at the same time. It indexes `m_loadingObjectsInZones[sector]`
  directly, and the ZDO handed to it on `OnDestroy` is one the generator cached at claim time. If
  that ZDO was destroyed in between it has already been pooled and reset — `ZDOPool.Release` runs
  synchronously inside `HandleDestroyedZDO`, while `Object.Destroy` defers `OnDestroy` to end of
  frame — so its sector reads as `(0,0)` and the lookup either throws or edits an unrelated zone's
  list, leaving the real claim in place. It now falls back to finding the entry where it actually is.

  Note this is *not* the case of a room prefab going missing after a mod is removed: `Load` already
  trims those, and that path was never broken.

- Transpiler bail-outs *(both)* — every transpiler here counts what it rewrote and returns the
  original instructions when the count is wrong, which is what lets this mod share a patched method
  with mods that hard-match IL. That bail-out did not work. `new List<CodeInstruction>(instructions)`
  copies the list but not the instructions, and Harmony hands the same instruction objects to every
  transpiler in the chain, so rewriting an operand through the copy edited the original stream too —
  and returning it "untouched" handed back the rewrite. Six of the nine bail-outs happened to be safe
  because they only trigger on a count of zero, with nothing yet rewritten. Three could not:
  `ZDOMan.Load`, `Player.AutoPickup` and `LiquidVolume.Awake` all bail on a *non-zero* wrong count,
  so they applied a partial rewrite while logging that they had not. `LiquidVolume.Awake` was the
  one that mattered — its comment promises it will not half-apply the `Allocator.Persistent` switch,
  which is exactly what it was doing. All nine now copy the instructions themselves.

### Terrain

- `Heightmap.ApplyModifiers` boundary paint reconcile *(client)* — **supersedes the 0.3.0 entry
  below, whose reasoning was wrong.** 0.3.0 resolved a disagreeing boundary texel to the per-channel
  *minimum*, argued as safe because it "can only ever remove paint, never invent it". That is true and
  still the wrong thing to do. `RebuildRenderMesh` assigns `UV = (x / m_width, y / m_width)` over a
  `(m_width + 1)` texture, so a zone's last one-metre row of quads interpolates *into* the boundary
  texel. Forcing that texel down to an unpainted neighbour's value turned the painted zone's final
  metre into a dirt-to-grass gradient — a pale band along the 64 m grid — and made the border column
  permanently unpaintable, because `Heightmap.Generate` rebuilds the mask from the base mask plus
  `TerrainComp` on every regeneration and the postfix re-applied the minimum every single time. The
  player painted, the paint vanished, and no amount of repainting helped.

  The texel is now resolved against the ground behind it on both sides:
  `Clamp(Max(supportA, supportB), Min(a, b), Max(a, b))`, where the support samples are one texel
  inward on each side. Paint that legitimately reaches the border has its own zone's interior behind
  it, so the maximum wins and the paint carries about a metre into the neighbour — the same falloff
  any paint edge has anywhere else in a zone. A lone stripe on the grid line with nothing behind it on
  either side still resolves to the minimum and is still removed. When the two sides already agree,
  `lo == hi` and the result is that value regardless of the support term, so repeated regeneration
  cannot drift, and both zones compute the same value from the same four inputs, so it does not matter
  which side regenerates first. The result is always one of the two values already stored for that
  texel — no colour is invented, only chosen. Unlike 0.3.0 this can raise a value as well as lower
  one; that is the correct outcome given the layout, since the ground at the border genuinely is
  painted and there is no way to represent paint stopping exactly on the line without a
  discontinuity. Alpha is still untouched, and nothing saved to the world is changed.

  Four-zone corner texels are now merged in a single pass over all four zones rather than pairwise
  twice, which under a clamp could settle on two different values and alternate between them.

- `TerrainOp.Awake` zone fan-out *(both)* — the root cause behind the seam, and previously recorded in
  0.3.0 as having none. `Awake` decides which zones an edit is recorded into from the tool's radius:
  `Heightmap.FindHeightmap(pos, GetRadius(), heightmaps)`. But `TerrainComp.PaintCleared` subtracts
  half a texel from x and z and *then* floors, via `WorldToVertexMask`, so the paint kernel's centre
  snaps to the vertex grid up to a full texel toward −x/−z and the kernel reaches about a metre
  further than `GetRadius()` on those two sides. Adjacent zones each hold their own copy of the shared
  boundary texel, so an edit placed in that band paints its own zone's column 0 / row 0 — the shared
  texel — while the west or south neighbour, excluded from the fan-out, keeps the unpainted copy. The
  asymmetry is directional, which is exactly why the artifact only ever appeared along one side of a
  chunk.

  The extra zones the paint kernel genuinely reaches are now sent a **paint-only** copy of the same
  operation, so both sides record it and the agreement is saved rather than repaired at render time.
  Paint-only because `LevelTerrain`, `RaiseTerrain` and `SmoothTerrain` call `WorldToVertex` without
  the half-texel shift, which rounds to nearest and stays symmetric — handing them to a zone vanilla
  left out would move terrain nothing asked to move, and `TerrainComp` records height as a delta
  against that zone's *current* height, which is the one place a genuine geometric step could be baked
  in. Only zones on the −x/−z side are considered, since that is the only direction the floor can run
  past the radius. This changes what is saved to the world and only affects edits made from now on;
  the reconcile above is what repairs terrain already recorded the broken way.

- `Heightmap.RebuildRenderMesh` neighbour refresh *(client)* — 0.3.0's fix recomputed a zone's
  neighbours only when the zone itself could be fixed, and zones are always built at the frontier of
  the loaded area, so a new zone almost never has all four of its own neighbours yet. What it *does*
  do is complete the neighbour set of the zone behind it, which had bailed to vanilla normals for
  exactly that reason and would otherwise stay that way forever — nothing else re-pokes it. The
  result was a permanent band of vanilla-normal terrain trailing the player, measured as 81% of
  shared vertices matching near the player against 43% further out. Neighbours are now refreshed
  unconditionally.

- `TerrainComp.Update` recovery *(both)*, follow-up to this release's own fix — recovering a terrain
  compiler that lost the startup race put it into `s_instances` without the deduplication
  `TerrainComp.Awake` does first. Vanilla's only guard against two compilers owning one zone is
  `GetAndCreateTerrainCompiler`, which searches that same list, so the compiler that lost the race is
  invisible to it and a second one gets created — and `Awake` destroys the incumbent when that
  happens. Recovering the first one without repeating that step left both live for one zone.

  `Heightmap.ApplyModifiers` resolves through `FindTerrainCompiler`, which returns the first list
  match, so one compiler's saved terrain and paint would be silently discarded and the other's
  applied — with list order following object creation order, not stably. That is the terrain
  flip-flopping the other fixes in this section exist to remove, reintroduced by the recovery.
  Recovery now performs the same deduplication `Awake` does, in the same order.

  A failed recovery is also no longer retried. It ran again on the next frame and every frame after,
  writing a full stack trace each time.

### Compatibility

- **ComfyMods BetterZeeLog** — installing the two together broke five methods. Both mods transpile
  `Container.RPC_RequestOpen`, `RPC_RequestStack` and `RPC_RequestTakeAll`,
  `ZSteamSocket.SendQueuedPackages` and `Projectile.FixedUpdate`. That mod locates its edit by
  matching the exact `ZLog.Log` and `Quaternion.LookRotation` call operands, and this one rewrites
  those operands in place; whenever ours went first its matcher missed and threw
  `Could not patch Container.RPC_RequestOpen()!`, at which point HarmonyX discarded the method
  entirely — losing *both* mods' fixes on it, and any third mod's patches on it too.

  Every transpiler in this mod now declares `Priority.Last`. Harmony applies transpilers in priority
  order rather than mod load order, so other mods now see vanilla IL and ours run afterwards on
  whatever is left. Ours are the right side to yield: each one counts what it rewrote and returns the
  method untouched when the count is wrong, so it degrades to a no-op instead of throwing. Where both
  mods are installed BetterZeeLog's versions of these three fixes are the ones that take effect.

  The fail-safe warnings these transpilers emit have been reworded to say the method may already have
  been rewritten by another mod, since "leaving it unpatched" read as a failure when nothing was
  wrong.

## 0.3.0

Terrain seams. Each entry names the vanilla method it fixes.

Note on what is *not* here: the base heightfield is seam-consistent by construction.
`HeightmapBuilder.Build` blends the four corner-biome heights with `SmoothStep`-warped coordinates,
and since `SmoothStep(0,1,0)==0`, `SmoothStep(0,1,1)==1`, and adjacent zones sample
`WorldGenerator.GetBiome` at the same world positions for their shared corners, both zones compute an
identical height at every shared vertex. World generation was ruled out before any of this was
written.

Also ruled out by measurement: comparing the shared boundary vertices of every adjacent loaded zone
on a live world put the height delta at **exactly zero** across 2600 shared vertices, which rules out
the cross-zone divergence described under "known, documented, not yet fixed" below for that world,
and confirms the seam is a shading artifact.

### Terrain

- `Heightmap.RebuildRenderMesh` *(client)* — vanilla calls `RecalculateNormals()` on a mesh containing
  only one zone's 33×33 vertices, so a vertex on a zone boundary is shaded from the triangles on one
  side only while the neighbouring zone shades the same world position from the other. The result is a
  hard lighting crease along every 64 m zone border, most visible on flat bright terrain — Plains and
  Meadows. Normals are now computed analytically from the height field by central difference, taking
  samples across the boundary from the neighbouring heightmap. Loaded neighbours are refreshed too,
  since their edge normals depend on this zone's heights. When a neighbour is not loaded, vanilla's
  normals are left alone rather than half-applying the fix.

  The neighbour refresh runs whether or not the newly built zone could be fixed itself, and that
  ordering matters: zones are always built at the frontier of the loaded area, so a new zone rarely
  has all four of its own neighbours yet — what it does do is complete the neighbour set of the zone
  behind it. Skipping the refresh in that case left a permanent band of vanilla-normal terrain
  trailing the player.
- `Heightmap.ApplyModifiers` *(client)* — terrain paint could disagree between the two sides of a zone
  boundary, leaving painted ground (usually dirt, from levelling and raising terrain) stopping dead in
  a hard straight line along the 64 m grid. Measured on a live world: 556 of 2600 shared vertices
  disagreed on the dirt channel with a maximum delta of 1.0 — fully painted on one side, unpainted on
  the other — while cultivated, paved and vegetation matched exactly.

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
- `Heightmap.SetPaintMask` / `Heightmap.UpdateTerrainAlpha` / `TerrainComp.UpdatePaintMask`
  *(client)* — all three walk the paint mask with the wrong stride or bounds. The paint arrays are
  `(m_width + 1)²`, but two of them index with a stride of `m_width`, which slips a column further
  left on every row — a diagonal skew, not a uniform offset — and both stop one short of the last row
  and column. `SetPaintMask` separately rejects index `m_width`, which is exactly the row and column a
  zone shares with its neighbour, so paint could never be written to the seam at all. All three sit on
  the `optterrain` console path, so this makes that repair command correct rather than changing
  anything during normal play.
- `TerrainComp.Update` *(both)* — `Awake` gives up without initialising when it cannot find its zone's
  heightmap, but `Update` still calls `CheckLoad` every frame, which dereferences the null
  `m_modifiedHeight` and then the null `m_hmap`. Besides the exception spam, that zone stops accepting
  terrain edits permanently, because the compiler was never added to `s_instances` and nothing can
  find it. The frame is now skipped, and initialisation is retried once the heightmap appears.

### Known, documented, not yet fixed

`TerrainComp` stores terrain edits as deltas *relative to the current height* and routes them to
whichever peer owns each zone (`TerrainComp.ApplyOperation` → RPC → `m_levelDelta[i] += targetY -
GetHeight(x,y)`). When an edit straddles a zone boundary and the two zones are owned by different
players, each half resolves the same shared vertex against a differently-stale snapshot, which would
produce a real geometric step on the 64 m line. The boundary height delta measured above says it did
not happen on that world; whether it happens in general is still open. No fix is shipped for it yet,
deliberately — the candidate fix changes ownership behaviour and should not be written on a hunch.

## 0.2.0

Server performance and data integrity. Each entry names the vanilla method it fixes.

### Performance

- `Game.ConnectPortals` *(server)* — replaced the quadratic portal-pairing scan the server runs every
  five seconds. Vanilla calls `FindRandomUnconnectedPortal` per unconnected portal, which allocates a
  list and rescans every portal doing a ZDO string lookup and comparison each; an unpaired portal
  never resolves, so it repeats that scan forever. Now three O(n) passes over a tag-keyed index with
  no steady-state allocation. Ownership is taken directly instead of round-tripping through
  `RPC_SetConnection`, and force-sends are batched per peer. Pairing is now deterministic rather than
  random, and all same-tag portals pair in one tick instead of one pair per tick.
- `ZDOMan.ConnectPortals` / `ZDOMan.ConnectSpawners` *(server)* — both matched a source list against a
  target list with nested loops at world load. Now hash-indexed. Pairing decisions are unchanged,
  including ConnectPortals consuming each target once and ConnectSpawners letting several spawners
  share one.
- `Player.AutoPickup` *(client)* — used the allocating `Physics.OverlapSphere` overload every frame
  per player. Now `OverlapSphereNonAlloc` against a reused buffer, with the loop bound rewritten to
  the hit count so it does not read past the live results.

### Correctness

- `ZNetScene.RemoveObjects` *(both)* — dereferenced every instance's ZDO with no validity check. One
  orphaned entry threw and aborted the entire unload pass, then threw again every frame, so nothing
  despawned and memory climbed. Orphans are now dropped from the instance table instead.
- `ZDOMan.Load` *(server)* — used `Dictionary.Add` for the ZDO index, so a save containing a duplicate
  ZDOID threw and made the world unloadable. Now keeps the later entry and logs a warning.
- `EffectArea.IsPointInsideArea` / `EffectArea.GetBaseValue` *(both)* — `m_tempColliders` is a fixed
  128-element buffer that `OverlapSphereNonAlloc` silently truncates, so in dense builds warmth,
  wetness and burning checks could miss the area that mattered. The buffer now grows.
- `EffectArea.CustomFixedUpdate` *(both)* — iterated `m_collidedWithCharacter` with no validity check.
  Unity never fires `OnTriggerExit` for a collider destroyed inside a trigger, so a character that
  died in the area stayed in the list and threw every physics step. Dead entries are stripped, and
  `Character.OnDestroy` now removes the character from every area it was standing in. This one is
  reachable on a dedicated server too — `CustomFixedUpdate` has no owner check.
- `Smelter.OnAddFuel` / `Smelter.OnAddOre` / `Fireplace.Interact` / `Fireplace.UseItem` *(client)* —
  these remove the item from your inventory and *then* send an RPC the owning peer may never process,
  destroying the fuel or ore. Ownership is now taken first so the call resolves locally.
- `Character.OnDeath` *(client)* — the per-player boss defeat key was only recorded on whichever
  client owned the boss, because `CheckDeath` runs owner-only. The owner now broadcasts it and every
  player within range (default 300 m, configurable) is credited. Most visible with Hildir's quest
  bosses. The server does not need the mod for this: an unrecognised routed-RPC hash makes
  `ZRoutedRpc.HandleRoutedRPC` return without invoking anything, and the message is still relayed on.

## 0.1.0

First release. Each entry names the vanilla method it fixes.

### Performance

- `ObjectDB.GetRecipe` *(client)* — replaced the unindexed linear scan (a string comparison per
  recipe, run for every worn item on every frame the inventory is open at a crafting station) with a
  name-indexed lookup. Fixes the workbench repair hitch and the sustained FPS drop that grows with the
  number of installed recipes. First-recipe-wins semantics are preserved. **Withdrawn in 0.4.0** — see
  above.
- `LiquidVolume.Awake` / `LiquidVolume.OnDestroy` *(client)* — tar pit raycast buffers were allocated
  with `Allocator.TempJob` (a 4-frame allocator) but kept for the object's whole lifetime, and
  disposed without an `IsCreated` guard. Now `Allocator.Persistent` with guarded disposal. Stops the
  `JobTempAlloc has allocations that are more than 4 frames old` log spam and the associated native
  memory growth.

### Correctness

- `Recipe.GetAmount` *(client)* — guarded the unchecked dereference of `GetFirstRequiredItem`, which
  returns null when the player holds none of the accepted ingredients. Was a `NullReferenceException`
  that broke the crafting/upgrade panel for "requires any one of these" recipes.
- `SpawnArea.Awake` *(both)* — null entries in a spawner's prefab table are now removed instead of
  throwing during spawn selection and killing the spawner permanently.
- `EffectArea.IsPointPlus025InsideBurningArea` / `EffectArea.GetBurningAreaPointPlus025` *(client)* —
  these tested only the cached bounds, never whether the area was active. `EffectArea` registers
  itself on `Awake` and is only unregistered on `OnDestroy`, so putting a fire out left its entry in
  the list forever and unlit fires kept cooking.
- `Character.CheckRun` *(client)* — run stamina no longer drains while mid-attack. Patched on the base
  virtual because `Player.CheckRun` spends the stamina before returning. Narrowed to players so
  creature movement is unaffected.
- `Projectile.FixedUpdate` *(both)* — `Quaternion.LookRotation` is no longer called with a zero
  vector, which made Unity log `Look rotation viewing vector is zero` every physics step per
  projectile.

### Notes

- Fixes are applied individually rather than through `Harmony.PatchAll`, so a Valheim update that
  breaks one patch target leaves the other fixes working and logs a single clear error.
- Toggles for transpiler-based fixes (tar pit leak, recipe crash, projectile spam) are read at patch
  time and need a game restart to take effect. The rest apply live.
