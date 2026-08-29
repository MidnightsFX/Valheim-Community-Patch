# Changelog

Each entry names the vanilla method it fixes, and is tagged with the side that fix is worth
installing on — *(server)*, *(client)* or *(both)*. See the README for what a one-sided install gets
you.

## 0.18.0

The VPO adaptation release: four techniques from ontrigger's ValheimPerformanceOptimizations
(MIT), reworked into this mod's per-fix toggles and fallback discipline.

- **Fix Reflection Probe Spikes** *(client)* — vanilla renders all six faces of the realtime
  reflection cubemap in one frame every few seconds (a steady 14-18 ms/s across profiled
  sessions, delivered as periodic single-frame spikes). Now one face per frame into a
  128px cube ("Reflection Resolution" advanced setting: 64-512), with reduced quality
  during the reflection render (lower LOD, shorter shadows, no characters/items) - a
  deliberate fidelity trade in an already-blurry environment reflection. Toggling off
  hands the probes back to vanilla's renderer at runtime. A face is additionally DEFERRED
  while the previous frame is over budget ("Reflection Frame Budget", default 33 ms, 0 to
  disable), because a ~10 ms face landing on a frame the streaming system was already
  saturating was measured tipping marginal frames past the spike threshold. Nothing
  consumes a half-built cubemap - the probe keeps showing the previous one until the sixth
  face publishes - so a defer moves only which frame pays. A starvation guard renders
  regardless after three consecutive defers, so a machine that never makes budget still
  finishes a full cycle in ~24 frames, well inside vanilla's 3 s refresh interval.
- **Fix Object Stream Rescan** *(both)* — the object stream stops rediscovering its own
  work list. Vanilla's CreateDestroyObjects rebuilds the candidate set from every sector in
  the loaded ring and re-sorts it thirty times a second, only to spawn its head; measured
  in a crossing-heavy session that was 24.3 ms/s for the ring scan and 28.3 ms/s for the
  sort in spiky seconds (13.7 and 20.0 in calm ones), against 36.5 ms/s for the actual
  Instantiate calls, with worst seconds at 200 and 214 ms. The pending set is now a
  persistent queue fed by the events that change it - a zone-set diff on the reference
  zone or either ring radius, ZDOMan's sector add/remove, a buffered flush of the ZDOData
  handler so a half-deserialised ZDO cannot enter mid-bounce, and the Created flag in both
  directions so an object something else destroyed is recreated exactly as vanilla would.
  FindSectorObjects is never called; the sort is lazy, re-run only when the player has
  moved 8 m or enough arrivals have piled up at the tail; not-ready and unresolvable
  entries park on a deferred list instead of being re-tested every pass. Spawn rate, order
  and the "Spawn Burst Divisor" budget are unchanged. It stands down entirely unless "Fix
  Unload Discovery Scan" is on and healthy, because replacing the pass leaves vanilla's
  earmark-based unload discovery with an empty keep-set. That fix's own orphan-recovery
  fallback now earmarks the keep set from what is actually loaded rather than from the
  caller's lists, so it is correct whichever path drove the pass. New admin-only "Verify
  Spawn Queue" rebuilds the candidate set the vanilla way every pass, acts on vanilla's
  answer, and names anything the queue is missing.
- **Fix Idle Support Checks** — the sleep now actually sleeps. A soak measured it
  engaging on **none** of 316329 support checks, 85% of them blocked because the piece was
  already flagged for a re-check, and the instrumentation added to find out why identified
  a self-sustaining cascade: ~450 pieces a second enter tracking while an area streams,
  each one seeding roughly 5.5 further re-checks. Two exact fixes, no heuristics:

  - **Arrivals no longer look like changes.** Vanilla's Awake stamps a piece's support to
    its material maximum as a placeholder and never restores the persisted value - it
    writes the stored support in four places and reads it in exactly one, the non-owner
    branch of GetSupport (WearNTear.cs:207). So a freshly streamed piece advertised an
    optimistic maximum to its neighbours and then dropped to its real value, a change out
    of nowhere that woke the whole neighbourhood. The stored value is now restored on
    Awake, which is strictly better information than a placeholder (it is exactly what a
    non-owner would have read for the same piece), and a piece returning to an unchanged
    base now seeds no wave at all. No collapse or damage decision is taken on the restored
    value - the wear pass consults support only after recomputing it.
  - **Vertical neighbours are no longer woken.** The neighbourhood test compared only the
    horizontal footprint, so in a multi-storey build a change on one floor woke every
    piece above and below it in the same column - the exact over-waking the horizontal
    test was introduced to prevent, never applied to the vertical. Heights are now part of
    the test, which is strictly tighter and cannot miss a real neighbour.
  - **A piece's neighbourhood is now its actual shape.** That region was bounded by a
    sphere around each collider box, sized to the box's longest diagonal so it would hold
    under any rotation. For the flat pieces most of a base is made of, that is enormously
    too generous vertically - a floor with half-extents (1, 0.1, 1) claimed a vertical
    reach of 1.42 instead of 0.1, swallowing the floors above and below it, which is why
    adding heights to the test on its own changed almost nothing. The region is now the
    true axis-aligned bound of the oriented box, exact for the axis-aligned and
    quarter-turned pieces that dominate and never smaller than the truth.
  - **The out-of-area stamp no longer poisons the stored value.** The wear pass stamps a
    piece outside the active area to its material maximum and PERSISTS that
    (WearNTear.cs:308-310), so the stored support - the only thing a non-owner reads, and
    what a returning piece restores from - was overwritten with a placeholder every time
    the ring edge swept past, defeating the arrival repair for exactly the pieces that
    needed it. The in-memory stamp is untouched, so an unwatched structure still cannot
    fail its support check; only the stored copy is put back to the last real value.

  The wake scan also stops re-examining neighbourhoods that are already awake: each grid
  cell tracks how many of its pieces are not flagged, and a cell at zero is skipped whole.
  Neighbours are only notified past a "Support Change Threshold" (default 0.01, 0 restores
  the exact compare); the piece still stores its exact support, each propagation step
  multiplies by at most one so a suppressed difference stays bounded rather than
  accumulating, and collapse thresholds sit orders of magnitude above it. Two honest
  results worth recording: the threshold was added expecting the game's fixed 128-collider
  surroundings buffer to be overflowing and returning unstable values, and direct
  measurement killed that outright - zero of ~18700 sampled boxes reached the limit, the
  worst held 82 - so the threshold is kept as a cheap bound, not as the cure; and the
  fully-awake-cell skip engaged on only a few percent of scans, because one unflagged
  piece in a cell defeats it. "Verify Support Sleep" now reports WHY checks could not
  sleep and how many visits the wear sleep had already skipped before them; new admin-only
  "Log Support Wake Stats" reports the change distribution - as a fraction of the piece's
  material maximum, since that runs from 100 to 2000 and one absolute number cannot mean
  the same thing on both - the scan traffic, how often the out-of-active-area
  maximum-support stamp fires, and, sampled because it costs a real physics query, how
  full the surroundings buffer actually gets.
- **Fix Support Lookup Cost** — a building piece's centre of mass costs one transform
  fetch instead of two. The support recompute asks for it for itself and for every
  neighbour it overlaps, and with the support sleep measured engaging on none of 229431
  visits in a soak, that and the transform reads around it were ~39 ms/s, the largest
  single line inside the wear updater. Caching the value per
  frame was measured costing 13.7 ms/s in map probing to avoid about 7 ms/s of transform
  reads, so the cache was withdrawn and only the cheap half kept: vanilla fetches the
  transform twice for that one expression and once is enough. Value-identical, cheaper
  than both vanilla and the cache, and no bookkeeping. Provenance: ontrigger's
  ValheimPerformanceOptimizations (MIT) derives it the same way.
- **Fix Idle Support Checks** — the wake sweep no longer costs a dictionary probe per
  candidate: registration hands each grid entry its piece's sleep state, so a wake sets a
  bool through a reference the entry already holds. Deaths, which arrive in storms (a
  streamed-out zone column, a collapsing structure), now queue their wake boxes and sweep
  once per frame instead of once per piece - each grid cell the storm reaches is scanned
  exactly once against the boxes that reach it, and a piece the first box woke costs one
  branch for each later box. The woken set is identical either way; only the sweep is
  deduplicated.
- **Fix Physics Catchup Spiral** *(both)* — after a long frame Unity runs up to ~16 fixed
  physics steps of catch-up in the next frame, turning every big hitch into two. The cap
  ("Max Physics Steps Per Frame", default 8) bounds the debt; dropped time is dropped
  exactly as vanilla drops it past its own higher cap.
