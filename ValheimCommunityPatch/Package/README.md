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

The mods involved:

- **[ComfyMods](https://github.com/redseiko/ComfyMods)** — redseiko (GPL-3.0), the same licence as
  this project. Seven of its mods — BetterZeeLog, LetMePlay, BetterServerPortals, Scenic, Compress,
  Effectual and Atlas — account for eleven of the entries below.
- **[MyPitsDontLeak](https://github.com/AzumattDev/MyPitsDontLeak)** — Azumatt (MIT).
- **Zen.ModLib** — ZenDragon. Used only as a *catalogue* of which vanilla bugs exist; no code was
  copied.
- **Iron Gate Studio** — Valheim itself. The decompiled game source is the reference used to locate
  defects; no game code is redistributed.

*Original* below means the defect was found and fixed here, with no other mod's implementation
involved.

### Performance

| Fix | Sourced from | What came from there |
| --- | --- | --- |
| Fix Portal Connection Scan | ComfyMods — BetterServerPortals | The indexing algorithm |
| Fix World Load Connection Scan | ComfyMods — Atlas | Its `ConnectSpawners` approach, extended here to portals and sync transforms |
| Fix Disconnect ZDO Sweep | Original | — |
| Fix Tar Pit Memory Leak | MyPitsDontLeak — Azumatt | The root cause; our implementation is transpilers rather than wholesale method replacement |
| Fix Auto Pickup Allocation | Zen.ModLib (catalogue) | The technique, rewritten |
| Fix ZDO Packet Allocation | ComfyMods — Compress | The technique, taken on its own without that mod's GZip protocol change |

### Terrain

| Fix | Sourced from | What came from there |
| --- | --- | --- |
| Fix Terrain Seams | Original | — |
| Fix Terrain Paint Seams | Original | — |
| Fix Terrain Paint Zone Fanout | Original | — |
| Fix Terrain Paint Mask Indexing | Original | — |
| Fix Terrain Compiler Init Race | Original | — |

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
| Fix Negative Stamina | Original | — |
| Fix Dungeon Load Stall | Original | — |

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
