# Changelog

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
