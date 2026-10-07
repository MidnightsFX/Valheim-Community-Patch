# Changelog

**0.34.0**
```
- Dungeons and camps generated in land ahead of you no longer build room models that are thrown away straight after, cutting a large hitch when exploring.
- Fixes a newly generated dungeon location sometimes loading all its rooms at once, freezing the game for a second or more.
- Locations you reach before the game has generated them now build in gradually instead of all in one frame, cutting the longest hitches when travelling through new land.
- Fixes holes in the ground where a location is about to appear while it is still loading.
- Locations and dungeon rooms no longer copy the hidden chests, trees and creatures they never use, making them much quicker to appear and unload.
- Adds an optional diagnostic that logs where the time goes when new land, locations and dungeons are generated.
- The ground at a portal's destination, or at your bed after you die, now starts building before you arrive, so loading screens can end sooner.
- Portals still wait for the server to send your destination when SteadyFrame is installed.
- While you stand at a portal, the server starts sending the buildings on the other side, so the loading screen after it is shorter.
- Rocks and copper deposits loading in or out no longer rebuild the whole patch of ground they sit on, trimming stutter when moving through forests, coasts and heaths.
```

**0.33.0**
```
- Exploring new ground no longer causes a small stutter each time the map reveals it.
- Dungeons and camps loading in near you no longer freeze the game for a moment.
- Several locations coming into range at once no longer stack into one long hitch.
- Cuts several small sources of throwaway memory created every frame.
- Portals, respawns and joining no longer drop you in before the buildings at your destination have arrived from the server, and no longer wait a fixed time when they already have.
- Loading screens finish building the land around you faster.
- The server finds out where you are as soon as you go through a portal, respawn or join, so nearby objects start arriving sooner.
- Fixes respawning at the world start instead of your bed when the server is slow to send the area around the bed.
- ValheimOptimized Compatibility fixes:
  - Fixes a game crash when ValheimOptimized is installed.
    - The terrain seam fixes from VCP are now disabled when ValheimOptimized is installed
    - Objects no longer spawn in double batches, distant terrain is no longer rebuilt twice, and building pieces notice its late terrain rebuilds.
```

**0.32.4**
```
- Fixes objects created by other players sometimes appearing late in an otherwise quiet area.
- Improves water color tint transition towards shore (configurable), 1.0 changed the shading to be much darker in shallow water.
```

**0.32.3**
```
- Fixes wrong biomes, weather and terrain colors on worlds resized or stretched by Expand World Size.
- Fixes a dedicated server endlessly destroying and recreating its own loaded objects, which crashed it after long uptime.
- Fixes the same loop, and terrain unloading under loaded objects, beyond the game's zone grid on worlds enlarged by Expand World Size.
- Items rejected by a station are refunded again when an up-to-date Eternal Fire is installed.
```

**0.32.2**
```
- Fixes building pieces at the edge of a player's loaded area being re-sent to everyone every second.
```

**0.32.1**
```
- Fixes endless free wood appearing in a player's inventory when Eternal Fire or AutomaticFuel is installed
    - "Refund Rejected Station Items" is disabled to support these mods. A warning will be logged if these mods are detected.
    - You will need to either remove these mods or deal with item loss when cooking, smelting, or fermenting
```

**0.32.0**
```
- Fix Unkillable Creatures (both): a creature could be left alive at zero health, ignoring every
  hit and never dropping loot. The game decides death on the creature's owner after the damage
  lands, and nothing retries a death that fails to finish. That happens to Deep North creatures
  whose owner changes during their death animation, which a busy server makes more likely, and
  to any creature whose death another mod's code breaks. Such a creature now finishes dying,
  within a second where its death never started and about 20 s after a death animation that
  never ended. A creature whose death threw an error is removed. A creature whose health another
  mod made NaN is put back to full health, and NaN health is no longer written.
- Fix Cloth Wind Shelter Errors (both): the Root Crown logged a `MagicaCloth component not found`
  error, stack trace included, each time one was created: dropped, put on a stand, or worn by a
  player coming into range. Its hat model carries the Deep North cloth wind shelter without the
  cloth it controls, which also left the shelter running an empty update every frame. It now
  switches itself off quietly, and the message goes to the debug log.
- The server garbage collector settings and the `vcp_gc` console command moved out to a separate
  plugin, Valheim Community Patch GC (ValheimCommunityPatchGC). They read and write the Mono
  runtime's own memory, which malware scanners flag. This mod no longer touches the collector, and
  its `Server - Garbage Collector` config section is no longer read and can be deleted. Install the
  new plugin on a dedicated server to keep the pre-grown mark stack.
```