- **Fix Map Generation Stall** *(client)* — the world map is recomputed from the generator
  on every login, a fixed multi-second block inside the load screen, for textures that are
  a pure function of the seed. They are now cached to disk per world name + seed + game
  version (auto-invalidating on updates), with corrupt caches deleted and regenerated.
  Exploration fog is untouched - it lives in the character save.
- **Fix Support Lookup Cost** — two more per-call costs recovered inside the support check:
  the LINQ own-collider Contains scans (an allocating enumerator plus an O(n) scan per
  overlap hit) become one map probe, and GetSupport's non-owner path stops computing the
  material-property default eagerly when a stored value exists. Both value-identical; the
  transpiler's count gate now covers all five rewritten call sites.

## 0.17.0

- **Fix Idle Wear Visits** *(both)* — the second sleep tier on building pieces. The support
  sleep still left every piece paying its full per-visit wear update — owner checks, area
  checks, wetness, biome, visual refresh — tens of thousands of times per sweep cycle just
  to conclude nothing wears today. A piece now skips the whole visit when every input is
  provably quiet: support-slept, owned by this machine (non-owner visits are the poll that
  keeps remote damage visible, so they always run vanilla), dry OR roofed while wet (a
  roof holds the rain branch provably inert, vanilla's separate cover pass keeps roof
  state fresh, and a roof change wakes the piece - important because several biomes'
  ambient environments are wet-flagged around the clock, and the wetness sample is
  debounced because a base straddling a biome border can flip it with every few steps),
  above the waterline, biome resolved and not Ashlands, inside the activated
  area, and no damage or repair since its last visit (both wake it immediately). Wear
  skips count toward the same hygiene streak as support skips, so the periodic full
  revalidation is preserved. Exposed pieces in wet weather and everything in the Ashlands
  run exactly vanilla. A new admin-only "Verify Wear Sleep" predicts every skip while
  running vanilla, flags any visit where a predicted-quiet piece's support, health or
  wetness actually changed, and reports WHY blocked visits could not sleep.

## 0.16.0

- **Fix Unload Discovery Scan** *(both)* — the object-unload pass discovered departures by
  elimination: stamp every ZDO in the loaded rings, then walk every loaded instance and
  remove whatever was not stamped — O(everything loaded) to find a handful of departures,
  ~12 ms of every second at a widened zone ring even after pacing and field-read
  optimizations. Discovery now asks the per-zone instance index directly (the same index
  behind "Fix Zone Occupancy Scan", extended to full per-zone instance lists with O(1)
  bookkeeping): iterate the few hundred zones that hold instances, keep the near ring,
  drop non-distant objects from the distant band, drop everything outside — vanilla's
  exact keep-set from the same sector values vanilla's own stores use. Effectively free at
  any ring size, the earmark stamping disappears, and unloading returns to vanilla's
  every-pass cadence (the sweep interval no longer applies while this is on). Orphan
  recovery is inherited from Fix Object Unload Crash's guarded sweep, and a new admin-only
  "Verify Unload Discovery" runs vanilla's walk alongside, compares removal sets
  member-by-member, and acts on vanilla's.

## 0.15.1

- **Fix Idle Support Checks** — three wake holes closed, found through live "Verify Support
  Sleep" divergences (five in 575k visits, all at the streaming ring's edge). Root cause: a
  piece's colliders join the support world at Awake, but the wake grid only learned it at
  its lazy SetupColliders, so streaming-edge pieces could arrive, change and die without
  waking the sleepers they support. Now: every piece Awake wakes envelope-overlapping
  sleepers around it (vanilla's fast path re-detects arriving support within a sweep, so
  this is parity); deaths and changes of never-registered pieces wake through a
  conservative box around their position; and the outside-active-area path — which stamps
  max support directly, bypassing UpdateSupport — is caught by a before/after compare
  around UpdateWear and treated as the value change it is. Re-run the verify to confirm
  zero divergences before trusting a long soak.
- **Fix Spawn Queue Churn** — new advanced setting "Spawn Burst Divisor" (default 100 =
  exactly vanilla): the per-frame spawn budget is the backlog divided by this value, so
  raising it spawns fewer objects per frame when entering built-up areas — smaller frame
  hits, slower pop-in.

## 0.15.0

- **Fix Idle Support Checks** — the wake fan-out is now exact. Waking a changed piece's
  neighbourhood used to dirty every piece sharing an 8 m grid cell — hundreds in a dense
  base instead of the handful whose support regions actually touch — and an hour of live
  profiling attributed ~31 ms of every second to that spray plus the spurious recomputes it
  caused. Grid entries now carry each piece's support envelope, and an exact overlap test
  picks the true neighbours before anything is woken. Same safety story (the verify covers
  it), far less work per wake. The hygiene revalidation cap also rose from 5 to 9
  consecutive skips.
- **Fix Smoke Overhead** *(client)* — at fire-heavy bases the smoke system measured ~29 ms
  of every second, almost all per-smoke-per-frame engine calls: a physics mass write per
  smoke per frame (the value is a smooth curve of the smoke's age), the 10 m render-chunk
  recheck reading every smoke's position every frame (smoke crosses a chunk every several
  seconds), and a second position read per smoke when building the particle batches. The
  mass now writes on 2% lifetime steps (drift from vanilla bounded at 0.04 on a 0..1
  curve), the chunk recheck runs at 4 Hz (particle positions are world-space, so stale
  membership renders identically), and each smoke's position is read once. The render loop
  also clamps to the 100-entry chunk arrays vanilla indexes unguarded.

## 0.14.0

- **Fix Idle Support Checks** *(both)* — the largest steady cost while standing in a large
  base: every building piece re-validates its cached structural support on every updater
  visit, re-resolving each cached neighbour with native calls just to confirm nothing
  changed (~100 ms of every second at 60-90k pieces, where the updater runs saturated).
  Pieces now sleep between the events that can actually change support: a piece placed or
  destroyed nearby (tracked through a coarse world grid of support envelopes), a
  neighbour's support value changing (recomputes still cascade as far as vanilla's
  relaxation waves would), terrain edits and every other vanilla invalidation signal (all
  funnel through `ClearCachedSupport`). Unsupported, about-to-collapse pieces never sleep,
  a piece revalidates through vanilla after at most 5 consecutive skips as a safety net,
  and the sleep decision stands down entirely if any wake hook failed to attach. A new
  admin-only "Verify Support Sleep" diagnostic runs vanilla on every visit while checking
  the predictions and logs engagement plus any missed wake.
- **Verify Zone Occupancy** now reports its summary every 250 comparisons instead of 2000 —
  the check runs a few times per second at most, so the old threshold could pass a whole
  session in silence (the first verify session's ~1,300 comparisons and 0 divergences were
  only recoverable from the absence of DIVERGED lines).

## 0.13.0

The streaming release. With the crossing stalls fixed, profiling a large base *streaming in*
showed the cost was no longer any single stall but repeated bookkeeping around the object
stream: re-sorting the spawn backlog 30 times a second, walking every loaded instance to ask
if a zone is empty, and five string-based engine invokes per spawned piece.

- **Fix Spawn Queue Churn** *(both)* — while a spawn backlog exists, every 30 Hz pass
  rebuilds the pending list (a Created check and a distance computation per near ZDO) and
  sorts the entire backlog, only to consume its head. Streaming a large base held the
  backlog in the tens of thousands for minutes: the re-sort alone measured ~56 ms/s and the
  refilter ~18 ms/s. The filtered, sorted queue now persists across passes with a cursor and
  is rebuilt — vanilla's exact filter and sort — every third pass or when the player changes
  zone. Spawn rate, order and budget are untouched; stale entries (created meanwhile,
  recycled by the ZDO pool, zone not ready) are guarded per-slot and re-examined within
  ~100 ms.
- **Fix Zone Occupancy Scan** *(both)* — deciding whether a zone can unload asks "does
  anything stand in it" by iterating all ~90k loaded instances with an alive-check and a
  transform read each (~29 ms/s while streaming). A per-zone tally of non-distant instances
  — maintained at the instance dictionary's single write site, the view's OnDestroy, and the
  ZDO sector-move path — answers in O(1). A new admin-only "Verify Zone Occupancy"
  diagnostic compares the tally against vanilla's walk.
- **Fix Piece Material Polling** *(both)* — every spawned piece with material variation
  schedules five polls through Unity's string-based invoke machinery just to wait for its
  random seed, then re-derives and re-writes identical values on every poll after the first
  success, allocating a System.Random per property and re-hashing shader property names each
  time (~35 ms/s while pieces stream). One shared ticker replaces the per-piece scheduling,
  property-name hashes are cached, and polling stops once the values are applied — they are
  pure functions of a write-once seed, so the repeats were state-level no-ops. Same retry
  schedule for unseeded pieces, same owner-side seed write, same visuals.

## 0.12.1

