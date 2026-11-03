# Performance in the 3D view: large colony, 4K, multiplayer

Research of Nov 3 2026, game 1.7.4995 rev309 (the port was built for 4990; branch port-1.7 at 20ccd737). Sections
1-5 are read-only research, and their gains are estimates. Section 6 holds the measurements of the same day in the
Proton test copy of the user's colony:
- the CPU side at 1280x720 (6.1-6.8);
- the GPU side at 3840x2160 full screen (6.9);
- the updated ranking (6.10), which replaces the order of section 2.

Sources:
- Five research passes covering IsoRim, the Waylith engine, the simulation, other performance mods, and
  reduced-resolution and foveated rendering. The claims used here were re-checked against the code they cite.
- The Proton Player.log of the Nov 3 session.
- The user's Prefs.xml, ModsConfig.xml and IsoRim settings.
- The user's answers of Nov 3 (section 5).

**The player's setup**
- RTX 3080 10 GB (driver 615.78.08) and a Ryzen 7 5800X (8 cores, 16 threads).
- 3840x2160 in exclusive fullscreen, UI scale 2, running under Proton.
- One map is 250x250. The Nov 3 hosted session loaded 2 maps with 86,354 spawned things ("0 of 86354 spawned
  things missing").
- Mods: Harmony, Multiplayer, Prepare Carefully, Replace Stuff, and the DLCs Royalty, Ideology, Biotech and
  Odyssey.

## Summary

- **In a session, frame time sets game speed.** Each client runs at most one Timer tick per frame. The server
  paces every client by the slowest player's whole frame time. So below 60 fps the game slows down one for one:
  45 fps means 75% speed.
  - The user hosts even when playing alone. On Nov 3 the log shows "Starting the server", and the only
    connection was LocalServerConn. So this applies to solo play too.
  - Vanilla single player is different: it runs up to two ticks per frame to make up for a slow frame.
  - Measured: frame time = drawing + one Timer tick, as the model of 1.1 says (6.2).
- **Measured: the CPU main thread is the bottleneck, at 4K full screen as at 1280x720.**
  - Drawing the 3D view at the user's far zoom takes about 12.7 ms of main thread per frame: about 5,000 draw
    items, against 6.4 ms for the flat view. Each Timer tick then adds about 3 ms at Normal, 6 ms at Fast and
    10 ms at Superfast.
  - The GPU (RTX 3080) is busy about a third of each frame at 4K, at clocks the driver lowers to 0.9-1.5 GHz. The
    render thread never waits for it. Frame times at 4K equal those at 1280x720 within 1-4% (6.9).
  - Real speed in 3D at 4K: Normal 94%; Fast 83% at night and 64% by day. By day, hauling makes 3D map sections
    rebuild at 7-10 ms each (6.4). At 720p, Superfast reached 69% at night and about 50% by day.
- **This fits what the user reported.**
  - It feels slowest at Fast. Measured: Fast in 3D runs at 64-83% of full speed.
  - It didn't get slower around Oct 27: the pawn tick-rate change of that day is about 4% of tick time (6.5).
  - It is slower with the friend in the game, because the slower player's frame paces both (1.1). The friend's PC
    is unmeasured.
- **The user's ideas:**
  - **Lower resolution: no gain, measured.** 2560x1440 full screen cut the GPU work by 35-40% and left frame time
    and speed unchanged (6.9). Rendering the 3D world at a lower resolution (render scale, section 2 item 3) would
    only save GPU power. Not worth building on this PC.
  - **Foveated rendering: not workable as such.** Waylith never turns on the GPU feature that foveated rendering
    needs (Vulkan's variable-rate shading), and it creates the GPU device before any mod loads. The cheap variant,
    lighter lamp and shadow shading away from the screen centre, would also save only GPU power. Lamp facing is
    about a third of the GPU work (6.9), but the GPU doesn't limit speed.
- **Order of work (6.10):**
  1. Session pacing: up to 2 Timer ticks per frame and a smoothed frame-time report. Est. full speed at Fast in 3D,
     by night and by day. It needs the two-instance soak test of section 2.
  2. Cheaper 3D section rebuilds: the day/night swing and the 7-10 ms spikes.
  3. Fewer draw items in 3D.

  Meanwhile, without code:
  - Zooming in is the biggest drawing saving measured: 4.8-5.4 ms instead of 12.7 ms at root size 15.
  - Turning 3D weather off while it rains saves 1.8 ms per frame.

  Dropped for this PC, at 0 speed measured: render scale, merged FXAA and grade, and foveated shading.

## 1. What is probably slow, and why

### 1.1 How frame time becomes game speed (verified in code)

**The client** (`Source/Client/Patches/TickPatch.cs`):
- Line 98: `ticksToRun = Multiplayer.IsReplay ? Mathf.CeilToInt(realTime / stpt) : 1`. A live session runs at
  most one Timer tick per frame.
- `DoUpdate` (from line 207) calls `RunCmds` before each Timer tick. `DoTick` then runs each map, and the world,
  for as many map ticks as that map's speed multiplier gives: 1, 3, 6 (12 when nothing is happening) or 15
  (`AsyncTimeComp.TickRateMultiplier`, `AsyncTimeComp.cs:21-50`).
- Line 96: `avgFrameTime = (avgFrameTime + Time.deltaTime*1000)/2`. This is the whole frame (ticks, map
  drawing, interface) averaged over two samples, and it is updated only on frames that ran a tick. It is sent to
  the server every 32 ms (lines 105-109).

**The server** (`Source/Common/MultiplayerServer.cs`):
- Line 194: `serverTimePerTick = max(frameTime)` over the players in game, clamped to 16.7-66.7 ms.
- The net loop runs about 30 times a second and advances the Timer at most twice per pass (line 120), so at most
  60 Timer ticks a second.
- It slows down by up to 3x when a player falls behind (line 142), and pauses when one is more than 90 ticks
  behind (line 123).

**The model.** Let R be the time to draw the map and the interface for one frame, and T the cost of one map tick.
Then per Timer tick:

`F = R + Σmaps(multiplier × T)`, and the Timer rate = `min(60, 1000 / F of the slowest player)`.

Illustrative numbers (not measured), with R = 12 ms and T = 1 ms on the home map:

| Speed (map ticks per Timer tick) | F | Timer ticks/s | Real speed |
|---|---|---|---|
| Normal (1) | 13 ms | 60 | 100% |
| Fast (3) | 15 ms | 60 | 100% |
| Superfast (6) | 18 ms | 56 | 93% |
| Ultrafast (15) | 27 ms | 37 | 62% |
| Normal with R = 24 ms (a slow 4K frame) | 25 ms | 40 | 67% |

The rule of thumb this gives:
- Slow already at Normal speed: the cause is the frame, meaning drawing or a slow friend.
- Slow only at Superfast or Ultrafast: the cause is the ticks.

Also:
- The slowest player paces everyone, so if the friend's PC is the slow one, nothing on this PC helps.
- The average covers only two frames, so one hitch (garbage collection, a map section rebuild) on either PC
  briefly slows both players.

### 1.2 Tick cost (CPU, main thread)

**The synced variable tick rate (VTR) changed on Oct 27 (commit 0977f96b).**
- **Before:** player counts were lost on every load, so every thing ticked at rate 15 all session. That was
  cheaper than vanilla, but bullets jumped.
- **Since:** on a map with a player on it, `AsyncTimeComp.VTR` is 1 (`AsyncTimeComp.cs:104`). Non-animal pawns,
  and things that don't override the rate getter, run `TickInterval` every tick (`VTRSyncPatch.cs:34-61`).
  Animals stay at 15, buildings at 6 at least (`Building.MinTickIntervalRate`; the clamp is in
  `Thing.DoTick`), and spawned projectiles at 1.
- **Vanilla, for comparison:** `GenTicks.GetCameraUpdateRate` gives `(int)(CurrentZoom+1)` in view, which is 3 at
  middle zoom, and 15 off screen.
- **Result:** the session now makes 3x (on-screen pawns) to 15x (off-screen pawns) as many pawn
  `TickInterval` calls as vanilla. If the colony felt fine before Oct 27, this is the main suspect.

**What a call costs.**
- Needs (150 ticks), the think tree (30) and healing (600) are gated by hash intervals, so they run equally often
  at any rate.
- What does grow with the rate: every tracker's call overhead, `Pawn_HealthTracker.HealthTickInterval` (every
  hediff and its comps), the job expiry check, the drivers' `tickIntervalAction` and the mental state checks.
  That is many small calls, roughly pawns × hediffs.
- Outside figures for the same lever:
  - Ludeon credits 1.6's VTR (with other changes) with 20-30% more TPS late game.
  - The author of the "Slower Pawn Tick Rate" mod reports about 370 to 550 TPS.

  Neither has been checked on this port.

**Other per-tick costs Multiplayer adds** (`AsyncTimeComp.cs`):
- `ForceCompleteScheduledJobs` (line 133) waits for the pathfinder's jobs instead of overlapping them with the
  tick. This is on purpose, for determinism (upstream #900).
- `ProcessPostTickVisuals` (line 148) runs every map tick for every pawn. `DrawTrackerTickPatch`
  (`Determinism.cs:131`) removes its view check. Vanilla runs it once per frame, for pawns in view only.
- `UpdateManagers` (line 174) runs every tick.

**Diagnostics.**
- Slow (.NET) desync tracing costs about 4 ms per map tick. It is off on this PC (no `desyncTracingMode` in
  Mod_Multiplayer-1.7_Multiplayer.xml); the friend's setting is unknown.
- With dev mode on, `PatchHitCounter.Hit` does a string-keyed lookup for every normal-ticker thing every tick.
  Dev mode is off here (Prefs.xml).

### 1.3 Drawing cost on the CPU (main thread and Waylith's render thread)

Waylith's cost is per draw command, not per triangle (`IsoRim/Source/Instancing.cs:11-14`).
`MapRenderFeature.CaptureFrame` turns every immediate draw into a draw item and re-sorts the persistent draws
every frame. That runs serially on the main thread, so it adds to F.

IsoRim's share:
- **The light pass doubles every lit draw.** `IsoGlow.DrawLit` submits the mesh and its "mark", every visible
  section layer gets `DrawLayerMarks`, and batched parts get a second instanced draw (`Instancing.cs:118-128`).
  Outlines are a third mesh on moving models.
- **People can't be batched.** Each person's merged body and head mesh is unique, so a batch group of one falls
  back to `DrawLit` (`Instancing.cs:120`). That is at least 6 commands per colonist, plus weapon, carried item,
  wounds and decals.
- **Per-frame effects are neither batched nor culled.** `View3D` (`View3D.cs:409-414`) calls `Effects`,
  `Spinners`, `Bobbers`, `Pulses`, `ActivityGlow` and `Glass` outside the collected phase.
  - `PartBatches.Add` returns false there (`Instancing.cs:79-81`), so nothing gets batched.
  - Culling only runs while things are drawn.
  - Water sparkles (`waterStyle`) scan the view's cells, and each sparkle is a lit draw.
- **Map sections are rebuilt whenever an item moves.**
  - IsoRim's walls and bodies layers listen to the "Things" change flag (`Layers.cs:467, 706`).
  - `Thing.DirtyMapMesh` sets that flag for every item or plant that spawns, despawns, moves or changes stack
    (4995 `Thing.cs`, the Position setter and SpawnSetup). Pawns don't set it.
  - So every haul re-bakes that section's walls and bodies. The walls rebuild also marks the section's cells dirty
    in the cell map (`Layers.cs:522`). That triggers a full 250x250 cell texture upload plus a scan of all 62,500
    cells for the tallest top (`Cells.cs:406-411`).
- **3D weather is on** (`weather3D=True`; the default is off). While it rains or snows, up to 4000 quads are
  rewritten and re-uploaded every frame (`Weather.cs:386-440`).
- **3D pawns skip the game's parallel pre-draw** (`Patches.cs:179-185`). IsoRim's replacement work for them runs
  single-threaded in the draw phase.
- **The view rectangle is wider.** IsoRim makes it the camera's whole ground footprint (`Patches.cs:1677-1683`),
  and the game's culling uses that rectangle for every drawable thing.

### 1.4 Drawing cost on the GPU (8.3 million pixels at 4K)

- **Lit faces are shaded twice.** IsoRim draws a base pass and then the light pass on top. There is no depth
  prepass, and Waylith never sorts opaque draws by depth, so hidden fragments are shaded too.
- **Sun shadows on faces** (`faceShadows`): up to 24 cell steps per sunlit 3D pixel by day.
- **Lamp facing** (`lampFacing`): up to 32 lamps per lamp-lit pixel at night or indoors.
- **Two full-screen passes:** FXAA (`antiAlias`) and the colour grade (`grade`). Each one copies the whole scene
  image first and splits the render pass.
- There is no MSAA to turn off (`SceneColorTargets` uses 1 sample).
- Perspective and first person only: 24 fog layers and the painted sky. The user plays orthographic.

### 1.5 Known and unknown

**Known**
- The hardware, the settings and the map size (above).
- Average fps from the log's frame counters, which include pauses and open menus:

  | Window | Average fps |
  |---|---|
  | 09:45-09:48 | 97.8 |
  | 09:55-10:02 | 85.3 |
  | 10:03-10:04 | 51.7 (menus open) |

**Unknown until measured**
- How the frame splits between ticks, drawing on the main thread, the render thread and the GPU.
- Tick time per map tick.
- Whether the slowness shows at Normal speed or only at fast speeds.
- The friend's frame time.
- The monitor's refresh rate (it sets the frame cap).

## 2. Ranked options

Ranked by expected value for this user while the bottleneck is unknown. Every option is either drawing only and
per player, or derived from synced state only, so none can desync on its own. Both players always run the same
build anyway: the join check compares assemblies.

| # | Option | Helps when | Expected gain | Effort | Risk | MP safety | User's choice? |
|---|---|---|---|---|---|---|---|
| 1 | Session pacing: catch-up ticks plus a smoother frame-time report | Frame (drawing) is a large share, or frames spike | +20-70% session speed below 60 fps (e.g. F = 20 + 15 ms: 28.6 to 40 Timer ticks/s); 0 at 60 fps or more | Small code, medium test | Medium (core timing) | Deterministic | No (could be a local toggle) |
| 2 | Synced pawn interval rate 3 (1 when drafted or fighting) | Tick-bound (fast speeds) | Pawn interval calls cut about 3x; tick time est. -5 to -20% | Small | Low-medium | Safe if it reads only synced state | Yes |
| 3 | Render scale for the 3D world, interface at full resolution, plus an FSR1-style upscaler | GPU-bound only | At 67% scale, 44% of the pixels: est. -40 to -48% GPU time; 0 if CPU-bound | 1-2 days, +0.5-1 day upscaler | Medium (engine internals) | Per player, drawing only | Yes (sharpness) |
| 4 | IsoRim CPU fixes with no visual change | Busy base, rain | Fewer section re-bakes and texture uploads; hundreds fewer draws | Small each | Low | Drawing only | No |
| 5 | Merge FXAA and grade into one pass | GPU-bound | est. 0.2-0.5 ms at 4K | Small | Low | Drawing only | No |
| 6 | "Foveated shading": lighter shadow and lamp work toward the screen edges | GPU-bound, by day or at night | Shading cost on about 60% of pixels, for those two features only | Medium | Low-medium (softer at the edges) | Drawing only | Yes |
| 7 | Level of detail by zoom (outlines, eyes, wounds, decals far away) | CPU and GPU, zoomed out | Many draws and about a third of model triangles | Medium | Low | Drawing only | No |
| 8 | Single-pass lighting option (no mark pass) | CPU and GPU | About half the lit draws and fragments | Medium-large | Medium | Drawing only | Yes (smoke over 3D faces no longer darkened) |
| 9 | 3D pawn and animal preparation on worker threads | Many pawns | Main thread relief in big colonies | Large | Thread safety; never `Rand` | Drawing only | No |

**1. Session pacing** (`TickPatch.cs:96-98`)
- **What:** use the replay rule `CeilToInt(realTime/stpt)` in live sessions too, capped at 2 Timer ticks per
  frame and limited by a time budget, as vanilla does. Report the time per Timer tick (frame time ÷ ticks run, but
  never less than the measured tick time) instead of the raw frame time. Smooth the report over about 1 s.
- **Why it's safe:** commands still run before every Timer tick (`DoUpdate`, line 209), so determinism is
  untouched.
- **Risks:**
  - Feedback with the server's slow-down for players behind and its pause at 90 ticks.
  - A choppier picture while catching up, as in vanilla.

  Both need a soak test with two instances.
- **What it fixes:** the CLAUDE.md gap "the slowest player's frame time paces the whole session" at its root, and
  it helps the solo hosted game as much.

**2. Synced pawn rate**
- **What:** a host setting, "simulation detail", stored in a synced game component. Default rate 3, which is what
  vanilla uses at middle zoom. Rate 1 for drafted pawns, pawns with `mindState.enemyTarget`, pawns in a mental
  state, and the first-person pawn. Projectiles stay at 1, animals at 15.
- **What gets coarser:** work bars and hediff severity step every 3 ticks, and a toil or job can end up to 2
  ticks late, as in vanilla.
- **What doesn't change:** walking, aiming, verbs and first-person control run every tick. Needs and mood are
  gated at 150 ticks.
- **Checks:** the bullets fixed in 0977f96b, and upstream's "sync up the animations" TODO in `VTRSyncPatch`.
  Never take the rate from the camera, the zoom or the 3D view.

**3. Render scale** (the user's "lower resolution" idea)
- **How it fits the engine:**
  - `MapRenderFeature.MapMainPass` draws the map into `SceneColorTargets`.
  - `PostFx.CompositePass` samples that image bilinearly into the screen (`PostFxComposite.frag.glsl`).
  - ImGui then draws the interface at full resolution.
- **Steps:**
  - Harmony prefixes scale the w and h arguments of `SceneColorTargets.EnsureSized(int framesInFlight, int w,
    int h)`, `SceneGrabTargets.SetExtent` and `WaterDepthTargets.EnsureSized`. The grab targets must match the
    scene size exactly.
  - A postfix on `UnityBuiltinGlobals.Publish` re-sets `_ScreenParams`, which `TerrainCurtain` and
    `GravshipShadow` need.
  - To apply a change, set `EngineApplication._displayModeChangeRebuildRequested`. The engine then waits for the
    render thread and rebuilds (EngineApplication.cs:607-632). Expect one hitch per change.
  - After each rebuild, read the target sizes back. On a mismatch, go back to scale 1 and log one error.
- **Where it shows:** a softer world, coarser 4x4 dither patterns (fades, see-through, fog lid) and relatively
  thicker outlines. Picking and the interface are unaffected.
- **Better upscaling:** AMD FSR1 (EASU, MIT licence) in a replacement composite shader shipped in IsoRim's
  Shaders folder, keeping bindings 0 and 1. IsoRim's existing sharpen option (off by default) could then be
  applied after the upscale instead of in the FXAA pass.
- **Later, only if a fixed scale proves useful:** dynamic resolution, in steps with hysteresis.

**4. IsoRim CPU fixes**
- **(a)** Remove "Things" from the walls and bodies layers' flags. Dirty them from a filtered
  `Thing.DirtyMapMesh` postfix through the existing `Mark`/`OwnFlag` path, only for the things those layers draw.
- **(b)** Wrap `View3D`'s per-frame effect calls in `PartBatches.Begin`/`Flush`, send sparkles through
  `PartBatches.Add`, and cull them with `Cull.MeshVisible`.
- **(c)** Animate 3D rain and snow in the vertex shader instead of rewriting the mesh every frame.

**6. Foveated rendering, judged**
- **Real foveation (variable-rate shading, VRS):** the RTX 3080 supports `VK_KHR_fragment_shading_rate`, but
  Waylith doesn't enable it.
  - The device is created in `EngineApplication`'s constructor, before mods load.
  - The only ways in are a `DOTNET_STARTUP_HOOKS` assembly (unverified on WaylithClr) or a Vulkan layer under
    Wine.
  - After that, every scene render pass and map pipeline would have to be rebuilt.
  - The gain would be 10-30% of fragment cost only. Not recommended.
- **Two passes, sharp centre and blurry rim:** everything would be drawn twice, which costs more CPU than it saves
  in GPU time. And on a monitor without eye tracking, RimWorld players look at the edges (alerts, colonist bar).
  Not recommended.
- **What works:** a smooth taper of `ShadowSteps` (24 down to 4) and of the lamp loop (32 down to 8) with distance
  from the screen centre. It only matters if the GPU is the limit.

**Not recommended:**
- Restoring the pathfinder overlap (high desync risk, upstream reverted it).
- RocketMan or Missile Girl: they declare `incompatibleWith rwmt.Multiplayer`, and their learned stat expiry is
  per process.
- RimThreaded: threaded ticking isn't deterministic.
- Butter++: it replaces `TickManagerUpdate`, which Multiplayer replaces too.
- Smart Speed: not a performance mod.
- Performance Optimizer: has no 1.7 build. Its Multiplayer Compatibility recipe is the template for any cache we
  would add: throttle by ticks only, keep interface caches separate, clear on every `FinalizeInit` and save.
- Performance Fish: it needs Prepatcher. **New in 4995:** `RimWorldWaylith.PrepatcherCompat.dll` (Oct 31), plus
  runtimeconfig redirects for `0PrepatcherAPI`, `PrepatcherImpl` and `0PrepatcherDataAssembly`, and
  `WaylithClr.FieldInjection: true`. This changes CLAUDE.md's "Prepatcher can't work on 1.7" for Prepatcher's
  API, but it is untested, high risk, and its multiplayer behaviour is unknown. It would need a solo test first.

## 3. Quick wins to try today

**How to read the result.** In a session, hover the small coloured square left of the Chat button. Its tooltip
shows "Current map avg TPS" (`IngameUI.cs:175`). The targets are:

| Speed | Target TPS |
|---|---|
| Normal | 60 |
| Fast | 180 |
| Superfast | 360 (720 when nothing is happening) |
| Ultrafast | 900 |

For fps, frame time and GPU load, launch with `MANGOHUD=1 DOTNET_TieredCompilation=0 %command%` (Bazzite ships
MangoHud). If GPU load stays near 97-100%, the GPU is the limit.

**Measured since (6.6, 6.9):** on this PC only zooming in and turning 3D weather off while it rains save main-thread
time, and the main thread is what limits speed. The GPU settings below (FXAA, grade, lamp facing, halos, face
shadows, aniso, water) save GPU work and power, not speed.

| Change (IsoRim settings unless noted) | Saves | What it costs visually |
|---|---|---|
| 3D weather off (`weather3D`, on now; the default is off) | CPU every frame while it rains or snows; the fog curtain | The game's flat rain, snow and fog sheets |
| FXAA off (`antiAlias`) | One scene copy and a full-screen pass at 4K | More jagged edges (less visible at 4K) |
| Colour grade off (`grade`) | One scene copy and a pass | The "Gentle" look; colours as the game draws them |
| Lamp facing off, halos off, face shadows off | GPU per pixel (night and indoors; by day) | The Oct 26 lighting: lamps light faces evenly, no glow, no wall shadows |
| Outlines off | Moving models' third mesh; the hulls in section meshes | No dark outlines |
| Anisotropic filtering 16 to 4 or 8 (`aniso`) | Some texture bandwidth | Ground slightly blurrier at grazing angles (little at pitch 65°) |
| Water style off (`waterStyle`) | The per-frame sparkle scan and draws | The game's water |
| 3D effects off (`effects3D`) | The unbatched per-frame effects | Flat effects |
| 3D trees, 3D small plants off | Unmeasured: possibly many plant draws in forests | Flat plants |
| Zoom in a little | Fewer sections and things drawn. Measured: root size 15 instead of the user's 47.4 cut the 3D frame's main thread from 12.7 to 4.8-5.4 ms (6.9) | Less map on screen |
| Multiplayer: slow (.NET) desync tracing off on both PCs; dev mode off | About 4 ms per map tick if on; a lookup per thing per tick | None |

**Lower resolution without code**
- **Through the game's options:** with exclusive fullscreen, 2560x1440 should make the monitor scale the image,
  interface included. The Nov 3 log shows a `Dialog_ResolutionConfirm` at 10:02:58, but the user says the test
  never happened: the dialog only switched to exclusive full screen. Measured instead (6.9): 2560x1440 exclusive
  full screen left frame time and speed unchanged.
- **Through gamescope** (FSR on the whole window, interface included):
  `gamescope -W 3840 -H 2160 -w 2560 -h 1440 -F fsr -f -- env DOTNET_TieredCompilation=0 %command%`. Flag
  names differ between versions; check `gamescope --help`.
- **Reading the result:** either way, if fps doesn't change, the CPU is the limit and option 3 won't help.

Fewer things on the map (dumped items, filth, corpses) lowers both tick and drawing cost, and so does less
hauling churn.

## 4. Measurement plan (windowed Proton test copy, while the user is not playing)

**Preconditions**
- The user's game isn't running: a 4K test window covers the screen and shares the GPU.
- Run RwDiff and DeterminismScan for 4990 to 4995 first (CLAUDE.md, after a game update). `~/rw-builds` has no
  1.7.4995 copy yet; the next `install_local.sh` saves one. The installed build ran on 4995 on Nov 3 with 0
  errors.
- Use the same build as the user.

**Setup**
- The prefix: `~/.rw-mp-test/proton-isorim/prefix`. Launch as in CLAUDE.md (Proton 11.0, `proton run
  ./RimWorld.exe`, without `-Headless`), with `DOTNET_TieredCompilation=0`, `-TargetFrameRate=0` (uncapped) and
  `MP_DIAG_PERF=1`. Set environment variables on the host side.
- Copy the user's ModsConfig.xml into the test Config. Never write to the game drive's save folder.
- Copy (cp, read only) the newest save of the large colony into the test prefix's Saves under a new name, such as
  `PerfTest-Gods.rws`. Gods.rws (Oct 28, 17 MB) is the newest .rws in the Proton Saves folder, and Autosave-1
  (Oct 26) is the alternative.
- Prefs in the test copy:
  - 3840x2160 fullscreen for the GPU runs, 1280x720 windowed for the CPU baseline.
  - Dev mode off for the tick runs. `PatchHitCounter` costs a lookup per thing per tick in dev mode, and IsoRim's
    Probe and the game's Frame profiler both need dev mode, so use them only in a separate pass.

**Instrumentation: a temporary Diag harness**
- Lives in `Source/Client/Diag/PerfTest.cs`, like CamTest of Oct 27: active only with `MP_DIAG_PERF=1`, and not
  committed.
- **Driving the run:**
  - Load the copied save and host programmatically (`HostUtil.HostServer`, no Steam or LAN), as in the Oct 20
    caravan run.
  - Fix the camera (`CameraDriver.SetRootPosAndSize`, and IsoRim's turn and pitch).
  - Step through the scenarios below: 10 s warm-up, then 30 s measured each.
  - Take `ScreenCapture.CaptureScreenshot` per scenario into `<test save data>/renders/perf/`.
  - Then quit.
- **Data:** one TSV row per second, plus a summary per scenario (median, p95, p99, max):
  - Frame: `Time.unscaledDeltaTime` per frame.
  - Pacing: `TickPatch.avgFrameTime`, `serverTimePerTick`, Timer ticks per second, map TPS, and ticks behind.
  - Ticks: `TickPatch.tickTimer` per Timer tick (it already wraps `DoTick`); a Stopwatch per map in
    `AsyncTimeComp.Tick`; counts of spawned pawns (non-animal and animal) and things.
  - Main thread: Stopwatches by Harmony prefix and postfix on `Map.MapUpdate`, IsoRim's `View3D` frame update
    (or its existing `Probe.Lap` slots), `MapRenderFeature.CaptureFrame`, `UIRoot.UIRootOnGUI`, and
    `EngineApplication.ThrottleToTargetFrameRate`. A long throttle means waiting on the render thread or the GPU.
  - Render thread: `FrameRenderer.RecordFrame`, and the fence wait in `RenderContext.TryAcquireNextFrame`. A long
    wait means the GPU is the limit.
  - GPU: on the host, `nvidia-smi --query-gpu=utilization.gpu,clocks.gr --format=csv -lms 200` during each
    scenario. If needed, a Waylith timestamp feature (`FrameRenderer.InsertFeature`, `CmdWriteTimestamp`
    top/bottom of pipe, read two frames later, the pattern of `GpuProbe`). IsoRim's `gradegpu=`/`skygpu=`
    approach of drawing a pass N extra times also works.
  - Draw volume: counters on UnityStubs' `Graphics.DrawMesh` and `DrawMeshInstanced` per frame, and section
    rebuilds per second (Regenerate counts on IsoRim's walls and bodies layers, and `IsoCellMap` uploads).
  - Garbage collection: `GC.CollectionCount(0..2)` and `GC.GetTotalPauseDuration()` per scenario.
  - Hook checks: every hook counts its hits and the summary flags zero. Small targets can be inlined (CLAUDE.md
    Facts).

**Scenarios** (each at the user's usual zoom, over the busiest area)

| # | Scenario | What it answers |
|---|---|---|
| S1 | Paused versus Normal, 3D off and on, at 4K | R (drawing alone) and what 3D adds |
| S2 | Paused, 4K versus 1280x720 | GPU-bound or not. If the frame time barely moves, it's the CPU |
| S3 | Normal, Fast, Superfast, Ultrafast, 3D on | T per map tick, and where Timer ticks/s first drops below 60 |
| S4 | Pawn rate (VTR) 1 versus 3 versus 15 at Superfast | Option 2's real gain. The harness overrides the result of the virtual `Pawn.UpdateRateTicks` getter for non-animal pawns (a postfix with the lowest priority); `VTRSync.MinimumVtr` is a const and can't be changed at run time |
| S5 | IsoRim toggles one at a time, paused and at Superfast: weather3D (rain forced), antiAlias, grade, lampFacing, lampHalos, faceShadows, outlines, trees3D, smallPlants3D, effects3D, waterStyle, aniso 16/4 | Cost of each feature, CPU versus GPU |
| S6 | Day versus night (time skipped), indoors versus outdoors | The sun-shadow march versus the lamp loop |
| S7 | Hauling burst (a stockpile being filled) | Section re-bakes and cell uploads per second |
| S8 (optional) | Two instances (host and joiner) | Only to check pacing logic. One PC shares the CPU and GPU, so its costs are not representative |

**Reading the results**
- **Drawing is the problem** if paused frame time R is above 16.7 ms at 4K, or if Normal speed runs below 60
  Timer ticks/s.
  - If R falls a lot at 1280x720, or the GPU is near 100%, it's the GPU: option 3, then 5 and 6.
  - Otherwise it's CPU drawing: options 4, 7 and 8.
- **Ticks are the problem** if speed only drops at Superfast or Ultrafast: options 2 and 1.
- **Hitches are the problem** if p99 frame time is much above the median: option 1's smoothing, and 4(a) for
  rebuild spikes.

## 5. Open questions for the user

1. When does it feel slow: at Normal speed, or only at Superfast or Ultrafast? Only in 3D, or in the flat view
   too?
2. Alone (hosted) or with your friend? What PC and screen does your friend have, does he use 3D, and does it feel
   slower with him in the game? His frame time paces you both.
3. Did it get slower around Oct 27? That fix made pawns on viewed maps tick every tick.
4. What refresh rate is your monitor, and what fps do you usually see?
5. What happened when you changed the resolution on Nov 3 at about 10:03? Was it faster at the lower resolution?
6. "Lower resolution outside of the game screen": did you mean the 3D world drawn at a lower resolution with a
   sharp interface (option 3), or a lower resolution only toward the screen edges (foveated, section 2 item 6)?
7. Which trade-offs are acceptable?
   - A slightly softer 3D world (render scale).
   - Work bars stepping every 3 ticks, as vanilla does at middle zoom (synced pawn rate).
   - Simpler lighting.
8. Which save is the large colony: Gods.rws (Oct 28), or something newer?
9. May the test copy run at 4K full screen on this PC while you're away?
10. Steam forces Proton for RimWorld since Oct 22. Was that deliberate? It keeps you on the same Windows build
    as your friend, which is safer for determinism (cross-OS float maths is untested,
    `docs/desync-risks-1.7.md`), so a native Linux run would only be a measurement.

**Answered on Nov 3**
- 1: it is slowest at Fast. (Whether only in 3D: not answered.)
- 2: it is slower with the friend in the game, and slower still when both run Fast.
- 3: it didn't get slower around Oct 27.
- 5: the resolution test never happened; the dialog only switched to exclusive full screen.
- 9: yes. The 4K full-screen run is 6.9.

## 6. Measurements (Nov 3, test copy)

- **6.1-6.8, the CPU side of the section 4 plan at 1280x720 windowed:** S1, S3, S4, S5 and S7, plus a paused 1280x720
  versus 2560x1440 comparison. Their GPU statements are hints.
- **6.9, the GPU side at 3840x2160 full screen:** S1, S2, S5 and S6, plus render scale (2560x1440 full screen).
- **6.10:** the interpretation and the updated ranking.
- **6.11:** what is measured, estimated and still open.

### 6.1 Setup

- **Build and game:** port-1.7 at 5ce4a4bc with a temporary harness (`Source/Client/Diag/PerfTest.cs`, gated by
  `MP_DIAG_PERF=1`, removed afterwards). Game 1.7.4995 rev309, Windows build under Proton 11.0, prefix
  `~/.rw-mp-test/proton-isorim/prefix`, `DOTNET_TieredCompilation=0`, `-TargetFrameRate=0`.
- **Window:** 1280x720 windowed, UI scale 1, dev mode off. The engine still presented with FIFO (vsync) at 120 Hz
  ("game target 120, present mode FifoKHR"), so frames never ran faster than 8.33 ms. The monitor the game sees
  runs at 120 Hz.
- **Save:** the Nov 3 session's own replay, `MpReplays/Cold Rocks.zip` (10:04), copied read-only as
  `PerfTest-ColdRocks.zip` and hosted the way the host window hosts a replay.
  - Gods.rws was the planned save, but it is a fresh game: one colonist, 5.9 s of play, game tick 280 on load,
    36,405 mostly natural things. It ran first and is kept below as a small-colony control.
  - Cold Rocks matches the Nov 3 log: 86,933 spawned things on 2 maps (the log said 86,354).
- **Mods and settings:** the user's mod list (Harmony, DLCs, Multiplayer, Prepare Carefully, Replace Stuff) and
  the user's IsoRim settings: orthographic, isoPitch 65, weather3D on, everything else at its default (on).
- **Hosting:** asyncTime and multifaction on, as the user plays. No network listeners, autosave off, pause on
  letter never.
  - The storyteller was switched off (`DebugSettings.enableStoryteller`) so no raid would land mid-run. Quests
    still ran.
  - Both maps were set to the same speed. Async time lets the user leave the second map paused, which would make
    ticks about 30% cheaper (6.3).
- **Camera:** the colony map's saved camera, root size 47.4. That is the user's last view and is zoomed far out.
  - The 3D view rect is 187x138 cells, with about 18,300 things and 39 pawns.
  - The flat view rect is 172x98 cells, with about 11,600 things.
- **Timing:** 10 s warm-up and 30 s measured per scenario (S5: 8 s and 20 s; set 2 pawn-rate windows: 4 s and
  10 s).
- **"Busy"** is frame time minus `EngineApplication.ThrottleToTargetFrameRate`, which is the main thread's wait for
  the render thread and for vsync. Where busy equals frame time, the main thread is the limit.

| Map | Pawns | Colonists | Humanlike | Animals | Other (mechs etc.) | Hostile to player | Things |
|---|---|---|---|---|---|---|---|
| 0, Colony (viewed) | 125 | 11 | 11 | 98 | 16 | 56 | 43,840 |
| 1, Settlement | 57 | 0 | 11 | 46 | 0 | 0 | 43,090 |

Three runs: run 1 (all 48 scenarios, 10:58-11:25), set 2 (tick breakdown and interleaved pawn rates, 11:26-11:31)
and set 3 (section layer breakdown, 11:33-11:35). All three logs had 0 errors.

### 6.2 Speed and frame time (S1, S3), run 1

The real speed is Timer ticks per second ÷ 60. The draw-only and tick-frame columns give the median busy time of
frames that ran no Timer tick and of frames that ran one.

| Scenario | fps | Frame ms (median / p95 / p99) | Draw-only frames, busy ms | Tick frames, busy ms | Timer ticks/s | Map TPS (target) | Real speed | Timer tick ms (mean / p95) | Map tick ms (colony / settlement) |
|---|---|---|---|---|---|---|---|---|---|
| Flat, paused | 120 | 8.3 / 10.3 / 11.7 | 6.5 | - | - | - | - | - | - |
| Flat, Normal | 113 | 8.4 / 12.0 / 14.4 | 6.6 | 10.4 | 57.1 | 57 (60) | 95% | 3.4 / 4.5 | 1.94 / 0.83 |
| 3D, paused | 79 | 12.8 / 14.6 / 16.9 | 12.6 | - | - | - | - | - | - |
| 3D, Normal | 65 | 15.4 / 18.1 / 26.9 | 12.1 | 15.6 | 56.5 | 56 (60) | 94% | 3.0 / 3.9 | 1.69 / 0.79 |
| 3D, Fast | 54 | 18.0 / 23.9 / 31.9 | 12.0 | 18.0 | 51.2 | 154 (180) | 85% | 6.0 / 7.7 | 1.09 / 0.55 |
| 3D, Superfast | 44 | 22.3 / 31.2 / 40.5 | 11.8 | 22.4 | 41.2 | 247 (360) | 69% | 10.1 / 12.8 | 0.93 / 0.47 |
| 3D, Ultrafast | 18 | 52.9 / 85.5 / 103.4 | 11.6 | 55.1 | 17.2 | 257 (900) | 29% | 28.4 / 34.9 | 1.17 / 0.46 |
| Flat, Superfast | 58 | 17.7 / 23.1 / 27.0 | 5.3 | 18.0 | 52.2 | 314 (360) | 87% | 11.5 / 14.8 | 1.17 / 0.47 |
| Flat, Ultrafast | 32 | 31.3 / 37.9 / 41.9 | 5.1 | 31.6 | 30.0 | 449 (900) | 50% | 25.7 / 29.9 | 1.02 / 0.39 |

- **The main thread is the limit in every 3D scenario.** The throttle wait is 0.01 ms per frame and the render
  thread waits only 0.04-0.06 ms per frame for the next image, so neither the render thread nor the GPU holds it
  back.
- **The model of 1.1 holds:** F = R + T per Timer tick.
  - R (a draw-only frame) is about 12 ms in 3D and 5-6.5 ms flat.
  - T is about 3 ms at Normal, 10 ms at Superfast and 28 ms at Ultrafast. That covers 6 or 15 ticks of each map
    plus the world.
  - At Superfast in 3D, 11.8 + 10.1 = 21.9 ms predicts 46 Timer ticks/s; 41 were measured.
- **Normal speed is nearly full speed** (94% in 3D). The slowdown starts at Fast. In 3D, Ultrafast (257 TPS) is
  no faster than Superfast (247 TPS).
- **Ticks cost less when they run back to back.** The colony map costs 1.7-1.9 ms per tick at Normal but 0.9-1.2
  ms at Superfast. CPU caches staying warm is the likely cause (estimated, not measured).
- **Small-colony control (Gods.rws, run before the switch):**
  - 3D paused: 2.5 ms busy.
  - Superfast in 3D: Timer tick 3.7 ms, 57 Timer ticks/s (full speed).
  - Ultrafast in 3D: Timer tick 6.8 ms, still 57/s.

  So the slowdown comes from the colony's size, not from the port or the 3D view as such.

### 6.3 Where the time goes

**Main thread, ms per frame (means), run 1**

The map update covers both maps; its drawing parts belong to the viewed map only.

| Scenario | Frame | Ticks (TickManagerUpdate) | Map.MapUpdate | of which section rebuilds | section draw | thing draw | rest | View3D.Tick | UI (OnGUI + Update) | FrameRenderer.CaptureFrame (map part) | Throttle | Draw items per frame | Render thread: record / GPU wait |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Flat, paused | 8.3 | 0.01 | 2.15 | 0.01 | 1.05 | 0.84 | 0.24 | 0.01 | 0.86 | 3.21 (2.44) | 2.10 | 2,346 | 2.9 / 4.97 |
| 3D, paused | 12.7 | 0.01 | 5.23 | 0.02 | 2.03 | 2.67 | 0.51 | 0.32 | 0.96 | 6.19 (5.34) | 0.01 | 5,221 | 7.2 / 0.04 |
| 3D, Normal | 15.3 | 2.64 | 5.19 | 0.16 | 1.97 | 2.57 | 0.49 | 0.30 | 1.10 | 6.12 (5.28) | 0.01 | 5,184 | 7.6 / 0.04 |
| 3D, Superfast | 22.9 | 9.55 | 5.64 | 0.60 | 1.99 | 2.54 | 0.50 | 0.31 | 1.11 | 6.38 (5.48) | 0.01 | 5,169 | 7.3 / 0.06 |
| 3D, Ultrafast | 54.6 | 26.56 | 17.99 | 11.28 | 3.06 | 3.09 | 0.57 | 0.40 | 1.30 | 7.86 (6.78) | 0.01 | 5,043 | 8.6 / 0.06 |
| Flat, Superfast | 17.4 | 10.41 | 2.31 | 0.18 | 1.04 | 0.83 | 0.25 | 0.01 | 0.97 | 3.39 (2.62) | 0.03 | 2,364 | 3.0 / 0.05 |

- **Drawing in 3D costs per draw item.** The 3D view more than doubles the items: 2,350 flat, 5,200 in 3D. Each
  costs:
  - about 1.0 µs in `MapRenderFeature.CaptureFrame` on the main thread (the doc's "re-sorts every frame" cost);
  - about 0.8-0.9 µs to submit (`DrawMapMesh` and `DrawDynamicThings`);
  - about 1.2-1.4 µs to record on the render thread.

  In all, the paused 3D frame costs 6.2 ms more than the flat one.
- **IsoRim's own frame update is small.** `View3D.Tick`, which holds the unbatched effects of 1.3, takes 0.3 ms.
- **The render thread is the next limit.** It records for 7.2-8.6 ms per frame in 3D, against 12.7 ms on the main
  thread. Any main-thread saving beyond about 4-5 ms in 3D makes the render thread the limit.
- **Persistent draws are always 0.** Section meshes go through the same per-frame immediate path as everything
  else.

**Tick work per Timer tick, set 2** (ms; per-pawn hooks added about 2-4% of overhead)

| Part | 3D, Superfast (36.8 Timer/s) | 3D, Normal (51.8 Timer/s) | Flat, Superfast (55.8 Timer/s) |
|---|---|---|---|
| Normal tick list, all | 5.87 | 1.98 | 5.70 |
| … pawns: animals, `Tick()` | 1.32 | 0.62 | 1.51 |
| … pawns: animals, `TickInterval` | 0.42 | 0.18 | 0.61 |
| … pawns: humanlike, `Tick()` | 0.88 | 0.31 | 0.69 |
| … pawns: humanlike, `TickInterval` | 1.02 | 0.27 | 1.04 |
| … pawns: other (mechs etc.), both | 0.50 | 0.15 | 0.52 |
| … everything else in the normal list | 1.73 | 0.46 | 1.32 |
| `Map.MapPostTick` | 1.51 | 0.27 | 0.84 |
| Multiplayer's per-tick `ProcessPostTickVisuals` | 0.64 | 0.19 | 0.46 |
| `Map.MapPreTick` | 0.36 | 0.20 | 0.52 |
| Long and rare tick lists, quests, path jobs | 0.37 | 0.12 | 0.42 |
| World tick (`AsyncWorldTimeComp.Tick`) | 1.74 | 0.57 | 1.73 |
| **Sum / measured Timer tick** | **10.50 / 10.66** | **3.32 / 3.36** | **9.68 / 9.77** |

- **Pawns are about 40% of the tick:** animals 17%, humanlike 18%, other 5%.
- **Other large parts:**
  - non-pawn normal tickers: 16%;
  - `MapPostTick`: 14%;
  - the world tick: 17%, that is 0.29 ms per world tick. Its contents weren't broken down.
  - Multiplayer's per-tick post-tick visuals: 6%.
- **The settlement map** (no colonists) costs 0.47 ms per tick against the colony's 0.93 ms, so about 28% of a
  Superfast Timer tick.

### 6.4 Section rebuilds in 3D (S7)

Set 3, 3D at Superfast. Run 1 shows the same pattern in every running scenario.

| Window | Sections rebuilt/s | Section rebuild time per frame (ms) | `SectionLayer_Things` | `SectionLayer_IsoRimWalls` | `SectionLayer_IsoRimBodies` | Other layers |
|---|---|---|---|---|---|---|
| 3D, quiet (night) | 7.6 | 1.31 (54 ms/s) | 8.1 ms per call, 46 ms/s | 1.2 ms, 6.6 ms/s | 0.05 ms, 0.3 ms/s | 0.6 ms/s |
| 3D, busy (day) | 16.0 | 5.00 (161 ms/s) | 9.0 ms per call, 136 ms/s | 1.5 ms, 23 ms/s | 0.06 ms, 1.0 ms/s | 0.7 ms/s |
| Flat | 4.1 | 0.03 (1.7 ms/s) | 0.38 ms per call | 0.004 ms | - | - |

- **One section rebuild costs 7-10 ms in 3D, against 0.4 ms flat.** 85% of it is the game's
  `SectionLayer_Things` printed in 3D, and 15% is IsoRim's walls layer. A rebuild runs whenever an item in the
  section moves, so hauling drives it.
- **This is the day/night swing.**
  - At Superfast in 3D, run 1 measured 41-44 Timer ticks/s at night against 30-31 by day (S5 baselines at 03h,
    14h and 02h).
  - At Ultrafast it took 11.3 ms per frame (21 rebuilds/s).
  - One rebuild in a frame is also a 7-10 ms spike.
- **4(a) as written in section 2 fixes only the walls layer**, about 15% of this cost. Cell texture uploads were
  0-0.3 per second and IsoRim's cell map update cost 0.05 ms per frame or less: negligible.

### 6.5 Pawn interval rate (S4)

Run 1 compared the rates in sequence, and its windows drifted with the time of day. Set 2 therefore interleaved
them at Superfast in 3D: three cycles of 1, 15 and 3, 10 s each, game hours 2 to 12. The pawn-interval columns are
`Pawn.TickInterval` time per Timer tick.

| Window | Timer ticks/s | Timer tick ms | Colony map tick ms | Humanlike interval | Other pawns' interval | Interval total |
|---|---|---|---|---|---|---|
| Cycle 1, rate 1 | 40.3 | 10.16 | 0.911 | 1.07 | 0.18 | 1.25 |
| Cycle 1, rate 15 | 39.8 | 10.04 | 0.899 | 0.60 | 0.06 | 0.66 |
| Cycle 1, rate 3 | 39.5 | 10.18 | 0.907 | 0.74 | 0.11 | 0.85 |
| Cycle 2, rate 1 | 33.6 | 11.72 | 1.093 | 1.30 | 0.18 | 1.48 |
| Cycle 2, rate 15 | 28.1 | 11.86 | 1.163 | 0.85 | 0.07 | 0.92 |
| Cycle 2, rate 3 (settlement forced to Normal) | 30.3 | 10.00 | 1.212 | 0.94 | 0.14 | 1.07 |
| Cycle 3, rate 1 (settlement forced to Normal) | 29.6 | 10.02 | 1.220 | 1.23 | 0.20 | 1.43 |
| Cycle 3, rate 15 (settlement forced to Normal) | 31.6 | 8.92 | 1.060 | 0.66 | 0.07 | 0.73 |
| Cycle 3, rate 3 (settlement forced to Normal) | 30.1 | 9.61 | 1.168 | 0.94 | 0.13 | 1.08 |

- **Rate 3 saves 0.40 ms per Superfast Timer tick, about 4%** (mean interval total 1.39 → 1.00 ms). Rate 15
  saves 0.62 ms. That is smaller than the drift between windows.
- **Calls fall about 15-fold, time only about 2-fold.** Each call does more work at a longer interval: 6.6 µs per
  humanlike call at rate 1, 57-82 µs at rate 15. Most interval work is gated by hash intervals, as 1.2 expected.
- **Only 27 pawns use the rate** (11 colonists and 16 mechs); animals stay at 15. "Settlement forced to Normal" is
  the game's threat slowdown (`forceNormalSpeedUntil`), which made that map tick once per Timer tick.

### 6.6 IsoRim settings (S5), CPU side only

The paused runs were at game hour 0, night, with no state drift. At Superfast, every Timer-tick figure followed
the time of day, so only the draw-only frames compare.

| Setting turned off | Paused: busy ms (median) | Paused: draw items | Superfast: draw-only busy ms (hour) |
|---|---|---|---|
| None (baselines) | 12.33 and 12.39 | 5,196 | 11.62 (03h), 11.15 (14h), 11.50 (02h), 11.37 (10h) |
| antiAlias | 12.43 | 5,195 | 11.56 (06h) |
| grade | 12.39 | 5,195 | 11.27 (08h) |
| lampFacing | 12.35 | 5,196 | 11.61 (10h) |
| lampHalos | 12.33 | 5,195 | 11.01 (12h) |
| faceShadows | 12.33 | 5,196 | 11.32 (16h) |
| outlines | 12.26 | 5,053 | 11.02 (19h) |
| trees3D | 12.09 | 4,930 | 11.01 (21h) |
| smallPlants3D | 12.44 | 5,151 | 11.42 (23h) |
| effects3D | 12.31 | 5,051 | 11.33 (05h) |
| waterStyle | 12.21 | 5,055 | 11.39 (08h) |
| weather3D, rain forced: on → off | 14.10 → 12.34 | 5,196 → 5,197 | 13.05 → 11.82 (12h, 14h) |

- **Only 3D weather has a CPU cost: 1.8 ms per frame while it rains.** Every other setting stays within ±0.25 ms
  of the baseline. trees3D (-0.24 ms, 266 fewer items) and outlines (-0.07 ms) are the largest.
- **FXAA, grade, lamp facing, halos and face shadows are GPU work.** Their cost can only show in a GPU-bound
  (4K) run.

### 6.7 Hitches and garbage collection (run 1)

| Scenario | Frame median ms | p99 | Max | GC gen0 / gen1 / gen2 in 30 s | GC pause, ms in 30 s | Allocated MB/s |
|---|---|---|---|---|---|---|
| Flat, paused | 8.3 | 11.7 | 23 | 10 / 0 / 0 | 25 | 5 |
| 3D, paused | 12.8 | 16.9 | 19 | 52 / 3 / 0 | 201 | 28 |
| 3D, Normal | 15.4 | 26.9 | 53 | 53 / 8 / 0 | 228 | 31 |
| 3D, Fast | 18.0 | 31.9 | 85 | 62 / 17 / 1 | 272 | 37 |
| 3D, Superfast | 22.3 | 40.5 | 72 | 69 / 19 / 0 | 324 | 44 |
| 3D, Ultrafast | 52.9 | 103.4 | 132 | 98 / 53 / 2 | 535 | 113 |
| Flat, Superfast | 17.7 | 27.0 | 63 | 56 / 11 / 0 | 329 | 30 |

- **Drawing the paused 3D view allocates 28 MB/s, against 5 MB/s flat:** a gen0 collection every 0.6 s.
- **GC pauses take 0.7-1.8% of the time,** about 3-4 ms each.
- **What makes p99 1.7-2x the median in running 3D:** section rebuilds (7-10 ms each) and GC pauses.
  Multiplayer's two-sample frame-time average passes such spikes on to the server's pacing (1.1).

### 6.8 GPU hint: 2560x1440 against 1280x720, windowed, paused

| Scenario | fps | Busy ms (median) | Render thread: record / GPU wait (ms per frame) | GPU load (nvidia-smi, 1 s samples) |
|---|---|---|---|---|
| 3D, 1280x720 | 81.8 | 12.44 | 7.0 / 0.04 | 38%, 652 MHz, 114 W |
| 3D, 2560x1440 | 81.5 | 12.44 | 7.1 / 0.04 | 39%, 1134 MHz, 126 W |
| Flat, 2560x1440 | 119.8 (vsync) | 6.86 | 3.0 / 4.80 (vsync) | 36%, 628 MHz, 111 W |

- **2560x1440 has 44% of 4K's pixels, and the frame time didn't move.** The render thread never waited for the
  GPU, and the GPU clocked up only to about 1.1 GHz. So the GPU isn't the limit at 1440p.
- **4K, measured later (6.9):** the GPU stays far below the CPU frame at 4K full screen too, about a third busy at
  reduced clocks.
- **GPU during the running scenarios:** 13-38% load at 270-670 MHz.

### 6.9 4K full screen (GPU side)

The GPU half of the section 4 plan: S1 and S2 at 3840x2160 full screen, S5 on the GPU, S6, and the same views at
2560x1440 full screen for render scale. The user approved the full-screen run on Nov 3 (section 5, question 9).

**Setup.** As in 6.1 (same replay copy, mod list, IsoRim settings, hosting and camera), except:
- **Display:** 3840x2160 exclusive full screen, UI scale 2 and dev mode off, as in the user's Prefs. Run in
  background was on, so a focus change couldn't stall the run. The engine still presented with FIFO ("3840x2160
  FifoKHR, 3 images") on the 119.88 Hz panel, so 8.33 ms is the floor.
- **The 1440p scenarios:** exclusive full screen at 2560x1440 (the engine's swapchain was 2560x1440). The display
  stack scaled it up to the panel.
- **Two runs:**
  - Run 4 (11:56-12:18): 39 scenarios.
  - Set 5 (12:24-12:29): 9 scenarios at the replay's own 21.6 h, because run 4's zoomed "night" views landed at
    6.5 h.

  Both logs had 0 errors.
- **Time of day:**
  - Run 4 began at 21.6 h. Its S5 night set ran at 4.8 h, before dawn.
  - The day set ran at 11.0 h, reached by running the game at Ultrafast in the flat view.
  - The colony map had fog weather throughout.
- **Zoomed views (S6):** root size 15, against the user's 47.4.
  - "Indoors" is centred on the colony's most built-up roofed spot (cell 134,62, a large tiled room), scored by
    constructed roofs and lit lamps within 10 cells. The map has 105 lit colony lamps.
  - "Outdoors" is the nearest open ground with no roof within 12 cells (cell 109,73, fields, 27 cells away).

**New instruments**
- **GPU timestamps** at the top and bottom of every frame's command buffer: GPU time per frame (a Harmony prefix and
  postfix on `FrameRenderer.RecordFrame`, results read 8 frames later without waiting).
- **The render thread's wait split in two:**
  - The fence wait: waiting for the GPU to finish the frame that last used this slot.
  - The swapchain image acquire (`GetNextFrameIndex`): vsync and presentation.
- **nvidia-smi** every 0.5 s: load, graphics clock and power.

**Reading the GPU numbers.** The driver lowers the graphics clock when the GPU has little to do. In these runs it
ran at 500-1,600 MHz (the maximum is 2,160 MHz), and nvidia-smi showed 32-45% load in every scenario, because the
clock followed the load. So GPU milliseconds depend on the clock the driver picked. To compare settings, the tables
also give GPU work in million cycles (ms × MHz / 1000). That conversion assumes the work scales with the core
clock; the memory clock stayed at 9,501 MHz throughout. It is an estimate, and so is every "at boost clock" figure
below (1.7-1.9 GHz).

**S1 and S2: speed and frame time, 4K against 1280x720**

The 720p columns are run 1 of 6.2 (UI scale 1, windowed). Real speed is Timer ticks/s ÷ 60 at Normal and map TPS ÷
180 at Fast.

| Scenario | 720p: fps / frame ms (median) / real speed | 4K: fps / frame ms (median / p99) | 4K: Timer ticks/s (TPS) | 4K: real speed | 4K: draw-only frames, busy ms | 4K: Timer tick ms |
|---|---|---|---|---|---|---|
| Flat, paused | 120 / 8.3 / - | 119.8 / 8.36 / 10.0 | - | - | 6.43 | - |
| Flat, Normal | 113 / 8.4 / 95% | 114.3 / 8.74 / 15.4 | 57.1 (57) | 95% | 6.80 | 3.49 |
| Flat, Fast | not run | 94.3 / 12.16 / 17.9 | 57.2 (172) | 95% | 6.31 | 6.53 |
| 3D, paused | 79 / 12.8 / - | 78.0 / 12.66 / 15.8 | - | - | 12.63 | - |
| 3D, Normal | 65 / 15.4 / 94% | 62.1 / 15.83 / 29.0 | 56.3 (56) | 94% | 12.72 | 3.20 |
| 3D, Fast (2-5 h) | 54 / 18.0 / 85% | 52.9 / 18.38 / 31.9 | 50.1 (150) | 83% | 12.29 | 6.19 |
| 3D, Fast by day (11-13 h) | not run | 42.1 / 20.36 / 50.3 | 38.4 (115) | 64% | 12.75 | 6.98 |

- **4K full screen runs as fast as 1280x720.** Frame times match within 1-4%, which is within the time-of-day drift.
- **At Fast in 3D the session runs at 83% at night and 64% by day.** This matches the user's report that the slowdown
  shows at Fast. The day penalty is the section rebuilds of 6.4: 12.8 per second by day against 3.4 at night,
  and `Map.MapUpdate` at 9.3 ms per frame against 5.5.
- **Normal speed is nearly full speed** (94%), and flat Fast reaches 95%.

**Where the frame waits, 4K** (ms per frame, means unless marked)

| Scenario | Main thread frame (median) | Main thread waiting for the render thread | Render thread recording | Render thread fence wait (GPU) | Render thread image acquire (vsync) | GPU time per frame (mean / p95) | GPU clock, MHz | GPU work, M cycles | Power, W |
|---|---|---|---|---|---|---|---|---|---|
| Flat, paused | 8.36 | 1.89 | 3.14 | 0.005 | 4.86 | 2.12 / 2.57 | 629 | 1.3 | 116 |
| 3D, paused | 12.66 | 0.01 | 7.18 | 0.005 | 0.04 | 4.21 / 4.54 | 1,412 | 5.9 | 138 |
| 3D, Normal | 15.83 | 0.01 | 8.02 | 0.006 | 0.04 | 5.29 / 5.68 | 1,103 | 5.8 | 129 |
| 3D, Fast | 18.38 | 0.01 | 7.78 | 0.008 | 0.05 | 6.14 / 6.61 | 951 | 5.8 | 124 |
| 3D, Fast by day | 20.36 | 0.01 | 7.66 | 0.008 | 0.05 | 6.93 / 7.90 | 869 | 6.0 | 119 |
| 3D, paused, zoomed indoors (night) | 8.36 (vsync) | 3.44 | 1.55 | 0.006 | 6.53 | 2.65 / 2.92 | 1,508 | 4.0 | 144 |

- **The main thread is still the limit in every 3D scenario at 4K.**
  - It waits 0.01 ms per frame for the render thread.
  - The render thread waits 0.005-0.008 ms for the GPU and 0.04-0.06 ms for a swapchain image.
  - Neither the render thread nor the GPU holds the frame back.
- **The GPU is busy about a third of each frame, at any speed.** The driver lowers the clock to match: 1,400 MHz
  paused, 870-950 MHz at Fast.
  - A 3D frame of the far view costs 5.8-6.2 million GPU cycles.
  - At boost clock that is an estimated 3.1-3.6 ms, against a 12.7-20 ms main-thread frame.
  - So the GPU would become the limit only above an estimated 280-330 fps, far above the 120 Hz cap.
- **The render thread costs the same at 4K as at 720p:** 7.2-8.0 ms of recording per 3D frame, against 7.2-8.6 ms
  in 6.3. Its cost is per draw item, not per pixel.
- **Flat paused and the zoomed views reach the 120 Hz cap.** The waiting there is the render thread's image acquire
  (vsync, 4.9-7.4 ms), not the GPU.
- **Zooming in is the largest drawing saving measured so far.**
  - At root size 15 the 3D frame's main thread needs 4.8-5.4 ms (1,000-1,200 draw items), against 12.7 ms at the
    user's 47.4 (about 5,000 items).
  - With T = 6.2 ms per Timer tick, the model of 1.1 then gives about 11 ms per frame at Fast, which is full speed.
    That is an estimate: Fast wasn't run zoomed in.

**S5 on the GPU: one setting off at a time, paused, 4K, far view**

M cycles per frame. The change is against the baseline in the same column.

| Setting turned off | Night (4.8 h) | Day (11.0 h) | Night (21.6 h, set 5) | Est. saving at boost clock |
|---|---|---|---|---|
| None (baselines) | 6.00 and 6.01 | 6.18, 6.19 and 6.19 | 5.93 and 5.94 | - |
| antiAlias (FXAA) | 5.61 (-6%) | 5.77 (-7%) | - | about 0.2 ms |
| grade | 5.67 (-6%) | 5.86 (-5%) | - | about 0.2 ms |
| lampFacing | 4.12 (-31%) | 4.26 (-31%) | 4.03 (-32%) | about 1.0 ms |
| lampHalos | 6.01 (0) | - | - | 0 |
| faceShadows | 5.96 (-1%) | 6.01 (-3%) | - | about 0.1 ms or less |
| outlines | 5.77 (-4%) | - | - | about 0.1 ms |
| trees3D | 5.78 (-4%) | - | - | about 0.1 ms |
| smallPlants3D | 5.84 (-3%) | - | - | under 0.1 ms |
| waterStyle | 6.00 (0) | - | - | 0 |
| aniso 16 → 4 | 5.97 (-0.5%) | - | - | 0 |
| All five of antiAlias, grade, lampFacing, lampHalos, faceShadows | 3.43 (-43%) | 3.42 (-45%) | - | about 1.4-1.6 ms |

- **Lamp facing is the only expensive GPU feature.**
  - It is about a third of the GPU work in the far view, by day as much as at night.
  - IsoRim skips the lamp loop where the sky alone reaches the game's clamp, which should be most pixels outdoors
    by day (CLAUDE.md, Known gaps, IsoRim lighting). The fog on the colony map may have kept the sky below that
    clamp. Not checked.
- **FXAA and the grade cost about 6% each.** Outlines and 3D trees cost about 4% each; they are also the largest
  CPU-side items in 6.6.
- **Halos, water and aniso are within noise** (the baselines agree within 0.2%). Face shadows cost 3% by day and
  nothing at night.
- **None of this changes the frame time,** because the main thread takes 12-20 ms. The measured milliseconds hardly
  moved at all (4.26 → 4.15 ms with lamp facing off): the driver dropped the clock from 1,408 to 993 MHz instead.
  Turning these settings off saves GPU power (138 → 125 W with all five off), not time.

**S6: day against night, indoors against outdoors** (paused, 3D, 4K, M cycles per frame, main-thread busy ms in
brackets)

| View | Night (21.6 h, set 5) | Morning (6.5 h) | Day (11.0 h) |
|---|---|---|---|
| Far view (the save's camera, root size 47.4) | 5.93 (12.8) | 6.00 at 4.8 h (12.7) | 6.18 (13.3) |
| Zoomed in, indoors | 3.99 (4.8) | 4.14 (4.9) | 4.08 (4.8) |
| Zoomed in, indoors, lampFacing off | 2.40 (-40%) | - | - |
| Zoomed in, indoors, all five off | 1.75 (-56%) | 1.73 (-58%) | - |
| Zoomed in, outdoors | 2.82 (5.0) | 3.12 (5.4) | 3.04 (5.1) |
| Zoomed in, outdoors, lampFacing off | 2.18 (-23%) | - | - |
| Zoomed in, outdoors, faceShadows off | - | - | 2.93 (-4%) |
| Zoomed in, indoors, flat view | 0.92 (3.1) | - | - |

- **Time of day changes the GPU work by 2-11% in every view.** It is highest in the morning and by day; face
  shadows account for an estimated 3-4% by day. The sun-shadow march is cheap at this pitch.
- **Indoors costs a third to 40% more GPU work than open ground at the same zoom.** That is lamp facing on lamp-lit
  faces: turning it off indoors at night removes 40%.
- **The paused far view costs the main thread 0.5-0.6 ms more by day** (`Map.MapUpdate` 6.1 against 5.6 ms). The
  cause wasn't broken down.

**Render scale: 2560x1440 against 3840x2160, both exclusive full screen, same views**

| Scenario | 4K: frame ms / GPU M cycles / W | 1440p: frame ms / GPU M cycles / W | GPU work at 1440p |
|---|---|---|---|
| Flat, paused | 8.36 (vsync) / 1.34 / 116 | 8.36 (vsync) / 0.81 / 112 | -40% |
| 3D, paused, night | 12.93 / 6.01 / 137 | 12.89 / 3.67 / 124 | -39% |
| 3D, paused, day | 13.27 / 6.19 / 137 | 13.45 / 3.75 / 123 | -39% |
| 3D, Fast, night | 18.38 / 5.84 / 124 (50.1 Timer ticks/s) | 18.98 / 3.81 / 115 (46.7 Timer ticks/s) | -35% |

- **1440p has 44% of the pixels and cut the GPU work by 35-40%, but frame time and speed didn't change.**
  - Paused frames are within 1.5%.
  - The lower Fast figure at 1440p most likely comes from the hour: it ran from 4.8 h into dawn, with 5.7 section
    rebuilds per second against 3.4.
- **Render scale therefore buys no speed on this PC, at 4K or below.** It would only lower GPU power by 9-14 W.
  This also answers the user's untried resolution test (section 3): a lower resolution won't make the game faster.

### 6.10 Interpretation and updated ranking

**What the measurements settle**
- **The user's slowdown on this PC is CPU-bound, at every resolution up to 4K full screen (6.9).**
  - At 4K the GPU is busy about a third of each frame, at clocks the driver lowers to 0.9-1.5 GHz. The render thread
    never waits for it (0.005-0.008 ms per frame), and frame times equal those at 1280x720 within 1-4%.
  - Normal speed is almost full speed (94% in 3D).
  - Fast in 3D, where the user notices the slowdown most, reaches 83-85% at night and 64% by day (4K, 6.9).
  - Superfast in 3D reaches 69% at night and about 50% by day.
  - Ultrafast in 3D reaches 29%, no faster than Superfast.
  - The flat view reaches 87% at Superfast and 50% at Ultrafast.
- **About half of a Superfast frame is the 3D draw (R ≈ 12 ms) and half the ticks (T ≈ 10 ms),** and the session
  runs one Timer tick per frame.
- **By day, 3D section rebuilds add 5-11 ms per frame.**
- **The 3D draw cost follows the number of draw items, not the pixels.** At the user's far zoom (root size 47.4)
  about 5,000 items take 12.7 ms of main thread. Zoomed in to root size 15, 1,000-1,200 items take 4.8-5.4 ms, at
  4K (6.9).
- **The "large colony" is large in things and animals, not in colonists:** 87,000 things on 2 maps, 144 animals,
  11 colonists. Pawn-rate changes therefore have little to work on.

**Updated ranking** (replaces the order of section 2 for this user; gains marked "est." are model estimates from
the measured R and T)

| # | Option | Measured basis | Expected gain |
|---|---|---|---|
| 1 | Session pacing: up to 2 Timer ticks per frame plus a smoothed report (old 1) | R ≈ 12 ms and T ≈ 10 ms at Superfast in 3D; R ≈ 12.3-13.4 ms and T ≈ 6.2-7.0 ms at Fast in 3D at 4K | Est. Fast in 3D from 83% to 100% at night and from 64% to about 100% by day; Superfast in 3D from 69% to about 100% at night, and from about 50% to about 85% by day; Ultrafast from 17 to about 24 Timer ticks/s |
| 2 | **New:** cheaper 3D section rebuilds | `SectionLayer_Things` takes 8-9 ms per rebuild in 3D, 21-24x flat; 7-21 rebuilds/s; 5-11 ms per frame by day; 7-10 ms spikes | Up to 5-11 ms per frame by day at fast speeds, most of the day/night swing, and smaller p99. Options: rebuild only the moved thing's part (cache each thing's baked mesh), keep haulable items out of the section mesh (draw them instanced), and a per-frame rebuild budget. Old 4(a) covers only the walls layer (about 15%) |
| 3 | Fewer draw items in 3D (old 7, level of detail at this far zoom, and old 8, single-pass lighting) | 5,200 items in 3D against 2,350 flat; about 2 µs per item on the main thread (capture 1.0, submission 0.8-0.9) and 1.2-1.4 µs on the render thread. The same at 4K: recording 7.2-8.0 ms per frame (6.9) | Est. 2-3 ms per frame if IsoRim's extra 2,850 items drop by a third to a half. The render thread, at 7-8.6 ms, then becomes the limit |
| 4 | Allocation in the 3D draw path | 28 MB/s paused in 3D against 5 MB/s flat; up to 113 MB/s at Ultrafast | Fewer gen0 and gen1 collections and smaller spikes; size unknown until profiled |
| 5 | Tick-side items other than the pawn rate | World tick 17% (0.29 ms per world tick, contents unknown); Multiplayer's per-tick post-tick visuals 6%; animals' `Tick()` 12% | Each a few percent of T. They need a look before any claim: post-tick visuals were moved into the tick on purpose, and determinism comes first |
| 6 | Synced pawn interval rate 3 (old 2) | Interleaved A/B: 0.40 ms of 10 ms per Superfast Timer tick | About 4%. Demoted |
| 7 | User settings, no code: zoom in a little; 3D weather off while it rains | Zoom: 12.7 ms of main thread at root size 47.4, 4.8-5.4 ms at 15 (6.9). Weather: 1.8 ms per frame CPU (6.6) | Zoom: est. full speed at Fast at root size 15 (not run at Fast); a middle zoom wasn't measured. Weather: 1.8 ms per frame while it rains |
| 8 | Render scale (old 3), FXAA and grade merged (old 5), foveated shading (old 6), a cheaper lamp loop | Measured at 4K full screen (6.9): the GPU is busy a third of each frame at reduced clocks, the render thread's fence wait is 0.005-0.008 ms, and 2560x1440 cut the GPU work by 35-40% with frame time unchanged. Lamp facing is a third of the GPU work, FXAA and the grade 6% each | 0 speed, measured. Lower GPU power only (9-14 W at 1440p, 12-13 W with the five lighting and post settings off). Dropped for this PC |
| 9 | Batching IsoRim's per-frame effects (old 4(b)) and pawn preparation on worker threads (old 9) | `View3D.Tick` 0.3 ms; thing draw 2.5-3.1 ms in 3D against 0.8 ms flat | Small: est. up to about 1.5 ms |

The session-pacing estimate assumes a second Timer tick in a frame costs the same T. It is the change with the
most core-timing risk, so it still needs the two-instance soak test of section 2.

### 6.11 Measured, estimated and still open

**Measured, at 1280x720 windowed (2560x1440 for 6.8 only), on this PC**
- Frame time and busy time distributions.
- Timer ticks/s, TPS, and tick time per Timer tick, per map tick and by part.
- The main-thread breakdown, render-thread recording and GPU-wait time, and draw items per frame.
- Section rebuilds by layer.
- Pawn-rate A/B.
- CPU effect of the IsoRim settings.
- GC counts, pauses and allocation rate.

**Measured, at 3840x2160 and 2560x1440 exclusive full screen (6.9)**
- S1 and S2: frame time, speed and tick time at Normal and Fast, 3D and flat.
- GPU time per frame from GPU timestamps, GPU clock, load and power.
- The render thread's fence wait and image acquire, and the main thread's wait for the render thread.
- The GPU side of S5, by night and by day.
- S6: day, morning and night, far view and zoomed in over indoors and open ground.
- Render scale: the same views at 2560x1440.

**Estimated**
- All gains in the ranking, from the measured R and T and per-item costs.
- The cause of cheaper back-to-back ticks (warm caches).
- GPU cycles and milliseconds at boost clock (6.9): they assume the GPU work scales with the core clock.
- Speed at Fast when zoomed in.

**Not covered here**
- The friend's PC and the two-instance pacing check (S8). The user reports that the game is slower with the friend
  in it and slower still when both run Fast.
- Fast at a closer zoom, and any zoom between 15 and 47.4.
- Why lamp facing costs as much by day as at night (the fog on the colony map is a guess).
- The GPU cost of 3D weather and 3D effects at 4K (not in this run; their CPU cost is in 6.6).
- The contents of the world tick.
- A profile of the 3D allocation sources.
- The perspective camera and first person: the user plays orthographic.

**Caveats**
- The storyteller was off.
- Both maps ran at the same speed.
- Superfast numbers swing ±20% with the in-game hour (6.4).
- The harness's own hooks were cheap in run 1. The per-pawn hooks of sets 2 and 3 add about 2-4% to their tick
  times.
- At 4K the GPU ran at the clocks the driver chose (500-1,600 MHz). The colony map had fog weather in every 4K
  scenario.
