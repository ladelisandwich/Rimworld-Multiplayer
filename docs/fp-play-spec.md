# First-person play (F5): binding implementation spec

This spec merges three designs: D1 (sync-first), D2 (feel-first) and D3 (incremental plan). It is binding. If an implementer finds a fact here wrong against 1.7.4990, they stop and tell the integrator. They don't work around it inside their own file.

- Repo: `/home/ahearn/Documents/Rimworld-Multiplayer`, branch `port-1.7`.
- Game: 1.7.4990.
- Decompile used: `scratchpad/acs/` (Assembly-CSharp) and `scratchpad/fp/` (UnityStubs, RimWorld.dll, Waylith).

---

## 0. Scores of the three designs (out of 10)

| Criterion | D1 sync-first | D2 feel-first | D3 incremental |
|---|---|---|---|
| Multiplayer correctness | **9** | 7 | 7 |
| Player feel | 7 | **9** | 6 |
| Feasibility on 1.7, Waylith and Harmony 3 | 7 | 6 | **8** |
| Scope control | 6 | 4 | **9** |
| Testability | 6 | 5 | **9** |
| **Total** | 35 | 31 | **39** |

**D1, sync-first.**
- Strengths:
  - The best simulation model: checks scheduled in the simulation, saved state, a server-stamped player id.
  - Release on PlayerResetGlobal, an order guard, resume after reload, a registry rebuilt on load.
- Weaknesses:
  - The hold patches the whole `JobDriver.DriverTick`, which also stops Delay toils, and reaches a private method through a delegate.
  - It schedules from `TickRateMultiplier` (see the determinism note in §1).
  - Space is always taken and needs a pause hook into Multiplayer.
  - Esc only leaves.
  - Wide scope (XP grants, many tables) and thin acceptance tests.

**D2, feel-first.**
- Strengths: an Esc panel, Space only during a check, a camera lead to hide latency, prompts, hold-to-stop.
- Weaknesses:
  - A view model, hit markers, 64 headings, a dead-man switch and a "normal speed" time vote that slows every player.
  - `GetOptions` at 2 Hz through a custom provider.
  - Shifts by a fraction of total work, so the effect depends on how many checks a job gets.
  - Too much for a first build, and thin on testing.

**D3, incremental.**
- Strengths: the clearest file ownership, Port-line list, harness, scene, compare script and per-milestone scenarios.
- Weaknesses:
  - The UI schedules checks, so the simulation can't see or save a pending check and results can arrive after the work is done.
  - Pace windows on `WorkSpeedGlobal` miss mining and depend on when stats are read.
  - 8-way movement, Esc leaves, a P pause key.

**What the merged spec takes from each:**
- D3's structure, ownership, harness and milestone discipline.
- D1's simulation model, commands, lifecycle and multiplayer hooks.
- D2's feel decisions: optimistic entry, Esc panel, Space rule, the camera lead later.

The details of each choice are in §1.

---

## 1. Decisions and conflict resolutions

