# Valheim Community Patch

Vanilla bug fixes and performance fixes. **No gameplay changes.**

This mod exists to fix things that are broken or pathologically slow in Valheim itself — crashes,
exceptions that silently kill a system, item loss, quadratic hot paths, per-frame allocations, log
spam. It deliberately ships no quality-of-life features, no balance changes, and no content, so you
can install it on a server without anyone having to agree about how the game should play.

Every fix has its own toggle in the config, so you can turn off any single one without a rebuild.
Toggles are admin-only and server-synced.

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

## Fixes in this release

Where a fix was sourced from, or corroborated against, another modder's work, that is recorded per
fix under [Credit and sources](#credit-and-sources).

### Performance

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
- **Fix Tar Pit Memory Leak** *(client)* — tar pits (`LiquidVolume`) allocated their raycast buffers
  with Unity's 4-frame temporary allocator but held them for the object's entire lifetime, leaking
  native memory and spamming `JobTempAlloc has allocations that are more than 4 frames old` on every
  load. Now allocated persistently and disposed safely.
- **Fix Auto Pickup Allocation** *(client)* — the auto-pickup check allocated a fresh array every
  frame for every player.
- **Fix ZDO Packet Allocation** *(both)* — writing one network package into another copied the whole
  thing onto the heap first. That is the innermost step of ZDO synchronisation, so the entire payload
  was duplicated once per peer per send tick, and again on every routed RPC and every receive. The
  bytes on the wire are unchanged; only the copy is gone.
- **Fix Mist Query Overhead** *(client)* — Mistlands fog asks "is this point inside a mist volume"
  for every particle it considers, and each ask reads every mist volume's position from the engine
  and scans all of them — thousands of native reads and full scans per frame in the mist, a large
  share of the biome's frame cost. Volumes are now snapshotted when they spawn or despawn, looked
  up by zone so each check touches only the handful nearby, and the mist system's ground probes are
  answered from terrain data instead of physics rays. Same fog, same monster sightlines.
- **Fix Heightmap Lookup Scan** *(both)* — "which terrain tile is this point on" was answered by
  scanning every loaded tile with a native position read per candidate, thousands of times a second,
  for tiles that never move. Now a zone-keyed lookup, with an admin-only "Verify Heightmap Registry"
  diagnostic that runs both and reports any disagreement.
- **Fix Static Object Ground Checks** *(both)* — every tree and rock continuously re-checks the
  ground under itself, reading its own position from the engine about six times per check. Now once.
  An advanced, default-off toggle can further answer the terrain height from heightmap data instead
  of a physics raycast.
- **Fix Grass Rebuild Burst** *(client)* — loading a zone with saved terrain edits regenerates every
  grass patch around you in a single frame, at hundreds of raycasts per patch — a reliable stutter
  entering built-up areas. The rebuild is now spread over a few frames, nearest patches first.
- **Fix Terrain Builder Throughput** *(both)* — the terrain build thread sleeps 10 ms after every
  single build even with work queued, and throws away finished results beyond 16 held. Zones near a
  moving player wait extra ticks for terrain that is already computed. It now sleeps only when idle
  and holds more results.
- **Fix Zone Collider Stall** *(client)* — **off by default this release.** Crossing into an
  already-explored zone cooks its terrain collision on the main thread, which is most of the stutter
  at zone boundaries in generated terrain. This bakes it on a background thread instead, for exactly
  that case — freshly generating zones and terraforming keep the immediate behaviour. Turn it on if
  you want to help prove it out.
- **Fix Prefab Query Scan** *(both)* — asking "every object of this prefab" scans every ZDO in the
  world. Vanilla only does that from a console command, but it is the API mods use, and several
  popular ones ask every tick — a continuous whole-world scan that was the largest remaining cost
  measured in a heavily modded session. Objects are now indexed by prefab as they are assigned,
  with an admin-only "Verify Prefab Index" diagnostic that runs both paths and reports any
  disagreement.
- **Fix Grass Ground Raycasts** *(client)* — grass placement casts hundreds of physics rays per
  frame to ask where the ground is, but those rays can only ever hit the terrain surface, whose
  shape is already known. The same surface height, slope and biome now come from terrain data
  directly. Grass is cosmetic and regenerated constantly, so nothing saved or synced is involved.
- **Fix Background Zone Pacing** *(server)* — the world pre-generates one full zone every 100 ms
  around each player, entirely inside a single frame, even when that frame is already struggling —
  the steady micro-stutter of exploring fresh terrain. Background pre-generation now waits a tick
  when the previous frame ran long, and an expensive generation imposes a short cooldown before
  the next (both configurable). The same zones generate identically; only the timing spreads out,
  and zones a player actually enters are never delayed.
- **Fix Water Material Lookup** *(client)* — every loaded water tile re-fetches its surface
  material from the engine every frame just to advance the water time on it. The material is now
  cached per tile; the water-time update itself is unchanged.
- **Fix Distant Terrain Hitch** *(client)* — every 256 m of travel, the far-terrain ring rebuilds
  all nine of its meshes in one frame, a fixed-cadence hitch most noticeable while sailing. The
  rebuild is now spread over a few frames (configurable; 9 per frame is vanilla). During the brief
  spread the distant ring is mid-transition, which under distance fog is far less visible than the
  hitch was.
- **Fix Idle Scene Sweep** *(both)* — thirty times a second the game rebuilds and sweeps its lists
  of every loaded object, whether or not anything changed. In a big base that one pass is a
  double-digit share of all frame time, almost entirely re-deriving the answer from 33 ms ago.
  Cheap change-tracking now proves "nothing changed" and skips those passes, with one full pass
  per second kept as a safety sweep and an admin-only "Verify Scene Idle Skip" diagnostic.
- **Fix Support Lookup Cost** *(both)* — every structural-support check walks the object hierarchy
  once per nearby collider to find which building piece owns it — a steady cost that scales with
  base size. A lookup table answers it instead, self-populating and falling back to the walk
  whenever it can't. Admin-only "Verify Support Lookup" compares both. Requires a restart to
  change.
- **Fix Light Flicker Overhead** *(client)* — torch flicker updates every flickering light every
  frame with per-light engine calls, invisible past a few dozen metres; it now stops beyond a
  configurable distance. Alongside it, a client-local "Point Light Limit" exposes the game's own
  dormant nearest-N point-light cap (with its built-in smooth fade) for torch-heavy bases —
  default -1 is exactly vanilla.
- **Fix Piece Event Stall** *(both)* — every building piece subscribes a heightmap event (terrain
  edits use it to flush cached support) whose subscriber array is copied whole on every subscribe
  and scanned whole on every unsubscribe — loading or unloading a chunk of a big base through it
  is a single multi-hundred-millisecond frame, plus an allocation per piece feeding GC pauses. A
  per-heightmap lookup table now delivers the same cache clears in constant time.
- **Fix Unload Sweep Cost** *(both)* — whenever the scene is changing, the game sweeps every
  loaded object thirty times a second to find the few that left the loaded area in the last
  33 ms — a steady share of frame time at large object counts, spent almost entirely on trivial
  calls. The sweep now runs on a configurable wall-clock interval (default 100 ms); departing
  objects linger imperceptibly longer at the far edge of the loaded distance, and each sweep
  costs the same — there are just fewer of them.
- **Fix Spawn Queue Churn** *(both)* — while an area streams in, the game rebuilds and re-sorts
  its entire spawn backlog thirty times a second, only to spawn the first few entries. The
  sorted queue now persists between frames and is rebuilt a few times a second instead; objects
  spawn at the same rate in the same order, only the repeated bookkeeping goes away.
- **Fix Object Stream Rescan** *(both)* — thirty times a second the game rediscovers which
  objects should be loaded by re-reading every zone around you and re-sorting the whole result,
  only to spawn the first few. That rediscovery is the zone-border stutter: it is at its most
  expensive exactly when you cross into a built-up area. The pending set is now kept as a running
  list that the events changing it update directly, so crossing a border adds one new column of
  zones instead of re-reading the loaded world. Objects spawn at the same rate, nearest first.
  Needs "Fix Unload Discovery Scan" on — it stands down otherwise — and an admin-only "Verify
  Spawn Queue" diagnostic checks the list against the full rescan.
- **Fix Zone Occupancy Scan** *(both)* — deciding whether a zone can unload walks every loaded
  object with two engine calls each, per candidate zone. A per-zone tally answers it instantly,
  with an admin-only "Verify Zone Occupancy" diagnostic comparing it against the walk.
- **Fix Piece Material Polling** *(both)* — each spawned piece with material variation schedules
  five string-based engine invokes just to wait for its random seed, then re-writes identical
  values on every poll after the first success. One shared ticker does the waiting, and polling
  stops once the values are applied — same seed, same math, same look.
- **Fix Idle Support Checks** *(both)* — every building piece re-validates its cached structural
  support on every updater visit, re-reading all its neighbours through engine calls just to
  confirm nothing changed — the single biggest steady cost standing in a large base. Pieces now
  sleep until an event that can change support fires (a neighbour built, destroyed or changed,
  or a terrain edit); support changes still cascade exactly as before, and an admin-only
  "Verify Support Sleep" diagnostic checks the sleep predictions against vanilla, reporting why
  any check could not sleep. Two things that used to keep pieces awake are gone: a piece
  streaming back in no longer looks like it changed (the game leaves a placeholder value on it
  at spawn and never restores what it last computed, so its real value arriving looked like an
  event), and a change on one floor no longer wakes every floor above and below it — the
  neighbourhood test now accounts for height, not just the footprint. Neighbours are also
  re-checked only past a "Support Change Threshold", far below anything that affects whether a
  build stands and settable to 0 for an exact comparison. Because the re-check signal is on
  almost permanently in a large base, a piece that has produced the same answer several times
  running ("Settled Piece Patience") may take a slower look when only a neighbour's value
  drifted — anything structural stays immediate, and any real change resets its patience.
- **Fix Smoke Overhead** *(client)* — every smoke puff pays several engine calls every frame: a
  physics mass write whose value is a smooth curve of the smoke's age, and two position reads —
  one to recheck which render chunk it belongs to (the answer changes every few seconds), one to
  build the particle batch. The mass now writes on 2% lifetime steps, the chunk recheck runs
  four times a second, and the position is read once. The smoke looks and moves the same.
- **Fix Unload Discovery Scan** *(both)* — finding which objects left the loaded area used to
  mean stamping every loaded object and walking all of them to see what was missed — a cost that
  scales with everything loaded, to find a handful of departures. A per-zone instance index now
  answers it directly at any world size, unloading returns to vanilla's immediate cadence, and
  an admin-only "Verify Unload Discovery" diagnostic compares the index against vanilla's walk.
- **Fix Idle Wear Visits** *(both)* — even with support checks asleep, every building piece
  still paid its full per-visit wear update just to conclude nothing wears right now. Pieces
  that are provably quiet — support-slept, locally owned, dry or safely roofed while wet,
  above the waterline, outside the Ashlands, undamaged since last visit — now skip the whole
  visit; weather changes, roof changes, damage and repairs wake them immediately, and exposed
  pieces in wet weather run exactly vanilla. Admin-only "Verify Wear Sleep" checks the
  predictions against vanilla and reports why blocked visits could not sleep.
- **Fix Reflection Probe Spikes** *(client)* — the realtime reflection cubemap renders all six
  faces in a single frame every few seconds: a steady share of frame time delivered as periodic
  spikes. It now renders one face per frame at a configurable resolution, with reduced quality
  during the reflection render (lower LOD, no characters or items) — a deliberate trade that is
  hard to spot in a blurry environment reflection. A face is also held back while the previous
  frame ran long, or after a face that was itself expensive, so it lands on a quiet frame
  instead of piling onto one that was already struggling; the reflection is never shown
  half-built, only the frame that pays for a face moves.
- **Fix Physics Catchup Spiral** *(both)* — after a long frame the engine runs up to ~16 fixed
  physics steps of catch-up in the next frame, turning every big hitch into two. A configurable
  cap (default 8) bounds the debt; dropped time is dropped exactly as vanilla drops it past its
  own higher cap.
- **Fix Location Biome Area Rescan** *(server)* — generating a world's locations asks the terrain
  the same question tens of millions of times to get about eighty thousand distinct answers. For
  each of the hundred thousand placement attempts made per location type, the game classifies a
  zone by sampling the terrain noise nine times, and it redoes that from scratch every attempt for
  the same few thousand zones. Each answer is now computed once and remembered for the rest of
  generation. Worlds come out identical — the value handed back is the one vanilla itself produced
  for those exact coordinates, and the random stream that decides placement is untouched — so the
  same seed still gives the same layout, and this is safe to turn on for an existing world.
- **Fix ZDO Value Write Allocation** *(both)* — every write of a ZDO field allocates an object just
  to ask whether the value changed, then compares it and drops it on the same line. That is the only
  path ZDO data is written through, so it is paid by every moving creature's velocity write, every
  animation parameter, and every health, fuel and growth change in the world. The comparison now
  runs against the value's own type instead of a boxed copy — the same answer, down to how it treats
  `NaN`, with the allocation gone.
- **Fix Doubled ZDO Lookups** *(both)* — every read of ZDO data searches for the object twice: once
  to ask whether it holds any data of that kind, then again to fetch it. These are the largest
  dictionaries the game keeps — one entry per object in the world — so each search is a cold read
  through megabytes, and the second one only re-derives what the first already found and threw
  away. One search now answers both. This sits under every health, fuel, growth, state and
  animation read in the world, and under the packing of every object the server sends to a peer or
  writes to a save, which does sixteen of these lookups per object.
- **Fix Collision Contact Allocation** *(both)* — reading a collision's contact points builds a
  brand new array every time, and the game reads them from inside physics callbacks that fire on
  every fixed step for every character the machine owns. The contact data now comes back in a reused
  buffer sized to the real contact count, so everything downstream — including a loop that bounds
  itself on the array's length — sees exactly what it saw before.
- **Fix Collision Callback Allocation** *(both)* — Unity allocates a collision object for every
  collision callback and discards it when the callback returns; it has a setting to reuse one
  instead, on by default in new Unity projects since 2018.3. Every vanilla handler was checked and
  none keeps a collision past its own callback. This is the one fix here that can affect other mods
  — a mod that stashes a collision object for later would read the next collision's data — so it is
  the first thing to turn off if a physics-touching mod starts misbehaving.
- **Fix Equipment Visual Refresh** *(client)* — every character, dropped armour piece and armour
  stand re-derives its whole equipment appearance every frame. Skin and hair colour are re-applied
  on every one of those frames, allocating material and renderer arrays and walking the beard and
  hair hierarchies to write a colour that has not changed since the character was made; that now
  happens only when something it depends on differs. And the fifteen equipment fields are read back
  one at a time, at *two* dictionary lookups each — thirty per character per frame to read fifteen
  values out of one table, which is now fetched once. The appearance is unchanged.

### Terrain

The two seam fixes — **Fix Terrain Seams** and **Fix Terrain Paint Seams** — change only what is
drawn, never what is saved. A dedicated server does build terrain meshes and paint textures, because
its colliders need them, but it never draws either, so both fixes would be computing for nobody
there. A listen host keeps them. **Fix Terrain Paint Zone Fanout** is the exception: it corrects what
gets recorded, so it runs everywhere.

- **Fix Terrain Seams** *(client)* — Valheim shades each 64 m zone's terrain using only that zone's
  own triangles, so the ground on either side of a zone border is lit slightly differently. That
  shows up as a hard crease running through flat terrain, most obvious in the Plains and Meadows.
  Lighting normals are now computed across the boundary so both sides agree. Terrain *geometry* was
  never the problem — world generation already lines up exactly at zone borders.
- **Fix Terrain Paint Seams** *(client)* — terrain paint could end up recorded on only one side of a
  zone border, drawing a hard straight line of dirt across the ground along the 64 m grid. Each zone
  keeps its own copy of the paint sitting on the line between them, and those two copies are now
  merged: paint that the ground on either side actually carries continues across the border, while a
  one-metre stripe that neither side carries is dropped. The merged value is always one of the two
  already stored, so no colour is invented, and nothing saved to the world is changed. Not cosmetic in
  one respect: a new paint operation seeds itself from what the heightmap currently holds, so painting
  near a border afterwards will bake the reconciled value into the saved terrain data.
- **Fix Terrain Paint Zone Fanout** *(both)* — the reason the two sides disagreed in the first place.
  Valheim decides which zones a terrain edit gets recorded into from the tool's radius, but the paint
  itself snaps to the terrain's vertex grid and so reaches about a metre further west and south than
  the tool claims — and the zone next door is never told about the ground they share. Paint is now
  also recorded into the zones it genuinely covers. This one changes what is saved to the world, and
  it only affects edits made from here on; repairing borders that already diverged is the reconcile
  above.
- **Fix Terrain Paint Mask Indexing** *(client)* — three places walk the terrain paint data with the
  wrong stride, skewing it diagonally, and refuse to write the row and column each zone shares with
  its neighbour. All three are only reachable from the `optterrain` console command, so this changes
  nothing during normal play — it makes that repair command correct.
- **Fix Terrain Compiler Init Race** *(both)* — if a zone's terrain compiler loads before its
  heightmap exists, vanilla throws a `NullReferenceException` every frame from then on *and* that
  zone silently stops accepting terrain edits. It now recovers once the heightmap appears.

### Correctness

- **Fix Object Unload Crash** *(both)* — `ZNetScene.RemoveObjects` dereferenced every instance's ZDO
  with no validity check. A single orphaned entry aborted the whole unload pass and then threw again
  every frame, so nothing despawned and memory climbed.
- **Tolerate Duplicate ZDOs On Load** *(server)* — a save containing a duplicate ZDO id made the
  world refuse to load at all. It now recovers and logs a warning.
- **Fix Effect Areas** *(both)* — two fixes. The fixed 128-collider buffer behind fire warmth and
  wetness checks silently truncated, so in a dense base the area that mattered could be missed
  entirely. And a character that died inside an effect area stayed in its list forever, throwing
  every physics step. The second one matters on a server too: that list is walked every physics step
  with no owner check, so a dangling entry there is a permanent exception loop.
- **Fix Fuel And Ore Loss** *(client)* — adding fuel or ore removes the item from your inventory and
  *then* sends a network message that is silently dropped if the owning player is lagging or has
  disconnected. The item is simply destroyed. Ownership is now taken first. This runs on whoever is
  doing the feeding, so it protects the players who have it installed.
- **Share Boss Defeat Keys** *(client)* — the per-player boss defeat key was recorded only on
  whichever client happened to own the boss, so in a group everyone else got no credit. Most visible
  with Hildir's quest bosses. Credit reaches the players who have this installed; the server does not
  need it, and does not need to be modded at all — it relays the message either way.
- **Fix Recipe Amount Crash** *(client)* — `Recipe.GetAmount` dereferenced a null item, so opening
  the crafting or upgrade panel for a "requires any one of these" recipe while carrying none of the
  ingredients threw and left the panel broken.
- **Fix Spawner Null Prefabs** *(both)* — a single null entry in a spawner's creature table (common
  after a game update or a removed creature mod) threw during spawn selection and permanently killed
  that spawner. Null entries are now dropped on load.
