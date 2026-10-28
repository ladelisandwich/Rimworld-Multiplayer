using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using K = IsoRim.Fp.FpTestKit;
using Key = UnityEngine.InputSystem.Key;
using S = IsoRim.Fp.FpScene;

namespace IsoRim.Fp
{
    // Milestone 1 scenarios (fp-play-spec 16, M1). m0-noop is M1.0's acceptance test and stays a regression test: with
    // play switched off, F5 is IsoRim's ride-along first person exactly as before. The m1-* scenarios (solo, windowed,
    // injected input) and mp1-* (host headless with Direct calls, client windowed) are written against the section 3
    // interfaces and pass once tracks A, B and C land.
    //
    // Deviation from the spec's table, forced by the scene: walking north from C1 meets the stove at +3 and the wall line
    // at +8 within about 7 cells, so m1-walk, m1-diag and m1-sector walk south (yaw 180, 180 + 45, 202.5) into the cleared
    // ground south of C1 instead (FpScene clears 20 cells south); m1-wall is the one that walks north.
    internal static class FpTestM1
    {
        internal static void Register(Dictionary<string, Func<IEnumerator<int>>> s)
        {
            s["m0-noop"] = NoOp;
            s["m1-enter"] = Enter;
            s["m1-look"] = Look;
            s["m1-walk"] = Walk;
            s["m1-diag"] = Diag;
            s["m1-sector"] = Sector;
            s["m1-wall"] = Wall;
            s["m1-idle"] = Idle;
            s["m1-interrupt"] = Interrupt;
            s["m1-pick"] = Pick;
            s["m1-lmb"] = Lmb;
            s["m1-far"] = Far;
            s["m1-rmb"] = Rmb;
            s["m1-leak"] = Leak;
            s["m1-esc"] = Esc;
            s["m1-alt"] = Alt;
            s["m1-space"] = Space;
            s["m1-draft"] = Draft;
            s["m1-ride"] = Ride;
            s["m1-down"] = Down;
            s["m1-load"] = Load;
            s["mp1-walk"] = MpWalk;
            s["mp1-race"] = MpRace;
            s["mp1-joinpoint"] = MpJoinPoint;
            s["mp1-leave"] = MpLeave;
            s["mp1-lmb"] = MpLmb;
        }

        private const string ControlJob = "IsoRimFp_Control";

        // ---------------------------------------------------------------- helpers

        private static string JobOf(Pawn p)
        {
            return p?.CurJob == null ? "none" : p.CurJob.def.defName + " " + p.CurJob.targetA;
        }

        private static bool InControlJob(Pawn p)
        {
            return p?.CurJob?.def?.defName == ControlJob;
        }

        private static bool Locked => Cursor.lockState == CursorLockMode.Locked && !Cursor.visible;

        private static bool Free => Cursor.lockState == CursorLockMode.None && Cursor.visible;

        private static string CursorText => "lockState " + Cursor.lockState + ", visible " + Cursor.visible;

        private static string Mode => "mode " + FpPlay.Mode + ", confirmed " + FpPlay.Confirmed + ", controller " + FpState.ControllerOf(S.C1)
            + ", first person " + FirstPerson.On + " (" + (FirstPerson.Pawn?.LabelShort ?? "-") + ")";

        private static void SelectOnly(Thing t)
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(t, false, false);
        }

        private static void CloseMainTab()
        {
            if (Find.MainTabsRoot.OpenTab != null)
            {
                Find.MainTabsRoot.EscapeCurrentTab(false);
            }
        }

