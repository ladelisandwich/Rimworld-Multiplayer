# Real-time and randomness scan of Assembly-CSharp

Assembly-CSharp.dll 1.7.9787.38423, scanned 2026-10-19. 
Every method that reads frame or wall-clock time (Time, RealTime, DateTime, Environment.TickCount, Stopwatch) or uses unseeded randomness (System.Random, Guid.NewGuid).

| Category | Call sites | Types |
|---|---|---|
| Time.time | 127 | 68 |
| Time.frameCount | 97 | 47 |
| Wall clock | 56 | 19 |
| Time.deltaTime | 46 | 28 |
| Enum.GetHashCode | 14 | 13 |
| RealTime | 14 | 8 |

## Ionic.Zlib.GZipStream

- `EmitHeader`: System.DateTime.get_Now

## LudeonTK.DebugActionNode

- `DirtyLabelCache`: UnityEngine.Time.get_frameCount
- `get_LabelNow`: UnityEngine.Time.get_frameCount

## LudeonTK.Dialog_Debug

- `get_BoundingRectCached`: UnityEngine.Time.get_frameCount x2

## LudeonTK.Dialog_OptionLister

- `get_BoundingRectCached`: UnityEngine.Time.get_frameCount x2

## PerformanceBenchmarkUtility

- `CheckBenchmark`: UnityEngine.Time.get_realtimeSinceStartup x2, UnityEngine.Time.get_frameCount
- `StartBenchmark`: UnityEngine.Time.get_realtimeSinceStartup, UnityEngine.Time.get_frameCount

## RimWorld.Alert

- `Notify_Started`: UnityEngine.Time.get_realtimeSinceStartup x2

## RimWorld.AlertBounce

- `.ctor`: UnityEngine.Time.get_time
- `CalculateHorizontalOffset`: UnityEngine.Time.get_time x2
- `DoAlertStartEffect`: UnityEngine.Time.get_time

## RimWorld.Alert_Critical

- `AlertActiveUpdate`: UnityEngine.Time.get_frameCount x2

## RimWorld.AlertsReadout

- `AlertsReadoutUpdate`: UnityEngine.Time.get_frameCount x4

## RimWorld.ArchonexusCountdown

- `ArchonexusCountdownUpdate`: UnityEngine.Time.get_deltaTime

## RimWorld.Building_Bed

- `SetBedOwnerTypeByInterface`: UnityEngine.Time.get_frameCount x2

## RimWorld.CompAbilityEffect_Chunkskip

- `FindClosestChunks`: UnityEngine.Time.get_frameCount x2

## RimWorld.CompBiosculpterPod

- `CanReachRequiredIngredients`: UnityEngine.Time.get_realtimeSinceStartup

## RimWorld.CompProjectileInterceptor

- `GetCurrentAlpha_Idle`: UnityEngine.Time.get_realtimeSinceStartup
- `GetCurrentAlpha_Selected`: UnityEngine.Time.get_realtimeSinceStartup

## RimWorld.CompSightstealer

- `Notify_BecameVisible`: Verse.RealTime.get_LastRealTime x2

## RimWorld.CompSkyfallerRandomizeDirection

- `CompTick`: UnityEngine.Time.get_deltaTime

## RimWorld.Designator_MoveGravship

- `SelectedProcessInput`: UnityEngine.Time.get_realtimeSinceStartup x2

## RimWorld.Designator_Place

- `HandleRotationShortcuts`: UnityEngine.Time.get_realtimeSinceStartup x2

## RimWorld.Designator_Plan_Copy

- `SelectedProcessInput`: UnityEngine.Time.get_realtimeSinceStartup x2

## RimWorld.Designator_Plan_CopySelectionPaste

- `SelectedProcessInput`: UnityEngine.Time.get_realtimeSinceStartup x2

## RimWorld.Dialog_FormCaravan

- `FlashMass`: UnityEngine.Time.get_time

## RimWorld.Dialog_LoadTransporters

- `FlashMass`: UnityEngine.Time.get_time

## RimWorld.Dialog_Search`1

- `WindowUpdate`: UnityEngine.Time.get_frameCount

## RimWorld.Dialog_Trade

- `FlashSilver`: UnityEngine.Time.get_time

## RimWorld.ExternalHistoryUtility

- `GetCurrentUploadDate`: System.DateTime.get_UtcNow
- `GetCurrentUploadTime`: System.DateTime.get_UtcNow