- **Fix Unload Sweep Cost** *(both)* — every 30 Hz object pass that is not provably idle runs
  `ZNetScene.RemoveObjects`: earmark every near and distant ZDO, then walk all loaded
  instances, to find the few objects that left the loaded area in the last 33 ms. The sweep
  is O(all instances) regardless of how many it removes — ~77 ms per moving second in a
  60-90k-instance base, ~8% of frame time, most of it millions of trivial getter calls. The
  sweep now runs on a wall-clock interval ("Object Unload Sweep Interval", default 100 ms,
  0 = vanilla) and whole passes are skipped in between: departing objects linger up to a
  tenth of a second longer at the far edge of the loaded distance, and nothing else changes.
  Unlike the withdrawn spawn-burst budget this throttles *checking* for work, not doing it,
  so nothing accumulates and each sweep costs the same as before — there are just ~3x fewer.
  Alongside it, the unload fast-pass reads the two fields its hot loop needs directly
  instead of through their trivial accessors.

## 0.12.0

The chunk-crossing release. With the mega-base steady state fixed (ZNetScene's object pass
is down from ~17% of frame time to ~1%), the remaining complaint was crossing a zone
boundary into a built-up area: a ~27-second episode of 12-19 fps with individual frames of
100-600 ms — the worst of it a delegate pile-up:

- **Fix Piece Event Stall** *(both)* — every building piece subscribes a C# event on its
  heightmap (`Heightmap.m_clearConnectedWearNTearCache`, whose only purpose is flushing
  cached support after terrain edits) in `Start` and unsubscribes in `OnDestroy`. A
  multicast delegate is an immutable array, so with 10-20k pieces on one heightmap each
  subscribe copies the whole list and each unsubscribe scans it — O(n²) for the batches a
  zone crossing loads and unloads, plus an n-element allocation per operation feeding GC
  pauses. The crossing's single worst frame (600 ms) was mostly this. Pieces now register
  in a per-heightmap lookup table — O(1), allocation-free — and a `Regenerate` hook
  delivers the same cache clears. The event stays intact for any other subscriber, and the
  toggle is safe to flip at runtime in both directions.

## 0.11.3

- **Fix Idle Scene Sweep** — the after-pass bookkeeping no longer counts pending candidates
  when the pass provably was not quiet: during world streaming the candidate list holds tens
  of thousands of entries and counting them 30 times a second just to conclude "not idle"
  was measured at whole seconds of login time. The count now runs only when its answer can
  matter. No behavior change.

## 0.11.2

- **Fix Idle Scene Sweep** — the change detector is now a ring-membership hash instead of a
  raw counter. Verify telemetry showed the counter engaging on only ~15% of passes in a
  lively multiplayer base: every incoming ZDO-data packet bounces its listed objects' sector
  out to a sentinel and back within one handler, and animals crossing sector lines inside
  the streamed ring bumped it too — all changes that net to nothing the object pass could
  see. The hooks now XOR the object's id into a running hash only when the touched sector is
  inside the streamed ring, so bounces and in-ring crossings cancel algebraically while real
  arrivals, departures, spawns and despawns register. Sectorless created/destroyed events
  keep their own counter, the 1-second safety pass still bounds everything else, and the
  filter ring is re-aligned at the start of every full pass so it can never be stale for a
  pass that could go on to skip. Verify results to date: 0 divergences (2,700 passes idle
  skip, 175,000 comparisons support lookup).

## 0.11.1

No behavior changes. The two new Verify diagnostics now report engagement — "would have
skipped X of Y passes" / comparison counts — every ~30 seconds and once when turned off,
because zero divergences is only evidence if the predicate actually armed during the
session. And the light-flicker distance gate caches its decision with the anchor, so
between refreshes it is one dictionary hit instead of per-frame distance math.

## 0.11.0

The mega-base release, from profiling a 60-90k-instance build at 42 fps: the per-frame
systems that scale with loaded object count. Fix totals are now 40 — 21 client, 5 server,
14 both.

### Performance

- `ZNetScene.CreateDestroyObjects` *(both)* — the streamed-object pass runs 30 times a
  second and is O(every loaded object) end to end: rebuild the near and distant lists from
  the sector stores, enumerate every near ZDO's Created flag (and sort the candidates),
  earmark every ZDO and sweep every live instance for removals. None of it depends on
  anything having changed, and in the profiled base the pass was 17% of ALL frame time
  (~97 s of a 10-minute window). Three always-on hooks now maintain a scene version at the
  choke points every relevant change crosses — `ZDOMan.AddToSector`/`RemoveFromSector`
  (verified the only mutators of the sector stores; `ZDO.SetSector` early-outs on
  same-sector so the hooks stay cold) and `ZDO.set_Created` (the one removal path with no
  sector signal) — and a pass is skipped outright when the reference zone, the version, and
  the active-area sizes are unchanged and the previous full pass ended with nothing pending
  (uncreated candidates are counted with a resolvable-prefab filter, so worlds carrying
  orphaned modded ZDOs still reach idle). One full vanilla-shape pass per second runs
  regardless as a safety sweep, bounding every exotic untracked path at ~1 second of
  staleness. On a busy server, peer exploration keeps the version moving and the pass
  simply stays vanilla. An admin-only `Verify Scene Idle Skip` predicts skips, always runs
  vanilla, and logs if a predicted-skippable pass did real work; the hooks are checked at
  first use and the whole fix stands down if any failed to attach.

- `WearNTear.UpdateSupport` *(both)* — resolves "which piece owns this collider" with a
  native hierarchy walk (`GetComponentInParent`) at three call sites, including once per
  cached support collider on every invocation even when the cache holds. Sliced over every
  piece of a large base continuously, the walks alone measured ~11.5 s of a 10-minute
  window. A collider-to-piece table now answers it: registered when a piece builds its
  collider list, learned on miss (so a cold table costs exactly vanilla), cleaned through a
  reverse map on destroy, cleared at scene shutdown. The three call sites are rewritten by
  a transpiler that requires exactly three replacements or backs out untouched. Every call
  site already null-checks the result, so a stale entry behaves as vanilla's "no ancestor";
  an admin-only `Verify Support Lookup` compares table and walk and acts on the walk. The
  overlap-box physics queries themselves are untouched — they are the algorithm.

