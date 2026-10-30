using System;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace IsoRim.Fp
{
    // Track E (fp-play-spec 10.7, 14): the skill-check ring and the work progress bar. Local to this player.
    //
    // When the simulation has a check pending for the played colonist (FpEntry.pendingSeq, scheduled and held by FpWork),
    // a ring shows round the crosshair: a hit arc somewhere between 4 and 11 o'clock, a great part in the arc's middle,
    // and after a short warning a spark that runs once round it, clockwise from 12 o'clock. The check key stops it (Great,
    // Hit or Miss by where it stopped; no press by the end is a timeout). The outcome goes to the simulation as
    // FpWork.SendResult, the only thing this file sends; everything it reads from the simulation is synced state.
    //
    // The arc and great widths come only from synced pawn data (the skill's level and passion, manipulation, an
    // inspiration) and the tuning def, so a colonist is as hard to play for every player. Where the arc sits, the clock
    // and the sounds are this player's own: a System.Random seeded once, never Verse.Rand.
    //
    // The progress bar under the status line shows the tracked work, where each planned check falls due, the piece's
    // tally and form, and from a press until the simulation answers, a ghost of the work the result is expected to add or
    // take away.
    public enum FpRingState : byte { Idle, Warn, Needle, Result }

    public static class FpSkillCheck
    {
        private const int WindowId = 0x49524650;    // "IRFP": the ring's immediate window
        private const float Radius = 70f;           // UI pixels at HUD scale 1
        private const double ResultSeconds = 0.8;   // the result shows this long
        private const double ResultHold = 0.4;      // ... at full strength, then fades
        private const double ShakeSeconds = 0.25;   // a miss shakes the ring this long
        private const double MaxStep = 0.1;         // a longer frame (a hitch, a long event) moves the clock this much at most
        private const double GhostSeconds = 5.0;    // the expected grant shows at most this long without an answer
        internal const double CursorGrace = 3.0;    // a warning waits this long in all for the cursor to be captured
        private const int TrackSegments = 48;
        private const float TrackStepDeg = 360f / TrackSegments;
        private const float DashDeg = 4f;           // colour-blind arc: dash length
        private const float JoinDeg = 0.4f;         // solid arcs: each segment overlaps the next, so no gap shows at the rim
        private const int MaxPips = 12;
        private const float StatusFromBottom = 112f;   // FpHud's status line (spec 14), UI pixels from the bottom
        private const float BarFromBottom = 92f;       // spec 10.7

        // Spec 10.7.
        private static readonly Color ConstructionColour = new Color32(0x7F, 0xA7, 0xC9, 0xFF);
        private static readonly Color MiningColour = new Color32(0xA6, 0x8B, 0x6D, 0xFF);
        private static readonly Color PlantsColour = new Color32(0x7D, 0xBE, 0x6A, 0xFF);
        private static readonly Color CookingColour = new Color32(0xE8, 0xA3, 0x3D, 0xFF);
        private static readonly Color CraftingColour = new Color32(0xC9, 0xA2, 0x6B, 0xFF);
        private static readonly Color ArtisticColour = new Color32(0xB5, 0x8C, 0xE0, 0xFF);
        private static readonly Color MedicineColour = new Color32(0xE0, 0x7A, 0x8A, 0xFF);
        private static readonly Color OtherColour = new Color32(0xCF, 0xCF, 0xCF, 0xFF);
        private static readonly Color GreatColour = new Color32(0xFF, 0xD2, 0x4A, 0xFF);
        private static readonly Color MissColour = new Color32(0xE0, 0x50, 0x50, 0xFF);
        private static readonly Color DimTextColour = new Color(1f, 1f, 1f, 0.6f);

        private static readonly Action DrawWindowAction = DrawWindow;

        // Where the arc sits: this player's own randomness (spec 10.7), never Verse.Rand.
        private static readonly System.Random rng = new System.Random(Environment.TickCount);

        private static FpRingState state;
        private static Game game;                 // the game this state belongs to: a reload starts afresh

        // The check the ring is for.
        private static Pawn ringPawn;
        private static FpEntry ringEntry;
        private static int ringJob = -1;
        private static int ringSeq;

        // The clock: real time that stops while the ring is frozen. The game's pause freezes it (no tick runs then, so the
        // check can't expire either). A free cursor (Alt, a menu, a window, the world map, focus lost) freezes only the
        // warning, and only for CursorGrace seconds in all: a ring that comes up while the player is in a menu waits a
        // moment for them, but once it has warned it runs, and a needle nobody can answer runs out (a miss). So freeing the
        // cursor can't hold a check off until the simulation voids it at its deadline.
        // clockStart is when the warning began on that clock; the needle starts ringWarn seconds later.
        private static double clockStart;
        private static double ringElapsed;        // seconds on that clock at the last update (the drawing uses it)
        private static double lastUpdate;
        private static bool frozenPrev;           // the time since the last update was frozen
        private static bool pausedPrev;           // ... by the game's pause (drawn as "paused")
        private static double cursorWaited;       // seconds this ring's warning has waited for the cursor
        private static int ringRaisedTick;        // the check's FpEntry.pendingTick (map ticks)
        private static bool forfeited;            // the result shown is the simulation's: the work was left (FpWork)
        private static bool keyArmed;             // at the last update the check key was the ring's, and the cursor captured

        // The ring as set up when it appeared.
        private static float arcStart, arcWidth, greatWidth;
        private static float needleDeg;
        private static SkillDef skill;
        private static int level;
        private static int passion;
        private static Color colour = OtherColour;

        // The result.
        private static byte outcome = 255;
        private static double resultAt = -1000;

        // The check answered last: its ring isn't shown again while the result travels (spec 10.7). Keyed by the entry
        // too, so a new control (a reload, leaving and taking control again) starts with nothing answered.
        private static FpEntry answeredEntry;
        private static int answeredJob = -1;
        private static int answeredSeq;

        // A result not sent yet: Multiplayer takes no command while it simulates, so it goes on the next frame it can.
        private static bool owed;
        private static Pawn owedPawn;
        private static FpEntry owedEntry;
        private static int owedJob;
        private static int owedSeq;
        private static byte owedOutcome;

        // The expected grant, from a press until the simulation resolves the check (e.issued moves on).
        private static FpEntry ghostEntry;
        private static int ghostJob = -1;
        private static int ghostIssued;
        private static byte ghostOutcome;
        private static double ghostUntil = -1000;

        private static int guiFrame = -1;         // an IMGUI event ran this frame: FpInput.FrameInputTime is this frame's

        private static InspirationDef surgeryInspiration;
        private static bool surgeryLooked;

        // Harness (FpTestM3): the last press as judged, in degrees from 12 o'clock, and its outcome (255 before any).
        internal static float LastPressDeg;
        internal static byte LastOutcome = 255;

        public static FpRingState State
        {
            get { return FpPlay.Mode == FpMode.Playing ? state : FpRingState.Idle; }
        }

        // The check key is the ring's: in the warning and while the spark runs, and for resultGrace seconds after a result
        // (a late double tap doesn't pause). Outside that, and while the game is paused (the ring is frozen then and Space
        // resumes the game, as in vanilla), the key keeps its game meaning. FpInput uses the key-down event up while this
        // is true and the cursor is captured.
        public static bool WantsKey
        {
            get
            {
                if (FpPlay.Mode != FpMode.Playing)
                {
                    return false;
                }
                switch (state)
                {
                    case FpRingState.Warn:
                    case FpRingState.Needle:
                        return !GamePaused();
                    case FpRingState.Result:
                        return Now - resultAt < FpTuning.Get.resultGrace;
                    default:
                        return false;
                }
            }
        }

        // Degrees clockwise from 12 o'clock.
        public static float ArcStartDeg
        {
            get { return arcStart; }
        }

        public static float ArcWidthDeg
        {
            get { return arcWidth; }
        }

        public static float GreatWidthDeg
        {
            get { return greatWidth; }
        }

        // Harness: where the spark is (0 in the warning; where it stopped once there's a result).
        public static float NeedleDeg
        {
            get { return needleDeg; }
        }

        // Degrees the spark runs per second of the ring's clock (313.04 with the default 1.15 s turn).
        internal static double DegPerSecond
        {
            get
            {
                float period = FpTuning.Get.ringPeriod;
                return period > 0f ? 360.0 / period : 360.0;
            }
        }

        private static double Now
        {
            get { return Time.realtimeSinceStartupAsDouble; }
        }

        private static float Scale
        {
            get { return Mathf.Clamp(FpSettings.hudScale, 0.75f, 1.5f); }
        }

        private static bool GamePaused()
        {
            return Find.TickManager != null && Find.TickManager.Paused;
        }

        // ---------------------------------------------------------------- lifecycle

        // From FpInput.OnRootUpdate, every frame of a game outside long events, playing or not. Runs after the frame's
        // IMGUI input pass (in which FpInput used up the check key's event while WantsKey was true) and after the capture
        // watchdog.
        internal static void Update()
        {
            double now = Now;
            double dt = Math.Max(0.0, now - lastUpdate);
            lastUpdate = now;
            if (Current.Game != game)
            {
                game = Current.Game;
                Reset();
            }
            Pawn p = FpPlay.Pawn;
            if (p == null)
            {
                Reset();
                return;
            }
            FpEntry e = FpRegistry.EntryOf(p);
            bool captured = FpInput.Captured;
            bool paused = GamePaused();
            bool armed = keyArmed;

            // The time since the last update counts as the state was then: stopped while frozen, and at most MaxStep (the
            // game doesn't tick through a hitch or a long event either, and the spark mustn't jump past the arc).
            if (frozenPrev)
            {
                clockStart += dt;
                if (!pausedPrev)
                {
                    cursorWaited += Math.Min(dt, MaxStep);   // the warning waited for the cursor
                }
            }
            else if (dt > MaxStep)
            {
                clockStart += dt - MaxStep;
            }
            frozenPrev = paused || !captured && state == FpRingState.Warn && cursorWaited < CursorGrace;
            pausedPrev = paused;

            SendOwed(p, e);
            switch (state)
            {
                case FpRingState.Idle:
                    if (CanStart(p, e))
                    {
                        Start(p, e, now);
                        frozenPrev = paused || !captured;   // a ring that comes up under a menu waits for the cursor
                    }
                    break;
                case FpRingState.Warn:
                case FpRingState.Needle:
                    if (!StillPending(p, e))
                    {
                        if (ResolvedAsMiss(e))
                        {
                            ShowForfeit(now);
                        }
                        else
                        {
                            Cancel("the check ended");
                        }
                        break;
                    }
                    Run(p, e, now, armed);
                    break;
                case FpRingState.Result:
                    if (now - resultAt >= ResultSeconds)
                    {
                        state = FpRingState.Idle;
                    }
                    break;
            }
            keyArmed = captured && WantsKey;
        }

        // Nothing showing and nothing owed: play ended, or a new game.
        private static void Reset()
        {
            state = FpRingState.Idle;
            ringPawn = null;
            ringEntry = null;
            ringJob = -1;
            ringSeq = 0;
            answeredEntry = null;
            answeredJob = -1;
            answeredSeq = 0;
            owed = false;
            owedPawn = null;
            owedEntry = null;
            ghostEntry = null;
            keyArmed = false;
            frozenPrev = false;
            pausedPrev = false;
            cursorWaited = 0.0;
            forfeited = false;
            needleDeg = 0f;
        }

        // Spec 10.7: playing and confirmed, checks on, a check pending on the colonist's current job, not answered yet.
        private static bool CanStart(Pawn p, FpEntry e)
        {
            if (!FpPlay.Confirmed || !FpSettings.skillChecks || e == null || e.pendingSeq == 0)
            {
                return false;
            }
            if (p.CurJob == null || p.CurJob.loadID != e.checkJob || p.Map == null || p.Map != Find.CurrentMap)
            {
                return false;
            }
            return !(answeredEntry == e && answeredJob == e.checkJob && answeredSeq == e.pendingSeq);
        }

        // The check the ring shows is still the pending one (the simulation voids it at its deadline, when the job or the
        // work toil ends, and on release; it counts it as missed when the player leaves the work). Read from the
        // simulation's entry only: a job that just changed is settled by the next map tick, and the ring runs until then
        // (an answer that arrives after it is stale and changes nothing).
        private static bool StillPending(Pawn p, FpEntry e)
        {
            return p == ringPawn && e != null && e == ringEntry && e.pendingSeq == ringSeq && e.checkJob == ringJob
                && FpPlay.Confirmed && FpSettings.skillChecks;
        }

        // The simulation settled the ring's check as a miss of its own (FpWork.ForfeitPending or LeftJob: the work was left,
        // or checks were turned off): shown as one, so leaving visibly costs the check.
        private static bool ResolvedAsMiss(FpEntry e)
        {
            return e != null && e == ringEntry && e.pendingSeq != ringSeq && e.lastOutcome <= FpOutcome.Miss && e.lastOutcomeTick >= ringRaisedTick
                && FpPlay.Confirmed && FpSettings.skillChecks;
        }

        private static void ShowForfeit(double now)
        {
            state = FpRingState.Result;
            outcome = FpOutcome.MissTimeout;
            forfeited = true;
            resultAt = now;
            Play(SoundDefOf.ClickReject);
            FpHits.Hit("Check ring forfeited");
            if (FpTestKit.Active)
            {
                FpTestKit.Info("ring forfeited job=" + ringJob + " seq=" + ringSeq + ": the simulation counted the check as missed");
            }
        }

        private static void Start(Pawn p, FpEntry e, double now)
        {
            FpTuningDef t = FpTuning.Get;
            skill = FpWork.SkillOf(p.jobs?.curDriver);
            Difficulty(p, skill, t, out level, out passion, out arcWidth, out greatWidth);
            float room = Mathf.Max(0f, t.arcEndMax - arcWidth - t.arcStartMin);
            arcStart = t.arcStartMin + (float)(rng.NextDouble() * room);
            colour = SkillColour(skill);
            ringPawn = p;
            ringEntry = e;
            ringJob = e.checkJob;
            ringSeq = e.pendingSeq;
            ringRaisedTick = e.pendingTick;
            state = FpRingState.Warn;
            clockStart = now;
            ringElapsed = 0.0;
            needleDeg = 0f;
            cursorWaited = 0.0;
            forfeited = false;
            Play(SoundDefOf.Tick_Low);
            FpHits.Hit("Check ring shown");
            if (FpTestKit.Active)
            {
                FpTestKit.Info("ring shown p=" + p.thingIDNumber + " job=" + ringJob + " seq=" + ringSeq + " skill=" + (skill?.defName ?? "none")
                    + " L=" + level + " passion=" + passion + " arc=" + arcStart.ToString("0.0") + "+" + arcWidth.ToString("0.0")
                    + " great=" + greatWidth.ToString("0.0"));
            }
        }

        private static void Cancel(string why)
        {
            state = FpRingState.Idle;
            FpHits.Hit("Check ring cancelled");
            if (FpTestKit.Active)
            {
                FpTestKit.Info("ring cancelled job=" + ringJob + " seq=" + ringSeq + ": " + why);
            }
        }

        // The warning, then the spark; a press while it runs, or its end, gives the result.
        private static void Run(Pawn p, FpEntry e, double now, bool armed)
        {
            FpTuningDef t = FpTuning.Get;
            double rate = DegPerSecond;
            ringElapsed = now - clockStart;   // constant while frozen: clockStart moves on with real time then
            double needleStart = clockStart + t.ringWarn;
            if (state == FpRingState.Warn && now >= needleStart)
            {
                state = FpRingState.Needle;
            }
            if (state != FpRingState.Needle)
            {
                return;
            }
            needleDeg = Mathf.Min(360f, (float)((now - needleStart) * rate));

            // A press counts only when the check key was the ring's as the frame's input ran (FpInput used its event up, so
            // it didn't pause the game) and the ring wasn't frozen. Polled: once per press, no auto-repeat.
            if (armed && FpKeyDefOf.R3D_FpSkillCheck != null && FpKeyDefOf.R3D_FpSkillCheck.JustPressed)
            {
                // The press is timed by the frame's input (FpInput.FrameInputTime, taken at this frame's first IMGUI event,
                // which ran before this update), less the player's display latency setting.
                double pressTime = guiFrame == Time.frameCount ? Math.Min(FpInput.FrameInputTime, now) : now;
                float press = (float)((pressTime - FpSettings.checkLatencyMs / 1000.0 - needleStart) * rate);
                if (press >= 360f)
                {
                    Finish(p, e, FpOutcome.MissTimeout, 360f, now);
                    return;
                }
                if (press >= 0f)
                {
                    float halfFrame = 0.5f * Time.unscaledDeltaTime * (float)rate;
                    Finish(p, e, FpMath.Judge(press, arcStart, arcWidth, greatWidth, halfFrame), press, now);
                    return;
                }
                // Before the spark started (in the warning, once the latency is taken off): ignored, as spec 10.7 says.
            }
            if (needleDeg >= 360f)
            {
                Finish(p, e, FpOutcome.MissTimeout, 360f, now);
            }
        }

        private static void Finish(Pawn p, FpEntry e, byte result, float pressDeg, double now)
        {
            state = FpRingState.Result;
            outcome = result;
            forfeited = false;
            resultAt = now;
            needleDeg = Mathf.Clamp(pressDeg, 0f, 360f);
            LastPressDeg = pressDeg;
            LastOutcome = result;
            answeredEntry = e;
            answeredJob = ringJob;
            answeredSeq = ringSeq;
            owed = true;
            owedPawn = p;
            owedEntry = e;
            owedJob = ringJob;
            owedSeq = ringSeq;
            owedOutcome = result;
            ghostEntry = e;
            ghostJob = ringJob;
            ghostIssued = e.issued;
            ghostOutcome = result;
            ghostUntil = now + GhostSeconds;
            switch (result)
            {
                case FpOutcome.Great:
                    Play(SoundDefOf.Tick_High);
                    break;
                case FpOutcome.Hit:
                    Play(SoundDefOf.Click);
                    break;
                default:
                    Play(SoundDefOf.ClickReject);
                    break;
            }
            FpHits.Hit("Check ring answered");
            if (FpTestKit.Active)
            {
                FpTestKit.Info("ring answered job=" + ringJob + " seq=" + ringSeq + " press=" + pressDeg.ToString("0.0") + " arc="
                    + arcStart.ToString("0.0") + "+" + arcWidth.ToString("0.0") + " great=" + greatWidth.ToString("0.0") + " outcome=" + result);
            }
            SendOwed(p, e);
        }

        // The result goes out once Multiplayer takes commands; it is dropped if the check ended meanwhile (the deadline, the
        // job ended, a reload).
        private static void SendOwed(Pawn p, FpEntry e)
        {
            if (!owed)
            {
                return;
            }
            if (p != owedPawn || e == null || e != owedEntry || e.pendingSeq != owedSeq || e.checkJob != owedJob)
            {
                owed = false;
                return;
            }
            if (!FpWork.SendResult(p, owedJob, owedSeq, owedOutcome))
            {
                return;   // Multiplayer takes no command now: the next frame tries again
            }
            owed = false;
            FpHits.Hit("Check result sent");
        }

        private static void Play(SoundDef sound)
        {
            if (FpSettings.checkSounds && sound != null)
            {
                sound.PlayOneShotOnCamera();
            }
        }

        // ---------------------------------------------------------------- difficulty (synced pawn data and the def only)

        private static void Difficulty(Pawn p, SkillDef sk, FpTuningDef t, out int lvl, out int pas, out float arc, out float great)
        {
            lvl = 0;
            pas = 0;
            SkillRecord r = Record(p, sk);
            if (r != null)
            {
                lvl = r.Level;
                pas = (int)r.passion;
            }
            float manip = p.health?.capacities != null ? p.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation) : 1f;
            bool inspired = Inspired(p, sk);
            FpRingNumbers n = t.RingNumbers;
            arc = FpMath.ArcWidth(lvl, pas, manip, inspired, n);
            great = FpMath.GreatWidth(lvl, pas, manip, inspired, n);
        }

        // The pawn's record of a skill, or null (no skills, no skill). Not Pawn_SkillTracker.GetSkill: it logs an error
        // for a skill the pawn lacks.
        private static SkillRecord Record(Pawn p, SkillDef sk)
        {
            if (sk == null || p.skills?.skills == null)
            {
                return null;
            }
            foreach (SkillRecord r in p.skills.skills)
            {
                if (r.def == sk)
                {
                    return r;
                }
            }
            return null;
        }

        // Creativity for crafting and art, the surgery inspiration for medicine.
        private static bool Inspired(Pawn p, SkillDef sk)
        {
            InspirationDef cur = p.mindState?.inspirationHandler?.CurStateDef;
            if (cur == null || sk == null)
            {
                return false;
            }
            if (sk == SkillDefOf.Crafting || sk == SkillDefOf.Artistic)
            {
                return cur == InspirationDefOf.Inspired_Creativity;
            }
            if (sk == SkillDefOf.Medicine)
            {
                if (!surgeryLooked)
                {
                    surgeryLooked = true;
                    surgeryInspiration = DefDatabase<InspirationDef>.GetNamedSilentFail("Inspired_Surgery");
                }
                return surgeryInspiration != null && cur == surgeryInspiration;
            }
            return false;
        }

        private static Color SkillColour(SkillDef sk)
        {
            if (sk == null)
            {
                return OtherColour;
            }
            if (sk == SkillDefOf.Construction)
            {
                return ConstructionColour;
            }
            if (sk == SkillDefOf.Mining)
            {
                return MiningColour;
            }
            if (sk == SkillDefOf.Plants)
            {
                return PlantsColour;
            }
            if (sk == SkillDefOf.Cooking)
            {
                return CookingColour;
            }
            if (sk == SkillDefOf.Crafting)
            {
                return CraftingColour;
            }
            if (sk == SkillDefOf.Artistic)
            {
                return ArtisticColour;
            }
            if (sk == SkillDefOf.Medicine)
            {
                return MedicineColour;
            }
            return OtherColour;
        }

        // ---------------------------------------------------------------- drawing

        // From the FpHud postfix (UIRoot_Play.UIRootOnGUI) on every event while playing. While the cursor is captured the
        // ring is an immediate window centred on the crosshair, above the game's other windows; while it isn't (the free
        // cursor, a menu, the world map, focus lost: the key can't answer it then), it is drawn dimmed in the HUD itself,
        // under the windows, so it can't take a click meant for one of them. The progress bar is always drawn in the HUD.
        internal static void OnGUI()
        {
            guiFrame = Time.frameCount;
            Event ev = Event.current;
            if (ev == null || ev.type != EventType.Repaint)
            {
                return;
            }
            Pawn p = FpPlay.Pawn;
            if (p == null || Find.UIRoot != null && Find.UIRoot.screenshotMode.Active || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            float s = Scale;
            if (state != FpRingState.Idle)
            {
                if (FpInput.Captured)
                {
                    float half = Half(s);
                    Rect r = new Rect(Mathf.Round(UI.screenWidth / 2f - half), Mathf.Round(UI.screenHeight / 2f - half), 2f * half, 2f * half);
                    Find.WindowStack.ImmediateWindow(WindowId, r, WindowLayer.Super, DrawWindowAction, doBackground: false,
                        absorbInputAroundWindow: false, shadowAlpha: 0f);
                }
                else
                {
                    DrawRing(new Vector2(UI.screenWidth / 2f, UI.screenHeight / 2f), s, 0.5f);
                }
            }
            DrawProgress(p, s);
        }

        // Half the ring window's side: the radius plus the great part, the spark and a miss's shake.
        private static float Half(float s)
        {
            return Mathf.Ceil((Radius + 16f) * s);
        }

        private static void DrawWindow()
        {
            GameFont font = Text.Font;
            TextAnchor anchor = Text.Anchor;
            try
            {
                // The window lives a frame past its last request: once the cursor is free the HUD draws the ring instead.
                if (FpPlay.Mode != FpMode.Playing || state == FpRingState.Idle || !FpInput.Captured)
                {
                    return;
                }
                float s = Scale;
                float half = Half(s);
                DrawRing(new Vector2(half, half), s, 1f);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: drawing the skill-check ring failed: " + e, 93652501);
            }
            finally
            {
                Text.Font = font;
                Text.Anchor = anchor;
                GUI.color = Color.white;
            }
        }

        // The ring centred on c (window or screen coordinates). GUI.color stays white throughout: Widgets.DrawLine tints with
        // its colour argument on top of GUI.color under UnityStubs (CLAUDE.md).
        private static void DrawRing(Vector2 c, float s, float alphaMul)
        {
            FpTuningDef t = FpTuning.Get;
            double now = Now;
            float a = alphaMul;
            bool result = state == FpRingState.Result;
            if (state == FpRingState.Warn)
            {
                a *= Mathf.Clamp01((float)(ringElapsed / Math.Max(0.01, t.ringWarn)));
            }
            else if (result)
            {
                a *= 1f - Mathf.Clamp01((float)((now - resultAt - ResultHold) / (ResultSeconds - ResultHold)));
                double since = now - resultAt;
                if (outcome <= FpOutcome.Miss && since < ShakeSeconds)
                {
                    c.x += 3f * s * Mathf.Sin((float)since * 2f * Mathf.PI * 20f) * (1f - (float)(since / ShakeSeconds));
                }
            }
            if (frozenPrev && !result)
            {
                a = Mathf.Max(a, 0.6f * alphaMul);   // a ring that appeared frozen still shows
            }
            if (a <= 0.01f)
            {
                return;
            }
            GUI.color = Color.white;
            float r = Radius * s;
            bool blind = FpSettings.colourBlind;

            Color track = new Color(1f, 1f, 1f, 0.25f * a);
            for (int i = 0; i < TrackSegments; i++)
            {
                Widgets.DrawLine(Rim(c, r, i * TrackStepDeg), Rim(c, r, (i + 1) * TrackStepDeg), track, Width(2f, s));
            }

            Color arcColour = colour;
            arcColour.a = a;
            DrawArc(c, r, arcStart, arcWidth, arcColour, Width(5f, s), blind);
            float greatStart = arcStart + (arcWidth - greatWidth) / 2f;
            Color core = blind ? Color.white : Color.Lerp(colour, Color.white, 0.5f);
            core.a = a;
            DrawArc(c, r, greatStart, greatWidth, core, Width(8f, s), false);
            if (blind)
            {
                Color edge = new Color(1f, 1f, 1f, a);
                float out1 = r + 9f * s;
                float in1 = r - 9f * s;
                Widgets.DrawLine(Rim(c, in1, greatStart), Rim(c, out1, greatStart), edge, Width(2f, s));
                Widgets.DrawLine(Rim(c, in1, greatStart + greatWidth), Rim(c, out1, greatStart + greatWidth), edge, Width(2f, s));
            }

            DrawSpark(c, r, s, a);
            DrawCentre(c, s, a);
            GUI.color = Color.white;
        }

        // An arc of the ring as straight segments (7.5 degrees each, so the chords keep to the circle), or every other
        // short dash for the colour-blind option.
        private static void DrawArc(Vector2 c, float r, float from, float width, Color col, float thick, bool dashed)
        {
            if (width <= 0f)
            {
                return;
            }
            int n = Mathf.Max(1, Mathf.CeilToInt(width / (dashed ? DashDeg : TrackStepDeg)));
            float step = width / n;
            for (int i = 0; i < n; i++)
            {
                if (dashed && (i & 1) == 1)
                {
                    continue;
                }
                float a0 = from + i * step;
                float a1 = from + (i + 1) * step;
                if (!dashed && i < n - 1)
                {
                    a1 += JoinDeg;
                }
                Widgets.DrawLine(Rim(c, r, a0), Rim(c, r, a1), col, thick);
            }
        }

        // The spark: a 6x6 white box on the rim with five trailing boxes 3 degrees apart, fading (spec 10.7). Still and dim
        // at 12 o'clock in the warning; stopped where it was pressed in the result.
        private static void DrawSpark(Vector2 c, float r, float s, float a)
        {
            float size = Mathf.Max(3f, Mathf.Round(6f * s));
            if (state == FpRingState.Warn)
            {
                Box(Rim(c, r, 0f), size, new Color(1f, 1f, 1f, 0.6f * a));
                return;
            }
            if (state == FpRingState.Needle)
            {
                for (int k = 5; k >= 1; k--)
                {
                    float deg = needleDeg - 3f * k;
                    if (deg < 0f)
                    {
                        continue;
                    }
                    float fade = 0.8f - (k - 1) * 0.175f;
                    Box(Rim(c, r, deg), Mathf.Max(2f, size - k * 0.4f * s), new Color(1f, 1f, 1f, fade * a));
                }
            }
            Box(Rim(c, r, needleDeg), size, new Color(1f, 1f, 1f, a));
        }

        // "<Skill> <level>" with the passion flames (and, in the warning, the key to press), or the result.
        private static void DrawCentre(Vector2 c, float s, float a)
        {
            if (state == FpRingState.Result)
            {
                string word;
                Color col;
                switch (outcome)
                {
                    case FpOutcome.Great:
                        word = "R3D_Fp_Check_Great".Translate();
                        col = GreatColour;
                        break;
                    case FpOutcome.Hit:
                        word = "R3D_Fp_Check_Hit".Translate();
                        col = Color.white;
                        break;
                    default:
                        word = "R3D_Fp_Check_Miss".Translate();
                        col = MissColour;
                        break;
                }
                col.a = a;
                Text.Font = s >= 0.9f ? GameFont.Medium : GameFont.Small;
                float lh = Text.LineHeight;
                Label(new Rect(c.x - 80f * s, c.y - lh / 2f, 160f * s, lh), word, col, TextAnchor.MiddleCenter);
                if (outcome == FpOutcome.MissTimeout)
                {
                    Text.Font = GameFont.Tiny;
                    Label(new Rect(c.x - 80f * s, c.y + lh / 2f, 160f * s, Text.LineHeight),
                        (forfeited ? "R3D_Fp_Check_Left" : "R3D_Fp_Check_TooLate").Translate(), new Color(1f, 1f, 1f, 0.7f * a), TextAnchor.UpperCenter);
                }
                return;
            }

            // One line just above the centre: the skill and level, then the passion flame; the key (or "paused") under it.
            // Kept compact: FpHud's hint line starts 18 px under the crosshair.
            Text.Font = s >= 0.9f ? GameFont.Small : GameFont.Tiny;
            string name = skill == null ? "R3D_Fp_Check_Work".Translate().Resolve()
                : (skill.skillLabel.NullOrEmpty() ? skill.LabelCap.Resolve() : skill.skillLabel.CapitalizeFirst());
            string label = "R3D_Fp_Check_Label".Translate(name, level);
            Texture2D flame = passion >= 2 ? SkillUI.PassionMajorIcon : passion == 1 ? SkillUI.PassionMinorIcon : null;
            float icon = flame != null ? Mathf.Round(14f * s) : 0f;
            float iconGap = flame != null ? Mathf.Round(2f * s) : 0f;
            float room = 2f * (Radius - 14f) * s - icon - iconGap;
            if (Text.CalcSize(label).x + 2f > room && Text.Font != GameFont.Tiny)
            {
                // A long skill name with its passion flame ("Construction 10" and the flame, M4-M6 test-copy screenshots)
                // didn't fit the ring's inside, and the level was cut off: the smaller font keeps the whole label.
                Text.Font = GameFont.Tiny;
            }
            float h = Text.LineHeight;
            float lw = Mathf.Min(Text.CalcSize(label).x + 2f, room);
            float x = Mathf.Round(c.x - (lw + iconGap + icon) / 2f);
            float y = Mathf.Round(c.y - h);
            Label(new Rect(x, y, lw, h), label, new Color(1f, 1f, 1f, a), TextAnchor.MiddleLeft);
            if (flame != null)
            {
                GUI.color = new Color(1f, 1f, 1f, a);
                GUI.DrawTexture(new Rect(x + lw + iconGap, Mathf.Round(y + (h - icon) / 2f), icon, icon), flame);
                GUI.color = Color.white;
            }
            y += h;

            string sub = null;
            if (pausedPrev)
            {
                sub = "R3D_Fp_Check_Paused".Translate();
            }
            else if (state == FpRingState.Warn)
            {
                sub = FpHud.KeyLabel(FpKeyDefOf.R3D_FpSkillCheck);
            }
            if (sub != null)
            {
                Text.Font = GameFont.Tiny;
                Label(new Rect(c.x - 64f * s, y, 128f * s, Text.LineHeight), sub, new Color(1f, 1f, 1f, DimTextColour.a * a), TextAnchor.UpperCenter);
            }
        }

        // ---------------------------------------------------------------- progress bar

        // Spec 10.7: bottom centre under the status line, filled to the tracked progress, a tick where each planned check
        // falls due, the piece's tally as pips and its form, and the ghost of the expected grant.
        private static void DrawProgress(Pawn p, float s)
        {
            float prog = FpWork.Progress01(p);
            if (prog < 0f)
            {
                return;
            }
            prog = Mathf.Clamp01(prog);
            FpTuningDef t = FpTuning.Get;
            FpEntry e = FpRegistry.EntryOf(p);
            JobDriver d = p.jobs?.curDriver;
            SkillDef sk = state != FpRingState.Idle && skill != null ? skill : FpWork.SkillOf(d);

            float w = Mathf.Round(240f * s);
            float h = Mathf.Max(3f, Mathf.Round(6f * s));
            Rect bar = new Rect(Mathf.Round(UI.screenWidth / 2f - w / 2f), Mathf.Round(BarY(s)), w, h);
            GUI.color = Color.white;
            Widgets.DrawBoxSolid(bar.ExpandedBy(1f), new Color(0f, 0f, 0f, 0.55f));
            Widgets.DrawBoxSolid(bar, new Color(1f, 1f, 1f, 0.12f));
            Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * prog, bar.height), SkillColour(sk));
            DrawGhost(p, e, d, bar, prog, t);
            DrawDueTicks(p, e, bar, s, t);
            DrawTally(p, bar, s);
            GUI.color = Color.white;
        }

        // Under FpHud's status line: at spec 10.7's height when that clears the status panel, else just under the panel.
        // Both move up with the status line while the game's gizmo grid is drawn (FpHud.StatusTop).
        private static float BarY(float s)
        {
            GameFont font = s < 0.9f ? GameFont.Tiny : s >= 1.3f ? GameFont.Medium : GameFont.Small;   // FpHud.FontFor
            float statusTop = FpHud.StatusTop(s);
            float panelBottom = statusTop + Text.LineHeightOf(font) + 2f * s;
            return Mathf.Max(statusTop + (StatusFromBottom - BarFromBottom) * s, panelBottom + 8f * s);
        }

        // From a press until the simulation resolves the check: the work the result is expected to add (light, ahead of the
        // fill) or take away (red, over the end of the fill), as FpWork grants it (a share of the work since the last check).
        private static void DrawGhost(Pawn p, FpEntry e, JobDriver d, Rect bar, float prog, FpTuningDef t)
        {
            if (e == null || d == null || e != ghostEntry || e.checkJob != ghostJob || e.issued != ghostIssued || Now >= ghostUntil)
            {
                return;
            }
            if (p.CurJob == null || p.CurJob.loadID != ghostJob)
            {
                return;
            }
            IFpWorkAdapter ad = FpWork.AdapterFor(d);
            if (ad == null)
            {
                return;
            }
            float total = ad.Total(d);
            if (total <= 0f)
            {
                return;
            }
            float seg = Mathf.Max(0f, ad.Done(d) - e.segStart) / total;
            float grant = ghostOutcome == FpOutcome.Great ? t.grantGreat : ghostOutcome == FpOutcome.Hit ? t.grantHit : t.grantMiss;
            float to = Mathf.Clamp(prog + seg * grant, 0f, 0.99f);
            if (to > prog)
            {
                Widgets.DrawBoxSolid(new Rect(bar.x + bar.width * prog, bar.y, bar.width * (to - prog), bar.height), new Color(1f, 1f, 1f, 0.45f));
            }
            else if (to < prog)
            {
                Color red = MissColour;
                red.a = 0.8f;
                Widgets.DrawBoxSolid(new Rect(bar.x + bar.width * to, bar.y, bar.width * (prog - to), bar.height), red);
            }
        }

        // A tick at each planned check's due point (FpMath.DuePermille with the same hash FpWork schedules with): passed
        // ones faint, the pending one gold.
        private static void DrawDueTicks(Pawn p, FpEntry e, Rect bar, float s, FpTuningDef t)
        {
            if (e == null || e.planned <= 0 || e.checkJob < 0 || p.CurJob == null || p.CurJob.loadID != e.checkJob)
            {
                return;
            }
            float tw = Mathf.Max(1f, Mathf.Round(2f * s));
            float over = Mathf.Round(2f * s);
            for (int k = 0; k < e.planned; k++)
            {
                int due = FpMath.DuePermille(k, e.planned, e.p0, t.endDuePermille, Gen.HashCombineInt(e.checkJob, k));
                float x = bar.x + bar.width * Mathf.Clamp(due, 0, 1000) / 1000f;
                Color col = k < e.issued ? new Color(1f, 1f, 1f, 0.3f)
                    : k == e.issued && e.pendingSeq != 0 ? GreatColour : new Color(1f, 1f, 1f, 0.85f);
                Widgets.DrawBoxSolid(new Rect(Mathf.Round(x - tw / 2f), bar.y - over, tw, bar.height + 2f * over), col);
            }
        }

        // The piece's tally right of the bar (gold Greats, white Hits, red Misses) and its form left of it.
        private static void DrawTally(Pawn p, Rect bar, float s)
        {
            FpTally tally = FpRegistry.TallyForWorker(p);
            if (tally == null || tally.Count == 0)
            {
                return;
            }
            float pip = Mathf.Max(3f, Mathf.Round(6f * s));
            float gap = Mathf.Max(2f, Mathf.Round(3f * s));
            float x = bar.xMax + Mathf.Round(10f * s);
            float y = Mathf.Round(bar.center.y - pip / 2f);
            int drawn = 0;
            drawn = Pips(ref x, y, pip, gap, tally.great, GreatColour, drawn);
            drawn = Pips(ref x, y, pip, gap, tally.hit, Color.white, drawn);
            drawn = Pips(ref x, y, pip, gap, tally.miss, MissColour, drawn);
            Text.Font = GameFont.Tiny;
            float lh = Text.LineHeight;
            if (tally.Count > drawn)
            {
                Label(new Rect(x, bar.center.y - lh / 2f, 60f, lh), "+" + (tally.Count - drawn), DimTextColour, TextAnchor.MiddleLeft);
            }
            int form = tally.Form;
            string sign = form > 0 ? "+" + form : form.ToString();
            Color fc = form > 0 ? GreatColour : form < 0 ? MissColour : Color.white;
            float fw = 100f * s;
            Label(new Rect(bar.x - Mathf.Round(10f * s) - fw, bar.center.y - lh / 2f, fw, lh), "R3D_Fp_Check_Form".Translate(sign), fc, TextAnchor.MiddleRight);
        }

        private static int Pips(ref float x, float y, float pip, float gap, int count, Color col, int drawn)
        {
            for (int i = 0; i < count && drawn < MaxPips; i++, drawn++)
            {
                Widgets.DrawBoxSolid(new Rect(x - 1f, y - 1f, pip + 2f, pip + 2f), new Color(0f, 0f, 0f, 0.6f));
                Widgets.DrawBoxSolid(new Rect(x, y, pip, pip), col);
                x += pip + gap;
            }
            return drawn;
        }

        // ---------------------------------------------------------------- helpers

        // A point on the ring at an angle clockwise from 12 o'clock (UI y grows down).
        private static Vector2 Rim(Vector2 c, float r, float deg)
        {
            float rad = deg * Mathf.Deg2Rad;
            return new Vector2(c.x + Mathf.Sin(rad) * r, c.y - Mathf.Cos(rad) * r);
        }

        private static float Width(float px, float s)
        {
            return Mathf.Max(1f, px * s);
        }

        private static void Box(Vector2 centre, float size, Color col)
        {
            Widgets.DrawBoxSolid(new Rect(Mathf.Round(centre.x - size / 2f), Mathf.Round(centre.y - size / 2f), size, size), col);
        }

        // A shadowed label; leaves GUI.color white and the anchor at upper left.
        private static void Label(Rect r, string text, Color col, TextAnchor anchor)
        {
            Text.Anchor = anchor;
            GUI.color = new Color(0f, 0f, 0f, 0.8f * col.a);
            Widgets.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text.StripTags());
            GUI.color = col;
            Widgets.Label(r, text);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
