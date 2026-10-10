# Valheim Community Patch

Vanilla bug fixes and performance fixes. **No gameplay changes.**

This mod exists to fix things that are broken or pathologically slow in Valheim itself — crashes,
exceptions that silently kill a system, item loss, quadratic hot paths, per-frame allocations, log
spam. It deliberately ships no quality-of-life features, no balance changes, and no content, so you
can install it on a server without anyone having to agree about how the game should play.

Performance fixes are always on; the config holds only their tuning values and the admin-only Verify
diagnostics. Correctness and terrain fixes each have their own toggle. Server memory fixes are the
exception: they are off by default and opt-in, see [Server memory](#server-memory-opt-in). The
dedicated-server garbage collector settings moved to a separate plugin, see
[Server garbage collector](#server-garbage-collector). Config values are admin-only and
server-synced.

Each fix is tagged with the side it is worth installing on. *(server)* fixes only do something on
the machine hosting the world — a dedicated server or a listen host. *(client)* fixes need a local
player, and are **not even applied** on a dedicated server, which is why the startup log there
reports a smaller number. *(both)* fixes do work wherever they are installed. A one-sided install is
safe; it just gets you a subset. See [Installation](#installation).

### Why so many fixes are client-side

A Valheim dedicated server is not a simulation host. It only creates game objects inside its *own*
active area, and a dedicated server never has a player to anchor that area to, so the game parks it
far outside the world for the whole run. Creatures, fires, chests, tar pits and everything else near
a player are created, owned and simulated on that player's client; the server holds the data and
relays it.

So the fixes a server gains from are the data-and-network ones — ZDO handling, world load, packet
allocation, socket logging — and almost everything else is worth having on the client.

## Performance Side by Side

[![](https://markdown-videos-api.jorgenkh.no/youtube/UrSF0NYgFpo)](https://youtu.be/UrSF0NYgFpo)

## Terrain Fix comparison

| Terrain Tears | Terrain Fixed |
|---|---|
| ![Terrain Tears](https://github.com/MidnightsFX/Valheim-Community-Patch/blob/master/Media/Terrain_Tears.png?raw=true)   |  ![Terrain Fixed](https://github.com/MidnightsFX/Valheim-Community-Patch/blob/master/Media/Terrain_Fixed.png?raw=true) |

| Shore Shade Tears | Shore Shade Fixed |
|---|---|
| ![Shore Tears](https://github.com/MidnightsFX/Valheim-Community-Patch/blob/master/Media/Shore_Tears.png?raw=true)   |  ![Shore Fixed](https://github.com/MidnightsFX/Valheim-Community-Patch/blob/master/Media/Shore_Fixed.png?raw=true) |

Shore tears are much more subtle as it is a shading issue. On the right side of the first image directly above the head you can see the shading issue.

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
- **Fix Tar Pit Buffer Disposal** *(client)* — disposes tar pit raycast buffers only when they were
  allocated, so a tar pit whose load failed cannot throw out of scene teardown.
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
- **Fix Light Flicker Overhead** *(client)* — stops updating torch flicker for lights past both a
  configurable distance and their own light LOD distance.
- **Fix Piece Event Stall** *(both)* — registers building pieces for terrain-rebuild cache clears in a
  per-heightmap table instead of an event whose subscribe copies and unsubscribe scans the whole list.
- **Fix Unload Sweep Cost** *(both)* — runs the object-unload sweep on a configurable wall-clock
  interval (default 100 ms) instead of on every pass.
- **Fix Spawn Queue Churn** *(both)* — keeps the sorted spawn backlog between frames instead of
  rebuilding and re-sorting it thirty times a second.
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
- **Fix ZDO Value Write Allocation** *(both)* — compares a ZDO field write against its stored value
  without boxing it.
- **Fix Doubled ZDO Lookups** *(both)* — reads ZDO data with one dictionary lookup instead of two.
- **Fix Collision Contact Allocation** *(both)* — reads a collision's contact points into a reused
  buffer instead of allocating an array per collision callback.
- **Fix Collision Callback Allocation** *(both)* — turns on Unity's `reuseCollisionCallbacks` so one
  collision object serves every callback; the first suspect if a physics-touching mod that stores
  collision objects misbehaves.
- **Fix Equipment Visual Refresh** *(client)* — re-applies a character's skin and hair color only
  when an input changed or another system overwrote it, and reads its equipment fields with one
  table lookup instead of thirty.
- **Fix Light Settings Subscription** *(client)* — registers lights for graphics-setting changes in a
  lookup table instead of a static event whose unsubscribe scans every other lit light.
- **Fix Portal Idle Updates** *(client)* — stops a portal re-writing its emission color, light and
  audio every frame once the connection fade has finished.
- **Fix Material Fader Settling** *(client)* — stops a finished fade re-applying the same material
  property block to every renderer every frame; lingering corpses are the common case.
- **Fix Cooking Slot Keys** *(both)* — reads and writes a cooking station's slot data through
  precomputed key hashes instead of building six strings per access, as `ArmorStand` already does.
- **Fix Station Range Scans** *(client)* — answers the per-frame build-mode station and extension
  range queries with squared distances and one position read per candidate instead of two.
- **Fix Smelter Catch-up Reads** *(both)* — memoises a smelter's fuel and ore reads against its data
  revision, so an hour of missed production is not re-read once per simulated second.
- **Fix Idle Sound Updates** *(client)* — drops a finished one-shot sound out of the per-frame audio
  updater list until something plays it again.
- **Fix Loading Zone Cadence** *(client)* — behind a portal, respawn or login loading screen,
  installs the zones around you nearest first, as many per tick as `Loading Zone Budget` allows
  (default 30 ms, 0 is vanilla), instead of one zone every 0.1 s, which at the highest simulation
  distance alone kept the loading screen up for 13.7 s.
- **Fix Reference Position Send Delay** *(client)* — tells the server where you are as soon as a
  portal, respawn or login moves you two or more zones, instead of at the next 2 s report, so the
  server starts sending the new area at once.
- **Fix Destination Terrain Delay** *(client)* — starts building the ground at a teleport's destination
  as the fade begins, and at your bed while you are dead, instead of only once you arrive, so the
  loading screen no longer waits for it. It never asks for more than the terrain builder can hold, so
  nothing finished is thrown away before it is used.
- **Fix Portal Destination Send Delay** *(both)* — while you stand within `Portal Prefetch Range`
  (default 10 m) of a connected portal, the server starts sending the building pieces and terrain
  edits around the other end, instead of only once you have gone through, so the loading screen behind
  the portal has less to wait for. The server checks that you really are at that portal and works out
  its destination itself, and it uses only what your connection has spare after the area around you,
  nearest pieces first. Needs the mod on the server and the client; players without it are unaffected.
  Can be turned off, for servers that would rather not send players areas they may not visit.
- **Fix Minimap Fog Upload** *(client)* — uploads only the patch of map fog you just revealed
  instead of the whole 8 MiB fog texture every time exploring reveals new ground.
- **Fix Dungeon Spawn Hitch** *(client)* — places the rooms of a dungeon or camp loading in near you
  over a few frames within `Dungeon Room Budget` (default 4 ms, 0 is vanilla) instead of all in one
  frame; the zone's contents wait for the last room as they do while the rooms load, and nothing
  is spread behind a loading screen.
- **Fix Location Spawn Hitch** *(client)* — spawns one location per frame plus as many more as
  `Location Spawn Budget` allows (default 4 ms, 0 is vanilla), through the game's own wait-and-retry,
  instead of every ready location in the same frame; nothing waits behind a loading screen.
- **Fix Location Room Preload** *(server)* — loads each location and its dungeon's rooms in the
  background as soon as its zone enters the ring generated ahead of the players, instead of when
  generation reaches it; the whole zone, ground included, waits for that load, so while travelling
  the ground went missing until it finished. A zone you are about to enter waits at most `Location
  Load Wait` (default 1 s) before loading what is left at once. Also makes a first-time location
  wait for its room prefabs as the game intends when a copy already standing nearby loaded only the
  location: the game then loaded every room in one frame, over a second for the Deep North halls.
- **Fix Background Dungeon Generation** *(server)* — lays out dungeons and camps generated in the
  ring ahead of the players with lightweight stand-ins instead of full room models. The game built
  every room model there only to read its layout and destroy it again within the frame; the dungeon,
  its contents and its saved room list come out the same, and the rooms are built when players
  arrive, as before. Rooms with terrain-layer colliders are still built in full, and the fix stands
  down when another mod hooks room placement. `Verify Background Dungeon Stand-ins` in the `Debug`
  section builds both and logs any difference.
- **Fix Location Model Placeholders** *(client)* — builds location and dungeon room models from a
  copy of the prefab without the networked objects (chests, trees, creatures, ore) the model never
  uses. Those come from the world's saved data, so the game clones them switched off and leaves them
  dead in the model while it is loaded; in the Deep North they are most of it, 33,390 of The Hole's
  33,587 objects, 99% of a north village house and two thirds of a hall. A template is made, between
  frames, once a prefab has been used twice and has enough of them; a networked object the model
  refers to stays in it as an empty, switched-off placeholder, so every reference survives. It keeps
  its prefab loaded, so a dungeon's rooms keep their templates between visits, and is dropped after
  five minutes unused; until then, and when another mod hooks location spawning or room placement,
  the game's own clone is used. `Verify Location Model Templates` in the `Debug` section builds both and logs any
  difference.
- **Fix Arriving Location Hitch** *(client)* — when the host reaches a zone holding a location
  before the game has generated it, generates the zone in the background first and loads it on the
  next tick the way already-generated land loads: objects a few per frame, the location model within
  Fix Location Spawn Hitch's budget and dungeon rooms within Fix Dungeon Spawn Hitch's. The game
  otherwise builds every object, the whole location model and every room model of such a zone in
  one frame. The world and its dungeons come out the same; only when the visible parts are built
  changes. The player's own zone, loading screens and zones without a location are left to the game.
- **Fix Hotbar Key Allocation** *(client)* — checks the hotbar keys with cached key names instead of
  formatting up to sixteen new strings every frame.
- **Fix Equipment Modifier Allocation** *(client)* — totals your equipment's movement, stamina and
  heat modifiers through typed field reads instead of boxing every value fifty times a second, with
  identical results.
- **Fix Active Area Check Allocation** *(both)* — tests whether a point is inside the loaded area
  without allocating an array per test, which wear updates, physics settling and the server's
  ownership release do thousands of times a second.
- **Fix Idle Creature Sync** *(both)* — stops a standing creature's owner re-sending it to every
  player each frame over physics jitter: its position, velocity and ground tilt are sent once they
  move more than 2 cm, 0.05 m/s or 1 degree from what was last sent, instead of on any change at
  all. Players, ships, carts and items are untouched. The one performance fix with a toggle, since
  it changes what other players see, by at most those amounts.
- **Fix Paint-Only Terrain Rebuilds** *(both)* — when something that only paints the ground loads
  or unloads, like the big rocks and copper deposits of the Black Forest, coast and heath, refreshes
  just the terrain paint under it instead of also rebuilding the ground's collision and shape. Each
  such refresh checks the ground's heights came out the same, and does the full rebuild if not.

### Terrain

**Fix Terrain Seams**, **Fix Terrain Paint Seams** and **Fix Swamp Plains Shore Seams** change only
what is drawn, never what is saved, so they are client-only. **Fix Terrain Paint Zone Fanout** and **Fix Terrain Paint Doubling** change
what is recorded, so they run on every side.

- **Fix Terrain Seams** *(client)* — computes terrain lighting normals across zone boundaries,
  removing the hard crease along the 64 m grid.
- **Fix Terrain Paint Seams** *(client)* — merges the two copies of the paint on a zone boundary so
  dirt continues across the line instead of stopping dead; a lone stripe on the border is dropped.
- **Fix Terrain Paint Zone Fanout** *(both)* — records terrain paint into every zone it actually
  covers, including the neighbour about a metre west or south that vanilla left out; affects edits
  made from now on.
- **Fix Terrain Paint Doubling** *(both)* — applies an edit that reaches two zones once to the
  ground they share, instead of twice (four times at a corner); in the Deep North that stops
  piling or clearing snow from leaving a ridge or trench along the zone line.
- **Fix Swamp Plains Shore Seams** *(client)* — stops shores where swamp blends into plains from being
  drawn with the Ashlands shoreline in hard straight lines along the zone grid. The game's terrain
  colors cannot express that blend without also meaning Ashlands, so the two are bridged by a
  narrow strip where they meet instead: black forest ground by default, or Ashlands ground that
  follows the crossover rather than the zone grid ("Swamp Plains Bridge Ground"; "Swamp Plains
  Blend Sharpness" sets the strip's width).
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
- **Refund Rejected Station Items** *(both)* — drops an item back at the player when a smelter, kiln,
  fireplace, cooking station or fermenter discards it on arrival because the station changed owner or
  filled up after the item left their inventory; covers a vanilla sender and the "last slot" race.
  Inactive when AutomaticFuel, or Eternal Fire older than 1.1.6, is installed.
- **Share Boss Defeat Keys** *(client)* — gives every nearby player the per-player boss defeat key,
  not only the one whose client owned the boss; works against a vanilla server.
- **Fix Recipe Amount Crash** *(client)* — guards the null dereference that broke the crafting panel
  for a "requires any one of these" recipe with none of the ingredients carried.
- **Fix Spawner Null Prefabs** *(both)* — drops null entries from a spawner's creature table on load
  instead of letting one kill the spawner.
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
- **Fix Teleport Ghost Players** *(server)* — tells a client to drop a player who teleported out of its
  loaded area, instead of leaving them standing frozen where they left (and still in local chat range).
- **Fix Unsaved Client Changes** *(server)* — marks an object a connected player placed or changed for
  the next world save; the chunked save format only rewrites chunks the game marked as changed, and it
  never marks one for a change that arrives from another player.
- **Fix Water Color Seams** *(client)* — colors each water tile by the depth blended across it, as its
  waves already are, instead of by one corner, removing the hard color line along the 64 m grid.
  `Water Color Shore Tint` (0 to 1, default 0.75) moves the sandy shallow-water color towards the deep-sea
  color, so one shallow corner no longer tints a whole tile of open sea; it changes color only, never waves.
- **Fix Non-Item ObjectDB Entries** *(both)* — drops prefabs that are not items from the game's item
  list on load; the 2026-09-09 update listed three (`PropFeastDeepNorth`, `SnowRoller`,
  `FrozenKing_Summon`) that break mods treating every entry as an item. They can still be spawned.
- **Clear Patrol Point On Taming** *(both)* — clears the patrol point a spawner stamped on a creature
  when you tame it, so it settles where you keep it instead of running back to where it spawned.
  Mostly boars, which cannot be told to follow or stay and so had no way to clear it. Telling a tamed
  creature to stay still sets a patrol point as normal.
- **Fix Map Auto-Close** *(client)* — stops the map closing for every player on the server whenever
  anyone wakes or kills a boss; vanilla closes an open map on every world key change. It still closes
  for a player with a boss within `Map Auto-Close Boss Range` (default 100 m, the boss health bar
  range).
- **Fix Biome Sector Lookup** *(both)* — makes the biome sector lookup agree with the actual biome near
  borders. Vanilla reads its 12 m biome grid rounded down, so within about 12 m of a border the player's
  biome, weather, spawn levels, the map's biome name and terrain coloring follow the neighbouring biome,
  and a `GetBiome error` warning is logged every second. Location placement keeps vanilla's lookup, so
  seeds still place locations where unmodded Valheim does.
- **Fix Loading Screen Hang** *(both)* — unregisters a networked object that was destroyed without the
  game's object manager being told, so a dungeon exit, portal trip or respawn next to it can finish
  loading. Vanilla leaves one behind on your client each time another player opens an Infested Mine
  treasure pile near you, and the loading screen then waits on it until you quit.
- **Fix Equipment Texture Errors** *(client)* — stops the `doesn't have a texture property` errors each
  Deep North Shadow logs when it is created, about eleven per Shadow and several Shadows per village.
  The game copies its body and armour textures from a material that has none of them; a missing
  texture is now read as empty, which is what the game ended up with anyway, and the Shadow looks the
  same.
- **Fix Unkillable Creatures** *(both)* — finishes the death of a creature left alive at zero health,
  which in vanilla ignores every hit for good. Mostly Deep North creatures whose owner changed during
  their death animation, which a busy server makes more likely, and creatures whose death another
  mod's code broke. A creature whose health another mod made `NaN` is put back to full health.
- **Fix Cloth Wind Shelter Errors** *(both)* — stops the `MagicaCloth component not found` error the
  Root Crown logs, stack trace included, each time one is created: dropped, put on a stand, or worn by
  a player coming into range. Its hat model carries the Deep North cloth wind shelter without the cloth
  it controls; the shelter now switches itself off quietly, and the crown looks the same.
- **Fix Loading Screen Wait** *(both)* — ends a portal, respawn or login loading screen once the server
  confirms it has sent every building piece and terrain edit around the destination and the area is
  built, instead of after a fixed 8 seconds. The fixed wait is too short for a big base on a busy
  server, which drops you in under floors that have not arrived yet, and longer than needed everywhere
  else. A client of a dedicated server needs the mod on the server too; against a server without it
  the game's own timings apply. A host or single-player game waits only for the area to be built. If
  the server has not confirmed within `Loading Confirmation Timeout` (default 20 s), the game's own
  checks decide.
- **Fix Lost Bed Spawn Point** *(client)* — keeps your bed as the respawn point while the server has
  not yet sent the area around it, for up to 30 s, instead of sending you to the world start. Vanilla
  decides the bed is gone as soon as the ground there has loaded, which can be before the buildings
  have; mods that shorten the respawn wait make it common.

The log fixes, Fix Biome Sector Lookup's warning, Fix Equipment Texture Errors and Fix Cloth Wind
Shelter Errors redirect rather than delete: turn on `EnableDebugMode` and the messages come back.

### Server memory (opt-in)

These are **off by default**. Each trades a little re-allocation or a shorter safety margin for
memory a busy dedicated server otherwise never gets back, which is the operator's call, so each
has its own toggle in the `Fixes - Server Memory` config section. They exist for servers whose
memory climbs with uptime under many players; a small server gains little from them. The `vcp_zdomem` server
console command (an admin can run it remotely) and the `Log ZDO Memory Stats` diagnostic print the
sizes involved, so the effect can be seen before and after turning one on.

- **Evict Dead ZDO Records** *(server)* — drops the record of a destroyed object after `Dead ZDO
  Retain Seconds` (default 600). Vanilla keeps the id of every object destroyed since the world
  loaded, to reject a stale copy a player might still send back, and never clears that list while the
  world is loaded; the stale copies it guards against arrive within seconds of the destroy.
- **Trim ZDO Data Pool** *(server)* — caps, at `ZDO Data Pool Max Depth` per field type, the pools
  that recycle objects' field tables, and clears the strings and byte arrays a recycled table still
  points at. Without it the pools hold as many tables as were ever in use at once, each keeping its
  last contents alive, so memory never comes back down from the busiest moment since the world
  loaded. A plateau rather than a climb, and the smallest of the three.
- **Fix ZDO Serialize Allocation** *(server)* — writes an object's fields to the network straight
  from their tables instead of copying all seven into fresh lists, plus seven callbacks, per object
  per player per send tick. Garbage-collector churn rather than a leak; the bytes sent are identical.

### Server garbage collector

The dedicated-server garbage collector settings and the `vcp_gc` console command that shipped in
0.31.0 are now a separate plugin,
[Valheim Community Patch GC](https://thunderstore.io/c/valheim/p/MidnightMods/ValheimCommunityPatchGC/).
They read and write the Mono runtime's own memory, which malware scanners flag. Install it on a
dedicated server alongside this mod; it pre-grows the collector's mark stack against
`Unexpected mark stack overflow` crashes on large worlds. The old `Server - Garbage Collector`
section in this mod's config is no longer read and can be deleted.

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
  — ontrigger (MIT). Independent corroboration of one of the performance entries below.
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
| Fix Tar Pit Buffer Disposal | MyPitsDontLeak — Azumatt | The root cause; our implementation is a transpiler rather than wholesale method replacement |
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
| Fix Light Flicker Overhead | MidnightsFX | — |
| Fix Piece Event Stall | MidnightsFX | — |
| Fix Unload Sweep Cost | MidnightsFX | — |
| Fix Spawn Queue Churn | MidnightsFX | — |
| Fix Zone Occupancy Scan | MidnightsFX | — |
| Fix Piece Material Polling | MidnightsFX | — |
| Fix Idle Support Checks | MidnightsFX | — |
| Fix Smoke Overhead | MidnightsFX | — |
| Fix Unload Discovery Scan | MidnightsFX | — |
| Fix Idle Wear Visits | MidnightsFX | — |
| Fix ZDO Value Write Allocation | MidnightsFX | — |
| Fix Doubled ZDO Lookups | MidnightsFX | — |
| Fix Collision Contact Allocation | MidnightsFX | — |
| Fix Collision Callback Allocation | MidnightsFX | — |
| Fix Equipment Visual Refresh | MidnightsFX | — |
| Fix Light Settings Subscription | MidnightsFX | — |
| Fix Portal Idle Updates | MidnightsFX | — |
| Fix Material Fader Settling | MidnightsFX | — |
| Fix Cooking Slot Keys | Iron Gate Studio | The key-hash pattern, taken from `ArmorStand.InitKeys` |
| Fix Station Range Scans | MidnightsFX | — |
| Fix Smelter Catch-up Reads | MidnightsFX | — |
| Fix Idle Sound Updates | MidnightsFX | — |
| Fix Loading Zone Cadence | MidnightsFX | — |
| Fix Reference Position Send Delay | MidnightsFX | — |
| Fix Destination Terrain Delay | MidnightsFX | — |
| Fix Portal Destination Send Delay | MidnightsFX | — |
| Fix Minimap Fog Upload | MidnightsFX | — |
| Fix Dungeon Spawn Hitch | MidnightsFX | — |
| Fix Location Spawn Hitch | MidnightsFX | — |
| Fix Location Room Preload | MidnightsFX | — |
| Fix Background Dungeon Generation | MidnightsFX | — |
| Fix Arriving Location Hitch | MidnightsFX | — |
| Fix Location Model Placeholders | MidnightsFX | — |
| Fix Hotbar Key Allocation | MidnightsFX | — |
| Fix Equipment Modifier Allocation | MidnightsFX | — |
| Fix Active Area Check Allocation | MidnightsFX | — |
| Fix Idle Creature Sync | MidnightsFX | — |
| Fix Paint-Only Terrain Rebuilds | MidnightsFX | — |

### Terrain

| Fix | Sourced from | What came from there |
| --- | --- | --- |
| Fix Terrain Seams | MidnightsFX | — |
| Fix Terrain Paint Seams | MidnightsFX | — |
| Fix Terrain Paint Zone Fanout | MidnightsFX | — |
| Fix Terrain Paint Doubling | MidnightsFX | — |
| Fix Swamp Plains Shore Seams | MidnightsFX | — |
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
| Fix Teleport Ghost Players | MidnightsFX | — |
| Fix Unsaved Client Changes | MidnightsFX | — |
| Fix Water Color Seams | MidnightsFX | — |
| Fix Non-Item ObjectDB Entries | MidnightsFX | — |
| Clear Patrol Point On Taming | MidnightsFX | — |
| Fix Map Auto-Close | MidnightsFX | — |
| Fix Biome Sector Lookup | MidnightsFX | — |
| Fix Loading Screen Hang | MidnightsFX | — |
| Fix Equipment Texture Errors | nezuma — ShadowPersonMaterialFix | The defect; that mod swaps the Shadow's shader, this one guards the reads |
| Fix Unkillable Creatures | MidnightsFX | The `NaN` health handling follows StarLevelSystem's |
| Fix Loading Screen Wait | MidnightsFX | — |
| Fix Lost Bed Spawn Point | MidnightsFX | — |

### Server memory

| Fix | Sourced from | What came from there |
| --- | --- | --- |
| Evict Dead ZDO Records | MidnightsFX | — |
| Trim ZDO Data Pool | MidnightsFX | — |
| Fix ZDO Serialize Allocation | MidnightsFX | — |

## Installation

Install with a mod manager, or drop `ValheimCommunityPatch.dll` into `BepInEx/plugins`.

Requires [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) and
[Jotunn](https://valheim.thunderstore.io/package/ValheimModding/Jotunn/).

**Install it on the server and on every client to get all of it.** It is not required on both:
a modded client can join a vanilla server, and a modded server accepts vanilla clients. The mod adds
no items, prefabs, recipes or save data, so a world it has touched still loads in vanilla, and the
network messages it sends are ignored by anyone who does not have it.

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

Object unloading follows the same principle. Fix Unload Discovery Scan and Fix Object Unload Crash
decide what to unload only after every other mod has had its say, so a mod that keeps an object
loaded (SeidrChest's remote chest, for example) still keeps it loaded, and a mod that replaces the
unload pass itself takes over from both. When another mod does change what stays loaded, the log
says so once with *"Unload discovery: another mod changed which objects stay loaded"*; that is also
the mechanism working.

Known overlap: **ComfyMods BetterZeeLog** fixes three of the same defects (container request logging,
"Failed to send data", and the projectile zero-velocity rotation warning). The two are safe to run
together, and BetterZeeLog's versions of those three take effect.

Known overlap: **Network Performance System** 1.9.1 and later apply the same idle creature fix as Fix
Idle Creature Sync. When it is installed this mod's version stands down and says so once in the log.

Known overlap: **ZenPortal** has its own rules for which portals may pair: portals without a rune
shard never link, each rune links one pair, and wood and stone portals can be kept apart. Fix Portal
Connection Scan replaces the game's pairing pass, which would skip those rules, so it stands down
whenever another mod patches that pass and says so once in the log with *"Portal pairing is changed
by ..."*. The game's own pairing then runs with the other mod's rules applied.

Known overlap: **AutomaticFuel**, and **Eternal Fire** older than 1.1.6, add fuel to fires, ovens and
smelters without taking an item, or send more than the station has room for. Refund Rejected Station
Items would turn each of those into a free item, so it stands down when either is installed and says
so once in the log with *"'Refund Rejected Station Items' is disabled because ..."*. Without it,
items put into fermenters, ovens and cooking stations can be lost in multiplayer, as in vanilla.
Eternal Fire 1.1.6 and later no longer does this, and the fix stays on beside it.

Known overlap: **Expand World Size** resizes and stretches the game's biome grid along with the world.
Fix Biome Sector Lookup reads the grid's size and spacing from the game on every lookup, so it follows
the resized grid. Before 0.32.3 it assumed the vanilla grid and gave wrong biomes, weather and terrain
colors on resized worlds.

Known overlap: **Fast Loading (Portals-Respawn-Login-Dungeons)** installs the zones around you itself
during loading screens, so Fix Loading Zone Cadence stands down when it is installed and says so once
in the log with *"Zone loading is changed by ..."*. Fix Loading Screen Wait and Fix Lost Bed Spawn
Point stay on beside it, and on a dedicated server they keep its shortened waits from dropping you in
before your buildings arrive or from losing your bed spawn point.

Known overlap: **SteadyFrame** replaces the game's fixed portal arrival wait with its own timing.
Before 0.34.0 that switched Fix Loading Screen Wait off for portals; now SteadyFrame's timing applies
and the arrival still waits for the server to send the destination, and the log says so once with
*"another mod has replaced the fixed portal arrival wait ..."*. Its building support lookup is the
same as Fix Support Lookup Cost's, so that fix stands down with a warning in the log and SteadyFrame's
version applies. SteadyFrame turns its own object streaming, terrain and housekeeping changes off when this
mod is installed.

Known overlap: **ValheimOptimized** builds terrain colliders on a background thread itself, as Fix
Zone Collider Stall does. Before 0.32.5 the two worked on the same terrain at once and crashed the
game. Fix Zone Collider Stall now stands down whenever another mod takes over building terrain
colliders, and says so once in the log with *"Terrain collider building is changed by ..."*.
ValheimOptimized also builds most terrain meshes on its own threads, out of reach of Fix Terrain Seams
and Fix Swamp Plains Shore Seams, so before 0.32.5 those reached only some of the ground and a fixed
spot lost the fix when it was rebuilt. Both now stand down whenever another mod takes over building
terrain meshes, and say so once in the log with *"Terrain mesh building is changed by ..."*. Zone
borders and swamp shores then look as they do with ValheimOptimized alone. ValheimOptimized also
replaces the game's object spawning and its distant-terrain rebuild. Fix Spawn Queue Churn now spawns
nothing on a pass another mod has already handled, where before 0.32.5 it spawned a second batch, and
Fix Distant Terrain Hitch stands down and says so once with *"Distant terrain rebuilding is changed by
..."*. Fix Piece Event Stall now also reaches building pieces when ValheimOptimized finishes a terrain
rebuild late, so their support no longer stays out of date. ValheimOptimized has its own versions of
the 0.33.0 fixes too. Fix Dungeon Spawn Hitch and Fix Location Spawn Hitch stand down and say so once
with *"Dungeon room placement is changed by ..."* and *"Location spawn timing is changed by ..."*,
Fix Hotbar Key Allocation says at startup that the method was already rewritten, and the other three
are simply not reached; in each case ValheimOptimized's version applies.

Known overlap: **HearthBelow** lets players dig into the ground. The dug-out ground exists only in
HearthBelow's own collision meshes, and the game's terrain data still describes the surface from
before digging. Fix Grass Ground Raycasts reads that data instead of casting a ray at the ground, so
before 0.34.1 grass regrew floating over dug-out ground after a hoe or cultivator edit there. Fix
Static Object Ground Checks replaces the ground check that HearthBelow hooks to stop objects in dug-out
ground being pushed up out of it. While HearthBelow is installed, both fixes stand down and say so
once in the log with *"HearthBelow is loaded, so '...' stands down"*. Grass placement and the ground
check then run as they do in the game, with HearthBelow's changes applied.

Known overlap: **Valheim Performance Overhaul** replaces each smoke puff's update with its own, as Fix
Smoke Overhead does. Before 0.34.2 both ran on every puff, so smoke aged twice as fast and rose in
shorter columns. Fix Smoke Overhead now leaves a puff's update to a mod that has already replaced it,
and says so once in the log with *"Smoke puff updates are replaced by ..."*. Its smoke rendering
change stays on. With Valheim Performance Overhaul's smoke option off, this mod's version applies as
before.

## Reporting a bug

Issues go to [GitHub](https://github.com/MidnightsFX/Valheim-Community-Patch). Please include your
`LogOutput.log` and the list of other mods you are running.

## Contributing a fix

To decide whether a fix belongs here, the test is:
- Does it fix a defect in the game itself?
- Does it address a significant performance problem (eg 2-5x slowdown)

If it does not meet these criteria, it will not be accepted. This mod is purely bug and performance fixes.