- **Require Lit Fire** *(client)* — the burning-area check tested only a fireplace's bounds, never
  whether it was lit, so an unlit or burnt-out fire still counted as a heat source and kept cooking.
  Whoever owns the cooking station decides, so in a mixed group this one is visible: food over a dead
  fire keeps cooking for a player without the mod.
- **Fix Run Attack Stamina Drain** *(client)* — holding sprint while attacking stopped your movement
  but kept charging run stamina for the whole swing. Stamina is simulated locally, so this only
  applies to players who have it installed.
- **Fix Projectile Rotation Spam** *(both)* — every projectile whose velocity reached exactly zero
  made Unity log `Look rotation viewing vector is zero` on every physics step.
- **Fix Send Failure Log Spam** *(both)* — a socket whose send queue has backed up logs `Failed to
  send data` once per frame, which is exactly when the machine can least afford to be formatting
  lines, capturing stack traces and writing to disk. Worse on a server, which holds one socket per
  connected player where a client holds one.
- **Fix Container Log Spam** *(both)* — every chest open, stack-all and take-all writes four lines to
  the log, including one built unconditionally out of the container's name. These land on whoever
  owns the container, so a player sorting their own base logs their own chests and a server logs the
  ones it owns.
- **Fix Item Icon Crash** *(client)* — an item whose stored icon variant no longer matches its icon
  list, after a game update or a removed item mod, threw an unhandled exception from every inventory,
  tooltip and crafting panel that tried to draw it.