- `LightFlicker` and the dormant point-light cap *(client)* — two halves. Torch flicker
  updates every flickering light every frame with per-light engine calls, invisible past a
  few dozen metres; flicker now stops updating beyond a configurable distance (default
  45 m — the game's own light LOD fades the light itself at 40), with each light's cached
  position refreshed every few frames so carried torches stay correct, and TTL-driven
  flash lights always updating since they destroy themselves from inside the update. And
  the game ships a complete priority-ranked point-light cap it never wires up:
  `LightLod.m_lightLimit` ranks all point lights by distance once a second and keeps the
  nearest N enabled with a smooth fade — the graphics menu only ever drives the shadow
  half. A client-local `Point Light Limit` (default -1 = exactly vanilla) now exposes it
  for torch-heavy bases.

## 0.10.0

One rework, driven by profiling the Mistlands on the previous build. It also fixed a
regression risk the collider work exposed: the collider assignment cook was measured moving
into this mod's own assignment hook in 0.9.0 and was fixed there by baking with the
collider's actual cooking options — 0.10.0's measurements confirm assignment cooks at ~2% of
their former cost.

### Performance

- **Fix Mist Query Overhead** (`Mister` / `ParticleMist`) *(client, rework)* — the 0.5.0
  version snapshotted mist volume positions once per frame and ran vanilla's loops as pure
  math. Mistlands profiling on the fixed build showed what remained: the loops are
  O(particle candidates × all loaded misters) — hundreds of misters at dozens of candidates
  per tick — and the per-frame snapshot rebuild itself re-read every mister position at
  several hundred fps. Three changes: the mister snapshot is now event-driven (misters never
  move — nothing in the vanilla assembly writes their transforms — so it rebuilds only on
  spawn/despawn, with a slow safety refresh as a hedge for hypothetical modded movers);
  misters are bucketed by 64 m zone so a query reads only its own zone's bucket — a handful
  of volumes instead of hundreds (queries with out-of-range radii, which vanilla never
  issues, fall back to the full snapshot); and `FindMaxMistAlltitude`'s 20 ground-probe
  raycasts per tick are answered from heightmap data via the shared registry and sampler,
  with the Random draws replicated exactly so particle randomness downstream is unchanged.
  Demisters keep the per-frame refresh — they are few and genuinely move.

## 0.9.0

Two new fixes and two reworks, from the next profiling pass. The prefab index also passed its
formal verification this cycle: 66 verify runs against a 1,039,827-ZDO world, zero
divergences. Fix totals are now 37 — 18 client, 5 server, 14 both.

### Config

- The four `Verify …` diagnostics moved from the Fixes sections into a new **Debug** section,
  so a new user browsing the config does not mistake them for fixes: each deliberately runs
  both the indexed path and vanilla's for comparison, costing exactly the work its fix exists
  to avoid. Moving a config entry changes its key, so a previously saved value for one of
  these is orphaned in the .cfg — harmless, since they all default to off and are only ever
  turned on deliberately.

### Performance

- `WaterVolume.UpdateMaterials` *(client)* — runs for every loaded water tile every frame and
  is a single line: fetch the surface material from the engine, set the advancing water time
  on it. The fetch (`Renderer.get_material`) answers the same reference for the volume's whole
  life yet cost ~1.4 s of a 10-minute coastal session in native calls. The material is now
  cached per volume, dropped when the volume disables; the per-frame water-time write itself
  is unchanged, as is every other `.material` user.

- `TerrainLod.RebuildAllHeightmaps` *(client)* — every 256 m of travel, the distant-terrain
  ring rebuilds all nine 81×81-vertex heightmaps in a single frame, each with a per-vertex
  live biome lookup on the main thread. A fixed-cadence hitch, most noticeable sailing. At
  most N tiles (default 3, configurable, 9 = vanilla) now rebuild per frame; the state
  machine re-enters until the ring completes, and a companion hook stops vanilla from
  re-asking the build thread about tiles already rebuilt this cycle, which would otherwise
  queue redundant builds. During the short spread the ring is positionally torn between old
  and new centres — at 800+ m under distance fog, far less visible than the hitch.

- **Fix Background Zone Pacing** (`ZoneSystem.CreateGhostZones`) *(server, rework)* — the
  first version deferred a generation tick when the previous frame ran long, which at high
  framerates almost never engages: the frame after a burst recovers well before the next
  100 ms tick, so the ten-bursts-a-second cadence survived, and measurement showed it. Each
  ghost generation is now also timed, and one that itself exceeded the frame budget arms a
  cooldown (default 2 ticks, configurable, 0 restores the old behaviour) before the next —
  identical bursts, further apart. The starvation guard and the untouched player-zone path
  carry over.

- **Fix Zone Collider Stall** (`Heightmap.RebuildCollisionMesh`) *(client, rework)* — the
  deferral now also covers delayed-poke rebuilds: `TerrainModifier` spawns and removals defer
  their terrain rebuild to LateUpdate by design, and that path turned out to carry most of
  the remaining main-thread collider cooking (~5 s of a 10-minute generation-heavy window).
  Safer than the original case, even: the collider keeps its previous mesh during the
  deferral — stale by centimetres for a frame or two, never absent. Urgent rebuilds
  (terraforming, load-time terrain) still cook synchronously, and rebuilds while no local
  player exists (loading) are unchanged.

## 0.8.0

No new fixes — two existing ones got cheaper, driven by another profiling pass.

- **Fix Terrain Seams** (`Heightmap.RebuildRenderMesh`) *(client)* — vanilla's rebuild computed
  normals and tangents that this fix then overwrote the same frame; the tangent half of that
  (Unity's generic `RecalculateTangents` pass) was pure waste, measured at ~2 s of a 10-minute
  generation-heavy window on top of the fix's own passes. The rebuild's tangent call is now
  transpiled into a runtime decision: it defers whenever this fix will supply tangents later in
  the frame, and those are computed analytically in the same loop as the normals — for this
  mesh's planar UV layout the tangent is a closed-form function of the normal (one shared
  formula, also used by the verify comparison). A rebuilt map whose cross-boundary pass bails
  (missing neighbours) gets analytic tangents from the vanilla normals it kept, since the
  rebuild's `mesh.Clear()` wiped the old ones; a rebuild with no processing hook alive (the
  menu scene) keeps Unity's pass. Normalization in the vertex loop is also done with one
  inverse square root instead of `Vector3.normalized`, which profiling showed as real cost at
  4225 vertices × 5 maps per burst.

- **Fix Grass Ground Raycasts** and **Fix Static Object Ground Checks** *(client / both)* —
  their data paths read `transform.position` once per query and went through the patched
  `FindHeightmap`'s full toggle-and-trampoline overhead. The heightmap lookup registry now also
  caches each tile's transform origin and serves in-mod callers directly, so a grass or ground
  query on the hot path does no native reads and no Harmony round-trip at all; both fall back
  to the public lookup when the registry cannot serve.

## 0.7.0

The two next-biggest measured items after 0.6.0: grass placement's raycast habit, and the
timing of background zone generation. Fix totals are now 35 — 16 client, 5 server, 14 both.

### Performance

- `ClutterSystem.GetGroundInfo` *(client)* — grass placement answers "where is the ground, and
  which way does it face" with a 1 km `Physics.Raycast` per clutter candidate: up to 80
  candidates per clutter type per 8 m patch, one patch per frame while moving, and a
  multi-patch burst when zones load — hundreds of raycasts per frame in the steady state, and
  after 0.6.0 the largest steady per-frame cost measured (~4.2 s of a 5-minute window in patch
  generation). The ray's mask is exactly the "terrain" layer and its handler dereferences the
  hit collider's `Heightmap` unconditionally, so the only thing it can ever hit is a zone
  heightmap's collision mesh — whose shape is already known. The query is now answered from
  heightmap data: same triangle split as the collision mesh (the shared `HeightmapSampling`
  helper), same interpolated height, same geometric normal, same biome call, with vanilla's
  ±500 m ray window replicated. A raycast sees the last baked collider while this reads current
  data, which is never staler. Clutter is cosmetic, client-local, never saved, and regenerated
  on a 2-second timeout — the lowest-risk place in the game for this substitution.

- `ZoneSystem.CreateGhostZones` *(server)* — the game generates one complete zone per 100 ms
  tick entirely in one frame, blind to how the frame is doing; walking into virgin terrain is
  one full generation burst every 100 ms, the dominant remaining spike source measured after
  0.6.0 (~6 s of the stutter-heavy seconds in 5 minutes). Most bursts are *ghost* zones —
  background pre-generation of the ring around the host and each peer, with no same-frame
  consumer. When the previous frame already exceeded a configurable budget (default 30 ms), the
  tick's ghost generation is now deferred to the next 100 ms tick. The same zones generate
  identically from the same seeds — only the timing spreads out. A starvation guard generates
  regardless after 4 consecutive skips, bounding the added latency to ~half a second under
  sustained load, and zones the player actually enters (`CreateLocalZones`) are never touched.
  On a dedicated server the ~20 ms simulation tick sits under the default budget, so pacing
  only engages when the server is genuinely struggling.

## 0.6.0

One fix, and it was the biggest item left on the profile: the prefab query scan. Fix totals are
now 33 — 15 client, 4 server, 14 both.

### Performance

- `ZDOMan.GetAllZDOsWithPrefabIterative` *(both)* — answers "every ZDO of this prefab" by
  scanning the whole world: every sector list plus the outside-sector map, dereferencing every
  ZDO to compare its prefab hash. Vanilla only reaches it from a console command, but it is the
  public API mods use, and several popular ones call it every `ZoneSystem.Update` tick from
  postfixes. Profiling a live modded session attributed ~8.6 seconds of a 10-minute window to
  these scans — the single largest item inside `ZoneSystem.Update` during stutter-heavy seconds,
  and after 0.5.0 the largest remaining cost in the whole profile.

  ZDOs are now bucketed by prefab hash as the prefab is assigned — on `ZDO.SetPrefab`, on the
  network deserialize path, with a full rebuild at world load — so the query returns O(matches)
  instead of O(world). The iterative contract is preserved exactly: an iteration already in
  flight (`index != 0`) finishes under vanilla so mods that spread the drain across frames keep
  their cursor semantics, a fresh iteration completes in one call (a legal fast completion —
  the return value means "iteration complete" and every caller loops until it), and the final
  `RemoveAll` over the caller's whole accumulated list is replicated verbatim, pre-existing
  entries included. Result ordering changes from sector-grouped to bucket order; callers treat
  the list as a set.

  Index maintenance runs even when the fix is toggled off (an index that missed changes before
  a mid-session switch-on would answer wrongly forever; only the read path checks the toggle),
  and unlike the disconnect-sweep index it is not gated on the network role — this method runs
  on clients, listen hosts and dedicated servers alike. An admin-only `Verify Prefab Index`
  runs both the index and vanilla's full scan on every query, acts on vanilla's answer, and
  logs any divergence. The maintenance hooks are checked at first use — against the specific
  hook class, since this mod patches two of the same methods for the disconnect-sweep index —
  and the whole fix stands down to vanilla if any failed to attach.

## 0.5.0

