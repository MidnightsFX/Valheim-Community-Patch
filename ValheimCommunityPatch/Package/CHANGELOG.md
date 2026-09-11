# Changelog

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