## RimWorld.GameConditionManager+MapBrightnessTracker

- `Tick`: UnityEngine.Time.get_deltaTime

## RimWorld.GenWorld

- `MouseTile`: UnityEngine.Time.get_frameCount x4

## RimWorld.GeneGizmo_ResourceHemogen

- `GizmoOnGUI`: UnityEngine.Time.get_time

## RimWorld.GlobalControlsUtility

- `DoRealtimeClock`: System.DateTime.get_Now x2

## RimWorld.GravshipAudio+ManagedSustainer

- `Update`: UnityEngine.Time.get_deltaTime

## RimWorld.GravshipAudio+TriggeredOneOff

- `Trigger`: UnityEngine.Time.get_time
- `get_CurrentTimestamp`: UnityEngine.Time.get_time
- `get_Finished`: UnityEngine.Time.get_time

## RimWorld.GravshipRenderer

- `BeginUpdate`: UnityEngine.Time.get_deltaTime x2
- `DrawGravship`: UnityEngine.Time.get_deltaTime
- `UpdateTakeoff`: UnityEngine.Time.get_deltaTime

## RimWorld.ITab_Pawn_Log_Utility+LogLineDisplayableLog

- `Draw`: UnityEngine.Time.get_deltaTime

## RimWorld.InfestationCellFinder

- `DebugDraw`: UnityEngine.Time.get_frameCount

## RimWorld.LargeBuildingCellFinder

- `DebugDraw`: UnityEngine.Time.get_frameCount

## RimWorld.LearningReadout

- `LearningReadoutOnGUI`: UnityEngine.Time.get_realtimeSinceStartup
- `TryActivateConcept`: Verse.RealTime.get_LastRealTime

## RimWorld.Lesson

- `OnActivated`: UnityEngine.Time.get_realtimeSinceStartup
- `get_AgeSeconds`: UnityEngine.Time.get_realtimeSinceStartup x2

## RimWorld.LessonAutoActivator

- `LessonAutoActivatorUpdate`: UnityEngine.Time.get_frameCount, UnityEngine.Time.get_deltaTime x2

## RimWorld.ListerHaulables

- `DebugString`: UnityEngine.Time.get_frameCount

## RimWorld.ListerMergeables

- `DebugString`: UnityEngine.Time.get_frameCount

## RimWorld.MusicManagerEntry

- `MaintainSilence`: UnityEngine.Time.get_frameCount
- `MusicManagerEntryUpdate`: UnityEngine.Time.get_frameCount x2, UnityEngine.Time.get_deltaTime x2

## RimWorld.MusicManagerPlay

- `MusicUpdate`: UnityEngine.Time.get_time x2
- `UpdateFadeout`: UnityEngine.Time.get_deltaTime
- `UpdateSubtleAmbienceSoundVolumeMultiplier`: UnityEngine.Time.get_deltaTime x2
- `get_CurTime`: UnityEngine.Time.get_time
- `get_NextSongTimer`: UnityEngine.Time.get_time

## RimWorld.MutantCount

- `GetHashCode`: Verse.DevelopmentalStage.GetHashCode

## RimWorld.OverlayDrawHandler

- `DrawPowerGridOverlayThisFrame`: UnityEngine.Time.get_frameCount
- `DrawZonesThisFrame`: UnityEngine.Time.get_frameCount
- `get_ShouldDrawPowerGrid`: UnityEngine.Time.get_frameCount
- `get_ShouldDrawZones`: UnityEngine.Time.get_frameCount

## RimWorld.OverlayDrawer

- `RenderPulsingOverlayInternal`: UnityEngine.Time.get_realtimeSinceStartup

## RimWorld.Page_ModsConfig

- `DoModRow`: Verse.RealTime.get_LastRealTime x2

## RimWorld.PawnOverlayDrawer+CacheKey

- `.ctor`: RimWorld.PawnOverlayDrawer+OverlayLayer.GetHashCode

## RimWorld.PawnRoleSelectionWidgetBase`1

- `WindowUpdate`: UnityEngine.Time.get_deltaTime

## RimWorld.PlaceWorker_WatermillGenerator

- `DrawGhost`: UnityEngine.Time.get_realtimeSinceStartup

## RimWorld.Planet.Caravan_GotoMoteRenderer

- `OrderedToTile`: UnityEngine.Time.get_time
- `RenderMote`: UnityEngine.Time.get_time

## RimWorld.Planet.ExpandableLandmarksUtility