| Topic | Decision (winner) | Why |
|---|---|---|
| Where synced state lives | **D1:** `FpMapComp : MapComponent`, plus a static `FpRegistry` index rebuilt on load | Release, hold and scheduling run in that map's tick context (its Rand and TicksGame) and are saved with the map, which is safe under async time. D2 and D3's GameComponent would need a per-map sweep anyway. |
| Who sent a command | **All three:** `TickPatch.currentExecutingCmdPlayerId` in Multiplayer.dll, read through a by-name hook | The server stamps it (`CommandHandler.cs:45`), so a client can't spoof it. |
| Movement encoding | **D1:** 16 sectors with alternating steps. The re-request timing is new (below). | D3's 8 ways snap to 45°. D2's 64-heading line walker is more code for little gain. |
| When to re-request a path | **New:** re-request when the pawn is at least half-way into `nextCell`, or at once when the direction changes | All three designs called `StartPath` when the pawn entered a cell. `StartPath` calls `ResetToCurrentPosition` when `Mathf.Approximately(nextCellCostLeft, nextCellCostTotal)` (`Pawn_PathFollower.cs:175-177`), which drops the step just started. That would cost about 1 tick per cell and stutter. |
| Check scheduling | **D1:** in the simulation, by progress fraction, with a hold and a deadline | A friend can see a pending check, it survives join points, the number of checks per job is capped, and every check counts on short jobs. |
| Game-speed factor for scheduling | **New:** a constant table on the map's `CurTimeSpeed` | `TimeSnapshot.GetAndSetFromMap` (`Source/Client/AsyncTime/SetMapTime.cs:240`) swaps in the map's synced `DesiredTimeSpeed`. `TickRateMultiplier` also depends on slow-down and nothing-happening caches, so D1's use of it is replaced. |
| Hold | **D1, narrowed** to a prefix on `JobDriver.DriverTickInterval` only | Every work type that can be held advances in its `tickIntervalAction`: `Toils_Recipe.cs:90`, `JobDriver_ConstructFinishFrame.cs:44`, `JobDriver_Mine.cs:46`, `JobDriver_PlantWork.cs:55`, `JobDriver_PlantSow.cs:48`, `JobDriver_AffectFloor.cs:33`, `JobDriver_SmoothWall.cs:34`, `JobDriver_RemoveBuilding.cs:50`. `DriverTick` (fail checks, Delay countdown) keeps running. `DriverTickInterval` has a try block, so it can't be inlined. Repair works in its `tickAction` (`JobDriver_Repair.cs:46`), so it is never held. |
| Speed effect | **D1:** grants as a share of the segment | Independent of the number of checks. Hard caps: +40% speed or −30% of work. D3's pace StatPart misses mining (`ticksToPickHit` is reset from MiningSpeed) and depends on when stats are read. |
| Outcome levers | **D2's single form score F (−4..+4), with D1's integer formula and D3's dilution** | One number for the HUD and every lever. Integer maths with constant tables: no Pow, Exp or trig in the simulation. |
| Ring look | **D1:** a spark needle, a sweet spot in the middle of the arc, centred on the crosshair | Our own look, and the eye is already at the crosshair. |
| Space | **D2:** Space is the check key only from the warning until 0.6 s after a result; otherwise it pauses as in vanilla | Keeps the vanilla pause habit and needs no pause hook into Multiplayer. Used only while a check shows, so an accidental pause vote is unlikely. |
| Esc | **D2:** Esc opens a first-person panel (Resume / Leave / Mod settings / Game menu) | The user asked for an Esc menu. |
| Entry | **D2:** optimistic (camera and capture at once, intents queued behind `FpSetControl`), plus **D1's** confirmation timeout | No input lag on entry. Commands from one client run in order. |
| Mental state / Lord / needs | **D1:** a mental state releases control. **D2/D3:** a Lord puts the pawn on autopilot. **D2:** AutoNeeds autopilot, a synced flag, in M2 | Rituals end and control should come back. A starving pawn would otherwise die. |
| Firing | **D1:** press sends `FpFire(hold:true)`, release sends `FpStopFire`, which caps attacks at what has already started | A quick click always gets its burst. No timing differences from tap detection. |
| Reach | 2.5 cells, or 1.5 from an interaction cell (D1). The right-click menu works at any distance (D2). | You walk up to things, and the right-click menu is the escape hatch. |
| Bills | Index-0 restricted bill with radius 20 (D1). `job.bill == bill` check (D3). "Continue" through `FpWorkBill` (D2). | |
| Resume after reload | **All:** in M1, not later | In Multiplayer every join point reloads every client (CLAUDE.md, commit 0977f96b). Without resume, first person would end on every join and autosave join point. |
| Order guard | **D1**, in M6 | Hardening, not needed for M1 to be usable. |
| Harness | **D3's FpTestKit**, plus a Multiplayer-side test host and a headless host | The camera-test pattern (`scratchpad/CamTest.cs`: `ImguiDispatch.DispatchInput` prefix plus `InputSystem.Queue*`) is proven. |
| Rejected | P pause key (D1, D3), dead-man switch (D2), "normal speed in FP" vote (D2), view model and hit markers (D2), sprint (all), `FloatMenuOptionProvider_FpUse` (D2), UI-scheduled checks and pace windows (D3), XP bonus (D1: the table stays in tuning at 0), tending and research checks (not in the user's list) | Scope, determinism or balance. |

### Corrections to the research reports (checked in the decompile)
- **`JobDriver_PlantWork.workDone` is `private`, not protected** (`JobDriver_PlantWork.cs:12`). Use `AccessTools.FieldRefAccess`.
- **A prefix on `UIRoot_Play.UIRootOnGUI` runs before `WindowStack.HandleEventsHighPriority`** (inside `base.UIRootOnGUI`, `UIRoot.cs:52`). So a window's own Esc does not "win" (D3 assumed it did). The capture rule (§7.2) makes sure no such window is open while the cursor is captured.
- **`PathUtility.BlocksDiagonalMovement` takes a `PathingContext`** (`PathUtility.cs:137`). The diagonal rule in §5.5 uses the cardinal-neighbour test instead.
- **`FloatMenuMakerMap.GetOptions` can post a `RejectInput` message** (`FloatMenuMakerMap.cs:46-49`). Call `ShouldGenerateFloatMenuForPawn` first when only computing hints.

---

## 2. Architecture

There are two layers, and they never mix.

| Local, per player | Synced simulation |
|---|---|
| Camera (yaw and pitch), captured cursor, key and mouse polling, crosshair ray, hints, menus, recipe picker, skill-check ring and its randomness, sounds, HUD, settings | Who controls which pawn and with which flags, the movement intent, idle facing, orders (vanilla synced calls plus our commands), check schedule and pending check, hold, check results and their effects, tallies, first-person bills |

**Rules for simulation code.** Simulation code is command bodies, map ticks, job drivers, StatParts and quality patches. It never reads:
- `Input`, `Time`, `View3D`, `FirstPerson`, `FpPlay`, `FpInput` or `FpCamera`;
- IsoRim settings or `FpSettings`;
- `FpIds.Local` or `MP.GetPlayers()`;
- `Prefs`, except for dev-only logging.

It also obeys these rules:
- No `Verse.Rand` (this feature needs none in the simulation).
- No Pow, Exp or trig: use the constant tables in `FpMath` and `FpTuningDef`.
- No iteration over a `Dictionary` or `HashSet` to make a decision. Lists are sorted by `thingIDNumber`, or by key for tallies.

**Rules for UI code.**
- It never writes game state. Every change goes through a vanilla synced call or one of our `[SyncMethod]`s.
- Every UI sender starts with `if (MpBridge.InSession && !MP.InInterface) return;`.

### 2.1 Files and owners

All new IsoRim-side code is in namespace `IsoRim.Fp`, under `Source/IsoRim/Fp/`, compiled into IsoRim.dll. Tracks:
- **I:** integrator.
- **H:** Multiplayer.dll side.
- **A:** input, camera, HUD.
- **B:** simulation core.
- **C:** picking and interaction.
- **D:** bills and orders.
- **E:** skill-check UI.
- **F:** skill-check simulation and levers.
- **G:** combat.

A track only edits its own files. In M1.0, I creates every file below with the public signatures from §3 as compiling stubs. Stubs return `false`, `null` or `0`, or do nothing. A signature change goes through I.

| Path | Owner | Milestone | Contents |
|---|---|---|---|
| `Source/IsoRim/IsoRim.csproj` | I | M1.0 | add `<Compile Include="Fp/**/*.cs" />` |
| `Fp/FpHooks.cs` | I | M1.0 | Facade for the Port lines (§4.1), `InstallAll(Harmony)`, `Patch(...)` helper, `FpHits` counters, dev actions |
| `Fp/FpIds.cs` | I | M1.0 | `FpIds`, `FpMpHooks`, `FpSyncRegistration` (`MP.RegisterAll`) |
| `Fp/FpSettings.cs` | I | M1.0 | per-player settings, `Expose()`, `DrawSection()` (§13.1) |
| `Fp/FpTuning.cs` | I | M1.0 | `FpTuningDef`, `FpTuning.Get` |
| `Fp/FpMath.cs` | I | M1.0 | all pure functions (§3.9), linked into `Source/Tests` |
| `Fp/FpKeys.cs` | I | M1.0 | `[DefOf] FpKeyDefOf` |
| `Fp/FpPlay.cs` | A | M1 | mode state machine, entry and exit, confirmation, resume, reselect |
| `Fp/FpCamera.cs` | A | M1 | play-mode yaw and pitch; eye offset (M6) |
| `Fp/FpInput.cs` | A | M1 | cursor watchdog, UIRootOnGUI, DispatchInputToWindows and HandleMapClicks prefixes, look, intent sending |
| `Fp/FpMenu.cs` | A | M1 | `Dialog_FpMenu` (the Esc panel) |
| `Fp/FpHud.cs` | A | M1, M6 | UIRootOnGUI postfix: crosshair, hint, status, cheat sheet, toasts. Calls E and G draw hooks. |
| `Fp/FpMapComp.cs` | B | M1 | `FpEntry`, `FpTally`, `FpMapComp`, `FpRegistry`, `FpState` |
| `Fp/FpSync.cs` | B | M1, M6 | `FpSetControl`, `FpSetIntent`, `FpSetFacing` (M6) and their UI senders |
| `Fp/FpControlJob.cs` | B | M1, M2, M6 | `JobDriver_FpControl`, `JobGiver_FpControl`, `FpStep`, CheckForJobOverride prefix, Game ctor postfix, order guard (M6), AutoNeeds (M2) |
| `Fp/FpPick.cs` | C | M1 | ray picker, `Current`, pointer substitution |
| `Fp/FpActions.cs` | C | M1 | LMB, RMB, hints, pinned menu point |
| `Fp/FpRecipeWindow.cs` | D | M2 | `Dialog_FpRecipes` |
| `Fp/FpBills.cs` | D | M2 | `FpStartRecipe`, `FpWorkBill`, bill clean-up, `TryOpenPicker` |
| `Fp/FpOrders.cs` | D | M4 | `FpOrderWork`, `TryOrderWork`, `OrderHint` |
| `Fp/FpSkillCheck.cs` | E | M3, M6 | the ring, result pop, progress bar, friend icon (M6) |
| `Fp/FpWork.cs` | F | M3 | tracking, scheduling, deadline, hold prefix, `FpCheckResult`, tally maintenance |
| `Fp/FpAdapters.cs` | F | M3 (Bill), M4 (the rest) | `IFpWorkAdapter` implementations |
| `Fp/FpLevers.cs` | F | M3, M4 | `StatPart_FpForm`, quality patches, StatPart injection |
| `Fp/FpCombat.cs` | G | M5 | `FpFire`, `FpStopFire`, aim, reticle |
| `Fp/Test/FpTestKit.cs`, `Fp/Test/FpScene.cs` | I | M1.0 | harness core and scene builder (§15) |
| `Fp/Test/FpTestM1.cs` … `FpTestM6.cs` | M1: I, M2: D, M3: E+F, M4: F, M5: G, M6: B | per milestone | scenarios |
| `Source/IsoRim/Defs/IsoRimFp_Keys.xml` | I | M1.0 | key category and bindings (§12) |
| `Source/IsoRim/Defs/IsoRimFp_Tuning.xml` | I (values from §13.2) | M1.0 | `IsoRimFp_Tuning` |
| `Source/IsoRim/Defs/IsoRimFp_Control.xml` | B | M1 | JobDef and ThinkTreeDef (§5.2) |
| `Source/IsoRim/Languages/English/Keyed/IsoRimFp_<Track>.xml` | each track | own milestone | keys `R3D_Fp_*`, one file per track: Core (I), Input (A), Control (B), Actions (C), Bills (D), Check (E), Work (F), Combat (G) |
| `Source/IsoRim/MpView.cs` | I | M1.0 | one line (§4.1, line 16) |
| `scripts/common.sh` (`stage_isorim`) | I | M1.0 | 3 lines (§4.3) |
| `scripts/fp_selftest.py`, `scripts/fp_compare.py` | I | M1.0 | runners (§15) |
| `Source/Tests/FpMathTest.cs`, `Source/Tests/Tests.csproj` (link `..\IsoRim\Fp\FpMath.cs`) | I | M1.0 | unit tests (§3.9) |
| `Source/Client/UI/IsoRimFp.cs` (new), `Patches/TickPatch.cs`, `AsyncTime/AsyncTimeComp.cs`, `AsyncTime/AsyncWorldTimeComp.cs`, `Networking/HostUtil.cs`, `Desyncs/PawnStateTrace.cs` | H | M1.0 | §4.2 |
| `Source/Client/Diag/FpSelfTestHost.cs` (new) | H | M1.0 | test host (§15.3) |
| `CLAUDE.md` | I | end of each milestone | Progress and Known gaps |

---

## 3. Interfaces (exact signatures; I writes them as stubs in M1.0)

```csharp
namespace IsoRim.Fp
{
    // ---------------- FpIds.cs (I) ----------------
    public static class FpIds
    {
        public const int SinglePlayer = -2;   // controller id outside a session
        public const int None = -1;           // no controller / no command running (in a session)
        public static int Executing { get; }  // !MpBridge.InSession ? SinglePlayer : (FpMpHooks bound ? executing() : None)
        public static int Local { get; }      // !MpBridge.InSession ? SinglePlayer : (bound ? local() : None)
        public static bool PlayOff { get; }   // InSession && (!FpMpHooks.Bound || playOff())  (ghost mode, replay, arbiter)
        public static string NameOf(int id);  // UI only: MP.GetPlayers() Username by Id, "?" if unknown, "you" if id == Local
    }
    public static class FpMpHooks             // Multiplayer.dll binds this by name (§4.2)
    {
        public static bool Bound { get; }
        public static void Install(Func<int> executingPlayerId, Func<int> localPlayerId, Func<bool> playOff);
        public static void OnPlayerLeft(int playerId);   // world command context, data only -> FpMapComp.ReleaseAllOf
        public static void OnSessionReset();             // data only -> FpMapComp.ResetAll
        public static string Describe(Pawn pawn);        // -> FpState.Describe ("" when not involved)
    }
    [StaticConstructorOnStartup] internal static class FpSyncRegistration
    { static FpSyncRegistration() { if (MP.enabled) MP.RegisterAll(typeof(FpSyncRegistration).Assembly); } }

    // ---------------- FpHooks.cs (I) ----------------
    public static class FpHooks               // everything here only forwards to the owning module
    {
        public static void InstallAll(Harmony h);         // calls FpInput.Install, FpHud.Install, FpControlJob.Install,
                                                          // FpWork.Install, FpLevers.Install, FpTestKit.Install, in this order
        public static void OnRootUpdate();                // -> FpInput.OnRootUpdate
        public static bool PointerMapPos(Vector2 ui, ref Vector3 result);              // -> FpPick
        public static bool ThingsUnderCrosshair(ref List<Thing> result, TargetingParameters parms); // -> FpPick
        public static bool HidesBracket(object thing);    // -> FpPlay
        public static bool LookFrame();                   // -> FpInput
        public static bool PlayActive { get; }            // FpPlay.Mode == FpMode.Playing
        public static bool Toggle();                      // -> FpPlay.TryToggle
        public static void CameraLeft(Pawn was);          // -> FpPlay
        public static void CameraReset(Pawn was);         // -> FpPlay (may run off the main thread: stores ints only)
        public static bool Steering { get; }              // PlayActive && FirstPerson.On
        public static float Yaw { get; }                  // FpCamera.Yaw
        public static float Pitch { get; }                // FpCamera.Pitch
        public static Vector3 EyeOffset { get; }          // FpCamera.EyeOffset (zero until M6)
        public static void ExposeSettings();              // FpSettings.Expose
        public static void DrawSettings(Listing_Standard l); // FpSettings.DrawSection
        public static bool PointerMapPoint(out Vector3 p);   // -> FpPick
        internal static bool Patch(Harmony h, string what, MethodBase target,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null);
            // null target or exception: Log.Warning("[IsoRim] First-person hook missing: " + what) once, returns false
    }
    public static class FpHits { public static void Hit(string name); public static string Dump(); public static void Reset(); }
    // + [DebugAction("IsoRim", "FP: patch hit counts")] logs Dump(); [DebugAction("IsoRim", "FP: reset hit counts")]

    // ---------------- FpPlay.cs (A) ----------------
    public enum FpMode : byte { Off, Playing }
    public static class FpPlay
    {
        public static FpMode Mode { get; }
        public static Pawn Pawn { get; }            // non-null while Playing
        public static bool Confirmed { get; }       // FpState.LocalControls(Pawn) has been seen since entry
        public static bool Controllable(Pawn p, out string reason);   // UI-side F5 test, §6.1
        public static void Leave(string message);   // UI: send FpSetControl(off) if needed, then FirstPerson.Leave(message)
        internal static bool TryToggle();
        internal static void CameraLeft(Pawn was);
        internal static void CameraReset(Pawn was);
        internal static bool HidesBracket(object thing);
        internal static void OnRootUpdate();        // arming, confirmation, auto-leave, resume, reselect (§6)
    }
    // ---------------- FpCamera.cs (A) ----------------
    public static class FpCamera
    {
        public static float Yaw { get; }            // degrees clockwise from north, 0..360
        public static float Pitch { get; }          // degrees below the horizon, clamped -80..80
        public static Vector3 EyeOffset { get; }    // M6: lead + bob; zero before
        public static void Begin(float yaw);        // pitch 8
        public static void Look(float dYaw, float dPitch);
    }
    // ---------------- FpInput.cs (A) ----------------
    public static class FpInput
    {
        public static bool Captured { get; }
        public static bool FreeCursorHeld { get; }
        public static bool PointerOverride { get; }   // Captured || FpActions.MenuOpen
        public static double FrameInputTime { get; } // Time.realtimeSinceStartupAsDouble at the first UIRootOnGUI event of this frame
        public static byte LastSentIntent { get; }
        public static bool TestForceFocus;            // harness only
        internal static void Install(Harmony h);
        internal static void OnRootUpdate();
        internal static bool LookFrame();
    }
    public class Dialog_FpMenu : Window { }         // FpMenu.cs (A)
    public static class FpHud                       // FpHud.cs (A)
    {
        public static void Toast(string text, Color color, float seconds = 1.5f);
        internal static void Install(Harmony h);    // UIRoot_Play.UIRootOnGUI postfix
    }

    // ---------------- FpPick.cs (C) ----------------
    public struct FpHit
    {
        public bool Valid; public Thing Thing; public IntVec3 Cell;
        public Vector3 Point;      // map space (x, 0, z), inside the thing's rect for things
        public float Dist; public bool InReach;
    }
    public static class FpPick
    {
        public static FpHit Current { get; }        // recomputed once per Time.frameCount while Mode == Playing
        public static FpHit Ray(Map map, Vector3 eye, Vector3 dir, float maxCells, Pawn ignore, float pawnRadiusScale = 1f);
        public static bool InReach(Pawn p, Thing t, IntVec3 cell);
        internal static bool PointerMapPos(Vector2 ui, ref Vector3 result);
        internal static bool ThingsUnderCrosshair(ref List<Thing> result, TargetingParameters parms);
        internal static bool PointerMapPoint(out Vector3 p);
    }
    // ---------------- FpActions.cs (C) ----------------
    public enum FpHintKind : byte { None, Action, TooFar, Invalid }
    public static class FpActions
    {
        public static bool MenuOpen { get; }
        public static Vector3 MenuPoint { get; }
        public static string Hint { get; }
        public static FpHintKind HintKind { get; }
        internal static void Update();              // from FpInput.OnRootUpdate
        internal static void PrimaryDown();         // LMB down while captured
        internal static void PrimaryUp();
        internal static void Secondary();           // RMB down while captured
    }

    // ---------------- FpMapComp.cs (B) ----------------
    public static class FpDir   { public const byte Stop = 255; }          // sectors 0..15 (FpMath)
    public static class FpFlags { public const byte AutoNeeds = 1, Checks = 2, Force = 4; }
    public static class FpOutcome { public const byte MissTimeout = 0, Miss = 1, Hit = 2, Great = 3; }
    public sealed class FpEntry : IExposable { /* fields in §9.1 */ }
    public sealed class FpTally : IExposable
    {
        public int key; public Thing target; public Pawn owner; public int jobLoadId;
        public int great, hit, miss, lastTick;
        public int Count => great + hit + miss;
        public int Form => FpMath.Form(great, hit, miss);
    }
    public sealed class FpMapComp : MapComponent
    {
        public readonly List<FpEntry> entries;   // sorted by pawn.thingIDNumber
        public readonly List<FpTally> tallies;   // sorted by key
        public readonly List<Bill> fpBills;      // bills FP created on this map (D maintains)
        public FpMapComp(Map map);
        public static FpMapComp For(Map map);    // cached lookup, null for null map
        public FpEntry Get(Pawn p);
        public FpEntry Control(Pawn p, int player, Faction faction, byte flags);  // add or update; keeps sort; registry
        public void Release(FpEntry e, bool countPendingAsMiss);  // map context only (§5.6)
        public FpTally TallyByKey(int key);
        public FpTally GetOrAddTally(int key, Thing target, Pawn owner, int jobLoadId);
        public void RemoveTally(FpTally t);
        public static void ReleaseAllOf(int playerId);   // data only, every map (world context safe)
        public static void ResetAll();                    // data only
    }
    public static class FpRegistry   // derived index; lookups only, never iterated for decisions
    {
        public static int ControlledCount { get; }
        public static int HeldCount { get; }
        public static int TallyCount { get; }
        public static FpEntry EntryOf(Pawn p);
        public static FpTally TallyForWorker(Pawn p);    // via FpWork.TallyKeyOf(p, p.CurJob)
        public static void SetHeld(FpEntry e, bool held);
        internal static void Rebuild();                  // from every map's FpMapComp
        internal static void Clear();                    // Game ctor postfix
    }
    public static class FpState      // read-only queries (UI and sim)
    {
        public static int ControllerOf(Pawn p);          // FpIds.None if not controlled
        public static bool LocalControls(Pawn p);        // ControllerOf(p) == FpIds.Local && Local != None
        public static string AutopilotReason(Pawn p);    // null if under control, else a translated reason
        public static string Describe(Pawn p);           // §9.3
    }
    // ---------------- FpSync.cs (B) ----------------
    public static class FpSync
    {
        public static void SendSetControl(Pawn p, bool on, byte flags);   // UI senders: InSession && !InInterface -> return
        public static void SendIntent(Pawn p, byte dir);
        public static void SendFacing(Pawn p, byte rot);                   // M6
        [SyncMethod(cancelIfAnyArgNull = true)] [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpSetControl(Pawn p, bool on, byte flags);
        [SyncMethod(cancelIfAnyArgNull = true)] [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpSetIntent(Pawn p, byte dir);
        [SyncMethod(cancelIfAnyArgNull = true)] [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpSetFacing(Pawn p, byte rot);                  // M6 (stub body in M1)
    }
    // FpControlJob.cs (B): JobDriver_FpControl, JobGiver_FpControl, static class FpControlJob { internal static void Install(Harmony h); }

    // ---------------- FpWork.cs / FpAdapters.cs / FpLevers.cs (F) ----------------
    public interface IFpWorkAdapter
    {
        string Name { get; }
        bool Matches(JobDriver d);           // type test only
        bool InWorkToil(JobDriver d);        // the skilled toil is the current toil
        float Total(JobDriver d);            // work units
        float Done(JobDriver d);             // 0..Total
        void Add(JobDriver d, float units);  // +/- units; result clamped to [0, Total * 0.99]; never completes the work
        bool CanHold { get; }
        bool NearCompletion(JobDriver d);    // the hold point (§10.4)
        float UnitsPerTick(JobDriver d);     // estimate from the stats the driver itself reads
        int TallyKey(JobDriver d);           // target thingIDNumber, or -(job.loadID + 1)
        Thing TallyTarget(JobDriver d);      // null for job-keyed
    }
    public static class FpWork
    {
        public static IFpWorkAdapter AdapterFor(JobDriver d);  // null if none, or the skill is Shooting/Melee/Social/Animals/null
        public static SkillDef SkillOf(JobDriver d);           // d.ActiveSkill
        public static float Progress01(Pawn p);                // UI: tracked progress 0..1, -1 if not tracking
        public static int TallyKeyOf(Pawn p, Job job);         // §10.6
        public static void SendResult(Pawn p, int jobLoadId, int seq, byte outcome);   // UI sender
        [SyncMethod(cancelIfAnyArgNull = true)] [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpCheckResult(Pawn p, int jobLoadId, int seq, byte outcome);
        internal static void Tick(FpMapComp comp, FpEntry e);  // called by B for each controlled entry, every tick
        internal static void OnRelease(FpMapComp comp, FpEntry e, bool countAsMiss);
        internal static void PruneTallies(FpMapComp comp);     // every 250 ticks
        internal static void Install(Harmony h);               // hold prefix
    }
    public class StatPart_FpForm : StatPart { }                // FpLevers.cs
    public static class FpLevers { internal static void Install(Harmony h); }

    // ---------------- FpSkillCheck.cs (E) ----------------
    public enum FpRingState : byte { Idle, Warn, Needle, Result }
    public static class FpSkillCheck
    {
        public static FpRingState State { get; }
        public static bool WantsKey { get; }   // Warn, Needle, or Result within tuning.resultGrace seconds
        public static float ArcStartDeg { get; }
        public static float ArcWidthDeg { get; }
        public static float GreatWidthDeg { get; }
        public static float NeedleDeg { get; }   // harness
        internal static void Update();           // from FpInput.OnRootUpdate
        internal static void OnGUI();            // from FpHud postfix, every event type
    }

    // ---------------- FpBills.cs / FpOrders.cs (D) ----------------
    public static class FpBills
    {
        public static bool TryOpenPicker(Pawn p, FpHit hit);   // true if hit is an own bill giver -> opens Dialog_FpRecipes
        public static string PickerHint(Pawn p, FpHit hit);    // "Use electric stove…" or null
        public static void SendStartRecipe(Pawn p, Thing bench, RecipeDef r, int count);
        public static void SendWorkBill(Pawn p, Bill bill);
        [SyncMethod(cancelIfAnyArgNull = true)] [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpStartRecipe(Pawn p, Thing bench, RecipeDef recipe, int count);
        [SyncMethod(cancelIfAnyArgNull = true)] [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpWorkBill(Pawn p, Bill bill);
        internal static void Tick(FpMapComp comp);              // every 60 ticks
        internal static void OnRelease(FpMapComp comp, FpEntry e);
    }
    public static class FpOrders
    {
        public static bool TryOrderWork(Pawn p, FpHit hit);    // undesignated rock/plant -> FpOrderWork
        public static string OrderHint(Pawn p, FpHit hit);     // "Mine granite", "Chop oak", "Harvest rice" or null
        [SyncMethod(cancelIfAnyArgNull = true)] [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpOrderWork(Pawn p, Thing target, byte kind);   // kind 0 Mine, 1 Harvest, 2 Cut
    }
    // ---------------- FpCombat.cs (G) ----------------
    public static class FpCombat
    {
        public static bool TryPrimaryDown(Pawn p, FpHit hit);  // drafted: true if it handled the click
        public static void PrimaryUp();
        public static string Hint(Pawn p, FpHit hit);          // drafted hint ("Shoot raider 63%") or undrafted "R: raise weapon" on hostiles
        public static void ReticleOnGUI(Vector2 centre);       // called by FpHud on Repaint while drafted
        [SyncMethod(cancelIfAnyArgNull = true)] [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpFire(Pawn p, LocalTargetInfo target, bool hold);
        [SyncMethod(cancelIfAnyArgNull = true)] [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpStopFire(Pawn p);
    }
}
```

### 3.9 FpMath (I, M1.0; pure C#, no Verse or Unity; unit tested)

**Constants.** `Dir8` runs clockwise from north:
`N(0,1) NE(1,1) E(1,0) SE(1,-1) S(0,-1) SW(-1,-1) W(-1,0) NW(-1,1)`.

**Functions:**
- `int RoundDiv(int a, int b)`, with b > 0, rounds half away from zero:
  - `a >= 0 ? (2a+b)/(2b) : -((-2a+b)/(2b))`.
  - Tests: (3,2)→2, (1,2)→1, (−3,2)→−2, (5,3)→2.
- `int Form(int g, int h, int m)`: `n = g+h+m`; returns 0 if n == 0, otherwise `Clamp(RoundDiv(2*(2g+h−2m), Max(n,2)), −4, 4)`.
  - Tests: (1,0,0)→2, (0,1,0)→1, (0,0,1)→−2, (3,0,0)→4, (0,3,0)→2, (0,0,3)→−4, (4,13,3)→2, (1,9,10)→−1.
- `byte SectorOf(float worldDeg, byte current, float hysteresisDeg)`:
  - `s = floor((deg+11.25)/22.5) mod 16`.
  - If `current != 255` and the angular distance from current's centre (`current*22.5`) is ≤ `11.25 + hysteresisDeg`, it returns `current`.
- `void StepOf(byte sector, int n, out int dx, out int dz)`:
  - An even sector s gives `Dir8[s/2]`.
  - An odd sector gives `Dir8[(s−1)/2]` when n is even and `Dir8[((s+1)/2) % 8]` when n is odd.
- `int SpeedFactor(int timeSpeed)`: `{1,1,3,6,15}[clamp(timeSpeed,0,4)]` (Paused, Normal, Fast, Superfast, Ultrafast).
- `int PlanChecks(float remTicks, int f, int p0Permille, int spacing, int minRemaining, int endPermille, int maxChecks)`:
  - Returns 0 if `remTicks < minRemaining*f` or `endPermille − p0Permille < 100`.
  - Otherwise `Clamp(((int)remTicks + spacing*f/2) / (spacing*f), 1, maxChecks)`.
- `int DuePermille(int k, int planned, int p0, int end, int hash)`:
  - `span = (end−p0)/planned`.
  - `j = (hash & 0x7FFFFFFF) % (span/2 + 1) − span/4`.
  - Returns `p0 + span*k + span/2 + j`.
- `float ArcWidth(int level, int passion, float manip, bool inspired, FpRingNumbers r)`:
  - `Clamp((r.arcBase + r.arcPerLevel*level) * PassionMul * Clamp(manip, r.manipMin, 1) * (inspired ? r.inspiredMul : 1), r.arcMin, r.arcMax)`.
  - PassionMul: none 1, minor `r.passionMinor`, major `r.passionMajor`.
- `float GreatWidth(...)`: the same, with the great numbers.
- `byte Judge(float pressDeg, float arcStart, float arcWidth, float greatWidth, float halfFrameDeg)`:
  - `greatStart = arcStart + (arcWidth − greatWidth)/2`.
  - Returns 3 if `pressDeg` is in `[greatStart − hf, greatStart + greatWidth + hf]`.
  - Else 2 if it is in `[arcStart − hf, arcStart + arcWidth + hf]`.
  - Else 1.
  - hf is clamped to ≤ 6.

`FpRingNumbers` is a plain struct copied from `FpTuningDef`, so the tests need no Verse.

---

## 4. Scaffold (M1.0, owners I and H, one commit, no behaviour change)

### 4.1 Port lines in the subtree (I)

There are exactly these 15, each ending `// Port: first-person play`, plus one line in the port-owned MpView.

| # | File, method, anchor | Line |
|---|---|---|
| 1 | `IsoRim/Source/Patches.cs` `ApplyAll()`, before `// Stacked floors (Floors)` | `Fp.FpHooks.InstallAll(harmony);` |
| 2 | `Patches.cs` `RootUpdate_Postfix()`, last statement | `Fp.FpHooks.OnRootUpdate();` |
| 3 | `Patches.cs` `UIToMapPosition_Prefix`, first statement | `if (Fp.FpHooks.PointerMapPos(__0, ref __result)) return false;` |
| 4 | `Patches.cs` `ThingsUnderMouse_Postfix`, after the `!View3D.Ready \|\| __result == null` return | `if (Fp.FpHooks.ThingsUnderCrosshair(ref __result, __2)) return;` |
| 5 | `Patches.cs` `DrawSelectionBracketFor_Prefix`, first statement | `if (Fp.FpHooks.HidesBracket(__0)) return false;` |
| 6 | `IsoRim/Source/View3D.cs` `HandleKeys()`, immediately before `HandleLook();` | `if (Fp.FpHooks.LookFrame()) { lookDragging = false; heldTurning = false; return; }` |
| 7 | `View3D.cs` `RightLookOn()`, first statement | `if (Fp.FpHooks.PlayActive) return false;` |
| 8 | `IsoRim/Source/FirstPerson.cs` `Toggle()`, first statement | `if (Fp.FpHooks.Toggle()) return;` |
| 9 | `FirstPerson.cs` `Leave()`, right after `pawn = null;` | `Fp.FpHooks.CameraLeft(was);` |
| 10 | `FirstPerson.cs` `Reset()`, first statement | `Fp.FpHooks.CameraReset(pawn);` |
| 11 | `FirstPerson.cs` `Yaw` getter, first statement | `if (Fp.FpHooks.Steering) return Fp.FpHooks.Yaw;` |
| 12 | `FirstPerson.cs` `Pitch` getter, first statement | `if (Fp.FpHooks.Steering) return Fp.FpHooks.Pitch;` |
| 13 | `FirstPerson.cs` `Eye`, the `Vector3 f = ...` line | `Vector3 f = Quaternion.Euler(0f, Fp.FpHooks.Steering ? Fp.FpHooks.Yaw : heading, 0f) * Vector3.forward * 0.12f + Fp.FpHooks.EyeOffset;` |
| 14 | `IsoRim/Source/IsoRimMod.cs` `IsoRimSettings.ExposeData()`, last statement | `Fp.FpHooks.ExposeSettings();` |
| 15 | `IsoRimMod.cs` `ViewTab`, after the `st.fpEyeHeight = Slider(...)` line | `Fp.FpHooks.DrawSettings(l);` |
| 16 (port-owned) | `Source/IsoRim/MpView.cs` `PointerMapPoint()`, first statement | `if (Fp.FpHooks.PointerMapPoint(out Vector3 fpPoint)) return fpPoint;` |

Notes:
- `FirstPerson.Toggle` is called by the polled F5 in `View3D.HandleKeys` (`View3D.cs:468-471`). Every IsoRim leave path ends in `FirstPerson.Leave` or `Reset`: F5, 3D off, pawn gone, the reset key (`View3D.ResetLook:938`), gravship flight (`ShipFlight.cs:218`), MpView jump (`MpView.cs:229`), and the Game constructor (`View3D.NewGame:1169`). Lines 8-10 therefore catch every path.
- `PickUnderPointer` has only two callers, MpView and `ThingsUnderMouse_Postfix`, so `Interaction.cs` needs no Port line.

### 4.2 Multiplayer.dll (H)

1. `Source/Client/Patches/TickPatch.cs`: add `public static int currentExecutingCmdPlayerId = -1;` next to `currentExecutingCmdIssuedBySelf` (line 24).
2. `AsyncTime/AsyncTimeComp.cs`:
   - after line 257, add `TickPatch.currentExecutingCmdPlayerId = cmd.playerId;`
   - in the `finally` next to line 323, add `TickPatch.currentExecutingCmdPlayerId = -1;`
3. `AsyncTime/AsyncWorldTimeComp.cs`: the same at 188 and 290.
4. `AsyncWorldTimeComp.HandleTimeVote` (349), right after `int tickableId = data.ReadInt32();`:
   ```csharp
   if (vote == TimeVote.PlayerResetGlobal) IsoRimFp.PlayerLeft(cmd.playerId);
   ```
   The server sends this vote stamped with the leaving player (`ServerPlayer.ResetTimeVotes`, `ServerPlayer.cs:121-130`, `fauxSource`) on disconnect, desync and rejoin request (`PlayerManager.cs:101,114`, `ServerPlayingState.cs:19`). Every client runs it at the same world tick.
5. `AsyncWorldTimeComp.cs:251` (the `PlayerCountResetAll` branch): add `IsoRimFp.ResetSession();`
6. `Networking/HostUtil.cs` `PrepareGame()`, after `VTRSync.Reset();`: add `IsoRimFp.ResetSession();`
7. `Desyncs/PawnStateTrace.cs` `Describe(Pawn, StringBuilder)`: append `IsoRimFp.Describe(pawn, b);` as its last statement.
8. New `Source/Client/UI/IsoRimFp.cs`, a `[StaticConstructorOnStartup] public static class IsoRimFp`. It follows `IsoRimView.cs`'s binding pattern.
   - `Bind()`: `GenTypes.GetTypeInAnyAssembly("IsoRim.Fp.FpMpHooks", "IsoRim.Fp")`. If null, return silently (standalone IsoRim).
   - Call `Install` through reflection with:
     - `() => TickPatch.currentExecutingCmdPlayerId`
     - `() => Multiplayer.session?.playerId ?? -1`
     - `() => Multiplayer.GhostMode || Multiplayer.IsReplay || Multiplayer.arbiterInstance`
   - Bind `OnPlayerLeft`, `OnSessionReset` and `Describe` with `AccessTools.MethodDelegate` on `AccessTools.DeclaredMethod`.
   - On an exception: `Log.Error` once, all delegates null.
   - Public API: `PlayerLeft(int)`, `ResetSession()`, `Describe(Pawn, StringBuilder)`, which appends `' ' + s` when s is non-empty.

### 4.3 Staging (I, `scripts/common.sh` `stage_isorim`, after the `cp -r "$src/Languages" ...` line)

```sh
mkdir -p "$d/IsoRim/Defs/IsoRimMp" || return 1
cp -r "$REPO_ROOT/Source/IsoRim/Defs/." "$d/IsoRim/Defs/IsoRimMp/" || return 1
cp -r "$REPO_ROOT/Source/IsoRim/Languages/." "$d/IsoRim/Languages/" || return 1
```

- These defs name types in our IsoRim.dll. That is why they belong in the IsoRim load folder: it is skipped exactly when a standalone IsoRim is active.
- `check_staged` already refuses duplicate paths.

---

## 5. Simulation core (B)

### 5.1 Control entry rules (sim side, inside `FpSetControl(p, on:true, flags)`)

`FpSetControl(on)` is accepted only when all of these hold. Otherwise nothing changes, and the issuer gets a message if `MP.IsExecutingSyncCommandIssuedBySelf || !MpBridge.InSession`, with `historical:false`.
- `p.Spawned`, `!p.Dead`, `!p.Downed`, `!p.InMentalState`, `p.RaceProps.Humanlike`, `p.IsColonistPlayerControlled` (`Pawn.cs:778`).
- `p.Faction == Faction.OfPlayer`. The issuer's faction is pushed during a map command (`AsyncTimeComp.cs:261`).
- `!p.IsPrisoner`.
- The pawn's main think tree contains a `JobGiver_FpControl`. Cache the answer per `ThinkTreeDef` in a `Dictionary<ThinkTreeDef,bool>` used for lookups only.
- The current controller is `None` or `FpIds.Executing`.

**Effects:**
1. If the issuer controls another pawn on any map (`FpRegistry` lookup of entries with `controller == Executing`, in map-index then thingID order), mark that entry `controller = None` (data only). Its own map tick releases it.
2. `comp.Control(p, Executing, p.Faction, flags)`. If the entry already exists with the same controller, only `flags` is updated. A re-sent `on` updates flags.
3. For a new controller: `p.jobs.ClearQueuedJobs()`. If `p.CurJob != null && !p.CurJob.playerForced`, call `p.jobs.EndCurrentJob(JobCondition.InterruptForced)`. The think tree then gives the control job.

`FpSetControl(p, on:false, flags)` is accepted when `entry.controller == Executing`, or, in M6 only, when `flags & Force` is set and `p.Faction == Faction.OfPlayer`. It calls `comp.Release(e, countPendingAsMiss: (flags & Force) == 0)`.

### 5.2 Defs (`IsoRimFp_Control.xml`, B)

```xml
<JobDef>
  <defName>IsoRimFp_Control</defName>
  <driverClass>IsoRim.Fp.JobDriver_FpControl</driverClass>
  <reportString>under first-person control.</reportString>
  <playerInterruptible>true</playerInterruptible>
  <casualInterruptible>false</casualInterruptible>
  <checkOverrideOnDamage>Never</checkOverrideOnDamage>
  <alwaysShowWeapon>true</alwaysShowWeapon>
  <suspendable>false</suspendable>
  <isIdle>false</isIdle>
</JobDef>
<ThinkTreeDef>
  <defName>IsoRimFp_ControlInsert</defName>
  <insertTag>Humanlike_PostMentalState</insertTag>
  <insertPriority>100</insertPriority>
  <thinkRoot Class="ThinkNode_Priority">
    <subNodes><li Class="IsoRim.Fp.JobGiver_FpControl" /></subNodes>
  </thinkRoot>
</ThinkTreeDef>
```

Where the insert sits (`Humanlike.xml:85`):
- **Below:** downed, burning, critical mental states, `ReactToCloseMeleeThreat` (71) and non-critical mental states.
- **Above:** `ThinkNode_QueuedJob` (89), drafted `JobGiver_Orders` (98: no auto-fire), LordDuty (116), needs, work and joy.

### 5.3 `JobGiver_FpControl.TryGiveJob(pawn)`

It returns null unless all of these hold:
- `FpRegistry.ControlledCount > 0`;
- `e = FpRegistry.EntryOf(pawn)` is non-null and `e.controller != None`;
- `pawn.jobs.jobQueue.Count == 0`;
- `pawn.GetLord() == null`;
- `FpControlJob.NeedsAutopilot(e, pawn) == false` (M2: when `e.flags & AutoNeeds` is set: `pawn.needs.food?.CurCategory >= HungerCategory.UrgentlyHungry`, `pawn.needs.rest?.CurCategory >= RestCategory.Exhausted`, or `HealthAIUtility.ShouldSeekMedicalRestUrgent(pawn)`. M1 returns false).

The job: `JobMaker.MakeJob(IsoRimFp_Control)` with `playerForced = true`, `expiryInterval = -1`, `locomotionUrgency = Jog`.

### 5.4 `JobDriver_FpControl`

- One toil, `defaultCompleteMode = Never`, `handlingFacing = true`. Its logic is in `tickAction`, which runs every tick whatever the variable tick rate is (`JobDriver.cs:342`, `Pawn.cs:1581`).
- `TryMakePreToilReservations` returns true.
- `Notify_PatherArrived()` and `Notify_PatherFailed()` are overridden to do nothing.
- `IsContinuation(Job j)` returns `j.def == IsoRimFp_Control`.
- Saved fields (ExposeData, all `Scribe_Values`): `lastBase` (IntVec3, default Invalid), `lastDir` (byte, 255), `stepCount` (int, 0).

**tickAction, in order:**
1. `e = FpRegistry.EntryOf(pawn)`. If `e == null || e.controller == None`, call `EndJobWith(Succeeded)` and return.
2. Every 60 ticks (`pawn.IsHashIntervalTick(60)`): if `pawn.GetLord() != null || NeedsAutopilot(e, pawn)`, call `EndJobWith(Succeeded)` and return.
3. Movement (§5.5).
4. M6: facing. When `!pather.Moving && e.facing != 255 && pawn.Rotation.AsByte != e.facing && !stances.FullBodyBusy`, call `pawn.rotationTracker.FaceCell(pawn.Position + new Rot4(e.facing).FacingCell)`. That call is already transpiled to `MpFacing.GameTurned`, so the drawn facing stays right.

### 5.5 Movement (in `tickAction`)

`FpSetIntent(p, dir)` stores `e.dir`. It requires `e.controller == Executing` and `dir <= 15 || dir == 255`. If `dir != Stop` and all of the following hold:
- the current job is not `IsoRimFp_Control`;
- `pawn.jobs.IsCurrentJobPlayerInterruptible()`;
- `FpState.AutopilotReason(p) == null`;

then it calls `pawn.jobs.ClearQueuedJobs(); pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);`.

**Each tick:**
```
P = pawn.pather; d = e.dir
if d == Stop:
    if P.Moving && P.Destination.Cell != P.nextCell: P.StartPath(P.nextCell, PathEndMode.OnCell)   // finish the step in progress
    lastBase = Invalid; lastDir = 255; return
if !P.Moving:                      // standing
    issue(pawn.Position); return
nearlyIn = P.nextCellCostLeft <= 0.5f * P.nextCellCostTotal
if d != lastDir                     -> issue(P.nextCell)          // direction change: at once
else if P.Destination.Cell == P.nextCell && nearlyIn && lastBase != P.nextCell -> issue(P.nextCell)   // keep one cell of headroom
issue(base):
    lastBase = base; lastDir = d
    want = FpStep.Choose(pawn, base, d, stepCount)    // Invalid when blocked
    if want.IsValid: stepCount++; P.StartPath(want, PathEndMode.OnCell)
    else if P.Moving && P.Destination.Cell != P.nextCell: P.StartPath(P.nextCell, PathEndMode.OnCell)
```

`FpStep.Choose(pawn, from, d, n)`:
1. `StepOf(d, n)` gives (dx,dz); `c = from + (dx,0,dz)`. Return `c` if `Passable(from, c)`.
2. If diagonal: try `(dx,0)`, then `(0,dz)`, in that fixed order.
3. If d is odd: try the other neighbour, `StepOf(d, n+1)`.
4. Return `IntVec3.Invalid`.

`Passable(from, to)` requires all of:
- `to.InBounds(map)`;
- `to.WalkableBy(map, pawn)` (`GenGrid.cs:121`);
- `!to.Fogged(map)`;
- for a diagonal, both `from+(dx,0)` and `from+(0,dz)` are `WalkableBy`;
- `to.GetDoor(map)` (`GridsUtility.cs:424`) is null, or `PawnCanOpen(pawn)` (`Building_Door.cs:439`).

Notes:
- Speed, doors, terrain and weather are vanilla (`TicksPerMove`, Jog). There are no reservations.
- `StartPath` already returns early when nothing changed (`Pawn_PathFollower.cs:164`).
- In Multiplayer, path jobs are completed each tick after `MapPreTick` (`AsyncTimeComp.cs:133`), so this is deterministic.

### 5.6 Release (`FpMapComp.Release(e, countPendingAsMiss)`, map context)

In order:
1. `FpWork.OnRelease(comp, e, countPendingAsMiss)`.
2. `FpBills.OnRelease(comp, e)`.
3. If `e.pawn?.CurJob?.def == IsoRimFp_Control`, call `e.pawn.jobs.EndCurrentJob(JobCondition.InterruptForced)`.
4. Remove the entry and update the registry.

### 5.7 `FpMapComp.MapComponentTick` (each tick, map context, entries in list order)

```
for each e in entries.ToArray():         // copy: Release mutates
    p = e.pawn
    if e.controller == None
       || p == null || p.Destroyed || !p.Spawned || p.Map != map || p.Dead || p.Downed || p.InMentalState
       || p.Faction != e.faction || p.IsPrisoner || !p.IsColonistPlayerControlled:
          Release(e, false); continue
    FpWork.Tick(this, e)                  // F (stub in M1)
if TicksGame % 60 == 0:  FpBills.Tick(this)          // D
if TicksGame % 250 == 0: FpWork.PruneTallies(this)   // F
if TicksGame % 250 == 0 && Prefs.DevMode: recount the registry against the comps; Log.ErrorOnce on a mismatch (log only, never fix)
if FpTestKit.Active: FpTestKit.OnMapTick(this)       // I
```

- **Lifecycle:**
  - `FinalizeInit()`: if `!MpBridge.InSession`, remove every entry; keep tallies and `fpBills` (single-player load). Then `FpRegistry.Rebuild()`.
  - `MapRemoved()`: remove this comp's entries and tallies from the registry.
  - A Game constructor postfix (B, `AccessTools.Constructor(typeof(Game))`) calls `FpRegistry.Clear()`.
- **`ReleaseAllOf(id)` and `ResetAll()`** only set `controller = None` and `dir = Stop`. The next map tick releases the entry, voiding any pending check. They never touch jobs.
- **`CheckForJobOverride` prefix** on `AccessTools.DeclaredMethod(typeof(Pawn_JobTracker), "CheckForJobOverride")` with `Pawn ___pawn` (the field is protected, `Pawn_JobTracker.cs:12`). It returns false (skips the check) when `FpRegistry.ControlledCount > 0`, an entry exists with `controller != None`, and `___pawn.CurJob?.playerForced == true`. Hit counter `CheckForJobOverride skipped`. Autopilot jobs are not player-forced, so they stay overridable.

---

## 6. Local mode: entry, exit, resume (A)

### 6.1 F5

`FirstPerson.Toggle` → Port 8 → `FpPlay.TryToggle()`:
- **`FirstPerson.On && Mode == Playing`:** `FpPlay.Leave(text "R3D_Fp_Left")`; return true.
- **`FirstPerson.On && Mode == Off`:** ride-along; return false (IsoRim leaves as before).
- **Otherwise,** chosen = the single selected `Pawn`, and play is armed when all of these hold:
  - `FpSettings.play`;
  - no Shift held (`Input.GetKey(LeftShift|RightShift)`);
  - `!FpIds.PlayOff`;
  - `Controllable(chosen, out _)`:
    - Spawned, on `Find.CurrentMap`, humanlike, `IsColonistPlayerControlled`;
    - `Faction == MpBridge.RealPlayerFaction`;
    - not Downed, not in a mental state, not a prisoner;
    - `FpState.ControllerOf(chosen)` is `None` or `FpIds.Local`;
    - in a session, `FpMpHooks.Bound`.

  If armed, `armed = chosen`. Return false in every case, and IsoRim's Toggle enters the camera (3D on, eye in the head).
- **F5 or Shift+F5 on anything else humanlike** (visitor, prisoner, a teammate's controlled pawn) is the unchanged IsoRim **ride-along**.

`FpPlay.OnRootUpdate`, while `armed != null`:
- If `FirstPerson.Pawn == armed`: start play.
  - `Mode = Playing`, `Pawn = armed`, `Confirmed = false`, `enterTime = now`.
  - `FpCamera.Begin(MpFacing.Drawn(armed).AsAngle)`.
  - `UI.UnfocusCurrentControl()`.
  - `FpSync.SendSetControl(armed, true, FlagsFromSettings())`.
  - Show the cheat sheet once per session.
- Clear `armed` in either case.

### 6.2 Confirmation and auto-leave (each frame, `OnRootUpdate`)

- **`!Confirmed`:**
  - If `FpState.LocalControls(Pawn)`, set `Confirmed = true`.
  - Else, after 3.0 s real time, leave to ride-along: `FirstPerson` stays on and Mode goes Off. Toast "R3D_Fp_NotTaken" with `FpIds.NameOf(ControllerOf)` when someone else holds the pawn.
  - Intents are sent while unconfirmed. They are queued behind `FpSetControl` (one client's commands run in order).
- **`Confirmed && !FpState.LocalControls(Pawn)`:** the simulation released the pawn.
  - Reason: Downed "R3D_Fp_EndDowned", Dead, InMentalState, otherwise "R3D_Fp_EndOther".
  - `Mode = Off`, then `FirstPerson.Leave(reason)`.
- **Selection:** while Playing, if the pawn isn't the only selection, `Find.Selector.ClearSelection(); Find.Selector.Select(Pawn, playSound:false, forceDesignatorDeselect:false)`. FloatMenuMap needs this (`FloatMenuMap.cs:27,35`), and so does Multiplayer's MapSelected context.

### 6.3 Leaving

- **`FpPlay.Leave(msg)`:**
  - If `Mode == Playing`: `Mode = Off`, set `leaving = true`.
  - If `Confirmed || pending`: `SendSetControl(Pawn, false, 0)`.
  - Then `FirstPerson.Leave(msg)`, then `leaving = false`.
- **`CameraLeft(was)`** (every IsoRim path: 3D off, map switch, the reset key, gravship, MpView jump, pawn gone):
  - If `Mode == Playing && !leaving && was == Pawn`: send off (when confirmed or pending), `Mode = Off`.
- **`CameraReset(was)`** (Game constructor, or the program state not Playing):
  - If `Mode == Playing && FpSettings.resume`: `resumeId = was.thingIDNumber`, `resumeUntil = -1` (set on the first Playing frame).
  - Always `Mode = Off`. **No command.**
- **Opening the world map** keeps control. `CameraBlocked` frees the cursor, and the intent becomes Stop.

### 6.4 Resume after a Multiplayer reload (M1)

In `OnRootUpdate`, when `resumeId != 0`:
- **Wait** until ProgramState is Playing, there is no long event, `Find.CurrentMap` is set and `View3D.Active`.
- Set `resumeUntil = now + 10 s` the first time.
- **Find the pawn** on `Find.CurrentMap` by `thingIDNumber`.
- **If** `FpState.LocalControls(pawn)`:
  - select it, set `armed = pawn`, call `FirstPerson.Toggle()`;
  - that triggers §6.1 arming, which sends `FpSetControl(on)` again. That is harmless: same controller, flag update.
  - Then clear `resumeId`.
- **If** not controlled, or `now > resumeUntil`: clear `resumeId`.
- **At the main menu** with no session: clear it.
- **Join points:** they reload from a save and the entry is saved, so a pending skill check survives and its ring restarts for the same seq.

---

## 7. Input and camera (A)

### 7.1 Controls

| Input | While captured | Notes |
|---|---|---|
| Mouse movement | look | `Input.GetAxis("Mouse X"/"Mouse Y")`, raw counts × `FpSettings.sensitivity` (default 0.07°/count). Never × delta time. `dPitch = (invertY ? +1 : −1) * dy * sens`, using IsoRim's `invertY`. Yaw is the body heading. Pitch is clamped −80..80. |
| W A S D (and arrows) | move, relative to the view | Polled `KeyBindingDefOf.MapDolly_Up/Down/Left/Right.IsDown` (rebindable). |
| LMB | main action (drafted: fire) | IMGUI MouseDown(0) in our prefix |
| RMB | full float menu | MouseDown(1) |
| Space (`R3D_FpSkillCheck`) | skill check | Only while `FpSkillCheck.WantsKey`; otherwise vanilla pause passes through |
| R (`KeyBindingDefOf.Command_ColonistDraft`) | draft or undraft | We call `pawn.drafter.Drafted = !Drafted` (synced by Multiplayer, `SyncMethods.cs:25`) and `Use()` the event |
| Left Alt held (`R3D_FpFreeCursor`) | free cursor | For gizmos, tabs and time controls; map clicks stay blocked |
| Esc (`KeyBindingDefOf.Cancel` or `KeyCode.Escape`) | opens `Dialog_FpMenu` | |
| F5 | leave | Shift+F5 enters ride-along |
| 1-4, Slower/Faster, Ultrafast (dev), `MpToggleChat`, `MpPingKey`, Development-category keys in dev mode | pass through | |
| Mouse wheel | swallowed | M6: cycles the LMB option among the top 3 |
| Shift | never bound | Shift is QueueOrder (`Pawn_JobTracker.cs:904`) |

### 7.2 Capture watchdog (`FpInput.OnRootUpdate`, every frame, every program state)

`Captured` requires all of:
- `Mode == Playing`, `FirstPerson.On`, `View3D.Ready`, `!View3D.CameraBlocked`;
- `Current.ProgramState == Playing`, `!LongEventHandler.ShouldWaitForEvent`;
- `Waylith.EngineWindow.IsFocused || TestForceFocus` (`Application.isFocused` is always true on 1.7: `UnityAppState.cs:32`);
- `!focusLostLatch`, `!FreeCursorHeld`, `GUIUtility.keyboardControl == 0`;
- no open window other than `ImmediateWindow` and `MainTabWindow_Inspect`. FloatMenu, `Dialog_FpRecipes`, `Dialog_FpMenu`, chat, main tabs and Multiplayer's dialogs all block.

**When `Captured` changes:**
- Capturing: `Cursor.visible = false; Cursor.lockState = CursorLockMode.Locked`. This is GLFW Disabled with raw motion, and it takes effect a frame later.
- Releasing: `Cursor.lockState = None; Cursor.visible = true`.
- Write these only on a change.
- Ignore mouse deltas for 2 frames.
- Ignore LMB and RMB MouseDown for 0.15 s after each capture.

**Focus:**
- Focus loss sets `focusLostLatch` and makes the intent Stop.
- A later MouseDown(0) with no window under the pointer clears the latch, and that click is swallowed ("R3D_Fp_ClickToResume" shows meanwhile).

**Intent, every frame while Playing:**
- `desired = Captured ? SectorOf(yaw + atan2(right, forward)·Rad2Deg, current, 4°) : Stop`, where `forward = W − S` and `right = D − A`. Zero vector means Stop.
- `pawnBusy = Pawn.CurJob?.def != IsoRimFp_Control`.
- If `pawnBusy && desired != Stop`, desired must have been held for `FpSettings.moveInterruptMs` (default 300 ms) before it is sent. The HUD shows a small fill ring meanwhile.
- **Sending:** on a change, at most every 100 ms; the latest value wins, and Stop is never dropped. Nothing is sent when `desired == LastSentIntent`.

### 7.3 Event filter: prefix on `AccessTools.DeclaredMethod(typeof(UIRoot_Play), "UIRootOnGUI")`

It has `[HarmonyPriority(Priority.First)]` and returns void.

**Every event:**
- Record `FrameInputTime` on the first event of a new `Time.frameCount`.
- If `Mode == Playing && Captured`:
  - **Mouse events** (MouseDown, MouseUp, MouseDrag, ScrollWheel, ContextClick): act on them, then `Use()`.
    - MouseDown(0) → `FpActions.PrimaryDown()`.
    - MouseUp(0) → `FpActions.PrimaryUp()`.
    - MouseDown(1) → `FpActions.Secondary()`.
  - **KeyDown:**
    - Esc → open `Dialog_FpMenu`.
    - F5 → nothing (the polled toggle handles it).
    - Draft key → toggle draft.
    - Skill-check key → consume only if `FpSkillCheck.WantsKey`.
    - Allow-listed keys (§7.1) → pass, no `Use()`.
    - Every other KeyDown → `Use()`. That blocks main-tab and gizmo hotkeys, comma and period, PageUp/Down and Space-pause during checks.
- If `Mode == Playing && !Captured`: `Use()` an F5 KeyDown only.
- If `Mode == Off`: when exactly one humanlike pawn is selected or `FirstPerson.On`, `Use()` the F5 KeyDown. That way Wildlife (`MainButtons.xml:67`) never opens together with first person.
- Counter: `UIRootOnGUI filter`.

### 7.4 Other input patches (A)

- **`AccessTools.Method(typeof(GUI), "DispatchInputToWindows")`** (UnityStubs, `GUI.cs:2136`, public static void): a prefix returning false when `Captured` and the event type is a mouse type. Counter: `DispatchInputToWindows mouse dropped`.
  - Fallback, only if the M1 leak test fails: each frame while captured, set `EngineApplication.Instance.Window.CursorPos` to the screen centre and call `InputSystem.QueueMouseMove(centre)` (fp_input §3).
- **`AccessTools.DeclaredMethod(typeof(MapInterface), "HandleMapClicks")`** (shared with IsoRim and Multiplayer, declaring type): a prefix returning false while `Mode == Playing`. Counter: `HandleMapClicks skipped`.
- **`LookFrame()`** (Port 6):
  - `Mode != Playing` → return false.
  - If `Captured` and the 2-frame settle is over → `FpCamera.Look(dx*sens, dPitch)`.
  - Return true. This skips IsoRim's button look, Q/E and the tilt keys.

### 7.5 `Dialog_FpMenu` (A)

- **Window settings:** 300×260, centred, `forcePause = false`, `absorbInputAroundWindow = true`, `closeOnCancel = true`, `closeOnClickedOutside = true`, `preventCameraMotion = false`, `doCloseX = false`.
- **Buttons:**
  - "Resume": closes.
  - "Leave first person": `FpPlay.Leave(...)`, closes.
  - "Mod settings": `new Dialog_ModSettings(LoadedModManager.GetMod<IsoRimMod>())` (`Dialog_ModSettings.cs:18`).
  - "Game menu": closes, then `Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Menu)`.
- A two-column controls list sits below the buttons.

---

## 8. Picking and interaction (C)

### 8.1 `FpPick.Ray`

The ray is in true space (height in cells) from `View3D.EyePosition` along `View3D.Forward`. It runs once per frame while Playing, lazily, cached by `Time.frameCount`. In OnGUI it therefore uses the camera of the frame on screen.

- **Floor:** with `Floors.On`, `k = Floors.FloorOfRow(pawn.Position.z)` and `Floors.FloorPlane(k, out floorH, out floorZ)`. The true point (X,Y,Z) maps to cell `(⌊X⌋, ⌊Z⌋ + floorZ)` at height `Y − floorH`; skip cells with `FloorOfRow(c.z) != k`. Without floors, floorH = floorZ = 0.
- **Walk:** a 2D DDA over cells, up to `tuning.pickMaxCells` (60). For each cell, work out the ray's height span `[yIn, yOut]` (in floor-relative units).
- **Stopping without a target:**
  - out of bounds;
  - fogged cell (no target);
  - the ray at or above the eye height with nothing hit by the end means "no target". It never falls back to the 170-cell far point.
- **Things** in `map.thingGrid.ThingsListAtFast(c)`:
  - **Skip:** the ignore pawn, `Filth`, `Mote`, invisible pawns (`IsPsychologicallyInvisible`), and blueprints `MpBridge.HidesBlueprint` hides.
  - **Pawns:** hit when the 3D distance from the ray to the vertical segment at `DrawPos` (xz), heights `[PickBottom, PickHeight]`, is ≤ `tuning.pawnPickRadius (0.45) × clamp(sqrt(BodySize), 0.6, 1.6) × pawnRadiusScale`.
  - **Others:** hit when `[yIn, yOut]` overlaps `[Interaction.PickBottom(t), Max(Interaction.PickHeight(t), 0.15)]`. Both are `internal` in the same assembly (`Interaction.cs:251,258`).
  - **Order within a cell:** pawn, then item, then plant, then building, then other.
  - **Full-fillage edifices** (walls, rock, closed doors) stop the walk: they are the hit, or block if not overlapped.
- **Ground:** if the ray reaches height 0 inside the cell, the hit is that cell, with no thing.
- **The result** is `FpHit`. `Point` is for:
  - pawns: `DrawPos` (x,0,z);
  - things: the ray's closest point, clamped into `OccupiedRect` with a 0.02 margin (as `MpView.cs:137-140`);
  - cells: the ground intersection.
- `InReach` = `InReach(pawn, thing, cell)`, true when either:
  - the xz distance from `pawn.DrawPos` to the nearest point of the target's rect (cells ±0.5) is ≤ 2.5;
  - or `thing.def.hasInteractionCell && pawn.Position.DistanceTo(thing.InteractionCell) <= 1.5`.

### 8.2 Pointer substitution (Port lines 3, 4, 16)

While `FpInput.PointerOverride` is true (captured, or our menu open):
- **`PointerMapPos(ui, ref result)`:** if `ui == UI.MousePositionOnUI`, return true with `result = MenuOpen ? MenuPoint : Current.Point`. This covers `UI.MouseCell` and `MouseMapPosition` and Multiplayer's MapMouseCell context (`SyncUtil.cs:110-114`). Never patch those tiny methods: 16 and 11 bytes of IL.
- **`ThingsUnderCrosshair`:** move the current or pinned target thing to the front (if `parms == null || parms.CanTarget(thing)`), and return true.
- **`PointerMapPoint`:** while Playing and Captured, return `Current.Point`. Friends see your aim as your cursor.

### 8.3 LMB (`FpActions.PrimaryDown`, captured)

```
hit = FpPick.Current; if (!hit.Valid) return
if Pawn.Drafted && FpCombat.TryPrimaryDown(Pawn, hit) return            // G (M5); stub false
if !hit.InReach && !FpSettings.interactAnyDistance: Toast("R3D_Fp_TooFar"); return
if FpBills.TryOpenPicker(Pawn, hit) return                               // D (M2); stub false
if FpOrders.TryOrderWork(Pawn, hit) return                               // D (M4); stub false
if !FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(Pawn).Accepted return
opts = FloatMenuMakerMap.GetOptions(new List<Pawn>{Pawn}, hit.Point, out _)   // fresh list: it is mutated
best = opts.Where(o => !o.Disabled && o.action != null && !o.isGoto)
           .OrderByDescending(o => o.Priority).ThenByDescending(o => o.orderInPriority).FirstOrDefault()   // FloatMenu.cs:193
if best == null: Toast("R3D_Fp_Nothing"); return
best.Chosen(true, null)          // null-safe (FloatMenuOption.cs:296); actions end in synced TryTakeOrderedJob(...) (SyncMethods.cs:40-41)
FpHud.Toast(best.Label, white)
```

**`PrimaryUp`** → `FpCombat.PrimaryUp()`.

**Hint** (`FpActions.Update`): recompute at most 4 times a second, and only when `hit.Thing` or `hit.Cell` changed. The label is the first non-null of:
- `FpCombat.Hint` (drafted, or a hostile pawn);
- `FpBills.PickerHint`;
- `FpOrders.OrderHint`;
- the best option's `Label`.

HintKind: TooFar when out of reach, Invalid when nothing.

### 8.4 RMB (`FpActions.Secondary`)

1. Build `opts` as above, with no reach limit. If there are none, toast and return.
2. `MenuPoint = hit.Point`; `MenuOpen = true`.
3. `menu = new FloatMenuMap(opts, hit.Thing?.LabelCap ?? "", hit.Point) { givesColonistOrders = true, vanishIfMouseDistant = false }` (`FloatMenuMap.cs:19`, `FloatMenu.cs:14`).
4. `Find.WindowStack.Add(menu)`, then set `menu.windowRect.center = (UI.screenWidth/2, UI.screenHeight/2 + 40)`.
5. Each Update: `MenuOpen = Find.WindowStack.IsOpen(menu)`. The watchdog releases the cursor while it is open and captures it again after.

---

## 9. Synced state, commands, save data

### 9.1 `FpEntry` fields (B saves all; F owns the check fields' meaning)

| Field | Type | Default | Scribe key |
|---|---|---|---|
| pawn | Pawn | — | `pawn` (`Scribe_References`) |
| faction | Faction | — | `faction` (ref) |
| controller | int | −1 | `ctrl` |
| dir | byte | 255 | `dir` |
| flags | byte | 0 | `flags` |
| facing | byte | 255 | `facing` (M6) |
| checkJob | int | −1 | `checkJob` |
| tallyKey | int | 0 | `tallyKey` |
| p0 | int (‰) | 0 | `p0` |
| planned | int | 0 | `planned` |
| issued | int | 0 | `issued` |
| seq | int | 0 | `seq` |
| pendingSeq | int | 0 | `pendingSeq` |
| pendingTick | int | 0 | `pendingTick` |
| deadlineTick | int | 0 | `deadlineTick` |
| lastResolveTick | int | −99999 | `lastResolve` |
| segStart | float | 0 | `segStart` |
| held | bool | false | `held` |
| lastOutcome | byte | 255 | `lastOutcome` |
| lastOutcomeTick | int | −99999 | `lastOutcomeTick` |
| carry | float | 0 | `carry` (repair rounding) |

- **`FpTally`:** `key`, `target` (ref), `owner` (ref), `jobLoadId`, `great`, `hit`, `miss`, `lastTick`, under the same names.
- **`FpMapComp.ExposeData`:**
  - `Scribe_Collections.Look(ref entries, "fpEntries", LookMode.Deep)`;
  - `Scribe_Collections.Look(ref tallies, "fpTallies", LookMode.Deep)`;
  - `Scribe_Collections.Look(ref fpBills, "fpBills", LookMode.Reference)`.
- **In PostLoadInit:** drop entries with a null pawn, drop null bills, keep tallies with a null target only if `owner != null`, and re-sort.
- **Every piece of synced state is saved.** Commit 0977f96b found that unsaved synced state plus join-point reloads desyncs.
- **Never saved:** camera, cursor, ring, HUD, settings, `FpRegistry`.

### 9.2 Commands

All are static `[SyncMethod(cancelIfAnyArgNull = true)]` and `[MethodImpl(NoInlining)]` (`MpUtil.MarkNoInlining` does nothing on .NET). They take the Pawn first, which routes them to that map's queue (`SyncDictRimWorld.cs:797`).

Authorization means an entry exists and `e.controller == FpIds.Executing`.

Messages to the issuer: only when `!MpBridge.InSession || MP.IsExecutingSyncCommandIssuedBySelf`, `historical: false`.

| Command (owner, milestone) | Payload | Sender and rate | Validation | Effect |
|---|---|---|---|---|
| `FpSetControl` (B, M1) | Pawn, bool on, byte flags | F5, leave, resume, settings change; at most one in flight per pawn, re-sent no more than once a second | §5.1 | §5.1 / §5.6 |
| `FpSetIntent` (B, M1) | Pawn, byte dir (0-15, 255) | on change, ≥100 ms apart, latest wins | authorized, dir valid | §5.5 |
| `FpSetFacing` (B, M6) | Pawn, byte rot (0-3) | idle camera quadrant change (10° hysteresis), ≥250 ms apart | authorized | `e.facing = rot` |
| `FpStartRecipe` (D, M2) | Pawn, Thing bench, RecipeDef, int count (1 or 5) | picker choice | §11.2 | §11.2 |
| `FpWorkBill` (D, M2) | Pawn, Bill | picker "Continue" | authorized; bill on bench's stack; `!DeletedOrDereferenced`; restriction is null or this pawn; `recipe.PawnSatisfiesSkillRequirements` | job as §11.2 step 4-5 with this bill |
| `FpCheckResult` (F, M3) | Pawn, int jobLoadId, int seq, byte outcome (0-3) | once per check | §10.5 | §10.5 |
| `FpOrderWork` (D, M4) | Pawn, Thing target, byte kind (0 Mine, 1 Harvest, 2 Cut) | LMB on an undesignated rock or plant, at most one in flight | §11.4 | §11.4 |
| `FpFire` (G, M5) | Pawn, LocalTargetInfo, bool hold | LMB down while drafted; resent at most 4/s when the snapped thing changes while held | §12 | §12 |
| `FpStopFire` (G, M5) | Pawn | LMB up | authorized | §12 |

Already synced and reused as is:
- `TryTakeOrderedJob` and `TryTakeOrderedJobPrioritizedWork`, with the Job exposed (`SyncMethods.cs:40-41`);
- the `Drafted` setter (`:25`);
- float-menu delegates;
- `BillStack` and `Bill` sync workers (`SyncDictRimWorld.cs:284-301`).

### 9.3 Desync aids

**`FpState.Describe(p)`** (B), appended to every `PawnStateTrace` digest:
```
fp c=<controller> d=<dir> f=<flags> j=<checkJob> q=<issued>/<planned> s=<pendingSeq> h=<0|1> t=<g>/<h>/<m>
```
It returns "" when the pawn has no entry and no tally.

**Hit counters** (`FpHits`, the IsoRim dev action "FP: patch hit counts"):
- A: `UIRootOnGUI filter`, `DispatchInputToWindows mouse dropped`, `HandleMapClicks skipped`, `HUD drawn`.
- B: `CheckForJobOverride skipped`, `Control job tick`, `Order refused` (M6).
- F: `Hold skipped interval`, `Check accepted`, `Check stale`, `Quality offset`, `StatPart applied`.
- D: `Bill started`, `Order work`.
- G: `Fire`, `StopFire`.

---

## 10. Skill checks

### 10.1 Which work (F)

A pawn is in skilled work when all of these hold:
- `e.flags & Checks` is set;
- `d = pawn.jobs.curDriver` is non-null;
- `a = FpWork.AdapterFor(d)` is non-null;
- `a.InWorkToil(d)`;
- `d.ActiveSkill` (`JobDriver.cs:110`) is not null, Shooting, Melee, Social or Animals.

**Adapters.** Implementers verify every field with ilspycmd; the private fields use cached `AccessTools.FieldRefAccess`. A failed lookup at startup disables only that adapter, with one `Log.Error`.

| Adapter (milestone) | Driver | Total / Done (units) | Add(u) and clamps | Hold point (`NearCompletion`) | Tally key |
|---|---|---|---|---|---|
| Bill (M3) | `JobDriver_DoBill`, not `Bill_Mech` | total = `job.bill.recipe.WorkAmountTotal(job.GetTarget(B).Thing)` (`Toils_Recipe.cs:150`); done = total − `workLeft` (public, `JobDriver_DoBill.cs:10`) | `workLeft -= u`, clamped to [total·0.01, total]; mirrored to `UnfinishedThing.workLeft` when TargetB is one (`Toils_Recipe.cs:117-127`) | `workLeft <= total·0.03` | TargetB `UnfinishedThing` id, else −(job.loadID+1) |
| Construct (M4) | `JobDriver_ConstructFinishFrame` | `frame.WorkToBuild` / `frame.workDone` (`Frame.cs:15,39`) | `workDone += u` in [0, WorkToBuild·0.99] | `workDone >= WorkToBuild·0.97` | Frame id |
| Mine (M4) | `JobDriver_Mine` | total = `MaxHitPoints − dmg`, with dmg = 80 for natural rock, else 40 (`JobDriver_Mine.cs:105`); done = clamp(Max − HP, 0, total) | `HitPoints -= round(u)`, clamped to [1, Max] | `HitPoints <= dmg` (the next pick hit destroys it) | Mineable id |
| PlantWork (M4) | `JobDriver_PlantWork` (Harvest, Cut, subclasses) | `def.plant.harvestWork` / private `workDone` (`:12`) | `workDone += u` in [0, harvestWork·0.99] | ≥ 0.97 | Plant id |
| Sow (M4) | `JobDriver_PlantSow` | `def.plant.sowWork` / private `sowWorkDone` (`:9`) | same | ≥ 0.97 | Plant id |
| Repair (M4) | `JobDriver_Repair` | total = Max − HP at tracking start (`e.carry` holds the fractional HP) | `HitPoints += round(u + carry)`, ≤ Max − 1 | `CanHold = false` | Building id |
| AffectFloor, SmoothWall, RemoveBuilding (M4) | `JobDriver_AffectFloor` (+ subclasses), `JobDriver_SmoothWall`, `JobDriver_RemoveBuilding` | BaseWorkAmount or private `totalNeededWork` / private `workLeft` | `workLeft -= u` in [total·0.01, total] | `workLeft <= total·0.03` | target thing id, else job |

Surgery, butchery, drugs, art and cooking all go through the Bill adapter. Tending and research are out of scope. Unknown drivers get no checks, logged once in dev mode.

### 10.2 Scheduling (F, `FpWork.Tick`, map context, every tick per controlled entry)

`f = FpMath.SpeedFactor((int)Find.TickManager.CurTimeSpeed)`. In map context this is the map's synced speed (`SetMapTime.cs:240`). T is `FpTuning.Get`.

```
if not in skilled work (10.1):
    if e.checkJob != -1 && pawn.CurJob?.loadID != e.checkJob:  void pending (10.4 void); e.checkJob = -1
    else if e.pendingSeq != 0: void pending          // left the work toil (e.g. finish toil)
    set held false; return
d, a = driver, adapter; job = pawn.CurJob; P = (int)(1000 * a.Done(d) / a.Total(d))
if job.loadID != e.checkJob:                          // start tracking
    void any pending; e.checkJob = job.loadID; e.tallyKey = a.TallyKey(d); e.p0 = P; e.segStart = a.Done(d)
    rem = (a.Total(d) - a.Done(d)) / Max(a.UnitsPerTick(d), 0.01f)
    e.planned = FpMath.PlanChecks(rem, f, P, T.checkSpacingTicks, T.minRemainingTicks, T.endDuePermille, T.maxChecks)
    e.issued = 0
    if Repair: e.startHp = HP; e.carry = 0
if e.pendingSeq == 0 && e.issued < e.planned:
    if P >= T.holdPermille: e.issued = e.planned      // too late, no more checks this job
    else if P >= FpMath.DuePermille(e.issued, e.planned, e.p0, T.endDuePermille, Gen.HashCombineInt(job.loadID, e.issued))
            && TicksGame - e.lastResolveTick >= T.minGapTicks * f:
        e.pendingSeq = ++e.seq; e.pendingTick = TicksGame; e.deadlineTick = TicksGame + T.deadlineTicks * f
if e.pendingSeq != 0 && TicksGame >= e.deadlineTick: void pending
FpRegistry.SetHeld(e, e.pendingSeq != 0 && a.CanHold && a.NearCompletion(d))
```

Never use `HashCode.Combine` here (CLAUDE.md); `Gen.HashCombineInt` is deterministic.

**Void pending:** `pendingSeq = 0; issued++; lastResolveTick = TicksGame; held = false`. There is no effect and no tally change.

**Resulting cadence at 1x** (`UnitsPerTick` about 1):
- a simple meal (300) gets 1 check;
- a table (about 1300) gets 3;
- a large sculpture (15000) gets 12, about every 20 s;
- a wall (about 80 ticks) gets none;
- granite mining (about 1800 ticks) gets 4.

At 3x or 6x there are proportionally fewer checks per job, roughly one per 8 s of real time.

### 10.3 Hold (F)

A prefix on `AccessTools.DeclaredMethod(typeof(JobDriver), "DriverTickInterval")` (`JobDriver.cs:428`):
```csharp
static bool Prefix(JobDriver __instance)
{ if (FpRegistry.HeldCount == 0) return true; var e = FpRegistry.EntryOf(__instance.pawn);
  if (e == null || !e.held) return true; FpHits.Hit("Hold skipped interval"); return false; }
```

The hold point sits inside 3% of the end. The hold lasts until the result arrives, or at most `deadlineTicks·f` (10 s of real time at nominal speed). Meanwhile `DriverTick` keeps running fail conditions and the Delay countdown.

### 10.4 Results (F)

`FpCheckResult(p, jobLoadId, seq, outcome)` is valid when all of these hold; otherwise it counts `Check stale` and does nothing:
- the entry exists and `e.controller == FpIds.Executing`;
- `p.CurJob?.loadID == jobLoadId == e.checkJob`;
- `seq == e.pendingSeq != 0`;
- `outcome <= 3`;
- the adapter matches.

```
d, a = curDriver, adapter
seg = Max(0, a.Done(d) - e.segStart)
grant = outcome == 3 ? T.grantGreat : outcome == 2 ? T.grantHit : T.grantMiss      // 0.40 / 0.15 / -0.30
a.Add(d, seg * grant)
e.segStart = a.Done(d)
tally = comp.GetOrAddTally(e.tallyKey, a.TallyTarget(d), p, job.loadID); great/hit/miss++ ; lastTick = TicksGame
xp = T.xp[outcome] ; if xp > 0: p.skills.Learn(SkillOf(d), xp)      // default 0
e.pendingSeq = 0; e.issued++; e.lastResolveTick = TicksGame; e.lastOutcome = outcome; e.lastOutcomeTick = TicksGame
FpRegistry.SetHeld(e, false); FpHits.Hit("Check accepted")
```

**`OnRelease(comp, e, countAsMiss)`:**
- If `countAsMiss` and a check is pending, and the pawn is still on the tracked job: apply outcome 1, as above. Leaving voluntarily doesn't escape a bad check.
- Otherwise void it.

**Caps:**
- A grant is a share of its own segment, so speed rises by at most 40% and falls by at most 30%.
- At most `maxChecks` (12) per job, never two pending at once, and at least `minGapTicks·f` between them.
- Form F is at most ±4. Outcomes are discrete; the command never carries a multiplier.

**Expected values:**

| Player | Great / Hit / Miss | Speed | F |
|---|---|---|---|
| Good | 20% / 65% / 15% | +13% | +2 |
| Poor | 5% / 45% / 50% | −6% | −1 |

### 10.5 Levers (F, `FpLevers`, M3 for Bill levers, M4 for the rest)

All levers read **F = `FpRegistry.TallyForWorker(pawn)?.Form ?? 0`**, so they also apply to an AI colonist who finishes a piece someone worked in first person.

- **Quality** (anything with `CompQuality`: crafted items, art, buildings through `Frame.cs:294`):
  - A prefix and a finalizer on `QualityUtility.GenerateQualityCreatedByPawn(Pawn pawn, SkillDef relevantSkill, bool consumeInspiration)` (`QualityUtility.cs:244`) set and clear `static int pendingOffset`.
  - A prefix on `GenerateQualityCreatedByPawn(int relevantSkillLevel, bool inspired)` (`:160`) does `if (pendingOffset != 0) relevantSkillLevel = Mathf.Clamp(relevantSkillLevel + pendingOffset, 0, 20)`.
  - Vanilla's Gaussian, inspiration and role offset are unchanged. Counter: `Quality offset`.
- **`StatPart_FpForm`**, added in code at startup (`stat.parts ??= new List<StatPart>(); part.parentStat = stat; stat.parts.Add(part)`). StatParts run before the stat's own clamp (`StatWorker.cs:804,832`).
  - `TransformValue(req, ref val)`:
    - return when `FpRegistry.TallyCount == 0`, when `req.Thing` isn't a Pawn, or when there is no tally or F == 0;
    - otherwise `k = table[F+4]`.
  - `ExplanationPart` gives "First-person skill checks (form +2): ×0.80".

| Stat | Table (F = −4 … +4) | Applied as |
|---|---|---|
| `ConstructSuccessChance` (max 1) | constructFail `[2.0,1.75,1.5,1.25,1,0.9,0.8,0.7,0.6]` | `val = 1 − (1 − val)·k` |
| `MedicalSurgerySuccessChance` | surgery `[0.80,0.85,0.90,0.95,1,1.02,1.04,1.06,1.08]` | `val *= k` (the def's `ClampToRange 0~0.98` comp still applies) |
| `FoodPoisonChance` | foodPoison `[2.0,1.75,1.5,1.25,1,0.85,0.7,0.6,0.5]` | `val *= k` (read at `CompFoodPoisonable.cs:42`) |
| `MiningYield` (max 1.25), `PlantHarvestYield` (max 1.5), `DrugHarvestYield` (max 1.5), `ButcheryFleshEfficiency`, `ButcheryMechanoidEfficiency` | yield `[0.80,0.85,0.90,0.95,1,1.03,1.06,1.08,1.10]` | `val *= k` |

No StatPart ever touches shooting, melee, warmup, move speed or work speed.

### 10.6 Tallies (F)

`TallyKeyOf(p, job)`:
- For DoBill: the `UnfinishedThing` id at TargetB, else −(job.loadID+1).
- For the thing-targeted adapters in §10.1: `job.targetA.Thing.thingIDNumber`.
- Otherwise −(job.loadID+1).

`PruneTallies` (every 250 ticks, list order) removes a tally when either:
- `target != null && target.Destroyed && TicksGame − lastTick > 250`;
- or the tally is job-keyed (`target == null`) and `owner?.CurJob?.loadID != jobLoadId`.

### 10.7 The ring (E, local)

**Lifecycle (`FpSkillCheck.Update`):**
- When Playing and Confirmed, `e.pendingSeq != 0`, the pair (checkJob, pendingSeq) hasn't been answered yet, and `FpSettings.skillChecks` is on, start a ring.
- **Warn:** `T.ringWarn` = 0.55 s, a 0.55 s fade-in and the sound `Tick_Low`.
- **Needle:** one clockwise turn from 12 o'clock in `T.ringPeriod` = 1.15 s (313.04°/s).
  - The clock is `Time.realtimeSinceStartupAsDouble`.
  - It freezes while `Find.TickManager.Paused` (the map's state) or while not `Captured`.
- **Press:** `FpKeyDefOf.R3D_FpSkillCheck.JustPressed` (polled; true once per press, no auto-repeat). Presses during Warn are ignored.
- **Judging:**
  - `pressDeg = (FrameInputTime − FpSettings.checkLatencyMs/1000 − needleT0) × 313.04`.
  - `hf = 0.5 × Time.unscaledDeltaTime × 313.04`.
  - `FpMath.Judge(...)` returns 3, 2 or 1.
  - No press by 360° gives outcome 0.
  - Then `FpWork.SendResult(pawn, checkJob, seq, outcome)` and the Result state for 0.8 s.
  - The pair is remembered so the ring isn't shown again while the command travels.
- **After a reload,** a still-pending pair restarts a fresh ring.

**Difficulty.** It is local, but built only from synced pawn data and the def, so it is the same for every player.
- L = `skills.GetSkill(skill).Level`; passion from `skills.GetSkill(skill).passion`; `manip = health.capacities.GetLevel(PawnCapacityDefOf.Manipulation)`.
- `inspired`: `Inspired_Creativity` for Crafting or Artistic, `Inspired_Surgery` for Medicine.
- `arc = FpMath.ArcWidth(L, …)`: 34° + 3°·L, ×1.1 or ×1.2 for passion, × manipulation clamped to [0.5,1], ×1.25 when inspired, clamped to [24°,110°].
- `great = FpMath.GreatWidth(...)`: 6° + 0.5°·L with the same multipliers, clamped to [4°,22°], centred in the arc.
- `arcStart` is uniform in [110°, 330° − arc], from a `System.Random` seeded once from `Environment.TickCount`. Never `Verse.Rand`.

**Look (our own).** Drawn in `Find.WindowStack.ImmediateWindow(0x49524650, rect, WindowLayer.Super, draw, doBackground:false, absorbInputAroundWindow:false, shadowAlpha:0f)`, called from `FpHud` on every event. It is centred on the crosshair and the crosshair is hidden while it shows.
- Radius 70 UI px × `hudScale`.
- Track: 48 `Widgets.DrawLine` segments, 2 px, white at α 0.25.
- Arc: 5 px, in the skill colour.
- Great core: 8 px, the skill colour lerped 50% toward white.
- Needle: a 6×6 white spark on the rim with 5 trailing boxes at −3° steps, α 0.8 → 0.1.
- Keep `GUI.color` white: `DrawLine` tints on top of it under UnityStubs (CLAUDE.md).
- Skill colours:

| Skill | Colour |
|---|---|
| Construction | #7FA7C9 |
| Mining | #A68B6D |
| Plants | #7DBE6A |
| Cooking | #E8A33D |
| Crafting | #C9A26B |
| Artistic | #B58CE0 |
| Medicine | #E07A8A |
| other | #CFCFCF |

- Colour-blind setting: the arc is dashed (every other segment) and the great core is solid white with tick marks at its edges.
- Centre label: "<Skill> <L>" plus passion pips.
- Result text:

| Result | Colour | Sound | Extra |
|---|---|---|---|
| "GREAT" | #FFD24A | `Tick_High` | |
| "HIT" | white | `Click` | |
| "MISS" | #E05050 | `ClickReject` | a ±3 px shake for 0.25 s |

  Sounds go through `PlayOneShotOnCamera` and are local only.

**Progress bar** (E, M3; drawn from `FpSkillCheck.OnGUI` on Repaint):
- Bottom centre, 240×6 at y = `screenHeight − 92`, filled to `FpWork.Progress01`.
- A tick at each check's due ‰ (computed with `FpMath.DuePermille` from the entry).
- Pips (gold, white, red) for the tally and "Form +F".
- A ghost segment of the expected grant from the press until `e.issued` changes.

**WantsKey** is true in Warn and Needle, and in Result for `T.resultGrace` (0.6 s). Space outside that pauses as in vanilla.

---

## 11. Bills and work orders (D)

### 11.1 Picker (`Dialog_FpRecipes`, M2)

**`TryOpenPicker`:**
- `hit.Thing is Building b`, `b is IBillGiver`, `b.def.AllRecipes` non-empty, `b.Faction == Faction.OfPlayer`, and in reach.
- Opens `Dialog_FpRecipes(pawn, b)`: 460×560, centred.
  - `forcePause = false`, `preventCameraMotion = false`, `absorbInputAroundWindow = true`, `closeOnClickedOutside = true`, `closeOnCancel = true`.

**Content:**
1. **Header:** bench label and status (power, fuel via `CompRefuelable`/`CompPowerTrader`).
2. **"Continue":** bills on the bench where `PawnRestriction` is null or this pawn, `ShouldDoNow()`, and the pawn meets the skill requirement. Each shows the unfinished thing's progress if any. Click → `SendWorkBill`.
3. **Recipes:** `b.def.AllRecipes.Where(r => r.AvailableNow && r.AvailableOnNow(b))` (`ITab_Bills.cs:83-88`). Each row has:
   - icon and label;
   - the time for this pawn: `r.WorkAmountTotal(null) / (pawn.GetStatValue(r.workSpeedStat ?? WorkSpeedGlobal) × table factor) / 60` s at 1x;
   - the skill requirement;
   - "no <ingredient> on map" from `map.resourceCounter`, when relevant.

   Rows that can't be done are greyed with a reason: skill, `WorkTypeIsDisabled`, `MissingRequiredCapacity`.
4. **Count:** 1 or 5.
5. **Keys:** 1-9 pick a row, Enter repeats the last choice, Esc closes.

### 11.2 `FpStartRecipe(p, bench, recipe, count)` (sim, M2)

1. **Validate:**
   - authorized;
   - `bench.Spawned && bench.Map == p.Map && bench.Faction == Faction.OfPlayer && bench is IBillGiver g`;
   - `bench.def.AllRecipes.Contains(recipe) && recipe.AvailableNow && recipe.AvailableOnNow(bench)`;
   - `g.BillStack.Count < T.maxBillStack (15)`;
   - `recipe.PawnSatisfiesSkillRequirements(p)`;
   - `count` is 1 or 5.
2. **WorkGiverDef:** the first in `DefDatabase<WorkGiverDef>.AllDefsListForReading` order whose `Worker is WorkGiver_DoBill`, whose `fixedBillGiverDefs` contains `bench.def`, and whose `workType == recipe.requiredGiverWorkType` (skip that test if null). Refuse if none, if `p.WorkTypeIsDisabled(wg.workType)`, or if `wg.Worker.MissingRequiredCapacity(p) != null`.
3. **Make the bill:**
   - `bill = (Bill_Production)recipe.MakeNewBill()`, with `repeatMode = RepeatCount`, `repeatCount = count`, `SetPawnRestriction(p)` (`Bill.cs:165`), `ingredientSearchRadius = T.ingredientRadius (20)`.
   - `g.BillStack.AddBill(bill)`, then move it to index 0 (`BillStack.Reorder` or `bills.Remove/Insert`, verify).
   - `comp.fpBills.Add(bill)`.
4. **Make the job:** `job = ((WorkGiver_Scanner)wg.Worker).JobOnThing(p, bench, forced: true)`.
   - If `job == null || job.bill != bill`: `g.BillStack.Delete(bill)`, remove it from `fpBills`, and message the issuer with "R3D_Fp_NoIngredients" (radius) plus `JobFailReason.Reason` if set.
   - Otherwise `p.jobs.TryTakeOrderedJob(job)`. Ingredient search and job ids happen inside the command on every client.

### 11.3 Clean-up (`FpBills.Tick`, every 60 ticks; `OnRelease`)

For each bill in `fpBills`, in list order:
- `DeletedOrDereferenced` → drop it from the list.
- `repeatCount <= 0 && bill.PawnRestriction?.CurJob?.bill != bill` → `bill.billStack.Delete(bill)` and drop it.
- `repeatCount > 0` and its pawn has no entry (released) → drop it from the list only. It stays as an ordinary restricted bill that the AI finishes.

`OnRelease(e)` runs the same rules for that pawn's bills at once.

### 11.4 `FpOrderWork(p, target, kind)` (M4)

**Validate:**
- authorized; target spawned on p's map; `!target.Position.Fogged(map)`; `p.CanReach(target, PathEndMode.Touch, Danger.Deadly)`.
- Kind 0: `target.def.mineable`, and the Mining work type is enabled.
- Kind 1: `target is Plant pl && pl.HarvestableNow`, and Growing or PlantCutting is enabled for the matched giver.
- Kind 2: `target is Plant` and it can be cut (tree or `def.plant.IsTree`/`harvestTag`), and PlantCutting is enabled.

**Effect:**
1. If the target has no designation of that def, `map.designationManager.AddDesignation(new Designation(target, DesignationDefOf.Mine | HarvestPlant | CutPlant))`. The issuer's faction is pushed.
2. Take the first `WorkGiver_Scanner` in `p.workSettings.WorkGiversInOrderNormal` where all of these hold:
   - `PotentialWorkThingRequest.Accepts(target)`;
   - `!ShouldSkip(p, true)`;
   - `HasJobOnThing(p, target, true)`.
3. `job = JobOnThing(p, target, true)`, then `p.jobs.TryTakeOrderedJobPrioritizedWork(job, giver, target.Position)`.

**UI rule (`TryOrderWork`):** the target is a Mineable or Plant with no Mine, HarvestPlant or CutPlant designation, and it is in reach. Hints: "Mine X", "Harvest X", "Chop X".

---

## 12. Shooting (G, M5): vanilla odds only

**`TryPrimaryDown(p, hit)`** (drafted only; returns false when undrafted):
- `verb = p.equipment?.PrimaryEq?.PrimaryVerb ?? p.meleeVerbs`.
- Aim: `FpPick.Ray(..., pawnRadiusScale: 1.3f)`. A targetable pawn wins, then a thing. A cell is used only if `verb.verbProps.targetParams.canTargetLocations` (`TargetingParameters.cs:9`). Otherwise there is no target, so a cell shot can't skip the cover roll at `Verb_LaunchProjectile.cs:166`.
- Pawns that aren't hostile are refused with the hint "RMB for orders". Vanilla's confirmations stay in the right-click menu.
- One `FpFire(p, target, hold:true)` per press. Locally there is no new `FpFire` until the attack job changes or 1.5 s passes.

**`PrimaryUp`** → `FpStopFire(p)`.

**`FpFire`** (sim):
1. Authorized; `p.Drafted`; the target is valid (spawned, same map, or a cell allowed as above).
2. Melee is chosen when the primary isn't ranged, or the target is a pawn within 1.5 cells. It is validated with `FloatMenuUtility.GetMeleeAttackAction(p, target, out fail)` (`FloatMenuUtility.cs:95`). Otherwise `GetRangedAttackAction(p, target, out fail)` (`:20`). The returned Action is **not** invoked; null means refuse, with the fail text to the issuer.
3. The job: `AttackStatic` (or `AttackMelee`), with `playerForced = true` and `maxNumStaticAttacks` (or `maxNumMeleeAttacks`) = `hold ? int.MaxValue : 1` (`Job.cs:51,53`). For ranged, also `endIfCantShootTargetFromCurPos = true`. Then `p.jobs.TryTakeOrderedJob(job)`.
4. Warmup (a busy stance that stops movement: no run-and-gun, `Pawn_PathFollower.cs:250`), cooldown, burst, `ShotReport` and every roll stay vanilla in the tick.

**`FpStopFire`** (sim): if the current job is `AttackStatic`, set `job.maxNumStaticAttacks = Max(1, numAttacksMade)` (private field, `JobDriver_AttackStatic.cs:12`). For melee, do the same with `numMeleeAttacksMade` (`JobDriver_AttackMelee.cs:11`). The attack already started finishes, so a click always gets its burst.

**Movement:** `FpSetIntent` with a direction ends the attack job, which is player-interruptible.

**Reticle** (`ReticleOnGUI`, drafted, Repaint, at most 10 Hz for the numbers):
- Red when `!verb.CanHitTarget(target)`.
- "63%" from `ShotReport.HitReportFor(p, verb, target).TotalEstimatedHitChance` (read-only).
- A warmup arc from `p.stances.curStance as Stance_Warmup`, a thinner cooldown arc, and the weapon label.

**No advantages:** no StatPart or patch on `ShootingAccuracyPawn`, `AimingDelayFactor`, `Verb.WarmupTime` or `MoveSpeed`. There is no sprint.

---

## 13. Settings and tuning

### 13.1 `FpSettings`

These are per player and never read by simulation code. They are saved inside IsoRim's settings file through Port 14 and drawn under the IsoRim View tab through Port 15, as a "First-person play" section.

| Field | Default | Range | Scribe key | Milestone |
|---|---|---|---|---|
| play | true | | `fpPlay` | M1 |
| sensitivity | 0.07 | 0.01–0.30 °/count | `fpSensitivity` | M1 |
| hints | true | | `fpHints` | M1 |
| cheatSheet | true | | `fpCheatSheet` | M1 |
| interactAnyDistance | false | | `fpInteractAnyDistance` | M1 |
| moveInterruptMs | 300 | 0–1000 | `fpMoveInterruptMs` | M1 |
| resume | true | | `fpResume` | M1 |
| hudScale | 1.0 | 0.75–1.5 | `fpHudScale` | M1 |
| autoNeeds | true | (sent as `FpFlags.AutoNeeds`) | `fpAutoNeeds` | M2 |
| skillChecks | true | (sent as `FpFlags.Checks`) | `fpSkillChecks` | M3 |
| checkLatencyMs | 16 | 0–100 | `fpCheckLatencyMs` | M3 |
| checkSounds | true | | `fpCheckSounds` | M3 |
| colourBlind | false | | `fpColourBlind` | M3 |
| showHitChance | true | | `fpShowHitChance` | M5 |
| eyeLead | true | | `fpEyeLead` | M6 |
| headBob | 0 | 0–1 | `fpHeadBob` | M6 |
| friendTags | true | | `fpFriendTags` | M6 |

Reused from IsoRim: `invertY`, `fpFov`, `fpEyeHeight`.

When `autoNeeds` or `skillChecks` changes while Playing and Confirmed, FpPlay re-sends `FpSetControl(on, newFlags)`. Without the synced Checks flag, a player who had turned checks off would make the simulation hold at 97% until the deadline.

### 13.2 `FpTuningDef` `IsoRimFp_Tuning` (`Source/IsoRim/Defs/IsoRimFp_Tuning.xml`)

These are gameplay numbers. The join check compares Defs, so every player runs the same ones. XML field names equal the C# fields.

| Group | Fields and values |
|---|---|
| Checks (ticks at 1x, × SpeedFactor) | `checkSpacingTicks` 480, `minRemainingTicks` 150, `minGapTicks` 180, `deadlineTicks` 600, `maxChecks` 12, `endDuePermille` 900, `holdPermille` 970 |
| Grants | `grantGreat` 0.40, `grantHit` 0.15, `grantMiss` −0.30; `xp` list [0,0,0,0] (index = outcome) |
| Tables (9 values, F −4..+4) | `constructFail`, `surgery`, `foodPoison`, `yield` (§10.5) |
| Ring | `ringPeriod` 1.15, `ringWarn` 0.55, `resultGrace` 0.6, `arcBase` 34, `arcPerLevel` 3, `arcMin` 24, `arcMax` 110, `greatBase` 6, `greatPerLevel` 0.5, `greatMin` 4, `greatMax` 22, `passionMinor` 1.1, `passionMajor` 1.2, `manipMin` 0.5, `inspiredMul` 1.25, `arcStartMin` 110, `arcEndMax` 330 |
| Interaction | `reachCells` 2.5, `interactionCellReach` 1.5, `pickMaxCells` 60, `pawnPickRadius` 0.45, `ingredientRadius` 20, `maxBillStack` 15 |

`FpTuning.Get` caches `DefDatabase<FpTuningDef>.GetNamed("IsoRimFp_Tuning")`.

### 13.3 Key bindings (`IsoRimFp_Keys.xml`, I)

- `KeyBindingCategoryDef IsoRimFp`, label "first person play", with no `checkForConflicts`: Space overlapping TogglePause is intended.
- `R3D_FpSkillCheck`: default A `Space`.
- `R3D_FpFreeCursor`: default A `LeftAlt`, B `RightAlt`.
- Everything else reuses vanilla or IsoRim bindings (§7.1). `[DefOf] FpKeyDefOf` holds the two.

---

## 14. HUD (A; E and G draw their parts)

The postfix on `UIRoot_Play.UIRootOnGUI` draws on Repaint only, while `Mode == Playing`, scaled by `hudScale`. Windows draw on top of it.

- **Crosshair** at (`UI.screenWidth/2`, `UI.screenHeight/2`): a 2×2 dot plus four 6 px ticks with a 5 px gap, 2 px thick, with a 1 px outline at α 0.6.

| HintKind | Colour |
|---|---|
| None | white |
| Action in reach | #7CD67C |
| TooFar | #9A9A9A |
| Invalid, or no line of fire | #E05A5A |

  It is hidden while the ring shows.
- **Hint** 18 px below the crosshair: "[LMB] <label>   [RMB] More" (Small font, shadowed), or "Too far — [RMB] More".
- **Status line**, bottom centre at y = `screenHeight − 112`:
  - "<Name> — <`curDriver.GetReport()`>";
  - plus one of: "Taking control…" (unconfirmed), "Autopilot: <reason> — F5 to leave", "Drafted";
  - plus need warnings (Hungry, Tired, Bleeding) from `pawn.needs` and `pawn.health`;
  - plus the move-interrupt fill ring while waiting.
- **Toasts:** above the status line, 1.5 s.
- **Cheat sheet:** top left, 8 s, once per session: "WASD move · Mouse look · LMB use · RMB menu · R draft · Space skill check · Alt cursor · Esc menu · F5 leave".
- **"Click to resume"** in the centre while `focusLostLatch`.
- **M3:** the ring and progress bar (E). **M5:** the reticle (G).
- **M6:**
  - needs strip (food, rest, mood, health) at bottom left;
  - red edge flash when `SummaryHealthPercent` drops;
  - friend tags "▶ <player>" over pawns controlled by others (3D through `IsoRimView`/`MpView.MapToUI`);
  - a small ring icon over a pawn with `pendingSeq != 0` and an outcome pop for 1.5 s after `lastOutcomeTick` (E). All of this is drawn from synced state on every client.

---

## 15. Test harness (I, M1.0)

### 15.1 `FpTestKit` (`Source/IsoRim/Fp/Test/`)

- **Activation:** `Active = Prefs.DevMode && Environment.GetEnvironmentVariable("ISORIM_FPTEST")` is set. It is inert otherwise.
  - The format is `<scenario>[:<role>]`, with role `solo` (default), `host` or `client`.
  - `Install` patches only when Active:
    - a postfix on `Root_Play.Update` steps the script;
    - a prefix on `AccessTools.Method(AccessTools.TypeByName("RimWorldWaylith.Imgui.ImguiDispatch"), "DispatchInput")` dequeues one input action per frame (`scratchpad/CamTest.cs`).
  - It sets `FpInput.TestForceFocus = true`.
- **Script API.** Scripts are `IEnumerator<int>` (yield the number of frames to wait):
  - `Key(Key k, bool down)`, `Tap(Key k)`, `HoldTicks(Key k, int gameTicks)`: input through `InputSystem.QueueKeyChange`.
  - `MouseDelta(dx, dy, frames)`: successive `InputSystem.QueueMouseMove` from the current position.
  - `Button(int b, bool down)`: `QueueMouseButton`.
  - `VirtualPointer(Vector2 screenPx)`.
  - `WaitTicks(n)`.
  - `SetSpeed(TimeSpeed)`: solo only.
  - `Check(name, ok, detail)` logs `FPTEST PASS|FAIL <name>: <detail>`.
  - `Shot(name)`: `ScreenCapture.CaptureScreenshot` into `<SaveData>/renders/fptest/<scenario>/`.
  - `Done()` logs `FPTEST DONE fails=<n>`, then `Root.Shutdown()`, then `Application.Quit()` 600 frames later.
  - `Direct.*`: for the headless host, call `FpSync.Send*`, `FpWork.SendResult` and so on with no input.
- **`OnMapTick(comp)`**, every 60 map ticks, one line per entry and per pawn with a tally:
  ```
  FPSTATE m=<map.uniqueID> t=<TicksGame> p=<thingIDNumber> pos=<x>,<z> job=<def>:<loadID> wl=<Done/Total‰ or -1> <FpState.Describe>
  ```
- **Scenario registry:** `FpTestKit` calls `FpTestM1.Register(...)` through `FpTestM6.Register(...)`. Later files are stubs until their milestone.

### 15.2 Scene (`FpScene`, solo and host)

On the first Playing frame of a `-quicktest -isorim3d` game:
- **C1** = `FreeColonistsSpawned` sorted by thingIDNumber, [0]. **C2** = [1].
- Clear non-pawn things in a 21×21 square centred on C1. Then spawn, at offsets from C1 (north = +z), with all def names verified:
  - a `FueledStove` at +3z, rotation North, refuelled to full;
  - 20 `RawPotatoes` at (+2, 0);
  - 50 `Steel` at (−2, +1);
  - a granite wall line (`Wall`, stuff `BlocksGranite`) from (−5, +8) to (+5, +8);
  - a `Granite` mineable at (+4, +4);
  - a damaged granite wall at (−4, +6), HP 50%;
  - a grown `Plant_TreeOak` at (−6, +2), and a grown `Plant_Rice` at (+6, +2) in a growing zone;
  - a `Table1x2c` frame with all resources delivered at (+3, −3);
  - a `Gun_AssaultRifle` equipped by C1;
  - a pirate-faction humanlike with no weapon at (+12, 0), downed with `Anesthetic`;
  - a `Weapon_GrenadeFrag` in storage at (+1, −2).
- Camera yaw 0, game speed Normal. M2 and later scenarios start from here.

### 15.3 Runners

**`scripts/fp_selftest.py <scenario> [--mp] [--timeout 900] [--proton PATH] [--host-windowed]`.** It runs on the host through `distrobox-host-exec`. The defaults and prefix handling follow `scripts/check_isorim_shaders.py`: `DEFAULT_PROTON`, ModsConfig writing, and the LocalLow path.

- **Prefixes:** `~/.rw-mp-test/fptest-{solo,host,client}/prefix`.
- **Per prefix:**
  - write `Config/ModsConfig.xml` (Harmony, Core, the installed DLCs, `rwmt.multiplayer`);
  - write `Config/Prefs.xml`, copied from `~/.rw-mp-test/proton-isorim/prefix` when present, then `fullscreen` False, `devMode` True, `runInBackground` True.
- **Launch:** `env STEAM_COMPAT_CLIENT_INSTALL_PATH=$HOME/.local/share/Steam STEAM_COMPAT_DATA_PATH=<prefix> DOTNET_TieredCompilation=0 ISORIM_FPTEST=<scenario>:<role> [FPTEST_HOST=1] python3 <proton> run ./RimWorld.exe <args>`, from the game folder.
- **Arguments:**
  - solo: `-quicktest -isorim3d` (windowed);
  - host: `-quicktest -isorim3d -Headless` (windowed with `--host-windowed`);
  - client: `-connect=127.0.0.1:30502` (windowed), started after the host logs `FPTEST HOSTED`.
- **Finish:**
  - wait for `FPTEST DONE` in each `…/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log`;
  - print the `FPTEST` lines;
  - count `Exception`/`[Error]` lines from the mod;
  - with `--mp`, run `fp_compare.py` and list new files in each `MpDesyncs` folder.
- **Exit code:** the fail count, plus 1 per error line or desync file. 99 on a timeout.

**`scripts/fp_compare.py host.log client.log`:**
- parses the `FPSTATE` lines into `(m,t,p) → rest`;
- requires ≥ 30 common keys;
- prints the first difference;
- exits 1 on any difference.

**`Source/Client/Diag/FpSelfTestHost.cs`** (H; active only when `FPTEST_HOST=1 && Prefs.DevMode`):
- On the first Playing frame without a session, call `HostUtil.HostServer(new ServerSettings { gameName = "fptest", direct = true, directAddress = "0.0.0.0:30502", lan = false, steam = false, arbiter = false, pauseOnJoin = false, pauseOnDesync = true, autoJoinPoint = AutoJoinPointFlags.Join }, fromReplay:false)`. Set `pauseOnLetter` to its "never" value; verify the enum.
- Log `FPTEST HOSTED`.
- While Active, whenever the world speed is Paused and no desync happened, send a Normal time vote (`TimeControlUI`'s internal sender).

**Windows:** two windowed instances run only while the user isn't playing. Cursor capture at the OS level (Proton or XWayland grab, first-frame jump, where the cursor lands on release, Alt-Tab) can't be checked by the harness. The user checks it in game (§17).

---

## 16. Milestones, owners and acceptance tests

Every milestone must pass all of these:
- `dotnet build Source/Multiplayer.sln -c Release` with no errors, and `dotnet test Source/Tests -c Release`;
- `scripts/build_release.sh` staging, so `check_staged` passes;
- `fp_selftest.py` for every scenario of the milestone and of all earlier milestones (regression);
- 0 errors from Multiplayer or IsoRim in Player.log;
- no IsoRim "Hooks missing" or FP "hook missing" warning;
- `HarmonyPatchWatchdog.CheckSplitPatches` reports nothing;
- an IL-size audit (CLAUDE.md): no new patch target under about 40 bytes of IL;
- one commit on `port-1.7`, and I updates CLAUDE.md Progress and Known gaps.

### M1: First playable (scaffold, look, capture, synced WASD, crosshair, LMB/RMB, Esc menu, multiplayer-safe control)

- **M1.0** (I + H, lands first): §4, all stub files, FpMath with tests, the harness, the scene, the runners. Acceptance: builds, unit tests pass, F5 behaves exactly as before (scenario `m0-noop`).
- **Parallel tracks:**
  - A: FpPlay, FpCamera, FpInput, FpMenu, FpHud (crosshair, hint, status, cheat sheet, toasts).
  - B: FpMapComp, FpSync, FpControlJob, `IsoRimFp_Control.xml`.
  - C: FpPick, FpActions.
  - I: FpTestM1.

Solo scenarios (windowed, injected input):

| ID | Steps | Pass |
|---|---|---|
| m1-enter | select C1, Tap F5, wait 120 frames | `Mode == Playing`, `Confirmed`, `Cursor.lockState == Locked && !Cursor.visible`, entry controller = −2, C1 job `IsoRimFp_Control` within 60 ticks, `Find.MainTabsRoot.OpenTab?.defName != "Wildlife"` |
| m1-look | MouseDelta(+20, 0, 10 frames), then (0, +20, 10 frames) | Yaw changed by 200 × 0.07 = 14° ± 1.5°, Pitch by −14° ± 1.5°; 40 more frames of +100 dy keeps Pitch ≥ −80 |
| m1-walk | Yaw 0; HoldTicks(W, 180) | Δz in [0.6, 1.1] × 180 / `C1.TicksPerMoveCardinal`; \|Δx\| ≤ 1; 60 ticks after release, position unchanged ±1 cell; job still control |
| m1-diag | Yaw 0; W+D for 150 ticks | Δx ≥ 3, Δz ≥ 3, \|Δx − Δz\| ≤ 1 |
| m1-sector | Yaw 22.5; W for 240 ticks | Δz > Δx > 0, Δx/Δz in [0.3, 0.65] |
| m1-wall | walk north into the wall line | stops at z+7, no path error in the log, job control |
| m1-idle | no input for 1200 ticks | position unchanged; job control the whole time |
| m1-interrupt | order C1 to haul the steel (vanilla `TryTakeOrderedJob`); W for 12 ticks (≈0.2 s); later W for 30 ticks | first: job unchanged; second: job becomes control |
| m1-pick | aim at the stove (set FpCamera); at the steel; at the stove with the wall behind | `FpPick.Current.Thing` is stove / steel / stove; never C1; `UI.MouseCell() == Current.Cell` |
| m1-lmb | aim at the steel (in reach), Button(0) down/up | within 30 ticks C1's job ≠ control and `targetA.Thing == steel` |
| m1-far | aim at the wall line (8 cells) | `HintKind == TooFar`; LMB leaves the job unchanged |
| m1-rmb | aim at the steel, Button(1) | `FloatMenuMap` open, `Cursor.lockState == None`; after 120 frames still open; `UI.MouseCell()` equals the pinned cell; harness calls `options[0].Chosen(true, menu)` and closes it; captured again within 5 frames |
| m1-leak | captured; VirtualPointer over the bottom-left main button, then over the inspect pane; Button(0) | no main tab opened, no gizmo used, `FpHits` `DispatchInputToWindows mouse dropped` > 0 or `UIRootOnGUI filter` > 0 |
| m1-esc | Tap Escape | `Dialog_FpMenu` open, cursor free; Tap Escape again → closed, captured; open again, click "Leave" → `Mode == Off`, no entry within 2 ticks, C1 job ≠ control within 60 ticks, cursor free |
| m1-alt | hold LeftAlt 30 frames, then release | unlocked while held, locked after |
| m1-space | Tap Space | `Find.TickManager.Paused` toggled (vanilla pause preserved in M1) |
| m1-draft | Tap R | `C1.Drafted` true, job is still control (no `Wait_Combat`); Tap R → false |
| m1-ride | Shift+F5 on C1; F5 on the downed pirate | `FirstPerson.On && Mode == Off`, no entry |
| m1-down | in play, dev-down C1 (`HealthUtility.DamageUntilDowned`) | entry removed within 1 tick, `Mode == Off`, toast with the "R3D_Fp_EndDowned" key |
| m1-load | in play, save, then load the save | after the load, no entry (single-player clear), `Mode == Off`, 0 errors |

Multiplayer scenarios (`--mp`; host headless with Direct, client windowed with injected input):

| ID | Steps | Pass |
|---|---|---|
| mp1-walk | host Direct-controls C1; client F5 on C2; both walk scripted squares for 60 s at Normal | `fp_compare` OK; both `FPTEST DONE fails=0`; no MpDesyncs files |
| mp1-race | at map tick ≥ 1200 both send `FpSetControl(C1, on)` | both logs show the same single controller for C1; the loser (if the client) is back to ride-along with a toast within 3 s |
| mp1-joinpoint | client controls C2 and walks; host forces a join point (Multiplayer's `CreateJoinPoint` command through the diag helper) | client `Mode == Playing && Confirmed` again within 10 s after the reload, compare OK |
| mp1-leave | client controls C2 and walks; the runner kills the client process | within 300 ticks the host logs C2 with no entry and job ≠ control; host `FPTEST DONE fails=0` |
| mp1-lmb | client LMB on the steel | `FPSTATE` job fields equal on both sides |

### M2: Workbench and AutoNeeds

- D: FpRecipeWindow, FpBills (picker, `FpStartRecipe`, `FpWorkBill`, clean-up), FpTestM2.
- B: AutoNeeds in `JobGiver_FpControl` and tickAction, `FpState.AutopilotReason`.

| ID | Pass |
|---|---|
| m2-cook | LMB on the stove → picker open, cursor free; choose "Cook simple meal" ×1 → within 60 ticks the bill is at index 0, restricted to C1, `repeatCount` 1, radius 20; C1 job DoBill with `job.bill == bill`; at 3x a meal appears; the bill is deleted ≤ 120 ticks after the job ends |
| m2-noingr | remove the potatoes, choose a meal → no bill left, the issuer gets the "R3D_Fp_NoIngredients" message, job unchanged |
| m2-15 | add 15 bills, pick a recipe → refused, stack still 15 |
| m2-continue | existing unrestricted bill listed; click → DoBill on that bill |
| m2-cancel | while fetching, hold W 0.5 s → job control; bill still present (repeatCount 1) |
| m2-autoneeds | set C1 food to 0.05 → within 120 ticks job is an eating job, HUD shows Autopilot; after eating, job control |
| mp2-cook | client cooks on C2; compare OK; the bill `loadID` is the same in both logs |

### M3: Skill checks on bills (E: ring; F: FpWork, Bill adapter, quality and food-poison levers)

| ID | Pass |
|---|---|
| m3-plan | craft a long recipe (e.g. `Make_Apparel_Duster`) at Normal → `e.planned == PlanChecks(...)` logged; the first pending check appears at the expected ‰ ± span/4; ring State goes Warn then Needle |
| m3-outcomes | the harness reads `ArcStartDeg`, `ArcWidthDeg`, `GreatWidthDeg` and injects Space at the great centre, the arc centre outside great, 20° before the arc, and never → outcomes 3, 2, 1, 0 reach the sim; tallies 1/1/2; each Done change = seg × grant ± 1e-3·Total |
| m3-hold | meal at Superfast with the ring answered late → `workLeft` constant while pending (30-tick sample), `Hold skipped interval` > 0; after the result the meal completes |
| m3-deadline | `TestForceFocus = false` (not captured, ring frozen) → after `deadlineTicks·f` the check is voided: no tally change, hold released |
| m3-stale | Direct send with a wrong seq, then with a wrong job → ignored, `Check stale` == 2 |
| m3-quality | in `Rand.PushState(42)`, 400 calls of `GenerateQualityCreatedByPawn(C1, Crafting)` with a forced tally of F = −4, 0 and +4 → mean category strictly increasing; F = 0 gives the same sequence as with no tally |
| m3-poison | F = −4 → `C1.GetStatValue(FoodPoisonChance)` = base × 2.0 (± 1e-5), and the explanation contains the FP line |
| m3-space | Space during Needle doesn't pause; Space 2 s after Result pauses |
| mp3-craft | client crafts with the scripted pattern G,H,M,H…; compare OK (wl and tally fields included) |

### M4: All skilled work (F: the other adapters and levers; D: FpOrders)

| ID | Pass |
|---|---|
| m4-construct | LMB on the table frame → FinishFrame job; checks happen; `ConstructSuccessChance` explanation shows FP; the finished table's quality used the offset (`Quality offset` > 0) |
| m4-mine | LMB on the undesignated granite → Mine designation plus Mine job; a Great reduces HP by seg × 0.4; `MiningYield` FP line |
| m4-plants | LMB on the oak → Chop; on the rice → Harvest; sow through the growing zone menu; checks on each; `PlantHarvestYield` FP line |
| m4-repair | LMB on the damaged wall → Repair; check results change HP; never held |
| m4-surgery | add a medical bill (dev) for C2 in bed; C1 LMB → operation (DoBill with `Bill_Medical`); checks; `MedicalSurgerySuccessChance` FP line |
| m4-smooth | designate a floor to smooth; LMB → checks on `JobDriver_AffectFloor` |
| mp4-work | client mines and builds for 10 min; compare OK, no desync |

### M5: Shooting (G)

| ID | Pass |
|---|---|
| m5-click | drafted C1 aims at the pirate, click → `AttackStatic` and `maxNumStaticAttacks == 1` after StopFire; `numAttacksMade == 1` when the job ends |
| m5-hold | hold LMB 3 s → `numAttacksMade >= 2`; release → the job ends after the current burst |
| m5-cell | rifle aimed at the ground → no job; equip the grenades → cell allowed |
| m5-odds | `ShotReport.HitReportFor(C1, verb, pirate).TotalEstimatedHitChance` equals the value for a vanilla `GetRangedAttackAction` order from the same cell; `Harmony.GetPatchInfo` on `ShootingAccuracyPawn`'s worker, `Verb.WarmupTime` and the `AimingDelayFactor` reads shows no owner `isorim.core` |
| m5-undrafted | undrafted LMB on the pirate → no attack; hint contains "R" |
| m5-wasd | while holding fire, W → attack job ends, job control |
| mp5-fire | client fires 20 bursts; compare OK; the pirate's health digest is equal on both sides |

### M6: Hardening and polish

Tracks: B (order guard, `FpSetFacing`, optional force release), A (friend tags, needs strip, damage flash, eye lead, head bob, wheel cycling), E (friend ring icon and outcome pop).
- **Order guard:** prefixes on `TryTakeOrderedJob` and `TryTakeOrderedJobPrioritizedWork` (declaring type `Pawn_JobTracker`, `__result = false`) and on the `Pawn_DraftController.Drafted` setter (`AccessTools.DeclaredPropertySetter`). They refuse when `FpIds.Executing >= 0 && FpRegistry.ControlledCount > 0 && e != null && e.controller >= 0 && FpIds.Executing != e.controller`. The issuer gets "R3D_Fp_ControlledBy". Counter: `Order refused`.
- **Eye lead:** while the local intent is moving and the sim pawn isn't yet moving that way, the lead grows along the intent heading at the pawn's speed (`60 / TicksPerMoveCardinal × SpeedFactor` cells/s, 0 while paused). It is capped at 0.35 cells and never into a cell that isn't `Walkable`. It decays with τ = 0.15 s once the pawn moves.
- **Head bob:** 0.012 × `headBob` cells vertical, one cycle per 2 cells, from `pather.MovePercentage`. Both go out through `FpCamera.EyeOffset`.

| ID | Pass |
|---|---|
| mp6-guard | the host Direct-orders C2 (controlled by the client) → refused on both sides, `Order refused` > 0 |
| mp6-both | both players in first person for 5 min with checks and firing; one autosave join point during a pending check → the ring reappears for the same seq; compare OK |
| m6-facing | idle, turn the camera 90° → after ≤ 300 ms `C1.Rotation` changes on the host (mp) |
| mp6-soak | 30 min with slow tracing on, both in first person: no desync |

---

## 17. Things only the user can check in game (I lists them in CLAUDE.md "to check")

**From M1:**
- the first frame of capture (a large jump?);
- where the cursor lands on release;
- Alt-Tab and click-to-resume;
- raw-motion sensitivity under Proton/XWayland;
- that no hidden click reaches the UI;
- that F5 doesn't open Wildlife;
- the walking feel at 1x (start and stop latency with a friend);
- the Esc menu;
- a friend joining while you are in first person.

**From M3:** the ring's readability and fairness at the user's frame rate, and the latency offset.

**From M5:** that firing feels responsive while the odds stay vanilla.

`run_two.sh` stays blocked while Steam forces Proton (CLAUDE.md); the user's two-PC session with the friend is the real multiplayer check.

---

## 18. Rules and risks

**Harmony:**
- Patch every target through its declaring type, with `AccessTools.DeclaredMethod`, `DeclaredPropertySetter` or `Constructor`. Shared targets: `UIRootOnGUI`, `HandleMapClicks`, `TryTakeOrderedJob`, the Game ctor.
- Never patch `UI.MouseCell`, `MouseMapPosition`, `MousePositionOnUI`, `KeyBindingDef.IsDown`/`JustPressed`, `Pawn_PathFollower.Moving` or `Input.mousePosition`.
- Every new patch has a hit counter.
- Tiered compilation stays off.

**Sync:**
- Every synced method is static, has `NoInlining`, and is registered by the single `MP.RegisterAll` in a `[StaticConstructorOnStartup]`. Static constructors run sequentially in `GenTypes` order (`StaticConstructorOnStartupUtility.cs:17-21`), so ids are stable for identical mod lists.
- Never touch `MP.*` from `IsoRimMod`'s constructor.

**Subtree:**
- The 15 Port lines are the only subtree edits. After any IsoRim subtree pull, re-check them along with the existing `MP:` and `Port` lines.
- `scripts/mp_targets.py` doesn't scan IsoRim, so FpAdapters' `FieldRefAccess` lookups log an error at startup if a field moves.

**Risks:**
1. Capture under Proton/XWayland is unverified. The harness checks only the requested state.
2. `GUI.DispatchInputToWindows` is in UnityStubs, outside `JitPatchableAssemblies`. If its prefix never fires, use the pointer-pin fallback (§7.4).
3. `FpRegistry` is a static index of synced state. It is rebuilt in `FinalizeInit` and cleared by the Game ctor; the dev recount logs a mismatch.
4. Movement starts one command round trip after the key (about 100-200 ms online). Only the camera (M6 lead) hides it.
5. A modified client could always send Great. Everything is capped (±4 form, +40% or −30% speed); acceptable for co-op.
6. Other mods' float-menu options that Multiplayer doesn't sync desync when chosen here exactly as in normal play. The hint calls `GetOptions` at ≤ 4 Hz, only on target change.
7. Another mod's always-open window would block capture; extend the allow-list if found.
8. The controlled pawn won't do work on its own; AutoNeeds (M2) covers food, rest and urgent medical rest only.