**0.31.0**
```
- Server garbage collector settings (dedicated server): a new `Server - Garbage Collector` config
  section and a `vcp_gc` server console command that prints the collector's state and changes each
  setting live. On a busy server Valheim's incremental collector ends every collection with an
  unbounded stop-the-world pass, measured at 150-380 ms every 12 s on a large world.
  Dedicated servers get these defaults on update; setting an entry to 0 (or off) gives back the
  game's own value. Clients and listen hosts ignore all of it.
  - GC Time Slice Milliseconds (Windows and Linux), default 10: the incremental collector's
    per-frame budget, which Valheim ships at 3 ms. Shortens that final pass.
  - GC Free Space Divisor (Linux), default 2: how far the heap grows between collections. Collects
    about a third less often for a few hundred MB more heap on a large world.
  - GC Mark Stack Target Entries (Linux), default 4194304 (64 MiB): grows the collector's mark stack
    ahead of need, so a large heap is far less likely to abort the server with "Unexpected mark
    stack overflow".
  `vcp_gc stats on|off` (Linux) switches the collector's own per-collection log, written to the
  server's standard error.
```

**0.30.1**
```
- Fix Equipment Texture Errors (client): Fix a Deep North creature causing error spam and lag due to its visual setup.
- Removed Fix Dungeon Load Stall (both), since the 1.0 update this can only apply to mods. Which should handle it themselves.
```

**0.30.0**
```
- Fixes Deep North snow piling excessively specifically on zone boundaries
- Fix terrain changes for 1.0 being silently dropped
- Removes unpatchall on destroy
- Fix Biome Sector Lookup (both): the biome grid added in the Deep North update reads the sample at
  or below a point, so within about 12 m of a biome border the player's biome, weather, spawn
  levels, the map's biome name and terrain coloring followed the neighbouring biome, and a
  `GetBiome error` warning was logged every second. Lookups now match the actual biome. Location
  placement keeps vanilla's lookup, so seeds place locations as before. The warning is still
  visible with EnableDebugMode on.
- Fix Idle Creature Sync (both): a standing creature's owner re-sent its whole network record to
  every player many times a second, because its position, velocity and ground tilt were compared
  exactly and physics jitter changes them every frame. In a base with about 60 idle tamed wolves
  this saturated the owners' upload and sent each other player about 118 KB/s. Changes smaller than
  2 cm, 0.05 m/s or 1 degree from what was last sent are no longer sent on their own; a moving
  creature is sent as before, and players, ships, carts and items are unaffected. On by default,
  with a toggle in `Fixes - Performance`. Stands down when Network Performance System 1.9.1 or
  later is installed, which applies the same fix.
- Fix Portal Connection Scan stands down when another mod changes how the game pairs portals, and
  says so once in the log. With ZenPortal installed it had been skipping ZenPortal's pairing rules,
  so portals without a rune shard linked to each other, a rune already in use could link a second
  pair, and wood portals linked to stone ones with `Connection - Same Type Only` on.
- Fix Loading Screen Hang (both): leaving a dungeon, taking a portal or respawning could stay on
  the loading screen until the player quit. Opening an Infested Mine treasure pile plays a coin
  effect with a second network object nested inside it, and on every other player's client that
  inner object was destroyed without the game being told. The loading check then waited forever
  for an object that no longer existed, anywhere within one zone of the pile. Such objects are now
  unregistered as they are destroyed, and one the loading check is found waiting on is repaired
  and named in the log.
```

**0.29.0**
```
- Fix compatibility with Portal mods which rewrite how portals work
- Clear Patrol Point On Taming (both): a creature spawned by a spawner that sets a patrol point kept
  that point after being tamed, so it ran back to where it spawned instead of staying where you kept
  it. The patrol point is now cleared when the creature is tamed. Boars were worst affected: they
  cannot be told to follow or stay, so nothing in the game could clear it. Telling a tamed creature
  to stay still sets a patrol point as before. Creatures tamed before this version keep their old
  point.
- Fix Map Auto-Close (client): the map closed for every player on the server whenever anyone woke
  or killed a boss. Vanilla closes an open map on every world key change, and a boss fight makes
  several. The map now stays open unless a boss is within `Map Auto-Close Boss Range` (default
  100 m, the boss health bar range), where it closes as before.
```

