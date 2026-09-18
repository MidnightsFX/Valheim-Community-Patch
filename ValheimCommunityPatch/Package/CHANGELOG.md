# Changelog

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
  waves in shallow water are no longer tinted by the deep-water colour. The 2026-09-09 Valheim update
  changed the water shader to shade shallow water by the deep-water colour, so a shallow corner of a
  tile could tint the whole tile's waves.
- Removed two performance patches, both had extremely minimal gains and ended up showing side effects on some systems
	- Reflection Probe Spikes (client)
	- Physics Catchup Spiral (both)
```

**0.25.0**
```
- Fix Water Colour Seams (client): shallow and deep water colour now blends smoothly across zone
  borders. The water shader coloured each 64 m water tile by the depth at its south-west corner only,
  so near shores the sea changed colour in a hard straight line along the zone grid while the waves
  across the same line stayed smooth. Shading now reaches its deep-water look at 5 m rather than 10 m
  ("Water Colour Depth Scale", default 2), so one shallow corner no longer tints a whole tile of deep
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