- `ExpandableLandmarksUpdate`: UnityEngine.Time.get_deltaTime

## RimWorld.Planet.ExpandableWorldObjectsUtility

- `ExpandableWorldObjectsOnGUI`: UnityEngine.Time.get_time
- `ExpandableWorldObjectsUpdate`: UnityEngine.Time.get_deltaTime x2

## RimWorld.Planet.WorldCameraDriver

- `CalculateCurInputDollyVect`: UnityEngine.Time.get_realtimeSinceStartup x2
- `Update`: UnityEngine.Time.get_deltaTime x2, UnityEngine.Time.get_time

## RimWorld.Planet.WorldDrawLayer_SettleTile

- `get_Alpha`: UnityEngine.Time.get_time

## RimWorld.Planet.WorldFeatures

- `UpdateAlpha`: UnityEngine.Time.get_deltaTime

## RimWorld.Planet.WorldSelectionDrawer

- `Notify_Selected`: UnityEngine.Time.get_realtimeSinceStartup

## RimWorld.PortraitsCache

- `Get`: UnityEngine.Time.get_time x2

## RimWorld.PortraitsCache+CachedPortrait

- `get_Expired`: UnityEngine.Time.get_time

## RimWorld.PortraitsCache+PortraitParams

- `.ctor`: Verse.PawnHealthState.GetHashCode

## RimWorld.PsychicEntropyGizmo

- `GizmoOnGUI`: UnityEngine.Time.get_time

## RimWorld.QualityRange

- `GetHashCode`: RimWorld.QualityCategory.GetHashCode

## RimWorld.QuickSearchUtility

- `get_CurrentSearchText`: UnityEngine.Time.get_frameCount

## RimWorld.ScenPart_MonolithGeneration

- `GetHashCode`: RimWorld.MonolithGenerationMethod.GetHashCode

## RimWorld.ScenPart_PawnModifier

- `GetHashCode`: RimWorld.PawnGenerationContext.GetHashCode

## RimWorld.ScenPart_PlayerPawnsArriveMethod

- `GetHashCode`: RimWorld.PlayerPawnsArriveMethod.GetHashCode

## RimWorld.Screen_Credits

- `PreOpen`: UnityEngine.Time.get_realtimeSinceStartup
- `WindowUpdate`: UnityEngine.Time.get_deltaTime x3, UnityEngine.Time.get_realtimeSinceStartup

## RimWorld.SelectionDrawer

- `Notify_Selected`: UnityEngine.Time.get_realtimeSinceStartup

## RimWorld.SelectionDrawerUtility

- `CalculateSelectionBracketPositionsUI`: UnityEngine.Time.get_realtimeSinceStartup
- `CalculateSelectionBracketPositionsWorld`: UnityEngine.Time.get_realtimeSinceStartup

## RimWorld.ShipCountdown

- `ShipCountdownUpdate`: UnityEngine.Time.get_deltaTime

## RimWorld.SocialCardUtility

- `CheckRecache`: UnityEngine.Time.get_frameCount

## RimWorld.StatRequest

- `GetHashCode`: RimWorld.QualityCategory.GetHashCode

## RimWorld.Storyteller

- `DebugString`: UnityEngine.Time.get_frameCount

## RimWorld.StorytellerUI

- `DrawStorytellerSelectionInterface`: UnityEngine.Time.get_time x2

## RimWorld.SubstructureGrid

- `DrawSubstructureCountOnGUI`: UnityEngine.Time.get_frameCount
- `DrawSubstructureFootprint`: UnityEngine.Time.get_frameCount x2

## RimWorld.TradeUI

- `DrawTradeableRow`: UnityEngine.Time.get_time

## RimWorld.TransferableUIUtility

- `DrawExtraInfo`: UnityEngine.Time.get_time

## RimWorld.UI_BackgroundMain

- `get_DeltaAlpha`: UnityEngine.Time.get_deltaTime

## RimWorld.XenotypeCount

- `GetHashCode`: Verse.DevelopmentalStage.GetHashCode

## Screen_ArchonexusSettlementCinematics

- `IsFinishedDisplayMessage`: UnityEngine.Time.get_realtimeSinceStartup
- `IsFinishedFadingIn`: UnityEngine.Time.get_realtimeSinceStartup
- `PreOpen`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.ArenaUtility+<>c__DisplayClass8_1

- `<PerformBattleRoyale>b__1`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.CameraDriver