Zone loading and generation performance, measured first. This release came out of profiling real
play sessions with a sampling profiler: freshly generating areas micro-stutter because
`ZoneSystem.Update` builds one whole zone per 100 ms tick in a single unbudgeted frame, and the
biggest measurable costs around that burst turned out to be mist emission, static-object ground
checks, terrain tile lookups, grass rebuilds, the terrain build thread's own pacing — and two of
this mod's existing fixes, which are reworked below. Fix totals are now 32 — 15 client, 4 server,
13 both.

### Performance

- `Mister.InsideMister` and the mist emission loops *(client)* — mist emission asks "is this point
  inside a mist volume" for every particle candidate, ten times a second, and each ask loops over
  every `Mister` reading `transform.position` — a native interop call — once or twice per volume,
  plus a matching per-particle loop over every `Demister` reading a native force-field range. In the
  Mistlands that multiplies to thousands of interop calls per frame; profiling attributed ~42% of
  all time in stutter-heavy seconds of a Mistlands session to these loops. Positions and ranges are
  now snapshotted into plain arrays once per frame, lazily on first query, and every query
  (`InsideMister`, `IsInsideOtherMister`, `IsCompletelyInsideOtherMister`, the demister checks, and
  through them the AI mist-sight checks) runs the same arithmetic over the snapshot. Staleness is
  bounded by one frame, which is inside vanilla's own tolerance — it already treats these positions
  as constant across each 100 ms tick. ComfyMods' Dramamist was reviewed and is complementary: it
  changes what the mist looks like, not what the queries cost, and the two touch no common method.

- `Heightmap.FindHeightmap(point)` and `Heightmap.HaveQueuedRebuild(point, radius)` *(both)* —
  "which terrain tile is this point on" is a linear scan of every loaded heightmap, with a native
  `transform.position` read per candidate, re-answered thousands of times a second — including once
  per grass update tick — for tiles that are instantiated at their zone centre and never move. The
  registry is now mirrored with cached centres and a zone-keyed dictionary: the common case is one
  `GetZone` and a dictionary hit, the fallback scan runs on cached floats in vanilla's own order,
  and the radius overload of `FindHeightmap` is deliberately left vanilla because terrain-op fanout
  writes terrain data through it. For a point exactly on a shared zone edge the fast path returns
  the `GetZone`-owning tile where vanilla returned whichever qualifying tile registered first — both
  contain the point and share identical edge vertices, so no caller can tell. An admin-only
  `Verify Heightmap Registry` runs both paths, acts on vanilla's answer and logs any real
  disagreement, and the maintenance hooks are checked at first use with the whole fix standing down
  to vanilla if any failed to attach.

- `StaticPhysics.SUpdate` *(both)* — the component on every tree, rock and placed prop that checks
  whether the ground under it disappeared re-reads `transform` and `transform.position` around six
  times per check across its helpers, and `SlowUpdater` drives a hundred of these checks per frame
  continuously. Every freshly generated object also schedules its first check 20 seconds after
  spawn, so a generation burst turns into a delayed wave. The check now fetches the transform once
  and reads the position once, with identical order, thresholds and side effects. An advanced,
  default-off `Static Ground Checks Use Heightmap Data` additionally answers the no-solids terrain
  height question from heightmap data — evaluating the same triangle surface the 10 km raycast
  would hit, split along the same diagonal the collision mesh indexes — instead of going through
  the physics engine; `m_checkSolids` objects keep the raycast, which genuinely needs to see rocks
  and buildings.

- `ClutterSystem.GeneratePatches` *(client)* — grass generation is normally budgeted to one patch
  per frame, but the `rebuildAll` path skips the limiter, and `TerrainComp.CheckLoad` fires it with
  a whole-zone radius whenever a zone with saved terrain edits loads: up to ~64 patches regenerated
  in one frame at hundreds of raycasts each — a reliable spike entering any built-up area. The
  rebuild is now budgeted (default 8 patches per frame, configurable): it re-runs vanilla's own
  one-patch ring walk N times, which regenerates the N nearest stale patches in vanilla's own
  centre-out order, then re-arms itself for the next frame until done. No placement logic is
  copied, so what gets generated cannot drift from vanilla.

- `HeightmapBuilder.BuildThread` *(both)* — the terrain build thread sleeps 10 ms after **every**
  loop iteration, including one that just finished a build with more work queued, capping terrain
  generation well below what the thread could do — and every such sleep directly extends
  `SpawnZone`'s is-terrain-ready wait and `RequestTerrainSync`'s main-thread busy spin. Its ready
  queue also silently discards the oldest finished result beyond 16 held, and the distant-terrain
  ring alone keeps 9 in flight, so finished terrain could be evicted and rebuilt from scratch. The
  same loop now sleeps only when idle and the cap is raised and configurable (default 32, roughly
  100 KB per held result). Mutex discipline is copied exactly. The build thread starts once, so the
  patched body only takes effect when patched before the singleton exists — which plugin load order
  guarantees, and a startup check logs if that assumption is ever violated.

- `Heightmap.RebuildCollisionMesh` *(client)* — **off by default this release.** Assigning a
  MeshCollider's `sharedMesh` cooks the PhysX mesh synchronously on the main thread, and every zone
  heightmap rebuild pays it. For an already-generated zone entering the active ring — the common
  case while travelling — that cook is most of the remaining boundary-crossing stutter. For exactly
  that case (`SpawnZone` with `SpawnMode.Client`, a local player present, and not the zone the
  reference position is in) the cook is now skipped, the mesh is baked on a worker thread with
  `Physics.BakeMesh`, and the collider is assigned at the end of a following frame's LateUpdate,
  where Unity finds the bake cached. Fresh generation, terraforming, and load-time terrain rebuilds
  keep the synchronous cook — their callers raycast the collider in the same frame. The deferral
  window extends the zone's no-collider state by a frame or two rather than introducing a new one:
  the frame before, the zone did not exist. Rebuild-while-baking and destroy-while-baking both
  settle the bake first. Enable `Fix Zone Collider Stall` to help soak it; it ships default-on once
  it has.

### Own overhead removed

Profiling the game also profiled this mod, and two of its fixes showed up. Both keep their config
keys and their behaviour; only their cost changed.

- **Fix Object Unload Crash** (`ZNetScene.RemoveObjects`) *(both)* — the guarded replacement ran a
  Unity-null alive check — a native interop call — on every instance, every unload pass, at 30 Hz,
  to defend against an orphan that appears roughly never. The steady state now runs vanilla's exact
  loops, with no guards and vanilla's exact cost, inside a try/catch; only a throw drops it into
  the guarded sweep, which cleans the orphan up and is safe to restart from scratch after a partial
  fast pass. The recovery path is also hardened against modded components whose teardown throws.

- **Fix Terrain Seams** (`Heightmap.RebuildRenderMesh`) *(client)* — three costs removed. The
  per-sample `transform.position.y` reads (four per vertex, 4225 vertices per map) are hoisted to
  one read per map. The eager neighbour refresh — which recomputed up to five maps per rebuild, and
  the same map repeatedly during generation bursts — is now a dirty set processed once per frame
  after all of the frame's rebuilds, each affected map exactly once. And Unity's generic
  `RecalculateTangents` pass is replaced by analytic tangents computed in the same loop as the
  normals — for this mesh's planar UV layout the tangent is a closed-form function of the normal.
  An advanced `Verify Terrain Tangents` diagnostic runs Unity's version instead, compares, and
  reports, in case terrain lighting ever looks suspect.

## 0.4.0

Install-side gating, ZDO and socket work, log spam, and the terrain paint seam.

### Install sides

- Every fix now declares which side it runs on, and the client-only ones are **no longer applied at
  all on a dedicated server** — a headless process has no local player, so nothing there could ever
  reach them. Of the 26 fixes, 12 are client, 4 are server and 10 are both; the startup log prints
  that breakdown, so a one-sided install is self-describing. Each toggle's description in the config
  carries the same tag, generated from the same declaration, so the two cannot drift apart.

  A dedicated server is identified by having no graphics device, which is the test Valheim itself
  uses (`RandEventSystem.RefreshPlayerEventData`) and the same one Jotunn's `GUIManager.IsHeadless`
  makes. Server-side fixes are deliberately *not* gated the same way: a client process can start
  hosting a world later in the same run, and the network role is not knowable when patches are
  applied. `Patch Every Side` in the config overrides the whole thing if the detection is ever wrong.

  The two terrain seam fixes keep their existing runtime `IsDedicated` check as well. They are the
  only client fixes whose target method genuinely runs and does real work on a dedicated server —
  it builds terrain meshes and paint textures for its colliders — so being unpatched there is a
  saving rather than a no-op.

### Performance