- **Fix Negative Stamina** *(client)* — `Player.AddStamina` bounds only the top of the range, so a
  mod that reduces stamina without checking the value first can take it below zero. While stamina is
  negative you cannot attack, run, dodge or build. `NaN` is worse: vanilla's own repairs are all
  comparisons, and every comparison against `NaN` is false, so regeneration never restarts and the
  broken value is written back to your character file on every save — it never recovers, not even
  across a reload. Stamina is now floored at zero where it is written, and a character already saved
  in that state is repaired as it loads. A `UseStamina` network message carrying `NaN` or infinity is
  dropped rather than applied, since the network handler screens nothing on the way in and the public
  wrapper's own `NaN` check runs one line before it multiplies by a world-key scalar that can itself
  be `NaN`.
- **Fix Dungeon Load Stall** *(both)* — a dungeon loads its rooms asynchronously and flags its zone as
  "loading" until every one of them arrives. A room whose asset fails to load never reports back, so
  the count never completes: the dungeon interior never appears, and the zone stays flagged forever.
  A flagged zone stops spawning objects, and anyone who spawns or teleports into it sits on the
  loading screen indefinitely. Failed rooms are now accounted for and dropped, so the rest of the
  dungeon still spawns and the zone finishes loading. This is *not* the same as a room prefab going
  missing after a mod is removed — vanilla already handles that one.

