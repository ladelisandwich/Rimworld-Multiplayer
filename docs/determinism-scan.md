# Determinism scan of Assembly-CSharp

Assembly-CSharp.dll 1.7.9787.38423, scanned 2026-10-19. 
Every method that calls string.GetHashCode, System.HashCode, a ValueTuple or Enum GetHashCode 
(all differ per process on .NET), WaylithJobRuntime, Parallel, Task.Run or ThreadPool (run in parallel).

| Category | Call sites | Types |
|---|---|---|
| HashCode | 27 | 17 |
| Unity jobs | 23 | 8 |
| Enum.GetHashCode | 14 | 13 |
| Parallel | 10 | 8 |
| ThreadPool | 4 | 3 |
| Task.Run | 1 | 1 |

## Gilzoide.ManagedJobs.ManagedJob

- `Run`: Unity.Jobs.IJobExtensions.Run
- `Schedule`: Unity.Jobs.IJobExtensions.Schedule x2

## Gilzoide.ManagedJobs.ManagedJobFor

- `Run`: Unity.Jobs.IJobForExtensions.Run
- `Schedule`: Unity.Jobs.IJobForExtensions.Schedule, Unity.Jobs.IJobExtensions.Schedule
- `ScheduleParallel`: Unity.Jobs.IJobForExtensions.ScheduleParallel, Unity.Jobs.IJobExtensions.Schedule

## Gilzoide.ManagedJobs.ManagedJobParallelFor

- `Run`: Unity.Jobs.IJobParallelForExtensions.Run
- `Schedule`: Unity.Jobs.IJobParallelForExtensions.Schedule, Unity.Jobs.IJobExtensions.Schedule

## Gilzoide.ManagedJobs.ManagedJobParallelForTransform

- `Schedule`: Unity.Jobs.IJobExtensions.Schedule

## Ionic.Zlib.ParallelDeflateOutputStream

- `Write`: System.Threading.ThreadPool.QueueUserWorkItem

## LayoutWorker

- `FillAllRooms`: System.HashCode.Combine x3

## RimWorld.Building_Trap

- `KnowsOfTrap`: System.HashCode.Combine

## RimWorld.DefOfHelper

- `RebindAllDefOfs`: System.Threading.Tasks.Parallel.ForEach

## RimWorld.Dialog_SaveFileList

- `ReloadFiles`: System.Threading.Tasks.Task.Run
- `ReloadFilesTask`: System.Threading.Tasks.Parallel.ForEach

## RimWorld.LayerConnection

- `GetHashCode`: System.HashCode.Combine

## RimWorld.MutantCount

- `GetHashCode`: Verse.DevelopmentalStage.GetHashCode

## RimWorld.PawnOverlayDrawer+CacheKey

- `.ctor`: RimWorld.PawnOverlayDrawer+OverlayLayer.GetHashCode

## RimWorld.Planet.FastTileFinder

- `Closest`: Unity.Jobs.IJobParallelForExtensions.Schedule
- `Query`: Unity.Jobs.IJobParallelForExtensions.Schedule
- `RegenerateCache`: System.Threading.Tasks.Parallel.For

## RimWorld.Planet.FastTileFinder+CachedTileData

- `GetHashCode`: System.HashCode.Combine

## RimWorld.Planet.FastTileFinder+TileQueryParams

- `GetHashCode`: System.HashCode.Combine x3

## RimWorld.Planet.Gravship

- `get_ConstantRandSeed`: System.HashCode.Combine

## RimWorld.PortraitsCache+PortraitParams

- `.ctor`: Verse.PawnHealthState.GetHashCode

## RimWorld.QualityRange

- `GetHashCode`: RimWorld.QualityCategory.GetHashCode

## RimWorld.ScenPart_MonolithGeneration

- `GetHashCode`: RimWorld.MonolithGenerationMethod.GetHashCode

## RimWorld.ScenPart_PawnModifier

- `GetHashCode`: RimWorld.PawnGenerationContext.GetHashCode

## RimWorld.ScenPart_PlanetLayer

- `GetHashCode`: System.HashCode.Add x5, System.HashCode.ToHashCode

## RimWorld.ScenPart_PlayerPawnsArriveMethod

- `GetHashCode`: RimWorld.PlayerPawnsArriveMethod.GetHashCode

## RimWorld.StatRequest