- `ZDOMan.RemoveOrphanNonPersistentZDOS` *(server)* — runs on every disconnect and walks *every ZDO
  in the world* to find the handful a departing player left ownerless. `Persistent` and `HasOwner`
  are cheap bit tests, but reaching them dereferences a separate heap object per entry, so on a
  multi-million-ZDO world that is essentially all cache misses across a multi-gigabyte heap, plus a
  linear peer scan and a dictionary lookup per candidate. Hundreds of milliseconds of frozen main
  thread every time somebody logs out, to find a few dozen objects.

  Non-persistent ZDOs are now bucketed by owner as ownership changes, so the sweep visits only the
  buckets whose owner is gone — O(orphans) instead of O(world). The index is maintained even when the
  fix is toggled off, because an index that missed every change before the switch would leak orphans
  forever; only the sweep reads the toggle. An admin-only `Verify Orphan Index` runs both the index
  and vanilla's full scan, acts on vanilla's answer and logs any divergence, so the index can be
  proven on a live server before it is trusted. The hooks are also checked at first use, and the
  whole fix stands down to vanilla if any of them failed to attach.

- `ZDOMan.ConnectSyncTransforms` *(server)* — the third of the world-load pairing loops, joining
  0.2.0's `ConnectPortals` and `ConnectSpawners`. Same nested-loop shape, same hash-indexed
  replacement, and it covers every ship and cart the world has ever held.

- `ZPackage.Write(ZPackage)` *(both)* — copied the source package's entire buffer onto the heap via
  `GetArray()` before writing it. That is the innermost step of ZDO synchronisation, so the whole
  payload was duplicated once per peer per send tick, and again on every routed RPC and every
  receive. Now written straight from the flushed stream buffer. The bytes on the wire are identical.

- `ObjectDB.GetRecipe` *(client)* — **removed.** 0.1.0 shipped a name-indexed lookup replacing the
  linear scan. It has been withdrawn: the index had to be invalidated on every `ObjectDB` mutation to
  stay correct, and no mod-compatible way to catch all of them was found. This is a regression back
  to vanilla behaviour for anyone upgrading from 0.1.0-0.3.0.

### Correctness

- `Player.AddStamina` *(client)* — bounds only the top of the range: `m_stamina += v` followed by a
  clamp against `m_maxStamina` and nothing else. Vanilla never passes a negative `v`, so this only
  opens up under mods, but any mod that reduces stamina with `AddStamina(-x)` — rather than
  `UseStamina`, whose RPC does floor at zero — takes the field arbitrarily negative. Every
  `HaveStamina` gate is a strict `>`, so the player then cannot attack, run, dodge, sneak or build,
  and cannot move at all while encumbered.

  A negative on its own is largely self-correcting — `UpdateStats` → `UpdateFood` → `SetMaxStamina`
  clamps to `[0, m_maxStamina]` once a second, and `Player.Load` clamps the same way — so the fix
  closes a sub-second window in which the gates read false, the value is published to the ZDO for
  other peers, and the HUD bar draws negative.

  `NaN` is the case nothing recovers from. Unity's `Mathf.Clamp` is a pair of comparisons, and every
  comparison against `NaN` is false, so all three of vanilla's clamps return it unchanged. The regen
  gate `m_stamina < maxStamina` is false as well, so regen never runs, and `Player.Save` writes the
  `NaN` back to the character file unvalidated — the player stays broken across every relog.
  `Player.RPC_UseStamina` is the network-facing route: it is a registered RPC, so the float arrives
  from whichever peer sent it and the handler checks only `v == 0`. The public `UseStamina` wrapper
  does screen `NaN`, but its guard sits one line above `v *= Game.m_staminaRate` — and
  `Game.m_staminaRate` is a public static filled from the `StaminaRate` world key by
  `Game.trySetScalarKey`, which parses with `float.TryParse(s, NumberStyles.Any, InvariantCulture,
  …)`. That accepts the literal `"NaN"`, so one malformed global key turns every *local* `UseStamina`
  call into a screened value multiplied straight back into `NaN`.

  The field is now floored at zero after `Player.AddStamina`, and after `Player.Load` so a character
  already saved in a bad state is repaired as it enters the world. `Player.RPC_UseStamina` is handled
  the other way round — a call whose `v` is not finite is dropped rather than applied and then
  cleaned up, because repairing after the fact would mean a peer sending garbage silently resets the
  victim's stamina to zero. For any finite `v` vanilla's own floor is already correct, so nothing
  else about that path changes.

  Floor only: `RPC_UseStamina` with a negative `v` can push stamina above `m_maxStamina`, but
  `SetMaxStamina`'s per-second clamp already pulls that back, and a ceiling here would fight mods
  that grant over-max stamina deliberately. One warning per player per episode, where an episode ends
  only once stamina is genuinely above zero — the patch's own write of `0f` does not count, so a mod
  breaching the floor every frame logs once rather than once a frame. The rejected-RPC warning is
  tracked separately and is once per player, since dropping a call leaves stamina untouched and there
  is no recovery event to end an episode on.

- `ItemDrop.ItemData.GetIcon` *(client)* — indexes `m_shared.m_icons` with the item's stored
  `m_variant` and no bounds check, so an item saved with a variant its prefab no longer has — after a
  game update or a removed item mod — throws from every inventory, tooltip and crafting panel that
  tries to draw it. The index is now checked, falling back to the first icon, and the item name is
  logged once rather than once per draw.

- `ZSteamSocket.SendQueuedPackages` *(both)* — logs `Failed to send data` through `ZLog.Log` on every
  frame a socket's send queue is backed up, which is exactly when the machine can least afford to be
  formatting lines, capturing stack traces and writing to disk. A server holds one socket per
  connected peer where a client holds one. Repointed to this mod's debug log; the `break` semantics
  of the surrounding loop are unchanged.

- `Container.RPC_RequestOpen` / `RPC_RequestStack` / `RPC_RequestTakeAll` *(both)* — four log lines
  per chest operation, including a leading one built unconditionally out of `gameObject.name`,
  marshalled fresh out of native Unity. These land on the container's owner — `ZNetView.InvokeRPC`
  addresses `m_zdo.GetOwner()`, and a relaying server never invokes the handler — so a player sorting
  their base logs their own chests and a server logs the ones it owns. Same redirect as above.

  Both log fixes redirect rather than delete: turning on `EnableDebugMode` brings the messages back.

- `DungeonGenerator.OnRoomLoaded` / `ZoneSystem.UnsetLoadingInZone` *(both)* — a dungeon claims its
  zone with `SetLoadingInZone` before it starts loading its rooms asynchronously, and only gives the
  claim back once `m_roomsToLoad` reaches zero. `OnRoomLoaded` returns early on any result other than
  `Succeeded` — *before* the decrement — so a single room whose asset fails to load means the counter
  never completes. `Spawn` never runs, so the dungeon interior never appears, and the only call to
  `UnsetLoadingInZone` never runs either, so the zone stays claimed until the generator itself
  unloads. Which it will not do while a player is standing in it.

  A zone stuck that way is not cosmetic. `IsZoneReadyForType` returns false, so `ZNetScene` stops
  creating that zone's objects; `IsAreaReady` returns false, so respawn completion and teleport
  completion never finish — anyone who spawns into or teleports to that zone sits on the loading
  screen indefinitely.

  Failed rooms are now accounted for like successful ones and dropped before `Spawn` walks them,
  since `PlaceRoom` opens with `roomData.m_prefab.Asset.GetComponent<Room>()` and would throw on an
  unloaded asset — landing back at a claimed zone by another route. A dungeon missing a room is a bad
  outcome; a zone the server can never finish loading is a worse one.

  `UnsetLoadingInZone` is guarded at the same time. It indexes `m_loadingObjectsInZones[sector]`
  directly, and the ZDO handed to it on `OnDestroy` is one the generator cached at claim time. If
  that ZDO was destroyed in between it has already been pooled and reset — `ZDOPool.Release` runs
  synchronously inside `HandleDestroyedZDO`, while `Object.Destroy` defers `OnDestroy` to end of
  frame — so its sector reads as `(0,0)` and the lookup either throws or edits an unrelated zone's
  list, leaving the real claim in place. It now falls back to finding the entry where it actually is.

  Note this is *not* the case of a room prefab going missing after a mod is removed: `Load` already
  trims those, and that path was never broken.

- Transpiler bail-outs *(both)* — every transpiler here counts what it rewrote and returns the
  original instructions when the count is wrong, which is what lets this mod share a patched method
  with mods that hard-match IL. That bail-out did not work. `new List<CodeInstruction>(instructions)`
  copies the list but not the instructions, and Harmony hands the same instruction objects to every
  transpiler in the chain, so rewriting an operand through the copy edited the original stream too —
  and returning it "untouched" handed back the rewrite. Six of the nine bail-outs happened to be safe
  because they only trigger on a count of zero, with nothing yet rewritten. Three could not:
  `ZDOMan.Load`, `Player.AutoPickup` and `LiquidVolume.Awake` all bail on a *non-zero* wrong count,
  so they applied a partial rewrite while logging that they had not. `LiquidVolume.Awake` was the
  one that mattered — its comment promises it will not half-apply the `Allocator.Persistent` switch,
  which is exactly what it was doing. All nine now copy the instructions themselves.