**0.28.0**
```
- Three opt-in server memory fixes, all off by default, in a new `Fixes - Server Memory` config
  section. They target a dedicated server whose memory climbs with uptime under many players:
  - Evict Dead ZDO Records (server): drops the record of a destroyed object after a few minutes.
    Vanilla keeps the id of every object destroyed since the world loaded for the life of the process.
  - Trim ZDO Data Pool (server): caps the pools that recycle objects' field tables and clears the
    strings and byte arrays a recycled table still points at, so memory can come back down from
    the busiest moment since the world loaded.
  - Fix ZDO Serialize Allocation (server): writes an object's fields to the network straight from
    their tables instead of copying all seven into fresh lists per object per player per send tick.
- `vcp_zdomem` server console command (admins can run it remotely) and the `Log ZDO Memory Stats`
  diagnostic, reporting the per-player table sizes, dead-object records, field table pool depths,
  what each memory fix has removed so far, and the managed heap, so the effect can be measured.
```

**0.27.1**
```
- Compatibility improvement for mods which load live ZDOs in the near sector which are far away
```

**0.27.0**
```
- Refund Rejected Station Items (both): an item that a smelter, kiln, fireplace, cooking station or
  fermenter discards on arrival is dropped back at the player who sent it, within auto-pickup range.
  Vanilla removes the item from your inventory and then sends a network message to whoever owned the
  station; the owner silently discards it if it no longer owns the station or, for a fireplace,
  cooking station or fermenter, if the station filled up in the meantime. Fix Fuel And Ore Loss
  protects a sender running this mod; this protects a vanilla sender, and the "last slot" race
  between two players, from whichever side receives the message.
```

**0.26.1**
```
- Removed Object Stream Rescan (both): on multiplayer clients it could re-create and destroy objects many
  times a second, in newly generated areas it left distant objects such as large trees unspawned until
  you came close, and it handed other mods' spawn and unload patches empty object lists. Spawning uses
  Spawn Queue Churn's cached version of the game's own pass instead.
- Fix Light Flicker Overhead (client): distant torches and fires no longer stay dark until you walk
  close, or after changing graphics settings. The Point Light Limit setting is removed; the game's
  Point Lights graphics option controls that cap.
- Fix Zone Collider Stall (client): ships, carts and placed location props always have terrain
  collision under them when they load.
```

**0.26.0**
```
- Fix Non-Item ObjectDB Entries (both): prefabs that are not items are now removed from the game's item
  list (ObjectDB) when it is built. The 2026-09-09 Valheim update listed three prefabs with no item
  component (PropFeastDeepNorth, SnowRoller and FrozenKing_Summon) among the items, so mods that treat
  every entry as an item threw or logged errors on them. They can still be spawned.
- Fixes shading on shallow water (client): the water shader now shades shallow water correctly, so the
  waves in shallow water are no longer tinted by the deep-water color. The 2026-09-09 Valheim update
  changed the water shader to shade shallow water by the deep-water color, so a shallow corner of a
  tile could tint the whole tile's waves.
- Removed two performance patches, both had extremely minimal gains and ended up showing side effects on some systems
	- Reflection Probe Spikes (client)
	- Physics Catchup Spiral (both)
```

**0.25.0**
```
- Fix Water Color Seams (client): shallow and deep water color now blends smoothly across zone
  borders. The water shader colored each 64 m water tile by the depth at its south-west corner only,
  so near shores the sea changed color in a hard straight line along the zone grid while the waves
  across the same line stayed smooth. Shading now reaches its deep-water look at 5 m rather than 10 m
  ("Water Color Depth Scale", default 2), so one shallow corner no longer tints a whole tile of deep
  water; the visible waves in shallow water run up to that factor taller than the waves boats ride.
```

**0.24.0**
```
- Fix Unsaved Client Changes (server): an object a connected player placed or changed is now written
  to disk by the next world save. The 2026-09-09 Valheim update's chunked save only rewrites chunks the
  game marked as changed, and it never marks one for a change arriving from another player, so a
  forge, workbench or chest a client built could be missing after a server restart unless something
  else in the same chunk had changed first.
```

**0.23.1**
```
- Fix Equipment Visual Refresh (client): a beard or hair no longer turns white (only a
  death respawn or new character) were affected.
```

**0.23.0**
```
- Fix Teleport Ghost Players (server): a player who teleports out of another player's loaded area is
  now removed from that player's game. The 2026-09-09 Valheim update broke the server's
  sector-invalidation notice for single-step jumps (teleport, portal, respawn), so the other client kept
  the traveller standing frozen where they left, still inside local chat range.
```

**0.22.1**
```
- Added Icon -_-
```

**0.22.0**
```
- Initial release of the Valheim Community Patch.
```