- `CalculateCurInputDollyVect`: UnityEngine.Time.get_realtimeSinceStartup x2
- `GetExtraVelocityFromReleasingDragButton`: UnityEngine.Time.get_time x2
- `Update`: UnityEngine.Time.get_deltaTime, UnityEngine.Time.get_time
- `get_CurrentViewRect`: UnityEngine.Time.get_frameCount x2
- `get_HitchReduceFactor`: UnityEngine.Time.get_deltaTime x2

## Verse.CameraPanner

- `Update`: UnityEngine.Time.get_deltaTime

## Verse.CameraShaker

- `get_ShakeOffset`: UnityEngine.Time.get_realtimeSinceStartup x2

## Verse.CameraSwooper

- `Update`: UnityEngine.Time.get_deltaTime

## Verse.CellRenderer

- `InitFrame`: UnityEngine.Time.get_frameCount x2

## Verse.Command_Target

- `Update`: UnityEngine.Time.get_frameCount x2

## Verse.DebugInputLogger

- `InputLogOnGUI`: UnityEngine.Time.get_frameCount

## Verse.DesignationDragger

- `DraggerUpdate`: UnityEngine.Time.get_realtimeSinceStartup x3
- `UpdateDragCellsIfNeeded`: UnityEngine.Time.get_frameCount x2

## Verse.Dialog_MessageBox

- `.ctor`: Verse.RealTime.get_LastRealTime
- `get_TimeUntilInteractive`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.Dialog_NodeTree