### Terrain

- `Heightmap.ApplyModifiers` boundary paint reconcile *(client)* — **supersedes the 0.3.0 entry
  below, whose reasoning was wrong.** 0.3.0 resolved a disagreeing boundary texel to the per-channel
  *minimum*, argued as safe because it "can only ever remove paint, never invent it". That is true and
  still the wrong thing to do. `RebuildRenderMesh` assigns `UV = (x / m_width, y / m_width)` over a
  `(m_width + 1)` texture, so a zone's last one-metre row of quads interpolates *into* the boundary
  texel. Forcing that texel down to an unpainted neighbour's value turned the painted zone's final
  metre into a dirt-to-grass gradient — a pale band along the 64 m grid — and made the border column
  permanently unpaintable, because `Heightmap.Generate` rebuilds the mask from the base mask plus
  `TerrainComp` on every regeneration and the postfix re-applied the minimum every single time. The
  player painted, the paint vanished, and no amount of repainting helped.

  The texel is now resolved against the ground behind it on both sides:
  `Clamp(Max(supportA, supportB), Min(a, b), Max(a, b))`, where the support samples are one texel
  inward on each side. Paint that legitimately reaches the border has its own zone's interior behind
  it, so the maximum wins and the paint carries about a metre into the neighbour — the same falloff
  any paint edge has anywhere else in a zone. A lone stripe on the grid line with nothing behind it on
  either side still resolves to the minimum and is still removed. When the two sides already agree,
  `lo == hi` and the result is that value regardless of the support term, so repeated regeneration
  cannot drift, and both zones compute the same value from the same four inputs, so it does not matter
  which side regenerates first. The result is always one of the two values already stored for that
  texel — no colour is invented, only chosen. Unlike 0.3.0 this can raise a value as well as lower
  one; that is the correct outcome given the layout, since the ground at the border genuinely is
  painted and there is no way to represent paint stopping exactly on the line without a
  discontinuity. Alpha is still untouched, and nothing saved to the world is changed.

  Four-zone corner texels are now merged in a single pass over all four zones rather than pairwise
  twice, which under a clamp could settle on two different values and alternate between them.

- `TerrainOp.Awake` zone fan-out *(both)* — the root cause behind the seam, and previously recorded in
  0.3.0 as having none. `Awake` decides which zones an edit is recorded into from the tool's radius:
  `Heightmap.FindHeightmap(pos, GetRadius(), heightmaps)`. But `TerrainComp.PaintCleared` subtracts
  half a texel from x and z and *then* floors, via `WorldToVertexMask`, so the paint kernel's centre
  snaps to the vertex grid up to a full texel toward −x/−z and the kernel reaches about a metre
  further than `GetRadius()` on those two sides. Adjacent zones each hold their own copy of the shared
  boundary texel, so an edit placed in that band paints its own zone's column 0 / row 0 — the shared
  texel — while the west or south neighbour, excluded from the fan-out, keeps the unpainted copy. The
  asymmetry is directional, which is exactly why the artifact only ever appeared along one side of a
  chunk.

  The extra zones the paint kernel genuinely reaches are now sent a **paint-only** copy of the same
  operation, so both sides record it and the agreement is saved rather than repaired at render time.
  Paint-only because `LevelTerrain`, `RaiseTerrain` and `SmoothTerrain` call `WorldToVertex` without
  the half-texel shift, which rounds to nearest and stays symmetric — handing them to a zone vanilla
  left out would move terrain nothing asked to move, and `TerrainComp` records height as a delta
  against that zone's *current* height, which is the one place a genuine geometric step could be baked
  in. Only zones on the −x/−z side are considered, since that is the only direction the floor can run
  past the radius. This changes what is saved to the world and only affects edits made from now on;
  the reconcile above is what repairs terrain already recorded the broken way.

- `Heightmap.RebuildRenderMesh` neighbour refresh *(client)* — 0.3.0's fix recomputed a zone's
  neighbours only when the zone itself could be fixed, and zones are always built at the frontier of
  the loaded area, so a new zone almost never has all four of its own neighbours yet. What it *does*
  do is complete the neighbour set of the zone behind it, which had bailed to vanilla normals for
  exactly that reason and would otherwise stay that way forever — nothing else re-pokes it. The
  result was a permanent band of vanilla-normal terrain trailing the player, measured as 81% of
  shared vertices matching near the player against 43% further out. Neighbours are now refreshed
  unconditionally.

- `TerrainComp.Update` recovery *(both)*, follow-up to this release's own fix — recovering a terrain
  compiler that lost the startup race put it into `s_instances` without the deduplication
  `TerrainComp.Awake` does first. Vanilla's only guard against two compilers owning one zone is
  `GetAndCreateTerrainCompiler`, which searches that same list, so the compiler that lost the race is
  invisible to it and a second one gets created — and `Awake` destroys the incumbent when that
  happens. Recovering the first one without repeating that step left both live for one zone.

  `Heightmap.ApplyModifiers` resolves through `FindTerrainCompiler`, which returns the first list
  match, so one compiler's saved terrain and paint would be silently discarded and the other's
  applied — with list order following object creation order, not stably. That is the terrain
  flip-flopping the other fixes in this section exist to remove, reintroduced by the recovery.
  Recovery now performs the same deduplication `Awake` does, in the same order.

  A failed recovery is also no longer retried. It ran again on the next frame and every frame after,
  writing a full stack trace each time.

### Compatibility

- **ComfyMods BetterZeeLog** — installing the two together broke five methods. Both mods transpile
  `Container.RPC_RequestOpen`, `RPC_RequestStack` and `RPC_RequestTakeAll`,
  `ZSteamSocket.SendQueuedPackages` and `Projectile.FixedUpdate`. That mod locates its edit by
  matching the exact `ZLog.Log` and `Quaternion.LookRotation` call operands, and this one rewrites
  those operands in place; whenever ours went first its matcher missed and threw
  `Could not patch Container.RPC_RequestOpen()!`, at which point HarmonyX discarded the method
  entirely — losing *both* mods' fixes on it, and any third mod's patches on it too.

  Every transpiler in this mod now declares `Priority.Last`. Harmony applies transpilers in priority
  order rather than mod load order, so other mods now see vanilla IL and ours run afterwards on
  whatever is left. Ours are the right side to yield: each one counts what it rewrote and returns the
  method untouched when the count is wrong, so it degrades to a no-op instead of throwing. Where both
  mods are installed BetterZeeLog's versions of these three fixes are the ones that take effect.

  The fail-safe warnings these transpilers emit have been reworded to say the method may already have
  been rewritten by another mod, since "leaving it unpatched" read as a failure when nothing was
  wrong.

## 0.3.0

Terrain seams. Each entry names the vanilla method it fixes.

Note on what is *not* here: the base heightfield is seam-consistent by construction.
`HeightmapBuilder.Build` blends the four corner-biome heights with `SmoothStep`-warped coordinates,
and since `SmoothStep(0,1,0)==0`, `SmoothStep(0,1,1)==1`, and adjacent zones sample
`WorldGenerator.GetBiome` at the same world positions for their shared corners, both zones compute an
identical height at every shared vertex. World generation was ruled out before any of this was
written.

Also ruled out by measurement: comparing the shared boundary vertices of every adjacent loaded zone
on a live world put the height delta at **exactly zero** across 2600 shared vertices, which rules out
the cross-zone divergence described under "known, documented, not yet fixed" below for that world,
and confirms the seam is a shading artifact.

### Terrain

- `Heightmap.RebuildRenderMesh` *(client)* — vanilla calls `RecalculateNormals()` on a mesh containing
  only one zone's 33×33 vertices, so a vertex on a zone boundary is shaded from the triangles on one
  side only while the neighbouring zone shades the same world position from the other. The result is a
  hard lighting crease along every 64 m zone border, most visible on flat bright terrain — Plains and
  Meadows. Normals are now computed analytically from the height field by central difference, taking
  samples across the boundary from the neighbouring heightmap. Loaded neighbours are refreshed too,
  since their edge normals depend on this zone's heights. When a neighbour is not loaded, vanilla's
  normals are left alone rather than half-applying the fix.

  The neighbour refresh runs whether or not the newly built zone could be fixed itself, and that
  ordering matters: zones are always built at the frontier of the loaded area, so a new zone rarely
  has all four of its own neighbours yet — what it does do is complete the neighbour set of the zone
  behind it. Skipping the refresh in that case left a permanent band of vanilla-normal terrain
  trailing the player.
