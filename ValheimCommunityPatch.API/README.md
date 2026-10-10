# Valheim Community Patch - API

## Overview

Valheim Community Patch (VCP) changes a lot of the same game code that performance and loading mods
change. Without this API, a mod that finds VCP patching a method it wants usually turns its own
feature off. The API lets it work through VCP instead: queue terrain first, reuse finished terrain,
pause background zone generation, and ask which of VCP's fixes are running.

The API is one file, `CommunityPatchAPI.cs`. It finds VCP at runtime by reflection, so your mod does
not need VCP to build or to run. When VCP is not installed, or is too old to have a member, that
member does nothing and returns a default.

## Setup

Either:

- **Copy** [`CommunityPatchAPI.cs`](CommunityPatchAPI.cs) into your project as it is. It compiles
  with C# 7.3 and references only `assembly_valheim` and `assembly_utils`, which every Valheim mod
  already does.
- **Or reference** `ValheimCommunityPatch.API.dll`, built from the same file by this project, and
  merge it into your plugin (ILRepack or similar). Do not ship it as a separate file in `plugins`:
  when two mods do that with different versions, whichever loads first wins for both.

Then add a soft dependency to your plugin class, so VCP loads before you do:

```csharp
[BepInDependency("MidnightsFX.ValheimCommunityPatch", BepInDependency.DependencyFlags.SoftDependency)]
```

The API decides whether VCP is installed the first time you use any of its members. Do not use it
before VCP has loaded; with the soft dependency above, your `Awake` is late enough.

## Usage

To check that VCP is installed:

```csharp
using ValheimCommunityPatch.API;

if (CommunityPatchAPI.IsAvailable) {
    // VCP is installed
}
```

Your copy of the file can be newer than the installed VCP. Members the installed VCP does not have do
nothing, and are listed so you can log them once:

```csharp
if (CommunityPatchAPI.UnboundMembers.Count > 0) {
    Logger.LogInfo("VCP is older than this mod's API file; not available: " +
                   string.Join(", ", CommunityPatchAPI.UnboundMembers));
}
```

### Fix status

Every fix has an id: the name of its patch class, for example `ZoneGenPacingPatch`. Ids stay the same
from release to release. `GetFixIds()` lists them all.

```csharp
int state = CommunityPatchAPI.GetFixState("SceneIdleSkipPatch");
// FixStateUnknown = 0     no fix has this id
// FixStateActive = 1      applied and running
// FixStateDisabled = 2    applied, switched off in the config
// FixStateStoodDown = 3   applied, but it has given way to another mod for this session
// FixStateNotApplied = 4  not used on this side, or it stood down at startup for a known mod
// FixStateFailed = 5      failed to apply, usually after a game update

if (CommunityPatchAPI.IsFixActive("SceneIdleSkipPatch")) {
    // leave that part to VCP
}
```

Most fixes decide whether to stand down the first time they are used in a world, once every mod has
patched. A fix can read Active at the main menu and StoodDown later, so ask when you need the answer.

### Terrain build queue

VCP replaces the loop of `HeightmapBuilder.BuildThread`, so patches on that loop never run. These
members do the common jobs through VCP's loop instead. They take effect while
`TerrainBuildLoopActive` is true. When it is false (VCP missing, or that fix inert), patch the builder
yourself.

**Build the zones you need first.** Pass the zone ids in the order you want them, nearest first for
example. The list stays in force until you replace or clear it, and covers zones the game requests
after the call, so you can set it as soon as you know the destination:

```csharp
List<Vector2s> ring = DestinationRing(destination);   // your own list, nearest first
CommunityPatchAPI.SetTerrainPriorityZones(ring);       // walking up to a portal / teleport starts
// ...
CommunityPatchAPI.ClearTerrainPriorityZones();         // arrived
```

Distant-terrain tiles are never promoted. The thread keeps at most `TerrainReadyCap` finished builds
and discards the oldest past that, so queue ahead of time only up to that many.

**Reuse finished terrain.** `TerrainBuildFinished` is raised for every build the thread finishes.
`SubmitFinishedTerrainBuild` hands a finished build back, as if the thread had just built it, so the
next request for that terrain takes it instead of rebuilding:

```csharp
private static readonly ConcurrentDictionary<Vector3, HeightmapBuilder.HMBuildData> Cache =
    new ConcurrentDictionary<Vector3, HeightmapBuilder.HMBuildData>();

// Once, in Awake:
CommunityPatchAPI.TerrainBuildFinished += data => {
    if (!data.m_distantLod) { Cache[data.m_center] = data; }   // build thread: no Unity calls here
};

// When returning through the same portal:
foreach (Vector3 center in DestinationCenters(destination)) {
    if (Cache.TryGetValue(center, out HeightmapBuilder.HMBuildData data)) {
        CommunityPatchAPI.SubmitFinishedTerrainBuild(data);
    }
}
```

Rules for the terrain hooks:

- `TerrainBuildFinished` is raised **on the build thread**. Keep handlers short, do not call Unity, and
  do not block. A handler that throws is removed and logged.
- The game only reads build data, so one instance can be handed back any number of times. Do not
  change it.
- `SubmitFinishedTerrainBuild` returns false, and does nothing, when the data was never built, was
  built for another world, or an equal build is already waiting. Clear your cache when the world
  changes.
- `SetTerrainPriorityZones` and `ClearTerrainPriorityZones` are for the main thread.
  `SubmitFinishedTerrainBuild` can be called from any thread.

### Ghost zones

On the host, the game generates "ghost zones": a ring of zones around the host and every peer that
nobody is standing in yet, one per tick, ready for when someone arrives. While a loading screen is up
nobody needs them. `SkipGhostZones` pauses that generation; zones players actually enter still load.

```csharp
CommunityPatchAPI.SkipGhostZones = true;    // loading screen up
// ...
CommunityPatchAPI.SkipGhostZones = false;   // arrived
```

Always clear it. The pause is for loading screens: if it stays set for more than two minutes, VCP
logs a warning and ignores it until it is cleared, so a missed reset cannot stop world generation for
the session. It affects every peer's ghost ring, not just the host's. It takes effect while
`SkipGhostZonesSupported` is true; when it is false, patch `ZoneSystem.CreateGhostZones` yourself.
Main thread.

## Versioning

- `FileApiVersion` is the version of your copy of the file. `ReceiverApiVersion` is the installed
  VCP's (0 when it is not installed).
- Members are only ever added. A member is never renamed, removed or changed in a way that breaks an
  older copy of the file, and each addition raises the version by one.
- Fix-state numbers never change meaning. Fix ids only change if a fix is removed, and then
  `GetFixState` returns `FixStateUnknown` for it.

| API version | VCP version | Added |
|---|---|---|
| 1 | 0.35.0 | Fix status, terrain build queue (priority zones, finished-build event, submit), `SkipGhostZones` |

## For VCP contributors

The file binds each member to a method of `ValheimCommunityPatch.APIReceiver`
(`ValheimCommunityPatch/Common/APIReceiver.cs`) by name and exact signature. That class only hands
each call to the fix that owns the behaviour. To add a member:

1. Add the behaviour to the fix as `internal static` members.
2. Add a public static method to `APIReceiver` that calls it, and raise `APIReceiver.ApiVersion`.
3. Add the delegate field, `Bind` call and public member to `CommunityPatchAPI.cs`, raise
   `FileApiVersion` and the project's `<Version>`, and add a row to the table above.

Never change an existing receiver method's name or signature. An older copy of the file would stop
binding it and lose that member without any error.