- `.ctor`: Verse.RealTime.get_LastRealTime
- `get_InteractiveNow`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.Dialog_Rename`1

- `WasOpenedByHotkey`: UnityEngine.Time.get_frameCount
- `get_AcceptsInput`: UnityEngine.Time.get_frameCount

## Verse.Dialog_ResolutionConfirm

- `.ctor`: UnityEngine.Time.get_realtimeSinceStartup
- `get_TimeUntilRevert`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.FeedbackItem

- `Update`: UnityEngine.Time.get_deltaTime x2

## Verse.FleckManager

- `FleckManagerUpdate`: UnityEngine.Time.get_deltaTime

## Verse.FloatMenuMap

- `DoWindowContents`: UnityEngine.Time.get_frameCount

## Verse.FloatMenuWorld

- `DoWindowContents`: UnityEngine.Time.get_frameCount

## Verse.GUIEventFilterForOSX

- `CheckRejectGUIEvent`: UnityEngine.Time.get_frameCount x2
- `RejectEvent`: UnityEngine.Time.get_frameCount

## Verse.GameInfo

- `GameInfoOnGUI`: UnityEngine.Time.get_realtimeSinceStartup
- `GameInfoUpdate`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.GameplayTipWindow

- `DrawContents`: UnityEngine.Time.get_realtimeSinceStartup x2

## Verse.Gen

- `FixedTimeStepUpdate`: UnityEngine.Time.get_deltaTime

## Verse.GenDraw

- `get_CurTargetingColor`: UnityEngine.Time.get_time

## Verse.GenMath

- `TestMathPerf`: System.Diagnostics.Stopwatch.StartNew x6, System.Diagnostics.Stopwatch.get_ElapsedTicks x6

## Verse.GenText

- `MarchingEllipsis`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.GenUI

- `GetSizeCached`: UnityEngine.Time.get_frameCount

## Verse.GizmoGridDrawer

- `DrawGizmoGrid`: UnityEngine.Time.get_frameCount x2
- `DrawGizmoGridFor`: UnityEngine.Time.get_frameCount
- `get_HeightDrawnRecently`: UnityEngine.Time.get_frameCount

## Verse.Graphic+AtlasReplacementInfoCacheKey

- `.ctor`: Verse.TextureAtlasGroup.GetHashCode

## Verse.Graphic_MoteWithAgeSecs

- `DrawWorker`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.Letter

- `DrawButtonAt`: UnityEngine.Time.get_time x2

## Verse.LetterStack

- `ReceiveLetter`: UnityEngine.Time.get_time

## Verse.Listing_Standard

- `get_BoundingRectCached`: UnityEngine.Time.get_frameCount x2

## Verse.LogMessage

- `.ctor`: System.DateTime.get_Now x2

## Verse.LongEventHandler

- `UpdateCurrentEnumeratorEvent`: UnityEngine.Time.get_realtimeSinceStartup x2

## Verse.Message

- `Flash`: Verse.RealTime.get_LastRealTime
- `ResetTimer`: Verse.RealTime.get_LastRealTime
- `get_Age`: Verse.RealTime.get_LastRealTime

## Verse.Message+<>c__DisplayClass44_0

- `<Draw>b__0`: Verse.RealTime.get_LastRealTime x2

## Verse.MessagesRepeatAvoider

- `MessageShowAllowed`: Verse.RealTime.get_LastRealTime x2

## Verse.Mote

- `RealtimeUpdate`: UnityEngine.Time.get_deltaTime
- `SpawnSetup`: UnityEngine.Time.get_realtimeSinceStartup
- `get_AgeSecs`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.PawnGenerator

- `PawnGenerationHistogram`: System.Diagnostics.Stopwatch.GetTimestamp x2

## Verse.PawnHeadOverlays

- `RenderStatusOverlays`: UnityEngine.Time.get_time x2

## Verse.PerfLogger

- `Duration`: System.Diagnostics.Stopwatch.GetTimestamp
- `Record`: System.Diagnostics.Stopwatch.GetTimestamp
- `Reset`: System.Diagnostics.Stopwatch.GetTimestamp

## Verse.PerfTest

- `TestStandardMilliseconds`: System.Diagnostics.Stopwatch..ctor, System.Diagnostics.Stopwatch.Start, System.Diagnostics.Stopwatch.Stop, System.Diagnostics.Stopwatch.get_Elapsed, System.Diagnostics.Stopwatch.get_ElapsedMilliseconds

## Verse.ProfilerUI.ProfilerFlamechartWidget

- `AdvanceBandLayout`: System.Diagnostics.Stopwatch.GetTimestamp
- `LayoutBands`: System.Diagnostics.Stopwatch.GetTimestamp

## Verse.ProfilerUI.ProfilerRamTable

- `Export`: System.DateTime.get_Now
- `Refresh`: System.Diagnostics.Stopwatch.GetTimestamp

## Verse.ProfilerUI.ProfilerSession

- `ReportRefreshCost`: System.Diagnostics.Stopwatch.GetElapsedTime

## Verse.ProfilerUI.ProfilerThreadFilter

- `Refresh`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.ProfilerUI.ProfilerViewAnimation

- `Advance`: System.Diagnostics.Stopwatch.GetTimestamp
- `Start`: System.Diagnostics.Stopwatch.GetTimestamp

## Verse.ProfilerUI.ProfilerVramTable

- `Export`: System.DateTime.get_Now x2
- `Refresh`: System.Diagnostics.Stopwatch.GetTimestamp

## Verse.ProfilerUI.Window_FrameProfiler

- `RefreshSummary`: System.Diagnostics.Stopwatch.GetTimestamp
- `StartNavRepeat`: UnityEngine.Time.get_realtimeSinceStartup
- `UpdateNavRepeat`: UnityEngine.Time.get_realtimeSinceStartup x2
- `WindowUpdate`: System.Diagnostics.Stopwatch.GetTimestamp

## Verse.Pulser

- `PulseBrightness`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.Rand

- `.cctor`: System.DateTime.get_Now
- `RandTests`: System.Diagnostics.Stopwatch..ctor, System.Diagnostics.Stopwatch.Start, System.Diagnostics.Stopwatch.Stop, System.Diagnostics.Stopwatch.get_ElapsedMilliseconds

## Verse.RealTime

- `Update`: UnityEngine.Time.get_frameCount, UnityEngine.Time.get_deltaTime x3, UnityEngine.Time.get_realtimeSinceStartup

## Verse.Region

- `DebugDrawMouseover`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.RoomTempTracker

- `DebugString`: UnityEngine.Time.get_frameCount x2

## Verse.Root

- `Update`: UnityEngine.Time.get_frameCount

## Verse.ScreenFader

- `get_CurTime`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.ScreenshotTaker

- `TakeNonSteamShot`: UnityEngine.Time.get_frameCount
- `Update`: UnityEngine.Time.get_frameCount

## Verse.SilhouetteUtility+SilhouetteCacheKey

- `GetHashCode`: Verse.Gender.GetHashCode, RimWorld.RotStage.GetHashCode

## Verse.Sound.MouseoverSounds

- `ResolveFrame`: UnityEngine.Time.get_frameCount
- `SilenceForNextFrame`: UnityEngine.Time.get_frameCount

## Verse.Sound.Sample

- `.ctor`: UnityEngine.Time.get_realtimeSinceStartup, UnityEngine.Time.get_frameCount
- `get_AgeRealTime`: UnityEngine.Time.get_realtimeSinceStartup
- `get_ContextVolumeMultiplier`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.Sound.SampleOneShotManager+<>c__DisplayClass7_0

- `<CanAddPlayingOneShot>b__0`: UnityEngine.Time.get_frameCount

## Verse.Sound.SampleSustainer

- `get_Volume`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.Sound.SoundParamSource_Perlin

- `ValueFor`: UnityEngine.Time.get_realtimeSinceStartup x2

## Verse.Sound.SoundParamSource_SourceAge

- `ValueFor`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.Sound.SoundSlotManager

- `CanPlayNow`: UnityEngine.Time.get_realtimeSinceStartup
- `Notify_Played`: UnityEngine.Time.get_realtimeSinceStartup x2

## Verse.Sound.SubSustainer

- `<.ctor>b__12_0`: UnityEngine.Time.get_frameCount, UnityEngine.Time.get_realtimeSinceStartup x2
- `StartSample`: UnityEngine.Time.get_realtimeSinceStartup x3, UnityEngine.Time.get_frameCount
- `SubSustainerUpdate`: UnityEngine.Time.get_realtimeSinceStartup x2

## Verse.Sound.Sustainer

- `.ctor`: UnityEngine.Time.get_frameCount
- `<.ctor>b__15_0`: UnityEngine.Time.get_frameCount
- `End`: UnityEngine.Time.get_realtimeSinceStartup
- `Maintain`: UnityEngine.Time.get_frameCount
- `SustainerUpdate`: UnityEngine.Time.get_frameCount
- `get_TimeSinceEnd`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.Steam.SteamDeck

- `HandleTouchScreenScrollViewScroll`: UnityEngine.Time.get_time x2, UnityEngine.Time.get_frameCount x2, UnityEngine.Time.get_deltaTime

## Verse.Steam.Workshop

- `Upload`: System.DateTime.get_Now

## Verse.TextureAtlasGroupKey

- `GetHashCode`: Verse.TextureAtlasGroup.GetHashCode

## Verse.ThreadLocalDeepProfiler

- `End`: System.Diagnostics.Stopwatch.Stop

## Verse.ThreadLocalDeepProfiler+Watcher

- `.ctor`: System.Diagnostics.Stopwatch.StartNew
- `get_ElapsedMilliseconds`: System.Diagnostics.Stopwatch.get_Elapsed

## Verse.TickManager

- `.ctor`: System.Diagnostics.Stopwatch..ctor
- `TickManagerUpdate`: UnityEngine.Time.get_deltaTime x2, System.Diagnostics.Stopwatch.Reset, System.Diagnostics.Stopwatch.Start, System.Diagnostics.Stopwatch.get_Elapsed x2, System.Diagnostics.Stopwatch.get_ElapsedMilliseconds

## Verse.TooltipHandler

- `DrawActiveTips`: UnityEngine.Time.get_realtimeSinceStartup
- `TipRegion`: UnityEngine.Time.get_realtimeSinceStartup

## Verse.UIHighlighter

- `HighlightOpportunity`: UnityEngine.Time.get_frameCount
- `HighlightTag`: UnityEngine.Time.get_frameCount x2

## Verse.UIHighlighter+<>c

- `<UIHighlighterUpdate>b__5_0`: UnityEngine.Time.get_frameCount

## Verse.UnityGUIBugsFixer

- `EnsureFrameMouseDelta`: UnityEngine.Time.get_frameCount
- `FixDelta`: UnityEngine.Time.get_frameCount x6
- `get_CurrentEventDelta`: UnityEngine.Time.get_frameCount x2

## Verse.WaterBody

- `GetHashCode`: Verse.WaterBodyType.GetHashCode

## Verse.Widgets

- `CheckPlayDragSliderSound`: UnityEngine.Time.get_realtimeSinceStartup x2

## Verse.WindowStack

- `Notify_GameStartDialogClosed`: UnityEngine.Time.get_time
- `get_SecondsSinceClosedGameStartDialog`: UnityEngine.Time.get_time

## Verse.WorkTypeDef

- `get_VisibleCurrently`: UnityEngine.Time.get_frameCount x2

## Verse.WorldComponent_GravshipController

- `DrawGravship`: UnityEngine.Time.get_deltaTime
- `WorldComponentUpdate`: UnityEngine.Time.get_deltaTime

