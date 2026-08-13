# Credits

Valheim Community Patch is GPL-3.0. Where a fix here was derived from, or independently confirmed
against, another modder's work, that is recorded below and in a comment at the top of the patch file.

## ComfyMods — redseiko (GPL-3.0)

<https://github.com/redseiko/ComfyMods>

Same licence as this project. Fixes derived from or corroborated by their work:

- **ComfyAutoRepair** — identified `ObjectDB.GetRecipe` as the cause of the workbench frame hitch.
  Our implementation differs: it builds the index eagerly and invalidates on both
  `ObjectDB.UpdateRegisters` and a recipe-count change, because invalidating only on `CopyOtherDB`
  leaves the cache stale for recipes other mods add later. The repair-all convenience feature of that
  mod is deliberately not included — this project ships fixes, not features.
- **BetterZeeLog** — `Projectile.FixedUpdate` zero-velocity `LookRotation` guard.
- **LetMePlay** — `SpawnArea.Awake` null-prefab purge.
- **BetterServerPortals** — the `Game.ConnectPortals` algorithm: tag-keyed index, pooled lists, direct
  ownership instead of an RPC round-trip, batched force-sends. Its "random portal" gameplay feature is
  deliberately not included.
- **Scenic** — the `ZNetScene.RemoveObjects` hardening.
- **Effectual** — both `EffectArea` defects: the truncated collider buffer and the dangling character
  references.
- **Atlas** — the `ZDOMan.ConnectSpawners` indexing approach, extended here to `ZDOMan.ConnectPortals`,
  and the `ZDOMan.Load` duplicate-id tolerance. Atlas's server tooling is not included.

## MyPitsDontLeak — Azumatt (MIT)

<https://github.com/AzumattDev/MyPitsDontLeak>

Identified the `LiquidVolume` native memory leak. Our implementation differs: that mod replaces
`Awake` and `OnDestroy` wholesale with prefixes, which breaks silently on any game update to those
methods and conflicts with other mods patching them. This uses targeted transpilers anchored on the
`NativeArray` constructor and `Dispose` call instead.

## Zen.ModLib — ZenDragon

Used only as a **catalogue** of which vanilla bugs exist. No code was copied: the copy available to
us is decompiler output with no accompanying licence, so every fix in that category was rewritten
from the decompiled game source. Bugs catalogued there and reimplemented here: the `Recipe.GetAmount`
null dereference, the unlit-fire `EffectArea` check, the run-attack stamina drain, the smelter and
fireplace fuel loss, the unshared boss defeat key, and the `Player.AutoPickup` per-frame allocation.

Where our implementation differs deliberately: the fuel-loss fix is a prefix rather than that mod's
backwards IL match; the boss-key broadcast uses one globally registered routed RPC instead of one
registered on every creature's `ZNetView`; and the run-attack fix is narrowed to players so creature
movement is unaffected.

## Iron Gate Studio

Valheim. The decompiled game source is used as reference for locating defects; no game code is
redistributed.
