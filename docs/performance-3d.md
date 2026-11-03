# Performance in the 3D view: large colony, 4K, multiplayer

Research of Nov 3 2026, game 1.7.4995 rev309 (the port was built for 4990; branch port-1.7 at 20ccd737). This
was read-only work: nothing has been measured, changed or launched. Every gain below is an estimate until the
plan in section 4 has been run. Sources:
- Five research passes covering IsoRim, the Waylith engine, the simulation, other performance mods, and
  reduced-resolution and foveated rendering. The claims used here were re-checked against the code they cite.
- The Proton Player.log of the Nov 3 session.
- The user's Prefs.xml, ModsConfig.xml and IsoRim settings.

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
- **The most likely bottleneck is the CPU main thread, not the GPU.** Two things load it:
  - The tick work. The colony is large, and since Oct 27 every non-animal pawn on a viewed map runs its
    interval work every tick.
  - IsoRim's per-frame draw submission. Every lit draw is doubled by the light pass, and every colonist takes at
    least 6 draw commands.

  An RTX 3080 at 4K could still be the limit, but the log's frame counters average 85-98 fps over several
  minutes (pauses included). Neither is measured yet.
- **The user's ideas:**
  - **Lower resolution: workable.** Render the 3D world at a lower resolution and keep the interface sharp.
    Waylith already draws the map into its own render target and upscales it in a final full-screen pass, with
    the interface drawn afterwards at full resolution. It takes about 1-2 days of work and helps only if the GPU
    is the limit.
  - **Foveated rendering: not workable as such.** Waylith never turns on the GPU feature that foveated rendering
    needs (Vulkan's variable-rate shading), and it creates the GPU device before any mod loads. A cheap variant
    is workable: lighter shading away from the screen centre.
- **Order of work:**
  1. Measure (one evening in the test copy).
  2. If tick work is the limit: the two tick-side changes (catch-up ticks, and a synced pawn rate of 3).
  3. If the GPU is the limit: render scale.
  4. Either way: IsoRim's CPU fixes, which don't change the look.

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
| Zoom in a little | Fewer sections and things drawn | Less map on screen |
| Multiplayer: slow (.NET) desync tracing off on both PCs; dev mode off | About 4 ms per map tick if on; a lookup per thing per tick | None |

**Lower resolution without code**
- **Through the game's options:** with exclusive fullscreen, 2560x1440 should make the monitor scale the image,
  interface included. The Nov 3 log shows a `Dialog_ResolutionConfirm` at 10:02:58, and Prefs.xml still says
  3840x2160, so that test may already have been tried.
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