The two log fixes redirect rather than delete: turn on `EnableDebugMode` and the messages come back.

## Credit and sources

This mod fixes vanilla defects, and other modders found — and in several cases already fixed — a good
number of them first. Where that is true it is recorded below, and in a comment at the top of the
patch file. [CREDITS.md][credits] has the detail: exactly what was taken, what was deliberately left
out, and how our implementation differs and why.

[credits]: https://github.com/MidnightsFX/Valheim-Community-Patch/blob/master/CREDITS.md
[vpo]: https://github.com/ontrigger/ValheimPerformanceOptimizations

The mods involved:

- **[ComfyMods](https://github.com/redseiko/ComfyMods)** — redseiko (GPL-3.0), the same licence as
  this project. Seven of its mods — BetterZeeLog, LetMePlay, BetterServerPortals, Scenic, Compress,
  Effectual and Atlas — account for eleven of the entries below.
- **[ValheimPerformanceOptimizations](https://github.com/ontrigger/ValheimPerformanceOptimizations)**
  — ontrigger (MIT). Three of the performance entries below, including the event-fed spawn queue the
  object-stream rescan fix is built on, plus independent corroboration of two more.
- **[MyPitsDontLeak](https://github.com/AzumattDev/MyPitsDontLeak)** — Azumatt (MIT).
- **[Terramizer](https://thunderstore.io/c/valheim/p/Terramizer/Terramizer/)** — R4V9N1. Three of
  the allocation fixes below. Its terrain-limit and placement-effect features are gameplay and
  visual changes and are deliberately not here.
- **Zen.ModLib** — ZenDragon. Used only as a *catalogue* of which vanilla bugs exist; no code was
  copied.
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
| Fix ZDO Value Write Allocation | Terramizer — R4V9N1 | The boxed comparison in `BinarySearchDictionary.SetValue`. That mod replaces the method with a hand-written copy driven by reflected field refs; this is a three-instruction IL edit, so the growth policy and binary search stay vanilla's |
| Fix Doubled ZDO Lookups | MidnightsFX | — |
| Fix Collision Contact Allocation | MidnightsFX | — |
| Fix Collision Callback Allocation | Terramizer — R4V9N1 | The same `Physics.reuseCollisionCallbacks` flag; the per-handler audit and the restore-on-disable are ours |
| Fix Equipment Visual Refresh | Terramizer — R4V9N1 | The ZDO int-table cache across `UpdateEquipmentVisuals`, same prefix-plus-transpiler shape, keyed here on ZDO identity. The `UpdateColors` change gate is not in that mod |

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
| Fix Item Icon Crash | ComfyMods — LetMePlay | The defect; deliberately a smaller fix here, see CREDITS.md |
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

- **Server only** — the 4 *(server)* fixes and the 10 *(both)* fixes. The 12 *(client)* fixes are not
  applied at all: a dedicated server has no local player, so nothing there could ever reach them. The
  startup log says exactly which counts you got.
- **Client only** — every *(client)* and *(both)* fix, for you specifically — 22 of the 26. The
  *(server)* fixes are installed but inert unless you are the one hosting.

Three caveats for mixed groups:

- **Require Lit Fire** and **Fix Run Attack Stamina Drain** are player-visible, so players with and
  without the mod will see slightly different behaviour on the same server.
- Config toggles are only synced from the server to clients that have the mod — with a one-sided
  install each machine uses its own config file.
- Several fixes are applied by rewriting the method rather than wrapping it, and those read their
  toggle once when the game starts. Their descriptions say so. Changing one of those — including a
  value the server syncs down mid-session — does not take effect on that machine until it restarts.

If both the server and a client have the mod, their versions must match on major and minor; Jotunn
refuses the connection otherwise, rather than letting the two sides disagree about behaviour.

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

To decide whether a fix belongs here, the test is: *is the current behaviour something Iron Gate
would call a bug?* If it is a matter of taste, it belongs in a different mod.