- `Heightmap.ApplyModifiers` *(client)* — terrain paint could disagree between the two sides of a zone
  boundary, leaving painted ground (usually dirt, from levelling and raising terrain) stopping dead in
  a hard straight line along the 64 m grid. Measured on a live world: 556 of 2600 shared vertices
  disagreed on the dirt channel with a maximum delta of 1.0 — fully painted on one side, unpainted on
  the other — while cultivated, paved and vegetation matched exactly.

  World generation never writes dirt at all (it only fills the mask's alpha), so every bit of it comes
  from a terrain operation. Unlike terrain height, which `TerrainComp` stores as a delta, paint is
  stored as an *absolute colour snapshot* seeded from whatever the heightmap texture held when the
  operation was recorded. Every distance and rounding calculation involved is symmetric between the
  two zones, so the divergence is state rather than arithmetic — if the zones' textures differed at
  that instant, both compilers permanently record different colours for the same ground and replay
  them on every regeneration.

  Shared boundary texels are now reconciled by taking the per-channel minimum. Minimum specifically:
  it can only remove paint, never invent it; paint applied legitimately across a boundary reaches both
  zones symmetrically and so is already equal and unaffected; and both zones compute the same value
  from the same inputs, so the result is continuous no matter which side is processed first. Alpha is
  left alone — it is world-generated, drives lava rendering in the Ashlands, and was measured as
  already consistent.
- `Heightmap.SetPaintMask` / `Heightmap.UpdateTerrainAlpha` / `TerrainComp.UpdatePaintMask`
  *(client)* — all three walk the paint mask with the wrong stride or bounds. The paint arrays are
  `(m_width + 1)²`, but two of them index with a stride of `m_width`, which slips a column further
  left on every row — a diagonal skew, not a uniform offset — and both stop one short of the last row
  and column. `SetPaintMask` separately rejects index `m_width`, which is exactly the row and column a
  zone shares with its neighbour, so paint could never be written to the seam at all. All three sit on
  the `optterrain` console path, so this makes that repair command correct rather than changing
  anything during normal play.
- `TerrainComp.Update` *(both)* — `Awake` gives up without initialising when it cannot find its zone's
  heightmap, but `Update` still calls `CheckLoad` every frame, which dereferences the null
  `m_modifiedHeight` and then the null `m_hmap`. Besides the exception spam, that zone stops accepting
  terrain edits permanently, because the compiler was never added to `s_instances` and nothing can
  find it. The frame is now skipped, and initialisation is retried once the heightmap appears.

### Known, documented, not yet fixed

`TerrainComp` stores terrain edits as deltas *relative to the current height* and routes them to
whichever peer owns each zone (`TerrainComp.ApplyOperation` → RPC → `m_levelDelta[i] += targetY -
GetHeight(x,y)`). When an edit straddles a zone boundary and the two zones are owned by different
players, each half resolves the same shared vertex against a differently-stale snapshot, which would
produce a real geometric step on the 64 m line. The boundary height delta measured above says it did
not happen on that world; whether it happens in general is still open. No fix is shipped for it yet,
deliberately — the candidate fix changes ownership behaviour and should not be written on a hunch.

## 0.2.0

Server performance and data integrity. Each entry names the vanilla method it fixes.

### Performance

- `Game.ConnectPortals` *(server)* — replaced the quadratic portal-pairing scan the server runs every
  five seconds. Vanilla calls `FindRandomUnconnectedPortal` per unconnected portal, which allocates a
  list and rescans every portal doing a ZDO string lookup and comparison each; an unpaired portal
  never resolves, so it repeats that scan forever. Now three O(n) passes over a tag-keyed index with
  no steady-state allocation. Ownership is taken directly instead of round-tripping through
  `RPC_SetConnection`, and force-sends are batched per peer. Pairing is now deterministic rather than
  random, and all same-tag portals pair in one tick instead of one pair per tick.
- `ZDOMan.ConnectPortals` / `ZDOMan.ConnectSpawners` *(server)* — both matched a source list against a
  target list with nested loops at world load. Now hash-indexed. Pairing decisions are unchanged,
  including ConnectPortals consuming each target once and ConnectSpawners letting several spawners
  share one.
- `Player.AutoPickup` *(client)* — used the allocating `Physics.OverlapSphere` overload every frame
  per player. Now `OverlapSphereNonAlloc` against a reused buffer, with the loop bound rewritten to
  the hit count so it does not read past the live results.

### Correctness

- `ZNetScene.RemoveObjects` *(both)* — dereferenced every instance's ZDO with no validity check. One
  orphaned entry threw and aborted the entire unload pass, then threw again every frame, so nothing
  despawned and memory climbed. Orphans are now dropped from the instance table instead.
- `ZDOMan.Load` *(server)* — used `Dictionary.Add` for the ZDO index, so a save containing a duplicate
  ZDOID threw and made the world unloadable. Now keeps the later entry and logs a warning.
- `EffectArea.IsPointInsideArea` / `EffectArea.GetBaseValue` *(both)* — `m_tempColliders` is a fixed
  128-element buffer that `OverlapSphereNonAlloc` silently truncates, so in dense builds warmth,
  wetness and burning checks could miss the area that mattered. The buffer now grows.
- `EffectArea.CustomFixedUpdate` *(both)* — iterated `m_collidedWithCharacter` with no validity check.
  Unity never fires `OnTriggerExit` for a collider destroyed inside a trigger, so a character that
  died in the area stayed in the list and threw every physics step. Dead entries are stripped, and
  `Character.OnDestroy` now removes the character from every area it was standing in. This one is
  reachable on a dedicated server too — `CustomFixedUpdate` has no owner check.
- `Smelter.OnAddFuel` / `Smelter.OnAddOre` / `Fireplace.Interact` / `Fireplace.UseItem` *(client)* —
  these remove the item from your inventory and *then* send an RPC the owning peer may never process,
  destroying the fuel or ore. Ownership is now taken first so the call resolves locally.
- `Character.OnDeath` *(client)* — the per-player boss defeat key was only recorded on whichever
  client owned the boss, because `CheckDeath` runs owner-only. The owner now broadcasts it and every
  player within range (default 300 m, configurable) is credited. Most visible with Hildir's quest
  bosses. The server does not need the mod for this: an unrecognised routed-RPC hash makes
  `ZRoutedRpc.HandleRoutedRPC` return without invoking anything, and the message is still relayed on.

## 0.1.0

First release. Each entry names the vanilla method it fixes.

### Performance

- `ObjectDB.GetRecipe` *(client)* — replaced the unindexed linear scan (a string comparison per
  recipe, run for every worn item on every frame the inventory is open at a crafting station) with a
  name-indexed lookup. Fixes the workbench repair hitch and the sustained FPS drop that grows with the
  number of installed recipes. First-recipe-wins semantics are preserved. **Withdrawn in 0.4.0** — see
  above.
- `LiquidVolume.Awake` / `LiquidVolume.OnDestroy` *(client)* — tar pit raycast buffers were allocated
  with `Allocator.TempJob` (a 4-frame allocator) but kept for the object's whole lifetime, and
  disposed without an `IsCreated` guard. Now `Allocator.Persistent` with guarded disposal. Stops the
  `JobTempAlloc has allocations that are more than 4 frames old` log spam and the associated native
  memory growth.

### Correctness

- `Recipe.GetAmount` *(client)* — guarded the unchecked dereference of `GetFirstRequiredItem`, which
  returns null when the player holds none of the accepted ingredients. Was a `NullReferenceException`
  that broke the crafting/upgrade panel for "requires any one of these" recipes.
- `SpawnArea.Awake` *(both)* — null entries in a spawner's prefab table are now removed instead of
  throwing during spawn selection and killing the spawner permanently.
- `EffectArea.IsPointPlus025InsideBurningArea` / `EffectArea.GetBurningAreaPointPlus025` *(client)* —
  these tested only the cached bounds, never whether the area was active. `EffectArea` registers
  itself on `Awake` and is only unregistered on `OnDestroy`, so putting a fire out left its entry in
  the list forever and unlit fires kept cooking.
- `Character.CheckRun` *(client)* — run stamina no longer drains while mid-attack. Patched on the base
  virtual because `Player.CheckRun` spends the stamina before returning. Narrowed to players so
  creature movement is unaffected.
- `Projectile.FixedUpdate` *(both)* — `Quaternion.LookRotation` is no longer called with a zero
  vector, which made Unity log `Look rotation viewing vector is zero` every physics step per
  projectile.

### Notes

- Fixes are applied individually rather than through `Harmony.PatchAll`, so a Valheim update that
  breaks one patch target leaves the other fixes working and logs a single clear error.
- Toggles for transpiler-based fixes (tar pit leak, recipe crash, projectile spam) are read at patch
  time and need a game restart to take effect. The rest apply live.
