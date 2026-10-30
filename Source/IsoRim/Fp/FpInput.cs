using System;
using System.Collections.Generic;
using HarmonyLib;
using LudeonTK;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace IsoRim.Fp
{
    // First-person play's input (fp-play-spec 7): the cursor capture watchdog, the mouse look, the movement intent, and
    // the IMGUI filter that keeps clicks and hotkeys away from the game's UI while the cursor is captured.
    //
    // Facts this rests on (UnityStubs and Waylith 1.7.4990, decompiled): Cursor.lockState Locked with visible false is
    // GLFW's disabled cursor with raw motion, applied in the next frame's PrePollEvents; the per-frame mouse delta
    // (Input.GetAxis "Mouse X/Y", y up) keeps coming while locked; nothing in the game or the engine releases the cursor
    // on Escape, focus loss or leaving, so this file always works out "captured" from scratch each frame and releases it
    // whenever any condition fails. IMGUI still dispatches mouse events at GLFW's invisible virtual pointer, so while
    // captured they are used up here (UIRootOnGUI) and kept from the windows (DispatchInputToWindows).
    // Application.isFocused is always true on 1.7; Waylith.EngineWindow.IsFocused is the real focus.
    public static class FpInput
    {
        private const float SectorHysteresisDeg = 4f;   // spec 7.2
        private const double IntentMinGap = 0.1;        // a changed intent at most every 100 ms (Stop always at once)
        private const double ClickGuardSeconds = 0.15;  // clicks after a capture are swallowed, not acted on
        private const int SettleFrames = 2;             // mouse deltas ignored round a capture change

        private static bool captured;           // worked out once a frame by the watchdog
        private static bool applied;            // what was last written to Cursor
        private static bool freeCursorHeld;
        private static bool focusLostLatch;
        private static bool resumeNeeded;       // the cursor is free only until a click on the map (HUD: "click to resume")
        private static int settleUntilFrame = -1;
        private static double clickGuardUntil;
        private static bool primaryDown;        // FpActions.PrimaryDown ran; its PrimaryUp is owed
        private static int frameInputFrame = -1;
        private static double frameInputTime;

        // The movement intent.
        private static byte lastSent = FpDir.Stop;
        private static bool intentKnown;        // false after an entry or reload: the next intent goes out whatever it is
        private static double lastSendTime = -1;
        private static byte lastSector = FpDir.Stop;
        private static double moveSince = -1;   // since when a direction is held (restarted when the colonist turns busy)
        private static bool wasBusy;
        private static float interruptFill = -1f;
        private static int autopilotToldJob = -1;   // the autopilot job a held movement key was last answered for (a toast)

        // The idle facing (M6, spec 9.2): the camera's quadrant with hysteresis, and what was last sent.
        private const float FacingHysteresisDeg = 10f;
        private const double FacingMinGap = 0.25;
        private const byte NoFacing = 255;
        private static byte facingQuadrant = NoFacing;
        private static byte facingSent = NoFacing;
        private static double facingSentAt = -1;

        // The pointer pin (M6): while captured, IMGUI's passes see the pointer at the screen's centre (see Install).
        private static bool pinned;
        private static Vector2 pinSaved;

        private static KeyBindingDef mpToggleChat, mpPingKey;
        private static List<KeyBindingDef> devKeys;
        private static bool keysLooked;

        public static bool Captured
        {
            get { return captured; }
        }

        public static bool FreeCursorHeld
        {
            get { return freeCursorHeld; }
        }

        // While true, the game's pointer is the crosshair, or the pinned point of the open first-person float menu (FpPick,
        // Ports 3, 4 and 16).
        public static bool PointerOverride
        {
            get { return FpPlay.Mode == FpMode.Playing && (captured || FpActions.MenuOpen); }
        }

        // Time.realtimeSinceStartupAsDouble at the first UIRootOnGUI event of this frame (the skill check's press time).
        public static double FrameInputTime
        {
            get { return frameInputTime; }
        }

        public static byte LastSentIntent
        {
            get { return lastSent; }
        }

        // Harness only (FpTestKit): treat the game window as focused.
        public static bool TestForceFocus;

        // For the HUD: the cursor is free because the window lost focus (or a text focus got stuck with no window open), and
        // a click on the map brings play back.
        internal static bool ResumeClickNeeded
        {
            get { return FpPlay.Mode == FpMode.Playing && resumeNeeded; }
        }

        // For the HUD: 0..1 while a held movement key waits to interrupt the colonist's job, -1 otherwise.
        internal static float InterruptFill
        {
            get { return interruptFill; }
        }

        internal static void Install(Harmony h)
        {
            FpHooks.Patch(h, "first-person event filter (UIRoot_Play.UIRootOnGUI)",
                AccessTools.DeclaredMethod(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)),
                prefix: new HarmonyMethod(typeof(FpInput), nameof(UIRootOnGUI_Prefix)) { priority = Priority.First });
            // UnityStubs' GUI (outside the game's JIT-patchable assemblies, but 261 bytes of IL: not inlined).
            FpHooks.Patch(h, "first-person window click filter (GUI.DispatchInputToWindows)",
                AccessTools.DeclaredMethod(typeof(GUI), "DispatchInputToWindows"),
                prefix: new HarmonyMethod(typeof(FpInput), nameof(DispatchInputToWindows_Prefix)));
            // Shared with IsoRim's right-button look and Multiplayer: always through the declaring type. 39 bytes of IL, but
            // its only caller is UIRoot_Play.UIRootOnGUI, itself patched (a Harmony replacement), and the game's runtime
            // never inlines Assembly-CSharp methods into dynamic methods (JitNoInlinePatchableIntoDynamicMethods).
            FpHooks.Patch(h, "first-person map click block (MapInterface.HandleMapClicks)",
                AccessTools.DeclaredMethod(typeof(MapInterface), nameof(MapInterface.HandleMapClicks)),
                prefix: new HarmonyMethod(typeof(FpInput), nameof(HandleMapClicks_Prefix)));
            // The selection's clicks (select, drag box, the game's right-click orders for the selected pawns), reached from
            // MapInterface.HandleLowPriorityInput through Selector.SelectorOnGUI. Private, 550 bytes of IL. Only this part
            // of SelectorOnGUI is skipped: its Esc deselect and the Inspect tab opening keep running.
            FpHooks.Patch(h, "first-person selection click block (Selector.HandleMapClicks)",
                AccessTools.DeclaredMethod(typeof(Selector), "HandleMapClicks"),
                prefix: new HarmonyMethod(typeof(FpInput), nameof(SelectorHandleMapClicks_Prefix)));
            // M6, the hover leak (CLAUDE.md, Known gaps, first-person play M1): IMGUI takes the pointer from
            // Input.mousePosition, which while captured is GLFW's invisible virtual cursor, drifting with every look; hover,
            // tooltips and mouse-over sounds followed it (a gizmo tooltip stayed up while looking round). Setting
            // Event.current.mousePosition doesn't hold (ImGuiGuiBackend.UpdateEventMouse rewrites it from that pointer at
            // every clip). Instead both IMGUI passes of a frame, RimWorld.dll's ImguiDispatch.DispatchInput (input events)
            // and Draw (layout and repaint), run with the engine's mouse position moved to the screen's centre, and moved
            // back after: the two InputSystem.QueueMouseMove calls add opposite deltas, so the frame's look delta (read
            // later, in the camera's update) is unchanged, and GLFW's own cursor is never touched (a warp would leave the
            // input bridge's last cursor position stale, which comes back as a look jerk on the next click). Last of the
            // prefixes, so the self-test's input for the frame is in first. Both methods have exception handlers, so the
            // JIT never inlines them.
            Type dispatch = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImguiDispatch");
            FpHooks.Patch(h, "first-person pointer pin (ImguiDispatch.DispatchInput)",
                dispatch == null ? null : AccessTools.DeclaredMethod(dispatch, "DispatchInput"),
                prefix: new HarmonyMethod(typeof(FpInput), nameof(ImguiPass_Prefix)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(FpInput), nameof(ImguiPass_Finalizer)));
            FpHooks.Patch(h, "first-person pointer pin (ImguiDispatch.Draw)",
                dispatch == null ? null : AccessTools.DeclaredMethod(dispatch, "Draw"),
                prefix: new HarmonyMethod(typeof(FpInput), nameof(ImguiPass_Prefix)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(FpInput), nameof(ImguiPass_Finalizer)));
            // With the pointer pinned, the map's thing tooltips (a pawn's name, health and, with a drafted colonist
            // selected, the game's "Shot by" report) popped up at the crosshair after the tooltip delay and covered the
            // hint under it (seen in the M5 self-test's screenshots at 1280 x 720). The crosshair's hint and the reticle
            // already say what is there, and the cursor is hidden, so they are skipped while captured. 423 bytes of IL,
            // the map's own (TooltipGiverList is Verse's; nothing else patches it); interface only.
            FpHooks.Patch(h, "first-person thing tooltips off while captured (TooltipGiverList.DispenseAllThingTooltips)",
                AccessTools.DeclaredMethod(typeof(TooltipGiverList), nameof(TooltipGiverList.DispenseAllThingTooltips)),
                prefix: new HarmonyMethod(typeof(FpInput), nameof(DispenseAllThingTooltips_Prefix)));
        }

        public static bool DispenseAllThingTooltips_Prefix()
        {
            if (!captured || FpPlay.Mode != FpMode.Playing)
            {
                return true;
            }
            FpHits.Hit("Thing tooltips skipped");
            return false;
        }

        // ---------------------------------------------------------------- the pointer pin (M6)

        // The screen's centre in the engine's mouse coordinates (framebuffer pixels from the bottom left, as the GLFW bridge
        // reports the cursor and the self-test's VirtualPointer takes it).
        internal static Vector2 PinPoint
        {
            get { return new Vector2(Screen.width / 2f, Screen.height / 2f); }
        }

        public static void ImguiPass_Prefix()
        {
            if (pinned || !captured || FpPlay.Mode != FpMode.Playing)
            {
                return;
            }
            try
            {
                UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse == null)
                {
                    return;
                }
                pinSaved = mouse.position.ReadValue();
                UnityEngine.InputSystem.InputSystem.QueueMouseMove(PinPoint);
                pinned = true;
                FpHits.Hit("Pointer pinned");
            }
            catch (Exception e)
            {
                pinned = false;
                Log.ErrorOnce("[IsoRim] First person: pinning the pointer for the interface failed: " + e, 93652309);
            }
        }

        public static void ImguiPass_Finalizer()
        {
            if (!pinned)
            {
                return;
            }
            pinned = false;
            try
            {
                UnityEngine.InputSystem.InputSystem.QueueMouseMove(pinSaved);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: giving the pointer back after the interface failed: " + e, 93652310);
            }
        }

        // Port 2 (every frame, every program state, from Root.Update): the play state machine, then the capture, then the
        // per-frame updates of picking, actions, the skill check and the intent. Each part catches its own failures, so
        // the cursor is always released when play ends even if another part throws every frame.
        internal static void OnRootUpdate()
        {
            try
            {
                FpPlay.OnRootUpdate();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the play update failed: " + e, 93652301);
            }

            bool playing = FpPlay.Mode == FpMode.Playing;
            bool want = false;
            try
            {
                bool focused = Waylith.EngineWindow.IsFocused || TestForceFocus;
                if (!playing)
                {
                    focusLostLatch = false;
                }
                else if (!focused)
                {
                    focusLostLatch = true;   // until a click on the map (UIRootOnGUI_Prefix)
                }
                freeCursorHeld = playing && FpKeyDefOf.R3D_FpFreeCursor != null && FpKeyDefOf.R3D_FpFreeCursor.IsDown;
                want = playing && CaptureAllowed(focused);
                resumeNeeded = playing && !want && Current.ProgramState == ProgramState.Playing && ResumeClickWanted();
            }
            catch (Exception e)
            {
                want = false;
                resumeNeeded = false;
                Log.ErrorOnce("[IsoRim] First person: the cursor capture check failed: " + e, 93652302);
            }
            SetCaptured(want);

            // Every frame of a game, playing or not: the action module closes its float menu once play ends, and the skill
            // check winds its ring down (each checks the mode itself). Not before a game: defs and textures may not be loaded.
            // Not during a long event either: a load clears the maps on the loading thread while the old game's pawns are
            // still referenced here (Thing.Spawned then logs "associated with invalid map index"), as the game itself skips
            // its interface while it waits for an event.
            if (Current.ProgramState == ProgramState.Playing && !LongEventHandler.ShouldWaitForEvent)
            {
                try
                {
                    FpActions.Update();
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] First person: the action update failed: " + e, 93652303);
                }
                try
                {
                    FpSkillCheck.Update();
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] First person: the skill check update failed: " + e, 93652304);
                }
                try
                {
                    FpCombat.Update();   // a held-back release goes out undrafted or over the world map too (once a frame)
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] First person: the trigger update failed: " + e, 93652312);
                }
            }
            if (!playing)
            {
                ResetIntent();
                return;
            }
            try
            {
                UpdateIntent();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the movement update failed: " + e, 93652305);
            }
            try
            {
                UpdateFacing();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the facing update failed: " + e, 93652311);
            }
        }

        // From FpPlay.Start: a new play (or a resumed one after a reload) starts with no intent known to have been sent.
        internal static void OnPlayStarted()
        {
            ResetIntent();
            focusLostLatch = false;
            primaryDown = false;
            facingQuadrant = NoFacing;
            facingSent = NoFacing;
            facingSentAt = -1;
        }

        // ---------------------------------------------------------------- the idle facing (M6)

        // Spec 9.2: the quadrant the camera looks into (with 10 degrees of hysteresis) goes to the simulation as FpSetFacing
        // whenever it changes, at most every 250 ms, latest wins. The control job turns the colonist that way whenever it
        // stands (FaceIdle). Sent while walking too, so the facing is already there when the colonist stops (otherwise the
        // colonist would first turn back to the old facing for a command's round trip). The first value goes out once
        // control is confirmed.
        private static void UpdateFacing()
        {
            Pawn p = FpPlay.Pawn;
            if (p == null)
            {
                return;
            }
            facingQuadrant = Quadrant(FpCamera.Yaw, facingQuadrant);
            if (!FpPlay.Confirmed || facingQuadrant == facingSent)
            {
                return;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (facingSentAt >= 0 && now - facingSentAt < FacingMinGap || !FpSync.CanSend)
            {
                return;   // the latest quadrant goes out once the gap has passed (or Multiplayer takes commands again)
            }
            FpSync.SendFacing(p, facingQuadrant);
            FpHits.Hit("Facing sent");
            facingSent = facingQuadrant;
            facingSentAt = now;
        }

        // Rot4 as a byte (0 north, 1 east, 2 south, 3 west) for a yaw clockwise from north; the current quadrant is kept
        // until the yaw is more than 45 + FacingHysteresisDeg degrees from its middle.
        internal static byte Quadrant(float yaw, byte current)
        {
            if (current <= 3 && Mathf.Abs(Mathf.DeltaAngle(yaw, current * 90f)) <= 45f + FacingHysteresisDeg)
            {
                return current;
            }
            return (byte)(Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 90f) & 3);
        }

        // The facing last sent (255 before the first), for the self-test.
        internal static byte FacingSent
        {
            get { return facingSent; }
        }

        private static void ResetIntent()
        {
            lastSent = FpDir.Stop;
            intentKnown = false;
            lastSendTime = -1;
            lastSector = FpDir.Stop;
            moveSince = -1;
            wasBusy = false;
            interruptFill = -1f;
        }

        // Spec 7.2: every condition for a captured cursor.
        private static bool CaptureAllowed(bool focused)
        {
            if (!FirstPerson.On || !View3D.Ready || View3D.CameraBlocked)
            {
                return false;
            }
            if (Current.ProgramState != ProgramState.Playing || LongEventHandler.ShouldWaitForEvent)
            {
                return false;
            }
            if (!focused || focusLostLatch || freeCursorHeld || GUIUtility.keyboardControl != 0)
            {
                return false;
            }
            return !BlockingWindowOpen() && !MapToolActive();
        }

        // A targeter (an ability or a grenade from a gizmo, a float-menu option such as "force to wear") or a selected
        // designator, started with the free cursor or from a menu: it is used with the visible cursor, as in the 3D view,
        // and the game's own map clicks run for it meanwhile (HandleMapClicks_Prefix). Right click or Esc cancels it.
        private static bool MapToolActive()
        {
            return Find.Targeter != null && Find.Targeter.IsTargeting
                || Find.DesignatorManager != null && Find.DesignatorManager.SelectedDesignator != null;
        }

        // Any open window that needs the pointer: everything but the immediate windows (overlays, the skill-check ring),
        // the inspect pane of the played colonist, and the dev palette (a dev-mode toolbar that stays open). An immediate
        // window that absorbs the input round it is modal (Multiplayer's IngameModal, with its cancel button) and blocks.
        // FloatMenu, the recipe picker, the Esc menu, Multiplayer's chat and dialogs, the main tabs all block.
        private static bool BlockingWindowOpen()
        {
            WindowStack ws = Find.WindowStack;
            if (ws == null)
            {
                return false;
            }
            for (int i = 0; i < ws.Count; i++)
            {
                Window w = ws[i];
                if (w is ImmediateWindow ? !w.absorbInputAroundWindow : w is MainTabWindow_Inspect || w is Dialog_DevPalette)
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        // The OS cursor follows `captured`, written only on a change (Waylith applies it next frame).
        private static void SetCaptured(bool want)
        {
            if (want != captured)
            {
                captured = want;
                FpHits.Hit(want ? "Cursor captured" : "Cursor released");
                settleUntilFrame = Time.frameCount + SettleFrames;
                if (want)
                {
                    clickGuardUntil = Time.realtimeSinceStartupAsDouble + ClickGuardSeconds;
                }
                else if (primaryDown)
                {
                    // The left button's release won't come through the filter any more: end a held action now.
                    primaryDown = false;
                    try
                    {
                        FpActions.PrimaryUp();
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[IsoRim] First person: releasing the left button failed: " + e, 93652306);
                    }
                }
            }
            if (want == applied)
            {
                return;
            }
            try
            {
                if (want)
                {
                    Cursor.visible = false;
                    Cursor.lockState = CursorLockMode.Locked;
                }
                else
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                applied = want;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: setting the cursor failed: " + e, 93652307);
            }
        }

        // Port 6 (View3D.HandleKeys, the Update pass): while playing the mouse look is play's own, and IsoRim's button look,
        // Q/E and tilt keys are skipped. Raw counts times the sensitivity, never times frame time.
        internal static bool LookFrame()
        {
            if (FpPlay.Mode != FpMode.Playing)
            {
                return false;
            }
            if (captured && Time.frameCount > settleUntilFrame)
            {
                float dx = Input.GetAxis("Mouse X");
                float dy = Input.GetAxis("Mouse Y");
                if (dx != 0f || dy != 0f)
                {
                    float s = FpSettings.sensitivity;
                    bool invert = IsoRimMod.Settings != null && IsoRimMod.Settings.invertY;
                    FpCamera.Look(dx * s, (invert ? 1f : -1f) * dy * s);
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- the movement intent

        private static bool Held(KeyBindingDef k)
        {
            return k != null && k.IsDown;
        }

        // Spec 7.2: the movement keys relative to the view, as one of 16 sectors (or Stop), sent on change. A key held
        // while the colonist is busy with another job only goes out once held for moveInterruptMs, so a tap doesn't cancel
        // an order; until then Stop is what the simulation has.
        private static void UpdateIntent()
        {
            Pawn p = FpPlay.Pawn;
            if (p == null)
            {
                return;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            byte desired = FpDir.Stop;
            if (captured)
            {
                int forward = (Held(KeyBindingDefOf.MapDolly_Up) ? 1 : 0) - (Held(KeyBindingDefOf.MapDolly_Down) ? 1 : 0);
                int right = (Held(KeyBindingDefOf.MapDolly_Right) ? 1 : 0) - (Held(KeyBindingDefOf.MapDolly_Left) ? 1 : 0);
                if (forward != 0 || right != 0)
                {
                    float deg = FpCamera.Yaw + Mathf.Atan2(right, forward) * Mathf.Rad2Deg;
                    desired = FpMath.SectorOf(deg, lastSector, SectorHysteresisDeg);
                }
            }
            lastSector = desired;

            bool busy = !FpControlJob.InControlJob(p);
            if (desired == FpDir.Stop)
            {
                moveSince = -1;
            }
            else if (moveSince < 0 || busy && !wasBusy)
            {
                // A new press, or a job (an order from the left button, say) began under a held key: hold again to walk off.
                moveSince = now;
            }
            wasBusy = busy;

            byte send = desired;
            interruptFill = -1f;
            if (busy && desired != FpDir.Stop)
            {
                double need = FpSettings.moveInterruptMs / 1000.0;
                double held = now - moveSince;
                // On autopilot (a lord's activity, or AutoNeeds eating or resting) walking doesn't interrupt the job
                // (FpSetIntent leaves it), so no fill promises it would: once held as long, a toast says what does. The
                // intent still goes out, so the direction is current when control comes back.
                bool autopilot = FpPlay.Confirmed && FpState.AutopilotKey(p) != null;
                if (held < need)
                {
                    send = FpDir.Stop;
                    interruptFill = autopilot ? -1f : need > 0 ? (float)(held / need) : 1f;
                }
                else if (autopilot && p.CurJob != null && p.CurJob.loadID != autopilotToldJob)
                {
                    autopilotToldJob = p.CurJob.loadID;
                    FpHud.Toast("R3D_Fp_Hud_AutopilotHold".Translate(FpState.AutopilotReason(p), FpHud.KeyLabel(KeyBindingDefOf.Command_ColonistDraft),
                        FpHud.KeyLabel(R3DKeyDefOf.R3D_FirstPerson)), new Color(1f, 0.82f, 0.45f), 3f);
                }
            }

            if (intentKnown && send == lastSent)
            {
                return;
            }
            bool due = !intentKnown || send == FpDir.Stop || lastSendTime < 0 || now - lastSendTime >= IntentMinGap;
            if (!due)
            {
                return;   // the latest value goes out once the gap has passed
            }
            if (!FpSync.SendIntent(p, send))
            {
                // Multiplayer takes no command now (a long event: an autosave, a join point, map generation): nothing is
                // recorded, so the latest value goes out on the first frame it can, a Stop included.
                return;
            }
            lastSent = send;
            lastSendTime = now;
            intentKnown = true;
        }

        // ---------------------------------------------------------------- IMGUI

        // Spec 7.3. First of all the UIRoot_Play.UIRootOnGUI prefixes, for every event: before the window stack's own
        // handling, the main buttons, the gizmos, the time controls and the map clicks.
        public static void UIRootOnGUI_Prefix()
        {
            try
            {
                Filter();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the input filter failed: " + e, 93652308);
            }
        }

        private static void Filter()
        {
            Event e = Event.current;
            if (e == null)
            {
                return;
            }
            int frame = Time.frameCount;
            if (frame != frameInputFrame)
            {
                frameInputFrame = frame;
                frameInputTime = Time.realtimeSinceStartupAsDouble;
            }
            EventType t = e.type;
            if (FpPlay.Mode == FpMode.Playing)
            {
                if (captured)
                {
                    // Waylith's dispatch (ImguiDispatch.Dispatch) marks a mouse event used before the OnGUI pass when the pointer
                    // is over a window drawn in the last pass (ImGuiGuiBackend.InlineControlsObscured): it sets the type only,
                    // while Event.Use sets the raw type as well. With the pointer pinned to the screen's centre (M6), that is
                    // the skill-check ring's own window, so every click, release and wheel step from the ring's warning to
                    // 0.8 s after its result was lost (M4-M6 test-copy run: mp4-work's click on the next rock right after a
                    // check's result ordered nothing). While captured no window gets a mouse event
                    // (DispatchInputToWindows_Prefix), so such an event is this filter's.
                    if (t == EventType.Used && IsMouseEvent(e.rawType))
                    {
                        t = e.rawType;
                        FpHits.Hit("Obscured mouse event taken");
                    }
                    CapturedEvent(e, t);
                    return;
                }
                if (t == EventType.KeyDown && IsFirstPersonKey())
                {
                    Consume(e);   // the polled first person key leaves; the game's Wildlife tab (also F5) mustn't open
                }
                else if (t == EventType.MouseDown && e.button == 0 && resumeNeeded
                    && Find.WindowStack.GetWindowAt(UI.MousePositionOnUIInverted) == null)
                {
                    focusLostLatch = false;
                    UI.UnfocusCurrentControl();
                    Consume(e);   // the click that brings play back does nothing else
                }
                return;
            }
            if (t == EventType.KeyDown && IsFirstPersonKey() && (FirstPerson.On || OneHumanlikeSelected()))
            {
                Consume(e);   // F5 means first person here, never the Wildlife tab as well
            }
        }

        private static bool IsMouseEvent(EventType t)
        {
            return t == EventType.MouseDown || t == EventType.MouseUp || t == EventType.MouseDrag || t == EventType.ScrollWheel;
        }

        private static void CapturedEvent(Event e, EventType t)
        {
            switch (t)
            {
                case EventType.MouseDown:
                    try
                    {
                        if (Time.realtimeSinceStartupAsDouble >= clickGuardUntil)
                        {
                            if (e.button == 0)
                            {
                                FpHits.Hit("LMB down");
                                primaryDown = true;
                                FpActions.PrimaryDown();
                            }
                            else if (e.button == 1)
                            {
                                FpActions.Secondary();
                            }
                        }
                        else
                        {
                            FpHits.Hit("Click swallowed after capture");
                        }
                    }
                    finally
                    {
                        Consume(e);
                    }
                    return;
                case EventType.MouseUp:
                    try
                    {
                        if (e.button == 0 && primaryDown)
                        {
                            primaryDown = false;
                            FpActions.PrimaryUp();
                        }
                    }
                    finally
                    {
                        Consume(e);
                    }
                    return;
                case EventType.ScrollWheel:
                    // M6 (spec 7.1): the wheel cycles the left button's option among the best few (down: the next one).
                    try
                    {
                        if (e.delta.y != 0f)
                        {
                            FpActions.CycleOption(e.delta.y > 0f ? 1 : -1);
                        }
                    }
                    finally
                    {
                        Consume(e);
                    }
                    return;
                case EventType.MouseDrag:
                case EventType.ContextClick:
                    Consume(e);
                    return;
                case EventType.KeyDown:
                    CapturedKeyDown(e);
                    return;
            }
        }

        private static void CapturedKeyDown(Event e)
        {
            if (e.keyCode == KeyCode.Escape || KeyBindingDefOf.Cancel.KeyDownEvent)
            {
                try
                {
                    Escape();
                }
                finally
                {
                    Consume(e);
                }
                return;
            }
            if (IsFirstPersonKey())
            {
                Consume(e);   // the polled toggle leaves (FpPlay.TryToggle); nothing else may take the key
                return;
            }
            if (KeyBindingDefOf.Command_ColonistDraft.KeyDownEvent)
            {
                try
                {
                    ToggleDraft();
                }
                finally
                {
                    Consume(e);
                }
                return;
            }
            if (FpKeyDefOf.R3D_FpSkillCheck != null && FpKeyDefOf.R3D_FpSkillCheck.KeyDownEvent && FpSkillCheck.WantsKey)
            {
                Consume(e);   // the check reads the key polled; the game's pause (Space too) must not run
                return;
            }
            if (Allowed())
            {
                return;
            }
            Consume(e);   // main-tab and gizmo hotkeys, next/previous colonist, zoom keys, ...
        }

        // Esc: a targeter or a selected designator (from the free cursor or a menu option) is cancelled first, as in the
        // game; otherwise the first-person menu opens.
        private static void Escape()
        {
            if (Find.Targeter != null && Find.Targeter.IsTargeting)
            {
                Find.Targeter.StopTargeting();
                return;
            }
            if (Find.DesignatorManager != null && Find.DesignatorManager.SelectedDesignator != null)
            {
                Find.DesignatorManager.Deselect();
                return;
            }
            if (!Find.WindowStack.IsOpen<Dialog_FpMenu>())
            {
                Find.WindowStack.Add(new Dialog_FpMenu());
            }
        }

        // R drafts or undrafts the played colonist. The Drafted setter is synced by Multiplayer (SyncMethods).
        private static void ToggleDraft()
        {
            if (MpBridge.InSession && !MP.InInterface)
            {
                return;
            }
            Pawn p = FpPlay.Pawn;
            if (p?.drafter == null || !p.drafter.ShowDraftGizmo)
            {
                return;
            }
            bool to = !p.drafter.Drafted;
            p.drafter.Drafted = to;
            (to ? SoundDefOf.DraftOn : SoundDefOf.DraftOff)?.PlayOneShotOnCamera();
        }

        // Spec 7.1: the keys that keep their game meaning while captured (time controls, Multiplayer's chat and ping, the
        // dev keys in dev mode). The pause key arrives here only when no skill check wants the key.
        private static bool Allowed()
        {
            if (KeyBindingDefOf.TogglePause.KeyDownEvent
                || KeyBindingDefOf.TimeSpeed_Normal.KeyDownEvent
                || KeyBindingDefOf.TimeSpeed_Fast.KeyDownEvent
                || KeyBindingDefOf.TimeSpeed_Superfast.KeyDownEvent
                || KeyBindingDefOf.TimeSpeed_Ultrafast.KeyDownEvent
                || KeyBindingDefOf.TimeSpeed_Slower.KeyDownEvent
                || KeyBindingDefOf.TimeSpeed_Faster.KeyDownEvent)
            {
                return true;
            }
            LookUpKeys();
            if (mpToggleChat != null && mpToggleChat.KeyDownEvent || mpPingKey != null && mpPingKey.KeyDownEvent)
            {
                return true;
            }
            if (Prefs.DevMode)
            {
                for (int i = 0; i < devKeys.Count; i++)
                {
                    if (devKeys[i].KeyDownEvent)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void LookUpKeys()
        {
            if (keysLooked)
            {
                return;
            }
            keysLooked = true;
            mpToggleChat = DefDatabase<KeyBindingDef>.GetNamedSilentFail("MpToggleChat");
            mpPingKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail("MpPingKey");
            devKeys = new List<KeyBindingDef>();
            foreach (KeyBindingDef k in DefDatabase<KeyBindingDef>.AllDefsListForReading)
            {
                if (k.category != null && k.category.defName == "Development")
                {
                    devKeys.Add(k);
                }
            }
        }

        private static bool IsFirstPersonKey()
        {
            return R3DKeyDefOf.R3D_FirstPerson != null && R3DKeyDefOf.R3D_FirstPerson.KeyDownEvent;
        }

        private static bool OneHumanlikeSelected()
        {
            Selector sel = Find.Selector;
            return sel != null && sel.NumSelected == 1 && sel.SingleSelectedThing is Pawn p && p.RaceProps != null && p.RaceProps.Humanlike;
        }

        // Click to resume (spec 7.2): only when the cursor is free because the window lost focus (or a text focus got
        // stuck with no window open), not for the Alt cursor, the world map or a window that needs the pointer.
        private static bool ResumeClickWanted()
        {
            if (!focusLostLatch && GUIUtility.keyboardControl == 0)
            {
                return false;
            }
            return !freeCursorHeld && FirstPerson.On && View3D.Ready && !View3D.CameraBlocked && !LongEventHandler.ShouldWaitForEvent
                && !BlockingWindowOpen() && !MapToolActive();
        }

        private static void Consume(Event e)
        {
            if (e.type != EventType.Used && e.type != EventType.Layout && e.type != EventType.Repaint)
            {
                e.Use();
                FpHits.Hit("UIRootOnGUI filter");
            }
        }

        // Spec 7.4: windows get no mouse events while captured (IMGUI aims them at GLFW's invisible virtual pointer, which
        // can sit over a main button or the inspect pane). Keys still reach a focused window (none that needs them is
        // open while captured) and UIRootOnGUI.
        public static bool DispatchInputToWindows_Prefix()
        {
            if (!captured || FpPlay.Mode != FpMode.Playing)
            {
                return true;
            }
            Event e = Event.current;
            if (e == null)
            {
                return true;
            }
            switch (e.type)
            {
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseDrag:
                case EventType.ScrollWheel:
                case EventType.ContextClick:
                    FpHits.Hit("DispatchInputToWindows mouse dropped");
                    return false;
            }
            return true;
        }

        // Spec 7.4: MapInterface.HandleMapClicks runs only the selected designator's and the targeter's input. While playing
        // it is skipped, except while one of them is active: the cursor is free then (MapToolActive) and the tool is used,
        // or cancelled with a right click or Esc, with the visible cursor.
        public static bool HandleMapClicks_Prefix()
        {
            if (FpPlay.Mode != FpMode.Playing || MapToolActive())
            {
                return true;
            }
            FpHits.Hit("HandleMapClicks skipped");
            return false;
        }

        // No selection clicks (select, double-click select, drag box, the game's right-click orders and float menu for the
        // selected pawns) while playing, captured or not: with the free cursor a right click on the ground would give the
        // played colonist a synced vanilla "go here", and a click would change the selection. A targeter or designator
        // takes its click first (MapInterface.HandleMapClicks runs before the selector) and uses the event.
        public static bool SelectorHandleMapClicks_Prefix()
        {
            if (FpPlay.Mode != FpMode.Playing)
            {
                return true;
            }
            FpHits.Hit("Selector clicks skipped");
            return false;
        }
    }
}
