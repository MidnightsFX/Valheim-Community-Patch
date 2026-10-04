# Issue 14: sector-zero unload loop

## Cause and change

Sector index zero represents both the valid grid corner and positions outside
the grid. `IndexToSector(0)` returns `(-256, -256)`. That coordinate does not
identify the positions of all objects in the bucket.

A dedicated server can use a reference position outside the grid. The game then
selects bucket-zero objects into its current-object lists. The distance-based
unload patch previously removed those same objects. Creation and removal
repeated on later updates. This rapidly consumed Unity instance identifiers.
The crash in issue 14 occurred after the identifier counter wrapped and a
dungeon room clone encountered an identifier collision.

The change uses membership in the game's near and distant lists for bucket
zero. Other sectors retain the distance-based selection. The temporary
membership set is cleared after each discovery pass.

Multiplayer validation also found stale index entries. A diagnostic snapshot
showed 433 proposed removals, all absent from the scene instance dictionary.
The final change filters departures against that dictionary and checks that
the indexed view is still the registered view for its ZDO. This also prevents
an old view from removing its replacement. Null-ZDO candidates retain the
existing guarded recovery path.

## Validation environment

- Date: 2026-10-03 / 2026-10-04 UTC.
- Base commit: `63731022a0d69ecba7549c7f4150adc75d97b5a0`.
- Production release used as the test baseline: Praetoris Season 8, `8.0.33`.
- Test server: Valdev, maintained Season 8 profile, `mwlPortIconTest` world.
- Clients: Valnet client 01 and client 02, maintained Season 8 profiles.
- Final candidate DLL SHA-256: `49066ff979c2c541937c97dc8bd4d4d4411af737a682af64346fd5f5552551ef`.
- Temporary diagnostic plugins were used only on test devices. Mod enforcement
  remained enabled; test DLL hashes were accepted in the test policy.
- Production was not changed.

## Empty-server reproduction

1. Use the Season 8 server mod set and the test world.
2. Leave the server without connected players.
3. Keep `Verify Unload Discovery` disabled for the initial measurement.
4. Measure the Unity identifier counter without changing its value.
5. Replace only CommunityPatch with the candidate and repeat.
6. Enable unload verification and compare the removal sets against the game.
7. Disable verification again and confirm that the loop does not return.

| Run | Five-second samples | Minimum identifier units | Maximum | Mean |
| --- | ---: | ---: | ---: | ---: |
| Before fix, 23:17:00–23:19:10 UTC | 27 | 92,858 | 108,592 | 101,484 |
| Initial candidate, 23:24:14–23:34:55 UTC | 129 | 0 | 60 | 35.19 |
| Final candidate, 00:23:14–00:25:39 UTC | 30 | 0 | 16 | 4.87 |
| Final candidate after three resets, 00:28:02–00:31:42 UTC | 45 | 0 | 10 | 5.33 |

Before the fix, the same zone-controller and Leviathan records were repeatedly
created and removed. After initial loading with the fix, diagnostic samples
reported no repeated creation or removal. The verification run completed
66 comparisons with zero differences and zero passes excluded because another
mod changed the lists.

## Sector-zero discovery checks

The final test invoked the actual removal prefix against 171 loaded sector-zero
records. It also added an unregistered view that referred to a real, registered
ZDO. Destruction was temporarily suppressed. An independent scan of the scene
instance dictionary supplied the expected removal set. The unregistered view
did not enter any removal set. The test restored the original current-object
lists, removed the artificial index entry, and removed its temporary hook
afterward.

| Current lists | Expected removals | Actual removals | Result |
| --- | ---: | ---: | --- |
| Neither list contains records | 171 | 171 | Pass |
| One record in the near list | 170 | 170 | Pass |
| One record in the distant list | 170 | 170 | Pass |
| Different records in both lists | 169 | 169 | Pass |
| All records in the near list | 0 | 0 | Pass |

## Dungeon reset and multiplayer checks

1. Join Valdev with both clients and the final candidate.
2. Teleport both players to the AshlandRuins location at `(1920, 36.13, -9344)`.
3. Confirm that both clients load the 18-room dungeon.
4. Move away and reset this location through StarLevelSystem's
   `ResetNamedLocation` API, using a 64 m search radius.
5. Repeat the reset three times in total. Run the first with server verification
   enabled and the next two with verification disabled.
6. Return both clients to the rebuilt dungeon and confirm that it loads again.

All three final-build resets reported `completed=True`, `zonesReset=1`,
`locationsRebuilt=1`, and `zonesUngenerated=0`. Each reset spawned 518 objects.
The diagnostic captured `CharredRuins9` cloning through the actual
`DungeonGenerator.PlaceRoom` path. Both clients remained connected and loaded
the rebuilt 18-room dungeon. No crash occurred. Server verification completed
123 comparisons with zero differences and zero excluded passes.

The second server verification interval completed another 368 comparisons with
zero differences. Diagnostics were also enabled directly on each client, then
both players traveled away from and back to the rebuilt dungeon. Client 01
reported 768 comparisons and 6,235 removals. Client 02 reported 638 comparisons
and 6,230 removals. Both reported zero differences and zero excluded passes.
Both clients loaded the 18-room dungeon again after returning.

## Restoration

Valdev's profile and all test-world files were restored from the pre-test
backup and compared by SHA-256 before restarting. Its original plugin hashes
were checked again after restart. Both clients' maintained profiles, selected
profiles, configuration, and Developer character files were restored and
checked. Temporary test plugins and the test character were removed. Both
Valnet machines were powered off because they were off before testing.

## Limits

The tests do not force identifier exhaustion. They verify that the repeated
creation and removal that drove exhaustion has stopped. A full-duration soak
through the original production failure interval has not been completed.
The production world and exact failed seed were not replayed. The actual
StarLevelSystem reset path and the same `CharredRuins9` prefab were exercised
in the Valdev world.

Client 02's existing Developer character encountered an MWL `mwl_port1` asset
load error during an earlier join. Final proof used a fresh test character on
that client. That character-specific join failure was not resolved by this
change. Early diagnostic requests before world loading completed were rejected
by the test helper and were repeated after the world was ready.