- `GetHashCode`: RimWorld.QualityCategory.GetHashCode

## RimWorld.Toils_Ingest+<>c__DisplayClass3_0

- `<CarryIngestibleToChewSpot>b__0`: System.HashCode.Combine

## RimWorld.XenotypeCount

- `GetHashCode`: Verse.DevelopmentalStage.GetHashCode

## Verse.DynamicDrawManager

- `ComputeCulledThings`: Unity.Jobs.IJobParallelForExtensions.Schedule
- `DrawSilhouettes`: Unity.Jobs.IJobParallelForExtensions.Schedule

## Verse.FleckSystemBase`1

- `<ForceDraw>g__Process|12_1`: System.Threading.ThreadPool.QueueUserWorkItem

## Verse.GenCollection

- `DictHashCode`: System.HashCode.Combine

## Verse.GenStepWithParams

- `GetHashCode`: System.HashCode.Combine

## Verse.GenThreading

- `ParallelFor`: System.Threading.ThreadPool.QueueUserWorkItem
- `ParallelForEach`: System.Threading.ThreadPool.QueueUserWorkItem

## Verse.GlowGrid

- `GlowGridUpdate_First`: Unity.Jobs.IJobParallelForExtensions.Schedule x2

## Verse.Graphic+AtlasReplacementInfoCacheKey

- `.ctor`: Verse.TextureAtlasGroup.GetHashCode

## Verse.LoadedModManager+<>c__DisplayClass19_0

- `<ParseAndProcessXML>b__0`: System.Threading.Tasks.Parallel.ForEach

## Verse.Map

- `get_NextGenSeed`: System.HashCode.Combine

## Verse.PathFinder

- `FinalizeRecyclePathJobData`: System.HashCode.Combine
- `FindPathNow`: Unity.Jobs.IJobExtensions.Run
- `ScheduleBatchedPathJobs`: Unity.Jobs.IJobExtensions.Schedule, Unity.Jobs.JobHandle.ScheduleBatchedJobs
- `ScheduleGridJob`: Unity.Jobs.IJobParallelForExtensions.Schedule x2

## Verse.PathFinder+MapGridRequest

- `GetHashCode`: System.HashCode.Combine

## Verse.PathFinder+UnmanagedGridTraverseParams

- `GetHashCode`: System.HashCode.Combine

## Verse.PathFinderCostTuning

- `GetHashCode`: System.HashCode.Combine x2

## Verse.PathFinderMapData

- `GatherData`: System.Threading.Tasks.Parallel.ForEach x2

## Verse.PlayDataLoader

- `DoPlayLoad`: System.Threading.Tasks.Parallel.ForEach x2

## Verse.ShortHashGiver

- `GiveAllShortHashes`: System.Threading.Tasks.Parallel.ForEach

## Verse.SilhouetteUtility+SilhouetteCacheKey

- `GetHashCode`: Verse.Gender.GetHashCode, RimWorld.RotStage.GetHashCode

## Verse.SimplifiedPastureNutritionSimulator+CacheKey

- `GetHashCode`: System.HashCode.Combine

## Verse.StaticConstructorOnStartupUtility

- `ReportProbablyMissingAttributes`: System.Threading.Tasks.Parallel.ForEach

## Verse.TextureAtlasGroupKey

- `GetHashCode`: Verse.TextureAtlasGroup.GetHashCode

## Verse.WaterBody

- `GetHashCode`: Verse.WaterBodyType.GetHashCode

## Verse.XmlToObjectUtils+FieldAliasCache

- `GetHashCode`: System.HashCode.Combine

## Multiplayer assessment

Checked against RimWorld 1.7.4982 and this fork (task 8, Oct 2026). On .NET 10 `Enum.GetHashCode` gives the
same value in every process; `System.HashCode` does not (random seed per process), and `ValueTuple.GetHashCode`
goes through it, so tuples differ per process as well (corrected Oct 20: the earlier "stable" check ran while
the old process-wide `HashCode.Combine` patch was active). The game makes no direct `string.GetHashCode` call;
its runtimeconfig also turns on `WaylithClr.MonoStringHashCode`. "Handled" means an existing patch covers the
call site, provided the patch fires (see the "MP: patch hit counts" debug action; a "HashCode.Combine seed in
..." counter at 0 after a session that generated maps, gravships, trap checks or layouts means the patch didn't
run there). Hash values that are only lookup keys inside one process need no patch.