        // F5 on a selected pawn, then wait for play to be confirmed (or fail after 10 s).
        private static IEnumerable<int> EnterPlay(Pawn p)
        {
            SelectOnly(p);
            yield return K.Tap(Key.F5);
            yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Playing && FpPlay.Confirmed, "for play to be confirmed on " + p.LabelShort, 600);
            // The idle colonist was usually wandering when play began, and the control job lets the step in progress
            // finish (spec 5.5): the walking scenarios measure from where it then stands.
            yield return K.WaitUntil(() => InControlJob(p) && !p.pather.Moving, "for " + p.LabelShort + " to stand in the control job", 600);
            yield return 30;
        }

        private static IEnumerable<int> LeavePlay()
        {
            if (FpPlay.Mode == FpMode.Playing || FirstPerson.On)
            {
                yield return K.Tap(Key.F5);
                yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Off && !FirstPerson.On, "for first person to end", 600);
            }
            yield return 10;
        }

        // Turns the play camera to an absolute yaw and pitch (FpCamera only takes deltas).
        private static void Turn(float yaw, float pitch)
        {
            FpCamera.Look(FpMath.DeltaDeg(FpCamera.Yaw, yaw), pitch - FpCamera.Pitch);
        }

        // Points the crosshair at a true-space point (x, height, z), from the camera's eye. Twice, a few frames apart: the
        // eye sits a little forward along the yaw, so it moves with the first turn.
        private static IEnumerable<int> AimAt(Vector3 target)
        {
            for (int i = 0; i < 2; i++)
            {
                Vector3 eye = View3D.EyePosition;
                Vector3 d = target - eye;
                float flat = Mathf.Sqrt(d.x * d.x + d.z * d.z);
                float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                float pitch = Mathf.Atan2(-d.y, Mathf.Max(flat, 0.01f)) * Mathf.Rad2Deg;
                Turn(yaw, pitch);
                yield return 3;
            }
        }

        // A thing's middle at a given height (cells).
        private static Vector3 At(Thing t, float height)
        {
            Vector3 c = t.TrueCenter();
            return new Vector3(c.x, height, c.z);
        }

        private static int ErrorCount()
        {
            return Log.Messages.Count(m => m.type == LogMessageType.Error);
        }

        // ---------------------------------------------------------------- M1.0

        // F5 with play off: IsoRim's ride-along first person, exactly as before the feature (and the port hooks fire).
        private static IEnumerator<int> NoOp()
        {
            bool play0 = FpSettings.play;
            FpSettings.play = false;
            K.Check("m0 hooks installed", FpHooks.MissingCount == 0, FpHooks.MissingCount == 0 ? "none missing" : string.Join("; ", FpHooks.Missing));
            int toggles0 = FpHits.Count("Port Toggle");
            int left0 = FpHits.Count("Port CameraLeft");
            SelectOnly(S.C1);
            yield return K.Tap(Key.F5);
            yield return 120;
            K.Check("m0 first person on C1", FirstPerson.On && FirstPerson.Pawn == S.C1, Mode);
            K.Check("m0 play stays off", FpPlay.Mode == FpMode.Off && FpRegistry.EntryOf(S.C1) == null && FpState.ControllerOf(S.C1) == FpIds.None, Mode);
            K.Check("m0 cursor free", Free, CursorText);
            K.Check("m0 job is the colonist's own", !InControlJob(S.C1), JobOf(S.C1));
            K.Info("open main tab after F5: " + (Find.MainTabsRoot.OpenTab?.defName ?? "none") + " (the game's Wildlife key is F5 too)");
            K.Check("m0 port hooks fire", FpHits.Count("Port Toggle") > toggles0 && FpHits.Count("Port OnRootUpdate") > 0 && FpHits.Count("Port LookFrame") > 0,
                "\n" + FpHits.Dump());
            K.Shot("m0_ride_along");
            CloseMainTab();
            yield return K.Tap(Key.F5);
            yield return 60;
            K.Check("m0 first person off again", !FirstPerson.On && FpHits.Count("Port CameraLeft") > left0, Mode);
            CloseMainTab();
            FpSettings.play = play0;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- M1 solo

        private static IEnumerator<int> Enter()
        {
            SelectOnly(S.C1);
            yield return K.Tap(Key.F5);
            // Measured from the key, before the 120 frames below: checked after them, any job that came within 120 frames
            // would have passed.
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => InControlJob(S.C1) || K.Ticks > tick0 + 60, "for the control job");
            int jobAfter = InControlJob(S.C1) ? K.Ticks - tick0 : -1;
            yield return 120;
            K.Check("m1-enter playing", FpPlay.Mode == FpMode.Playing && FpPlay.Confirmed && FpPlay.Pawn == S.C1, Mode);
            K.Check("m1-enter cursor captured", Locked, CursorText);
            K.Check("m1-enter controller is single player", FpState.ControllerOf(S.C1) == FpIds.SinglePlayer, Mode);
            K.Check("m1-enter control job within 60 ticks", jobAfter >= 0 && jobAfter <= 60 && InControlJob(S.C1),
                JobOf(S.C1) + ", control job " + (jobAfter >= 0 ? jobAfter + " ticks after the key" : "not within 60 ticks"));
            K.Check("m1-enter no Wildlife tab", Find.MainTabsRoot.OpenTab?.defName != "Wildlife", Find.MainTabsRoot.OpenTab?.defName ?? "none");
            K.Shot("m1_enter");
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Look()
        {
            float sens0 = FpSettings.sensitivity;
            bool invert0 = IsoRimMod.Settings.invertY;
            FpSettings.sensitivity = 0.07f;
            IsoRimMod.Settings.invertY = false;
            foreach (int w in EnterPlay(S.C1)) yield return w;
            float yaw0 = FpCamera.Yaw;
            yield return K.MouseDelta(20f, 0f, 10);
            yield return 5;
            float dYaw = FpMath.DeltaDeg(yaw0, FpCamera.Yaw);
            K.Check("m1-look yaw +14", Mathf.Abs(dYaw - 14f) <= 1.5f, "yaw " + yaw0.ToString("0.0") + " -> " + FpCamera.Yaw.ToString("0.0") + " (" + dYaw.ToString("0.0") + ")");
            float pitch0 = FpCamera.Pitch;
            yield return K.MouseDelta(0f, 20f, 10);
            yield return 5;
            float dPitch = FpCamera.Pitch - pitch0;
            K.Check("m1-look pitch -14", Mathf.Abs(dPitch + 14f) <= 1.5f, "pitch " + pitch0.ToString("0.0") + " -> " + FpCamera.Pitch.ToString("0.0"));
            yield return K.MouseDelta(0f, 100f, 40);
            yield return 5;
            K.Check("m1-look pitch clamped", FpCamera.Pitch >= -80f, "pitch " + FpCamera.Pitch.ToString("0.0"));
            foreach (int w in LeavePlay()) yield return w;
            FpSettings.sensitivity = sens0;
            IsoRimMod.Settings.invertY = invert0;
            yield return K.Done();
        }

        private static IEnumerator<int> Walk()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            Turn(180f, 8f);
            yield return 10;
            IntVec3 p0 = S.C1.Position;
            yield return K.HoldTicks(180, Key.W);
            IntVec3 p1 = S.C1.Position;
            float perMove = S.C1.TicksPerMoveCardinal;
            int dz = p0.z - p1.z;   // southward
            int dx = p1.x - p0.x;
            float lo = 0.6f * 180f / perMove, hi = 1.1f * 180f / perMove;
            K.Check("m1-walk distance", dz >= lo && dz <= hi && Mathf.Abs(dx) <= 1,
                "moved " + dx + "," + (-dz) + " in 180 ticks, " + perMove.ToString("0.0") + " ticks per cell, south expected " + lo.ToString("0.0") + ".." + hi.ToString("0.0"));
            yield return K.WaitTicks(60);
            IntVec3 p2 = S.C1.Position;
            K.Check("m1-walk stops", (p2 - p1).LengthHorizontalSquared <= 2, "position after release " + p1 + ", 60 ticks later " + p2);
            K.Check("m1-walk still in control", InControlJob(S.C1), JobOf(S.C1));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Diag()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            Turn(180f, 8f);   // W + D: south-west
            yield return 10;
            IntVec3 p0 = S.C1.Position;
            yield return K.HoldTicks(150, Key.W, Key.D);
            IntVec3 d = S.C1.Position - p0;
            int ax = -d.x, az = -d.z;
            K.Check("m1-diag diagonal", ax >= 3 && az >= 3 && Mathf.Abs(ax - az) <= 1, "moved " + d.x + "," + d.z + " (south-west expected)");
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Sector()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            Turn(202.5f, 8f);   // S by SW: sector 9, alternating S and SW steps
            yield return 10;
            IntVec3 p0 = S.C1.Position;
            yield return K.HoldTicks(240, Key.W);
            IntVec3 d = S.C1.Position - p0;
            int ax = -d.x, az = -d.z;
            float ratio = az > 0 ? (float)ax / az : 0f;
            K.Check("m1-sector heading", az > ax && ax > 0 && ratio >= 0.3f && ratio <= 0.65f,
                "moved " + d.x + "," + d.z + ", |dx|/|dz| " + ratio.ToString("0.00"));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Wall()
        {
            int errors0 = ErrorCount();
            foreach (int w in EnterPlay(S.C1)) yield return w;
            Turn(0f, 8f);
            yield return 10;
            yield return K.HoldTicks(300, Key.W);
            yield return K.WaitTicks(30);
            IntVec3 rel = S.C1.Position - S.Origin;
            K.Check("m1-wall stops below the wall line", rel.z == S.WallLineZ - 1, "stopped at " + rel + " from the origin");
            K.Check("m1-wall no path errors", ErrorCount() == errors0, (ErrorCount() - errors0) + " new errors");
            K.Check("m1-wall still in control", InControlJob(S.C1), JobOf(S.C1));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Idle()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            IntVec3 p0 = S.C1.Position;
            bool always = true;
            string lost = "";
            for (int i = 0; i < 20; i++)
            {
                yield return K.WaitTicks(60);
                if (!InControlJob(S.C1) && always)
                {
                    always = false;
                    lost = " (lost at sample " + i + ": " + JobOf(S.C1) + ")";
                }
            }
            K.Check("m1-idle stands still", S.C1.Position == p0, p0 + " -> " + S.C1.Position);
            K.Check("m1-idle control job throughout", always, JobOf(S.C1) + lost);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Interrupt()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            Turn(180f, 8f);
            // A long player-forced job: carry the steel 15 cells south.
            Job haul = JobMaker.MakeJob(JobDefOf.HaulToCell, S.Steel, S.Origin + new IntVec3(0, 0, -15));
            haul.count = S.Steel.stackCount;
            haul.haulMode = HaulMode.ToCellNonStorage;
            S.C1.jobs.TryTakeOrderedJob(haul, JobTag.Misc);
            yield return K.WaitTicks(5);
            Job ordered = S.C1.CurJob;
            K.Check("m1-interrupt ordered job running", ordered?.def == JobDefOf.HaulToCell, JobOf(S.C1));
            yield return K.HoldTicks(12, Key.W);
            yield return K.WaitTicks(10);
            K.Check("m1-interrupt short press keeps the job", S.C1.CurJob == ordered, JobOf(S.C1));
            yield return K.HoldTicks(30, Key.W);
            yield return K.WaitUntil(() => InControlJob(S.C1), "for the control job after the long press", 300);
            K.Check("m1-interrupt long press takes over", InControlJob(S.C1), JobOf(S.C1));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Pick()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            foreach (int w in AimAt(At(S.Stove, 0.4f))) yield return w;
            yield return 5;
            FpHit h = FpPick.Current;
            K.Check("m1-pick stove", h.Valid && h.Thing == S.Stove && Verse.UI.MouseCell() == h.Cell,
                "hit " + (h.Thing?.ThingID ?? "none") + " cell " + h.Cell + ", mouse cell " + Verse.UI.MouseCell());
            foreach (int w in AimAt(At(S.Steel, 0.05f))) yield return w;
            yield return 5;
            h = FpPick.Current;
            K.Check("m1-pick steel", h.Valid && h.Thing == S.Steel && Verse.UI.MouseCell() == h.Cell,
                "hit " + (h.Thing?.ThingID ?? "none") + " cell " + h.Cell + ", mouse cell " + Verse.UI.MouseCell());
            // Just under the stove's top: the wall line is straight behind it along this line.
            foreach (int w in AimAt(At(S.Stove, Mathf.Max(0.1f, Interaction.PickHeight(S.Stove) - 0.05f)))) yield return w;
            yield return 5;
            h = FpPick.Current;
            K.Check("m1-pick stove before the wall", h.Valid && h.Thing == S.Stove, "hit " + (h.Thing?.ThingID ?? "none"));
            K.Check("m1-pick never self", h.Thing != S.C1, "hit " + (h.Thing?.ThingID ?? "none"));
            K.Shot("m1_pick");
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Lmb()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            foreach (int w in AimAt(At(S.Steel, 0.05f))) yield return w;
            yield return 30;   // past the click guard after capture, and the hint
            K.Check("m1-lmb steel in reach", FpPick.Current.Thing == S.Steel && FpPick.Current.InReach, "hint " + FpActions.Hint + " (" + FpActions.HintKind + ")");
            yield return K.Button(0, true);
            yield return K.Button(0, false);
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => !InControlJob(S.C1) || K.Ticks > tick0 + 30, "for the left-button order");
            K.Check("m1-lmb order on the steel", !InControlJob(S.C1) && S.C1.CurJob?.targetA.Thing == S.Steel, JobOf(S.C1));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // The east end of the wall line (5, 8 from C1): the line of sight passes east of the stove, which is in reach and
        // straight ahead of the middle of the wall line.
        private static IEnumerator<int> Far()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            foreach (int w in AimAt(At(S.WallLine[S.WallLine.Count - 1], 0.6f))) yield return w;
            yield return 30;
            K.Check("m1-far hint too far", FpActions.HintKind == FpHintKind.TooFar, "hint " + FpActions.Hint + " (" + FpActions.HintKind + "), hit " + (FpPick.Current.Thing?.ThingID ?? "none"));
            string job0 = JobOf(S.C1);
            yield return K.Button(0, true);
            yield return K.Button(0, false);
            yield return K.WaitTicks(30);
            K.Check("m1-far click does nothing", InControlJob(S.C1) && JobOf(S.C1) == job0, job0 + " -> " + JobOf(S.C1));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static readonly AccessTools.FieldRef<FloatMenu, List<FloatMenuOption>> menuOptions =
            AccessTools.FieldRefAccess<FloatMenu, List<FloatMenuOption>>("options");

        private static IEnumerator<int> Rmb()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            foreach (int w in AimAt(At(S.Steel, 0.05f))) yield return w;
            yield return 30;
            IntVec3 aimed = FpPick.Current.Cell;
            yield return K.Button(1, true);
            yield return K.Button(1, false);
            yield return 10;
            FloatMenuMap menu = Find.WindowStack.WindowOfType<FloatMenuMap>();
            K.Check("m1-rmb menu open, cursor free", menu != null && Free, "menu " + (menu != null) + ", " + CursorText);
            yield return 120;
            menu = Find.WindowStack.WindowOfType<FloatMenuMap>();
            K.Check("m1-rmb menu stays open", menu != null, "after 120 frames");
            K.Check("m1-rmb pointer pinned", FpActions.MenuOpen && Verse.UI.MouseCell() == FpActions.MenuPoint.ToIntVec3(),
                "mouse cell " + Verse.UI.MouseCell() + ", menu point " + FpActions.MenuPoint + ", aimed " + aimed);
            K.Shot("m1_rmb");
            yield return 2;   // the capture is taken at the end of the frame: keep the menu open until then
            menu = Find.WindowStack.WindowOfType<FloatMenuMap>();
            if (menu != null)
            {
                List<FloatMenuOption> options = menuOptions(menu);
                if (options != null && options.Count > 0)
                {
                    K.Info("choosing \"" + options[0].Label + "\"");
                    options[0].Chosen(true, menu);
                }
                Find.WindowStack.TryRemove(menu, false);
            }
            yield return 1;   // FpActions.MenuOpen stays true until its next frame's update
            yield return K.WaitUntil(() => Locked, "for the cursor to be captured again", 5);
            K.Check("m1-rmb captured again", Locked, CursorText);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Leak()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            CloseMainTab();
            int filtered0 = FpHits.Count("UIRootOnGUI filter") + FpHits.Count("DispatchInputToWindows mouse dropped");
            bool drafted0 = S.C1.Drafted;
            float s = Prefs.UIScale;
            // Engine pixels from the bottom left: the first main button, then the inspect pane above the buttons.
            foreach (Vector2 p in new[] { new Vector2(40f * s, 15f * s), new Vector2(150f * s, 120f * s) })
            {
                yield return K.VirtualPointer(p);
                yield return 2;
                yield return K.Button(0, true);
                yield return K.Button(0, false);
                yield return 10;
            }
            int filtered = FpHits.Count("UIRootOnGUI filter") + FpHits.Count("DispatchInputToWindows mouse dropped") - filtered0;
            // Inspect is the game's own: Selector.SelectorOnGUI opens it whenever something is selected and no tab is open,
            // and the played colonist always is. Any other tab came from a click.
            MainButtonDef tab = Find.MainTabsRoot.OpenTab;
            K.Check("m1-leak no main tab", tab == null || tab == MainButtonDefOf.Inspect, tab?.defName ?? "none");
            K.Check("m1-leak no gizmo", S.C1.Drafted == drafted0 && Find.Selector.SingleSelectedThing == S.C1, "drafted " + S.C1.Drafted + ", selected " + Find.Selector.SingleSelectedThing);
            K.Check("m1-leak clicks filtered", filtered > 0, filtered + " filtered events");
            CloseMainTab();
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Esc()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            yield return K.Tap(Key.Escape);
            yield return 10;
            K.Check("m1-esc menu open, cursor free", Find.WindowStack.IsOpen<Dialog_FpMenu>() && Free, CursorText);
            yield return K.Tap(Key.Escape);
            yield return 10;
            K.Check("m1-esc menu closed, captured", !Find.WindowStack.IsOpen<Dialog_FpMenu>() && Locked, CursorText);
            yield return K.Tap(Key.Escape);
            yield return 10;
            Dialog_FpMenu menu = Find.WindowStack.WindowOfType<Dialog_FpMenu>();
            // The menu's "Leave" button: Track A publishes its last drawn screen rect as a static Rect LeaveButtonRect (UI
            // pixels, top left); without it the harness calls the button's action directly.
            System.Reflection.FieldInfo leaveField = AccessTools.DeclaredField(typeof(Dialog_FpMenu), "LeaveButtonRect");
            Rect? leave = leaveField != null && leaveField.IsStatic ? leaveField.GetValue(null) as Rect? : null;
            if (menu != null && leave.HasValue && leave.Value.width > 0f)
            {
                float s = Prefs.UIScale;
                Vector2 c = leave.Value.center;
                yield return K.VirtualPointer(new Vector2(c.x * s, Verse.UI.screenHeight * s - c.y * s));
                yield return 2;
                yield return K.Button(0, true);
                yield return K.Button(0, false);
            }
            else
            {
                K.Info("Dialog_FpMenu.LeaveButtonRect not found: leaving through FpPlay.Leave");
                FpPlay.Leave(null);
                menu?.Close(false);
            }
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => K.Ticks >= tick0 + 2, "two ticks");
            K.Check("m1-esc leave", FpPlay.Mode == FpMode.Off && FpRegistry.EntryOf(S.C1) == null, Mode);
            yield return K.WaitUntil(() => !InControlJob(S.C1) || K.Ticks > tick0 + 60, "for the control job to end");
            K.Check("m1-esc job released", !InControlJob(S.C1), JobOf(S.C1));
            K.Check("m1-esc cursor free", Free, CursorText);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Alt()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            yield return K.Key(Key.LeftAlt, true);
            yield return 15;
            K.Check("m1-alt free while held", Free && FpInput.FreeCursorHeld, CursorText);
            yield return 15;
            yield return K.Key(Key.LeftAlt, false);
            yield return 10;
            K.Check("m1-alt captured after", Locked, CursorText);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Space()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            bool paused0 = Find.TickManager.Paused;
            yield return K.Tap(Key.Space);
            yield return 10;
            K.Check("m1-space pauses as in vanilla", Find.TickManager.Paused != paused0, "paused " + paused0 + " -> " + Find.TickManager.Paused);
            if (Find.TickManager.Paused != paused0)
            {
                yield return K.Tap(Key.Space);
                yield return 10;
            }
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Draft()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            yield return K.Tap(Key.R);
            yield return K.WaitTicks(10);
            K.Check("m1-draft drafted, still in control", S.C1.Drafted && InControlJob(S.C1), "drafted " + S.C1.Drafted + ", " + JobOf(S.C1));
            yield return K.Tap(Key.R);
            yield return K.WaitTicks(10);
            K.Check("m1-draft undrafted", !S.C1.Drafted, "drafted " + S.C1.Drafted);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Ride()
        {
            SelectOnly(S.C1);
            yield return K.Chord(Key.LeftShift, Key.F5);
            yield return 60;
            K.Check("m1-ride Shift+F5 rides along", FirstPerson.On && FirstPerson.Pawn == S.C1 && FpPlay.Mode == FpMode.Off && FpRegistry.EntryOf(S.C1) == null, Mode);
            CloseMainTab();
            foreach (int w in LeavePlay()) yield return w;
            CloseMainTab();
            SelectOnly(S.Pirate);
            yield return K.Tap(Key.F5);
            yield return 60;
            K.Check("m1-ride F5 on the pirate rides along", FirstPerson.On && FirstPerson.Pawn == S.Pirate && FpPlay.Mode == FpMode.Off && FpRegistry.EntryOf(S.Pirate) == null,
                Mode + ", pirate " + S.Pirate.ThingID);
            CloseMainTab();
            foreach (int w in LeavePlay()) yield return w;
            CloseMainTab();
            yield return K.Done();
        }

        // The messages on screen now (Verse.Messages keeps them private).
        private static List<Message> LiveMessages()
        {
            return AccessTools.Field(typeof(Messages), "liveMessages")?.GetValue(null) as List<Message> ?? new List<Message>();
        }

        private static IEnumerator<int> Down()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            HealthUtility.DamageUntilDowned(S.C1);
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => K.Ticks >= tick0 + 1, "one tick");
            K.Check("m1-down entry removed", FpRegistry.EntryOf(S.C1) == null, "downed " + S.C1.Downed + ", " + Mode);
            yield return 10;
            K.Check("m1-down play ends", FpPlay.Mode == FpMode.Off, Mode);
            string expected = "R3D_Fp_EndDowned".Translate();
            bool shown = LiveMessages().Any(m => m.text == expected);
            K.Check("m1-down message", shown, "expected \"" + expected + "\", shown: " + string.Join(" | ", LiveMessages().Select(m => m.text)));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Load()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            int id = S.C1.thingIDNumber;
            Game game0 = Current.Game;
            int errors0 = ErrorCount();
            GameDataSaveLoader.SaveGame("fptest-m1-load");
            yield return 30;
            GameDataSaveLoader.LoadGame("fptest-m1-load");
            yield return K.WaitUntil(() => Current.Game != game0 && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null
                && !LongEventHandler.ShouldWaitForEvent, "for the save to load", 3600);
            yield return 60;
            Pawn c1 = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id);
            K.Check("m1-load no entry", c1 != null && FpRegistry.EntryOf(c1) == null && FpState.ControllerOf(c1) == FpIds.None,
                c1 == null ? "C1 not found after loading" : "controller " + FpState.ControllerOf(c1));
            K.Check("m1-load play off", FpPlay.Mode == FpMode.Off, Mode);
            K.Check("m1-load no errors", ErrorCount() == errors0, (ErrorCount() - errors0) + " new errors");
            yield return K.Done();
        }

        // ---------------------------------------------------------------- M1 multiplayer

        private static int Players
        {
            get
            {
                try
                {
                    return MpBridge.InSession ? MP.GetPlayers().Count : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        // fp_compare.py (run by the runner after every --mp scenario) needs at least 30 FPSTATE lines both players logged,
        // one per controlled pawn every 60 ticks: each scenario keeps a pawn controlled this long while both still run.
        private const int CompareTicks = 1900;

        // The game was reloaded (hosting, a join point): the scene's things are new objects, found again by their cells.
        // Any queued event counts, not only a running one: hosting queues its "MpSaving" event (the save and reload) in the
        // same call that starts the session, and ShouldWaitForEvent is false while an event is only queued, so the scene
        // was adopted from the game about to be replaced.
        private static IEnumerable<int> AfterReload()
        {
            yield return K.WaitUntil(() => Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null
                && !LongEventHandler.AnyEventNowOrWaiting, "for the reload to finish", 7200);
            K.Check("scene found again", S.Adopt(Find.CurrentMap, out string error), error ?? S.Describe());
        }

        // Host: hosted by the Multiplayer-side test host (which reloads the game), then a client in and loaded.
        private static IEnumerable<int> HostWaitForClient()
        {
            yield return K.WaitUntil(() => MpBridge.InSession, "for hosting", 3600);
            foreach (int w in AfterReload()) yield return w;
            yield return K.WaitUntil(() => Players >= 2, "for the client to join", 18000);
            yield return 600;   // the client loads the map
        }

        // Host: before acting, wait until a first-person command of the client's has run here. A command the host issues
        // while the client is still finishing its join load is lost on the client: Multiplayer's join (Loader.PostLoad)
        // replaces the world command queue, which (consistentCommandOrder) also holds map commands, after the map queues
        // were built from the snapshot, so a map command arriving between the end of the load and PostLoad never runs
        // there ("Random state from commands doesn't match" on the host's first command, 2 of 5 early mp1-walk runs).
        // A client only sends once its scenario runs, which is after PostLoad. Reported, not fixed here.
        private static IEnumerable<int> HostWaitForClientCommand()
        {
            yield return K.WaitUntil(() => K.FirstCommandTick(false) >= 0, "for the client's first command", 36000);
        }

        // Client: a command that does nothing (an intent for a pawn nobody controls) as the "I'm in" signal above.
        private static IEnumerable<int> ClientReady()
        {
            while (!K.Direct.Intent(S.C2, FpDir.Stop))
            {
                yield return 1;
            }
            yield return K.WaitUntil(() => K.FirstCommandTick(true) >= 0, "for the ready signal to run", 1800);
        }

        // Host: the scenario runs until the client quits (its Done shuts it down), so the server isn't gone under it.
        private static IEnumerable<int> HostWaitForClientDone()
        {
            yield return K.WaitUntil(() => Players < 2, "for the client to finish", 36000);
            yield return 120;
        }

        // Walks a square: each side `ticks` long, by the play camera's yaw (client) or by direct intents (host).
        private static IEnumerable<int> ClientSquare(int ticks)
        {
            foreach (float yaw in new[] { 0f, 90f, 180f, 270f })
            {
                Turn(yaw, 8f);
                yield return 5;
                yield return K.HoldTicks(ticks, Key.W);
            }
        }

        private static IEnumerable<int> HostSquare(Pawn p, int ticks)
        {
            foreach (byte dir in new byte[] { 0, 4, 8, 12 })
            {
                K.Direct.Intent(p, dir);
                yield return K.WaitTicks(ticks);
            }
            K.Direct.Intent(p, FpDir.Stop);
            yield return K.WaitTicks(30);
        }

        private static IEnumerator<int> MpWalk()
        {
            if (K.IsHost)
            {
                foreach (int w in HostWaitForClient()) yield return w;
                foreach (int w in HostWaitForClientCommand()) yield return w;   // the client's control of C2
                K.Direct.SetControl(S.C1, true, FpSettings.Flags);
                yield return K.WaitUntil(() => FpState.ControllerOf(S.C1) == FpIds.Local, "for the host's control of C1", 600);
                int until = K.Ticks + 3600;
                while (K.Ticks < until)
                {
                    foreach (int w in HostSquare(S.C1, 120)) yield return w;
                }
                K.Direct.SetControl(S.C1, false, 0);
                foreach (int w in HostWaitForClientDone()) yield return w;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                foreach (int w in EnterPlay(S.C2)) yield return w;
                int until = K.Ticks + 3600;
                while (K.Ticks < until)
                {
                    foreach (int w in ClientSquare(120)) yield return w;
                }
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
            }
            else
            {
                K.Check("mp1-walk role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }

        private static IEnumerator<int> MpRace()
        {
            if (K.IsHost)
            {
                foreach (int w in HostWaitForClient()) yield return w;
                foreach (int w in HostWaitForClientCommand()) yield return w;   // the client's ready signal
                int race = RaceTick();
                yield return K.WaitUntil(() => K.Ticks >= race, "for the race tick " + race, 7200);
                K.Direct.SetControl(S.C1, true, FpSettings.Flags);
                yield return K.WaitTicks(240);
                K.Check("mp1-race one controller", FpState.ControllerOf(S.C1) != FpIds.None, "controller " + FpState.ControllerOf(S.C1) + " (" + FpIds.NameOf(FpState.ControllerOf(S.C1)) + ")");
                foreach (int w in HostWaitForClientDone()) yield return w;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                // The client starts without -isorim3d: the 3D view goes on first (IsoRim's "Preparing 3D" long event), so
                // F5 at the race tick sends the on within a frame or two, as the host's Direct call does.
                View3D.Active = true;
                yield return K.WaitUntil(() => View3D.Ready && !LongEventHandler.AnyEventNowOrWaiting, "for the 3D view", 1800);
                foreach (int w in ClientReady()) yield return w;
                int race = RaceTick();
                SelectOnly(S.C1);
                yield return K.WaitUntil(() => K.Ticks >= race, "for the race tick " + race, 7200);
                yield return K.Tap(Key.F5);
                yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Playing, "for play to start on C1", 1800);
                double sent = Time.realtimeSinceStartupAsDouble;
                yield return K.WaitUntil(() => FpPlay.Mode != FpMode.Playing || FpPlay.Confirmed, "for the race's outcome", 600);
                double took = Time.realtimeSinceStartupAsDouble - sent;
                int c = FpState.ControllerOf(S.C1);
                if (c == FpIds.Local)
                {
                    K.Check("mp1-race client won", FpPlay.Mode == FpMode.Playing && FpPlay.Confirmed, Mode);
                }
                else
                {
                    K.Check("mp1-race client lost: back to ride-along within 3 s", FpPlay.Mode == FpMode.Off && FirstPerson.On && took <= 3.5,
                        Mode + ", after " + took.ToString("0.00") + " s, controller " + FpIds.NameOf(c));
                }
                yield return K.WaitTicks(CompareTicks);
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
            }
            else
            {
                K.Check("mp1-race role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }

        // Both players send FpSetControl(C1, on) at this tick (spec: at map tick >= 1200): 300 ticks after the client's
        // ready signal ran, which every machine saw at the same tick.
        private static int RaceTick()
        {
            int ready = K.FirstCommandTick(K.IsClient);
            return System.Math.Max(1200, ready + 300);
        }

        private static IEnumerator<int> MpJoinPoint()
        {
            if (K.IsHost)
            {
                foreach (int w in HostWaitForClient()) yield return w;
                yield return K.WaitUntil(() => FpRegistry.EntryOf(S.C2) != null, "for the client's control of C2", 3600);
                yield return K.WaitTicks(300);
                // The Multiplayer-side test host's helper (FpSelfTestHost, track H) creates a join point.
                var force = AccessTools.Method("Multiplayer.Client.Diag.FpSelfTestHost:ForceJoinPoint");
                K.Check("mp1-joinpoint helper", force != null, force != null ? "forcing a join point" : "FpSelfTestHost.ForceJoinPoint not found");
                force?.Invoke(null, null);
                foreach (int w in HostWaitForClientDone()) yield return w;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                foreach (int w in EnterPlay(S.C2)) yield return w;
                Game game0 = Current.Game;
                Turn(180f, 8f);
                yield return K.HoldTicks(120, Key.W);
                yield return K.WaitUntil(() => Current.Game != game0, "for the join point's reload", 7200);
                foreach (int w in AfterReload()) yield return w;
                yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Playing && FpPlay.Confirmed, "to be playing again", 600);
                K.Check("mp1-joinpoint resumed", FpPlay.Mode == FpMode.Playing && FpPlay.Confirmed, Mode);
                yield return K.HoldTicks(60, Key.W);
                yield return K.WaitTicks(CompareTicks);
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
            }
            else
            {
                K.Check("mp1-joinpoint role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }

        private static IEnumerator<int> MpLeave()
        {
            if (K.IsHost)
            {
                foreach (int w in HostWaitForClient()) yield return w;
                yield return K.WaitUntil(() => FpRegistry.EntryOf(S.C2) != null, "for the client's control of C2", 3600);
                yield return K.WaitUntil(() => Players < 2, "for the client to vanish", 36000);
                int tick0 = K.Ticks;
                yield return K.WaitUntil(() => FpRegistry.EntryOf(S.C2) == null && !InControlJob(S.C2) || K.Ticks > tick0 + 300, "for C2's release");
                K.Check("mp1-leave released", FpRegistry.EntryOf(S.C2) == null && !InControlJob(S.C2), "after " + (K.Ticks - tick0) + " ticks: " + JobOf(S.C2));
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                foreach (int w in EnterPlay(S.C2)) yield return w;
                yield return K.WaitTicks(CompareTicks);
                Turn(180f, 8f);
                yield return K.Key(Key.W, true);
                yield return 30;
                // The process dies mid-walk, without a disconnect: what a crash or a killed game looks like to the server.
                // The DONE line first, and a second for the log writer to put it on disk (a kill right after it lost it).
                Log.Message("FPTEST DONE fails=" + K.Fails + " scenario=" + K.Scenario + " role=" + K.Role + " (killing the client)");
                yield return 60;
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                yield break;
            }
            else
            {
                K.Check("mp1-leave role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }

        private static IEnumerator<int> MpLmb()
        {
            if (K.IsHost)
            {
                foreach (int w in HostWaitForClient()) yield return w;
                foreach (int w in HostWaitForClientDone()) yield return w;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                foreach (int w in EnterPlay(S.C2)) yield return w;
                // C2 stands at (-2, -2), three cells south of the steel: two steps north first.
                Turn(0f, 8f);
                yield return K.HoldTicks(30, Key.W);
                yield return K.WaitTicks(30);
                foreach (int w in AimAt(At(S.Steel, 0.05f))) yield return w;
                yield return 30;
                yield return K.Button(0, true);
                yield return K.Button(0, false);
                // Watched for, not sampled once later: the haul to the stockpile cell 3 cells away is over within about
                // 120 ticks, after which C2 is back in the control job.
                int tick0 = K.Ticks;
                yield return K.WaitUntil(() => S.C2.CurJob?.targetA.Thing == S.Steel || K.Ticks > tick0 + 120, "for the left-button order");
                K.Check("mp1-lmb order on the steel", S.C2.CurJob?.targetA.Thing == S.Steel, JobOf(S.C2) + " after " + (K.Ticks - tick0) + " ticks");
                yield return K.WaitTicks(CompareTicks);
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
            }
            else
            {
                K.Check("mp1-lmb role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }
    }
}
