# Valheim Community Patch

Vanilla bug fixes and performance fixes. **No gameplay changes.**

This mod exists to fix things that are broken or pathologically slow in Valheim itself — crashes,
exceptions that silently kill a system, item loss, quadratic hot paths, per-frame allocations, log
spam. It deliberately ships no quality-of-life features, no balance changes, and no content, so you
can install it on a server without anyone having to agree about how the game should play.

Every fix has its own toggle in the config, so you can turn off any single one without a rebuild.
Toggles are admin-only and server-synced.

## Fixes in this release

### Performance

- **Cache Recipe Lookups** — `ObjectDB.GetRecipe` was an unindexed linear scan doing a string
  comparison per recipe. `InventoryGui` calls it for every worn item *every frame* the inventory is
  open at a crafting station, so standing at a workbench cost thousands of string comparisons per
  frame — tens of thousands once other mods add recipes. This is the workbench repair hitch. Now
  indexed by item name.
- **Fix Tar Pit Memory Leak** — tar pits (`LiquidVolume`) allocated their raycast buffers with
  Unity's 4-frame temporary allocator but held them for the object's entire lifetime, leaking native
  memory and spamming `JobTempAlloc has allocations that are more than 4 frames old` on every load.
  Now allocated persistently and disposed safely.

### Correctness

- **Fix Recipe Amount Crash** — `Recipe.GetAmount` dereferenced a null item, so opening the crafting
  or upgrade panel for a "requires any one of these" recipe while carrying none of the ingredients
  threw and left the panel broken.
- **Fix Spawner Null Prefabs** — a single null entry in a spawner's creature table (common after a
  game update or a removed creature mod) threw during spawn selection and permanently killed that
  spawner. Null entries are now dropped on load.
- **Require Lit Fire** — the burning-area check tested only a fireplace's bounds, never whether it
  was lit, so an unlit or burnt-out fire still counted as a heat source and kept cooking.
- **Fix Run Attack Stamina Drain** — holding sprint while attacking stopped your movement but kept
  charging run stamina for the whole swing.
- **Fix Projectile Rotation Spam** — every projectile whose velocity reached exactly zero made Unity
  log `Look rotation viewing vector is zero` on every physics step.

## Installation

Install with a mod manager, or drop `ValheimCommunityPatch.dll` into `BepInEx/plugins`.

Requires [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) and
[Jotunn](https://valheim.thunderstore.io/package/ValheimModding/Jotunn/).

This mod must be installed on the server **and** on every client.

## Reporting a bug

Issues go to [GitHub](https://github.com/MidnightsFX/Valheim-Community-Patch). Please include your
`LogOutput.log` and the list of other mods you are running.

To decide whether a fix belongs here, the test is: *is the current behaviour something Iron Gate
would call a bug?* If it is a matter of taste, it belongs in a different mod.