### System.HashCode

| Call site | Runs in | Assessment |
|---|---|---|
| `Verse.Map.get_NextGenSeed` (int x3) | simulation: seed for every map generation | handled by `SeedHashCombinePatch` |
| `RimWorld.Planet.Gravship.get_ConstantRandSeed` (int x2) | simulation: gravship rand seed | handled by `SeedHashCombinePatch` |
| `RimWorld.Building_Trap.KnowsOfTrap` (int x2, into `Rand.ValueAsync`) | simulation: trap avoidance roll | handled by `SeedHashCombinePatch` |
| `LayoutWorker.FillAllRooms` (int x2, into `RandBlock`) x3 | simulation: structure generation | handled by `SeedHashCombinePatch` |
| `RimWorld.Planet.FastTileFinder+CachedTileData.GetHashCode`, `+TileQueryParams.GetHashCode` | simulation: query cache keys | safe: dictionary lookups only, never iterated |
| `Verse.PathFinder.FinalizeRecyclePathJobData`, `+MapGridRequest`, `+UnmanagedGridTraverseParams`, `Verse.PathFinderCostTuning.GetHashCode` | simulation: pathfinder cache keys and a `Log.WarningOnce` key | safe: lookups only |
| `Verse.SimplifiedPastureNutritionSimulator+CacheKey.GetHashCode` | simulation: cache key | safe: lookups only |
| `Verse.GenStepWithParams.GetHashCode` (Def + struct) | simulation: map generation step list | safe: equality only; seed parts use `GenStep.SeedPart`, not this hash |
| `RimWorld.LayerConnection.GetHashCode` | world generation | safe: stored in lists, hash unused for ordering |
| `RimWorld.ScenPart_PlanetLayer.GetHashCode` | scenario fingerprint | UI only |
| `RimWorld.Toils_Ingest` lambda (Pawn, Thing) | simulation, but only as a `Log.WarningOnce` key | safe |
| `Verse.GenCollection.DictHashCode` | portrait cache parameters | UI only |
| `Verse.XmlToObjectUtils+FieldAliasCache.GetHashCode` | XML loading at startup | safe |

### Enum.GetHashCode (14 sites)

All safe: stable per value on .NET, same as Mono.

### Parallel work

| Call site | Runs in | Assessment |
|---|---|---|
| `RimWorld.Planet.FastTileFinder.Query`, `.Closest` (Unity job) | simulation: settlement/caravan tile picks | handled: one batch in MP (`FastTileFinderQueryDeterminismPatch`, now also `Closest`); the result array is race-filled otherwise |
| `RimWorld.Planet.FastTileFinder.RegenerateCache` (Parallel.For) | simulation | safe: writes one cache slot per index |
| `Verse.GlowGrid.GlowGridUpdate_First` (Unity job x2) | simulation (MP ticks it) | safe: per-cell output |
| `Verse.PathFinder.FindPathNow`, `ScheduleBatchedPathJobs`, `ScheduleGridJob`, `Gilzoide.ManagedJobs.ManagedJob` (path grid doors) | simulation | safe: each path job is single-threaded and MP completes scheduled path jobs every tick; grid jobs write per cell |
| `Verse.PathFinderMapData.GatherData` (Parallel.ForEach x2) | simulation | safe: per-cell gather |
| `Verse.ShortHashGiver.GiveAllShortHashes` (Parallel.ForEach) | startup | safe: parallel over def types, each type sorted by defName with its own taken set |
| `RimWorld.DefOfHelper`, `Verse.LoadedModManager`, `Verse.PlayDataLoader`, `Verse.StaticConstructorOnStartupUtility`, `Verse.GenThreading` callers (`DirectXmlCrossRefLoader`, def lists, `ThingCategoryNodeDatabase`), `Ionic.Zlib.ParallelDeflateOutputStream` | startup, loading, saving | safe |
| `Verse.DynamicDrawManager`, `Verse.FleckSystemBase.ForceDraw`, `Gilzoide.ManagedJobs.ManagedJobParallelFor` (`PreDrawThings`), `RimWorld.Dialog_SaveFileList` | rendering and UI | UI only |

No `Waylith.Jobs.WaylithJobRuntime` call in Assembly-CSharp: the game schedules through `Unity.Jobs`
(UnityStubs), which forwards to Waylith.
