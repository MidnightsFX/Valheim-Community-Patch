# Valheim Community Patch

Vanilla bug fixes and performance fixes. **No gameplay changes.**

This mod exists to fix things that are broken or pathologically slow in Valheim itself — crashes,
exceptions that silently kill a system, item loss, quadratic hot paths, per-frame allocations, log
spam. It deliberately ships no quality-of-life features, no balance changes, and no content, so you
can install it on a server without anyone having to agree about how the game should play.

Performance fixes are always on; the config holds only their tuning values and the admin-only Verify
diagnostics. Correctness and terrain fixes each have their own toggle. Config values are admin-only
and server-synced.

Each fix is tagged with the side it is worth installing on. *(server)* fixes only do something on
the machine hosting the world — a dedicated server or a listen host. *(client)* fixes need a local
player, and are **not even applied** on a dedicated server, which is why the startup log there
reports a smaller number. *(both)* fixes do work wherever they are installed. A one-sided install is
safe; it just gets you a subset. See [Installation](#installation).

### Why so many fixes are client-side

A Valheim dedicated server is not a simulation host. It only creates game objects inside its *own*
active area, and a headless server never has a player to anchor that area to, so that area stays at
world origin for the whole run. Creatures, fires, chests, tar pits and everything else near a player
are created, owned and simulated on that player's client; the server holds the data and relays it.

So the fixes a server gains from are the data-and-network ones — ZDO handling, world load, packet
allocation, socket logging — and almost everything else is worth having on the client.

## Performance Side by Side

[![](https://markdown-videos-api.jorgenkh.no/youtube/UrSF0NYgFpo)](https://youtu.be/UrSF0NYgFpo)

## Terrain Fix comparison

| Terrain Tears | Terrain Fixed |
|---|---|
| ![Terrain Tears](https://github.com/MidnightsFX/Valheim-Community-Patch/blob/master/Media/Terrain_Tears.png?raw=true)   |  ![Terrain Fixed](https://github.com/MidnightsFX/Valheim-Community-Patch/blob/master/Media/Terrain_Fixed.png?raw=true) |

## Fixes in this release

Where a fix was sourced from, or corroborated against, another modder's work, that is recorded per
fix under [Credit and sources](#credit-and-sources).

### Performance

- **Fix Portal Connection Scan** *(server)* — pairs portals through a tag index instead of rescanning
  every portal for each unconnected one every five seconds.
- **Fix World Load Connection Scan** *(server)* — indexes the world-load pairing of portals, spawners
  and sync transforms with their targets instead of comparing every pair.
- **Fix Disconnect ZDO Sweep** *(server)* — indexes non-persistent objects by owner, so a disconnect
  sweeps only the departing player's objects instead of every ZDO in the world.
- **Fix Tar Pit Memory Leak** *(client)* — allocates tar pit raycast buffers persistently and disposes
  them safely, ending the native memory leak and the `JobTempAlloc` log spam.
- **Fix Auto Pickup Allocation** *(client)* — reuses one collider buffer for the per-frame auto-pickup
  check instead of allocating an array every frame.
- **Fix ZDO Packet Allocation** *(both)* — writes one network package into another straight from its
  buffer instead of copying the whole payload onto the heap first.
- **Fix Mist Query Overhead** *(client)* — answers mist volume queries from a zone-bucketed snapshot
  and heightmap data instead of scanning every mist volume with native reads and physics rays per
  particle.
- **Fix Heightmap Lookup Scan** *(both)* — finds the terrain tile under a point with a zone-keyed
  lookup instead of scanning every loaded tile.
- **Fix Static Object Ground Checks** *(both)* — reads a tree or rock's position once per ground check
  instead of about six times.
- **Fix Grass Rebuild Burst** *(client)* — spreads a whole-area grass rebuild over a few frames,
  nearest patches first, instead of regenerating every patch in one frame.
- **Fix Terrain Builder Throughput** *(both)* — makes the terrain build thread sleep only when idle
  and hold more finished results.
- **Fix Zone Collider Stall** *(client)* — bakes the terrain collider of an already-generated zone on
  a background thread instead of the main thread.
- **Fix Prefab Query Scan** *(both)* — answers "every object of this prefab" from an index instead of
  scanning every ZDO in the world.
- **Fix Grass Ground Raycasts** *(client)* — reads the ground height, slope and biome for grass
  placement from terrain data instead of casting a physics ray per blade.
- **Fix Background Zone Pacing** *(server)* — defers background zone pre-generation by a tick when the
  previous frame ran long or the last generation was expensive (both configurable).
- **Fix Water Material Lookup** *(client)* — caches each water tile's surface material instead of
  fetching it from the engine every frame.
- **Fix Distant Terrain Hitch** *(client)* — rebuilds the far-terrain ring a few tiles per frame
  (configurable; 9 is vanilla) instead of all nine at once.
- **Fix Idle Scene Sweep** *(both)* — skips the 30 Hz object create/destroy pass when cheap
  change-tracking proves nothing changed, keeping one full pass per second as a safety sweep.
- **Fix Support Lookup Cost** *(both)* — resolves which building piece owns a collider through a
  lookup table instead of a hierarchy walk per collider.
- **Fix Light Flicker Overhead** *(client)* — stops updating torch flicker beyond a configurable
  distance, and exposes the game's dormant point-light cap as a client-local "Point Light Limit"
  (-1 is vanilla).
- **Fix Piece Event Stall** *(both)* — registers building pieces for terrain-rebuild cache clears in a
  per-heightmap table instead of an event whose subscribe copies and unsubscribe scans the whole list.
- **Fix Unload Sweep Cost** *(both)* — runs the object-unload sweep on a configurable wall-clock
  interval (default 100 ms) instead of on every pass.
- **Fix Spawn Queue Churn** *(both)* — keeps the sorted spawn backlog between frames instead of
  rebuilding and re-sorting it thirty times a second.
- **Fix Object Stream Rescan** *(both)* — keeps the set of objects waiting to spawn as a running queue
  fed by the events that change it, instead of rediscovering it from every zone around you thirty
  times a second.
- **Fix Zone Occupancy Scan** *(both)* — answers whether a zone still holds objects from a per-zone
  tally instead of walking every loaded object.
- **Fix Piece Material Polling** *(both)* — waits for a piece's random material seed on one shared
  ticker and stops polling once the values are applied.
- **Fix Idle Support Checks** *(both)* — lets a building piece skip its structural support re-check
  until a neighbour is built, destroyed or changed or the terrain is edited; "Support Change
  Threshold" and "Settled Piece Patience" tune how a neighbour's small drift wakes it.
- **Fix Smoke Overhead** *(client)* — writes each smoke puff's physics mass on 2% lifetime steps,
  rechecks its render chunk four times a second, and reads its position once per frame.
- **Fix Unload Discovery Scan** *(both)* — finds objects that left the loaded area through a per-zone
  instance index instead of walking every loaded object; "Object Unload Frame Budget"
  caps how many are handed to the engine per pass (default 250, 0 is vanilla).
- **Fix Idle Wear Visits** *(both)* — skips a building piece's whole wear visit while it is provably
  quiet: support asleep, locally owned, dry or roofed while wet, above the waterline, outside the
  Ashlands, and undamaged since the last visit.
- **Fix Reflection Probe Spikes** *(client)* — renders the realtime reflection cubemap one face per
  frame at a configurable resolution and reduced quality, holding a face back while the frame is over
  budget.
- **Fix Physics Catchup Spiral** *(both)* — caps how many fixed physics steps one frame may run to
  catch up after a stall (default 8; vanilla is about 16).
- **Fix Location Biome Area Rescan** *(server)* — remembers each zone's biome-area answer during
  location generation instead of recomputing it for every placement attempt; worlds come out
  identical.
- **Fix ZDO Value Write Allocation** *(both)* — compares a ZDO field write against its stored value
  without boxing it.
- **Fix Doubled ZDO Lookups** *(both)* — reads ZDO data with one dictionary lookup instead of two.
- **Fix Collision Contact Allocation** *(both)* — reads a collision's contact points into a reused
  buffer instead of allocating an array per read.
- **Fix Collision Callback Allocation** *(both)* — turns on Unity's `reuseCollisionCallbacks` so one
  collision object serves every callback; the first suspect if a physics-touching mod that stores
  collision objects misbehaves.
- **Fix Equipment Visual Refresh** *(client)* — re-applies a character's skin and hair colour only
  when an input changed, and reads its equipment fields with one table lookup instead of thirty.
- **Fix Light Settings Subscription** *(client)* — registers lights for graphics-setting changes in a
  lookup table instead of a static event whose unsubscribe scans every other lit light.

### Terrain

**Fix Terrain Seams** and **Fix Terrain Paint Seams** change only what is drawn, never what is saved,
so they are client-only. **Fix Terrain Paint Zone Fanout** changes what is recorded, so it runs on
every side.

- **Fix Terrain Seams** *(client)* — computes terrain lighting normals across zone boundaries,
  removing the hard crease along the 64 m grid.
- **Fix Terrain Paint Seams** *(client)* — merges the two copies of the paint on a zone boundary so
  dirt continues across the line instead of stopping dead; a lone stripe on the border is dropped.
- **Fix Terrain Paint Zone Fanout** *(both)* — records terrain paint into every zone it actually
  covers, including the neighbour about a metre west or south that vanilla left out; affects edits
  made from now on.
- **Fix Terrain Paint Mask Indexing** *(client)* — corrects the stride and bounds the `optterrain`
  console command uses to walk terrain paint data.
- **Fix Terrain Compiler Init Race** *(both)* — recovers a zone's terrain compiler that loaded before
  its heightmap existed, instead of throwing every frame and ignoring edits.

### Correctness

- **Fix Object Unload Crash** *(both)* — recovers from an orphaned scene instance during object
  unloading instead of aborting the pass every frame.
- **Tolerate Duplicate ZDOs On Load** *(server)* — loads a save containing duplicate ZDO ids, keeping
  the later one and logging a warning, instead of refusing to load.
- **Fix Effect Areas** *(both)* — grows the fixed 128-collider buffer behind fire warmth and wetness
  checks when it fills, and removes destroyed characters from effect areas instead of throwing every
  physics step.
- **Fix Fuel And Ore Loss** *(client)* — takes ownership of a smelter, kiln or fireplace before adding
  fuel or ore, so the item cannot be lost to a dropped network message.
- **Share Boss Defeat Keys** *(client)* — gives every nearby player the per-player boss defeat key,
  not only the one whose client owned the boss; works against a vanilla server.
- **Fix Recipe Amount Crash** *(client)* — guards the null dereference that broke the crafting panel
  for a "requires any one of these" recipe with none of the ingredients carried.
- **Fix Spawner Null Prefabs** *(both)* — drops null entries from a spawner's creature table on load
  instead of letting one kill the spawner.
- **Require Lit Fire** *(client)* — counts a fireplace as a heat source only while it is lit;
  player-visible in a mixed group.
- **Fix Run Attack Stamina Drain** *(client)* — stops run stamina draining while you are mid-attack;
  player-visible in a mixed group.
- **Fix Projectile Rotation Spam** *(both)* — stops the `Look rotation viewing vector is zero` log
  line that a projectile at zero velocity writes every physics step.
- **Fix Send Failure Log Spam** *(both)* — redirects the per-frame `Failed to send data` log line to
  debug logging.
- **Fix Container Log Spam** *(both)* — redirects the four log lines written on every chest open,
  stack-all and take-all to debug logging.
- **Fix Item Icon Crash** *(client)* — draws the first icon for an item whose stored icon variant is
  out of range instead of throwing from every UI panel.
- **Fix Negative Stamina** *(client)* — floors player stamina at zero (`NaN` included), repairs a
  character that loads in broken, and drops a `UseStamina` network message carrying `NaN` or infinity.
- **Fix Dungeon Load Stall** *(both)* — counts a dungeon room whose asset failed to load as finished
  and drops it, so the zone is not left flagged as loading forever.

The two log fixes redirect rather than delete: turn on `EnableDebugMode` and the messages come back.

## Credit and sources

This mod fixes vanilla defects, and other modders found — and in several cases already fixed — a good
number of them first. Where that is true it is recorded below, and in a comment at the top of the
patch file.

[vpo]: https://github.com/ontrigger/ValheimPerformanceOptimizations

The mods involved:

- **[ComfyMods](https://github.com/redseiko/ComfyMods)** — redseiko (GPL-3.0), the same licence as
  this project. Seven of its mods — BetterZeeLog, LetMePlay, BetterServerPortals, Scenic, Compress,
  Effectual and Atlas — account for eleven of the entries below.
- **[ValheimPerformanceOptimizations](https://github.com/ontrigger/ValheimPerformanceOptimizations)**
  — ontrigger (MIT). Three of the performance entries below, including the event-fed spawn queue the
  object-stream rescan fix is built on, plus independent corroboration of two more.
- **[MyPitsDontLeak](https://github.com/AzumattDev/MyPitsDontLeak)** — Azumatt (MIT).
- **Zen.ModLib** — ZenDragon. Used as a reference; no code was used.
- **Iron Gate Studio** — Valheim itself. The decompiled game source is the reference used to locate
  defects; no game code is redistributed.

### Performance

| Fix | Sourced from | What came from there |
| --- | --- | --- |
| Fix Portal Connection Scan | ComfyMods — BetterServerPortals | The indexing algorithm |
| Fix World Load Connection Scan | ComfyMods — Atlas | Its `ConnectSpawners` approach, extended here to portals and sync transforms |
| Fix Disconnect ZDO Sweep | MidnightsFX | — |
| Fix Tar Pit Memory Leak | MyPitsDontLeak — Azumatt | The root cause; our implementation is transpilers rather than wholesale method replacement |
| Fix Auto Pickup Allocation | Zen.ModLib (catalogue) | The technique, rewritten |
| Fix ZDO Packet Allocation | ComfyMods — Compress | The technique, taken on its own without that mod's GZip protocol change |
| Fix Mist Query Overhead | MidnightsFX | — |
| Fix Heightmap Lookup Scan | MidnightsFX | — |
| Fix Static Object Ground Checks | MidnightsFX | — |
| Fix Grass Rebuild Burst | MidnightsFX | — |
| Fix Terrain Builder Throughput | MidnightsFX | — |
| Fix Zone Collider Stall | MidnightsFX | — |
| Fix Prefab Query Scan | MidnightsFX | — |
| Fix Grass Ground Raycasts | MidnightsFX | — |
| Fix Background Zone Pacing | MidnightsFX | — |
| Fix Water Material Lookup | MidnightsFX | — |
| Fix Distant Terrain Hitch | MidnightsFX | — |
| Fix Idle Scene Sweep | MidnightsFX | — |
| Fix Support Lookup Cost | MidnightsFX; corroborated by [ontrigger's ValheimPerformanceOptimizations][vpo] (MIT) | Arrived at the same map-probe and lazy-default forms, and the single-fetch centre of mass |
| Fix Light Flicker Overhead | MidnightsFX | — (Point Light Limit exposes a dormant vanilla mechanism) |
| Fix Piece Event Stall | MidnightsFX | — |
| Fix Unload Sweep Cost | MidnightsFX | — |
| Fix Spawn Queue Churn | MidnightsFX | — |
| Fix Zone Occupancy Scan | MidnightsFX | — |
| Fix Piece Material Polling | MidnightsFX | — |
| Fix Idle Support Checks | MidnightsFX | — |
| Fix Smoke Overhead | MidnightsFX | — |
| Fix Unload Discovery Scan | MidnightsFX | — |
| Fix Idle Wear Visits | MidnightsFX | — |
| Fix Reflection Probe Spikes | [ontrigger's ValheimPerformanceOptimizations][vpo] (MIT) | Face-sliced probe rendering with quality clamps |
| Fix Physics Catchup Spiral | [ontrigger's ValheimPerformanceOptimizations][vpo] (MIT) | The maximumDeltaTime cap and its default |
| Fix Object Stream Rescan | [ontrigger's ValheimPerformanceOptimizations][vpo] (MIT) | Event-fed spawn queue, the zone-set diff, and the 8 m re-sort threshold |
| Fix Location Biome Area Rescan | worldGenAccelerator — jneb802 / warpalicious (MIT) | The observation that per-zone biome-area evaluation dominates location generation. No code, and not that mod's approach, which trades vanilla world layout for the speed |
| Fix ZDO Value Write Allocation | MidnightsFX | — |
| Fix Doubled ZDO Lookups | MidnightsFX | — |
| Fix Collision Contact Allocation | MidnightsFX | — |
| Fix Collision Callback Allocation | MidnightsFX | — |
| Fix Equipment Visual Refresh | MidnightsFX | — |
| Fix Light Settings Subscription | MidnightsFX | — |

### Terrain

| Fix | Sourced from | What came from there |
| --- | --- | --- |
| Fix Terrain Seams | MidnightsFX | — |
| Fix Terrain Paint Seams | MidnightsFX | — |
| Fix Terrain Paint Zone Fanout | MidnightsFX | — |
| Fix Terrain Paint Mask Indexing | MidnightsFX | — |
| Fix Terrain Compiler Init Race | MidnightsFX | — |

### Correctness

| Fix | Sourced from | What came from there |
| --- | --- | --- |
| Fix Object Unload Crash | ComfyMods — Scenic | The approach |
| Tolerate Duplicate ZDOs On Load | ComfyMods — Atlas | The duplicate-id tolerance |
| Fix Effect Areas | ComfyMods — Effectual | Both defects; our fix for the dangling reference differs |
| Fix Fuel And Ore Loss | Zen.ModLib (catalogue) | The root cause; rewritten as prefixes |
| Share Boss Defeat Keys | Zen.ModLib (catalogue) | The defect and approach; rewritten with one globally registered RPC |
| Fix Recipe Amount Crash | Zen.ModLib (catalogue) | The defect, rewritten |
| Fix Spawner Null Prefabs | ComfyMods — LetMePlay | The same fix |
| Require Lit Fire | Zen.ModLib (catalogue) | The defect, rewritten |
| Fix Run Attack Stamina Drain | Zen.ModLib (catalogue) | The defect; narrowed here to players only |
| Fix Projectile Rotation Spam | ComfyMods — BetterZeeLog | The same fix |
| Fix Send Failure Log Spam | ComfyMods — BetterZeeLog | The defect; that mod removes the call, this one redirects it |
| Fix Container Log Spam | ComfyMods — BetterZeeLog | The defect; that mod removes the calls, this one redirects them |
| Fix Item Icon Crash | ComfyMods — LetMePlay | The defect; a smaller fix here that leaves the shared item data alone |
| Fix Negative Stamina | MidnightsFX | — |
| Fix Dungeon Load Stall | MidnightsFX | — |

## Installation

Install with a mod manager, or drop `ValheimCommunityPatch.dll` into `BepInEx/plugins`.

Requires [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) and
[Jotunn](https://valheim.thunderstore.io/package/ValheimModding/Jotunn/).

**Install it on the server and on every client to get all of it.** It is not required on both:
a modded client can join a vanilla server, and a modded server accepts vanilla clients. The mod adds
no items, prefabs, recipes or save data, so a world it has touched still loads in vanilla, and the
one network message it sends is ignored by anyone who does not have it.

What a one-sided install gets you:

- **Server only** — every *(server)* and *(both)* fix. The *(client)* fixes are not applied at all: a
  dedicated server has no local player, so nothing there could ever reach them. The startup log says
  exactly which counts you got.
- **Client only** — every *(client)* and *(both)* fix, for you specifically. The *(server)* fixes are
  installed but inert unless you are the one hosting.

Three caveats for mixed groups:

- **Require Lit Fire** and **Fix Run Attack Stamina Drain** are player-visible, so players with and
  without the mod will see slightly different behavior on the same server.
- Config values are only synced from the server to clients that have the mod — with a one-sided
  install each machine uses its own config file.
- Several fixes are applied by rewriting the method rather than wrapping it, and those read their
  config once when the game starts. Their descriptions say so. Changing one of those — including a
  value the server syncs down mid-session — does not take effect on that machine until it restarts.

If both the server and a client have the mod, their versions must match on major and minor; Jotunn
refuses the connection otherwise, rather than letting the two sides disagree about behavior.

If a machine really does run headless but should still get the client fixes, set `Patch Every Side`
in the config and restart. This is a last resort — it exists in case the graphics-device check that
identifies a dedicated server ever gets it wrong.

## Running alongside other mods

Several fixes here are applied by rewriting instructions inside a vanilla method rather than wrapping
it, and other mods sometimes rewrite the same methods. All of this mod's rewrites are deliberately
scheduled to run **after** everyone else's, and each one checks that it found what it expected and
leaves the method alone if it did not. So where another mod has already fixed the same defect, that
mod's version wins and this one stands down rather than fighting it.

When that happens you will see a line like *"found no ZLog.Log calls to redirect, so this fix is
inactive"* in the log. That is the mechanism working, not a failure. A real failure looks different —
`fix(es) failed` on the startup line, with the exception above it.

Known overlap: **ComfyMods BetterZeeLog** fixes three of the same defects (container request logging,
"Failed to send data", and the projectile zero-velocity rotation warning). The two are safe to run
together, and BetterZeeLog's versions of those three take effect.

## Reporting a bug

Issues go to [GitHub](https://github.com/MidnightsFX/Valheim-Community-Patch). Please include your
`LogOutput.log` and the list of other mods you are running.

## Contributing a fix

To decide whether a fix belongs here, the test is:
- Does it fix a defect in the game itself?
- Does it address a significant performance problem (eg 2-5x slowdown)

If it does not meet these criteria, it will not be accepted. This mod is purely bug and performance fixes.
