# Desync risks on RimWorld 1.7 and what this port does about them

Written Oct 2026 for 1.7.4982 with Multiplayer 0.11.5.17. Facts below were checked on this machine
(Bazzite, .NET 10 WaylithClr) unless marked as untested.

## Checked and not a problem

- **Identity hash codes** (`object.GetHashCode` on things, comps, pawns) are the same in every process on
  .NET 10 as long as objects are allocated and hashed in the same order: two processes produced identical
  sequences. `HashSet<Thing>` iteration therefore matches between clients that run the same simulation, which is
  the property upstream relied on with Mono. UI code that hashes new objects can still perturb the sequence,
  which is why the existing GlowGrid and Tradeable hash patches stay.
- **`Enum.GetHashCode` and `ValueTuple.GetHashCode`** are stable across processes on .NET 10 (checked); only
  `System.HashCode` is per process. Assembly-CSharp 1.7 makes no direct `string.GetHashCode` call, and the
  game's runtimeconfig turns on `WaylithClr.MonoStringHashCode`, so string-keyed dictionaries iterate the same
  way everywhere.
- **Real time in the simulation.** A scan of every `Time`, `RealTime`, `DateTime`, `Stopwatch`, `System.Random`
  and `Guid.NewGuid` use (`docs/realtime-scan.md`, tool `DeterminismScan --realtime`) finds 354 call sites in
  1.7 against 333 in 1.6. All 18 new ones are UI: profiler windows, sound sampling, GUI event fixes. Nothing new
  reached tick code.
- **Lambda and transpiler targets.** All 315 compiler-generated members Multiplayer looks up by name are
  unchanged between 1.6 and 1.7, and the nine transpiled methods whose IL changed were reviewed
  (`docs/rwdiff-1.6-1.7.md`).
- **Parallel work.** `FastTileFinder.Query` and `Closest` run as one batch in multiplayer; glow grid, path grid
  and `PathFinderMapData` jobs write one output per cell; path jobs are completed every tick by Multiplayer.

## Handled, but keep an eye on

- **Harmony patches dropped by tiered compilation.** The biggest 1.7-specific desync source: with tiering on,
  hot patched methods are re-JIT'ed and the detour is gone. Mitigation: `DOTNET_TieredCompilation=0`.
  `HarmonyPatchWatchdog` logs an error at startup when tiering is on, offers a restart with it off from the
  main menu (the game's restart inherits the environment), and logs an error in play if the tick patch stops
  firing. A friend who skips this gets a guaranteed desync; the restart offer is there so nobody has to
  remember a launch option.
- **Tiny patched methods inlined into early-compiled callers** (tiering off makes the JIT optimize everything
  on first call). Found once (`LoadedObjectDirectory.Clear`); the "Patch hit counts" debug action exists to
  catch the next one. Any `HashCode.Combine<int xN>` counter at 0 after a long session means the game-side
  `GetHashCode` methods need patching instead.
- **`System.HashCode` in simulation code**: `Map.NextGenSeed`, `Gravship.ConstantRandSeed`, trap avoidance
  rolls, layout `RandBlock`s. All int overloads, covered by `Combine2..8` when they fire (see above).

## Not handled

- **Cross-OS floating point.** On .NET, `Math.Sin/Cos/Pow` and friends call the platform C runtime (glibc on
  Linux, UCRT on Windows), and results can differ in the last bit for some inputs. Mono had the same property,
  so this isn't new, but a Linux host with Windows clients is the configuration most likely to desync in long
  sessions. Untested until a Windows friend joins (task 11). If it bites, the desync traces from task 10 will
  point at the method; a software implementation of the specific function is the usual fix.
- **Frame-time reads in pre-existing simulation code** (same in 1.6): `CompSkyfallerRandomizeDirection.CompTick`
  (`Time.deltaTime`, probably visual), `WorldComponent_GravshipController.WorldComponentUpdate`
  (`Time.deltaTime`, gravship cutscene timing) and `ShipCountdown.ShipCountdownUpdate`. Watch the gravship
  one during Odyssey travel tests; `MapBrightnessTracker.Tick` is already patched.

## Process recommendations

- Test with `scripts/run_two.sh`, dev mode on, and run "Patch hit counts" before quitting.
- After every game or Harmony update: rebuild, re-run `DeterminismScan` (both modes) and `RwDiff` against the
  previous build in `~/rw-builds`, and read the two reports' attention lists before playing.
- Keep desync traces on for long sessions once task 10 brings them back on .NET.
