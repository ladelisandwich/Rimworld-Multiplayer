using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim.Fp
{
    // First-person play's local mode (fp-play-spec 6): whether this player is playing a colonist, entry through IsoRim's
    // first person key, the wait for the simulation to confirm control, every way out, and taking control again after a
    // Multiplayer reload. Local to this player: everything that changes the game goes out as FpSync's commands, and what
    // the simulation decided is read back through FpState.
    //
    // Entry is optimistic: the camera and the captured cursor start at once and the movement intents queue behind the
    // FpSetControl command (one client's commands run in order), so there is no input lag on entry. If the simulation
    // doesn't confirm within ConfirmSeconds (another player won the pawn, or it refused), the view stays as IsoRim's
    // ride-along first person.
    public enum FpMode : byte { Off, Playing }

    public static class FpPlay
    {
        private const double ConfirmSeconds = 3.0;    // spec 6.2: then ride-along
        private const double ResumeSeconds = 10.0;    // spec 6.4: how long a reload's resume looks for the pawn
        private const double ResendSeconds = 1.0;     // FpSetControl re-sent (flags changed) at most this often

        private const double OrphanSeconds = 2.0;     // a pawn this player controls with nobody playing it is let go after this

        private static FpMode mode;
        private static Pawn pawn;
        private static bool confirmed;
        private static bool pendingOn;                // play asked for control and it isn't confirmed yet
        private static bool onSent;                   // that FpSetControl(on) went out (Multiplayer took the command)
        private static double enterTime;              // when it went out: the confirmation clock
        private static Pawn orphan;                   // a pawn this player controls while not playing it, and since when
        private static double orphanSince;
        private static byte sentFlags;
        private static double lastControlSend;
        private static Pawn armed;                    // TryToggle accepted this pawn; play starts once IsoRim's camera is in it
        private static bool leaving;                  // Leave is running: its own FirstPerson.Leave mustn't count as a camera leave
        private static bool resuming;                 // the resume calls IsoRim's toggle: no Shift test
        private static Pawn deferredOff;              // an off to send once out of the simulation (see SendOff)
        private static bool cheatSheetShown;          // once per session (reset at the main menu)
        private static Pawn lateOnPawn;               // a pawn play gave up on: an on that lands late is taken back
        private static double lateOnUntil;

        // Written by CameraReset, which may run on the loading thread (the Game constructor): plain values only.
        private static bool afterReload;              // play was on when the game reloaded
        private static int reloadPawnId;
        private static double reloadUntil = -1;      // set on the first ready frame after the reload

        public static FpMode Mode
        {
            get { return mode; }
        }

        // Non-null while Playing.
        public static Pawn Pawn
        {
            get { return mode == FpMode.Playing ? pawn : null; }
        }

        // FpState.LocalControls(Pawn) has been seen since entry.
        public static bool Confirmed
        {
            get { return mode == FpMode.Playing && confirmed; }
        }

        private static double Now
        {
            get { return Time.realtimeSinceStartupAsDouble; }
        }

        // The UI-side F5 test (spec 6.1): an own, healthy, spawned colonist on the map shown, that nobody else controls.
        // The simulation checks again when FpSetControl runs; this only decides whether to try. reason is translated.
        public static bool Controllable(Pawn p, out string reason)
        {
            reason = null;
            if (p == null)
            {
                reason = "R3D_Fp_Why_NoPawn".Translate();
                return false;
            }
            if (!p.Spawned || p.Map == null || p.Map != Find.CurrentMap)
            {
                reason = "R3D_Fp_Why_NotHere".Translate();
                return false;
            }
            if (p.RaceProps == null || !p.RaceProps.Humanlike)
            {
                reason = "R3D_Fp_Why_NotHumanlike".Translate();
                return false;
            }
            if (p.IsPrisoner)
            {
                reason = "R3D_Fp_Why_Prisoner".Translate();
                return false;
            }
            if (p.Faction == null || p.Faction != MpBridge.RealPlayerFaction)
            {
                reason = "R3D_Fp_Why_NotYours".Translate();
                return false;
            }
            if (p.Dead || p.Downed)
            {
                reason = "R3D_Fp_Why_Downed".Translate();
                return false;
            }
            if (p.InMentalState)
            {
                reason = "R3D_Fp_Why_Mental".Translate();
                return false;
            }
            if (!p.IsColonistPlayerControlled)
            {
                reason = "R3D_Fp_Why_NotYours".Translate();
                return false;
            }
            if (MpBridge.InSession && !FpMpHooks.Bound)
            {
                reason = "R3D_Fp_Why_NoMultiplayer".Translate();
                return false;
            }
            int c = FpState.ControllerOf(p);
            if (c != FpIds.None && c != FpIds.Local)
            {
                reason = "R3D_Fp_Why_ControlledBy".Translate(FpIds.NameOf(c));
                return false;
            }
            return true;
        }

        // UI: send FpSetControl(off) if needed, then FirstPerson.Leave(message) (the camera, its message). Also the Esc
        // menu's "Leave" and F5 while playing.
        public static void Leave(string message)
        {
            if (mode == FpMode.Playing)
            {
                Pawn p = pawn;
                bool send = confirmed || pendingOn && onSent;
                mode = FpMode.Off;
                leaving = true;
                try
                {
                    if (send)
                    {
                        SendOff(p);
                    }
                    FirstPerson.Leave(message);
                }
                finally
                {
                    leaving = false;
                    ClearPlay();
                }
                return;
            }
            FirstPerson.Leave(message);
        }

        // Port 8 (FirstPerson.Toggle, polled F5): true when play handled the key and IsoRim's toggle must not run. Arms play
        // when the key takes control; IsoRim's toggle then puts the camera in the colonist and OnRootUpdate starts play.
        internal static bool TryToggle()
        {
            armed = null;
            if (FirstPerson.On)
            {
                if (mode == FpMode.Playing)
                {
                    Leave("R3D_Fp_Left".Translate());
                    return true;
                }
                return false;   // ride-along: IsoRim leaves as before
            }
            Selector sel = Find.Selector;
            Pawn chosen = sel != null && sel.NumSelected == 1 ? sel.SingleSelectedThing as Pawn : null;
            if (chosen == null || !FpSettings.play)
            {
                return false;
            }
            if (!resuming && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
            {
                return false;   // Shift + the key: ride-along on purpose
            }
            if (FpIds.PlayOff)
            {
                if (MpBridge.InSession && !FpMpHooks.Bound && OwnColonist(chosen))
                {
                    Messages.Message("R3D_Fp_RideAlongBecause".Translate(chosen.LabelShort, "R3D_Fp_Why_NoMultiplayer".Translate()),
                        MessageTypeDefOf.RejectInput, false);
                }
                return false;   // ghost mode, a replay or the arbiter: ride-along only
            }
            if (!Controllable(chosen, out string reason))
            {
                // Only for the player's own colonists: they probably meant to take control. Anyone else is a plain ride-along.
                if (OwnColonist(chosen) && !resuming)
                {
                    Messages.Message("R3D_Fp_RideAlongBecause".Translate(chosen.LabelShort, reason), MessageTypeDefOf.RejectInput, false);
                }
                return false;
            }
            armed = chosen;
            return false;
        }

        private static bool OwnColonist(Pawn p)
        {
            return p != null && p.Faction != null && p.Faction == MpBridge.RealPlayerFaction && p.RaceProps != null && p.RaceProps.Humanlike;
        }

        // Port 9 (FirstPerson.Leave): the camera let go of the colonist by any IsoRim path (3D off, map switch, the reset
        // key, a gravship flight, Multiplayer's jump to a player, the colonist gone). Play ends with it.
        internal static void CameraLeft(Pawn was)
        {
            if (mode != FpMode.Playing || leaving || was != pawn)
            {
                return;
            }
            bool send = confirmed || pendingOn && onSent;
            mode = FpMode.Off;
            if (send)
            {
                SendOff(pawn);
            }
            ClearPlay();
        }

        // Port 10 (FirstPerson.Reset): a new or loaded game (the Game constructor, also every Multiplayer reload), or the
        // program state not Playing. May run off the main thread, so it only stores plain values; no command goes out (the
        // entry is saved with the map and comes back with the reload, see UpdateAfterReload).
        internal static void CameraReset(Pawn was)
        {
            if (mode == FpMode.Playing)
            {
                Pawn p = pawn ?? was;
                if (p != null)
                {
                    reloadPawnId = p.thingIDNumber;
                    reloadUntil = -1;
                    afterReload = true;
                }
            }
            mode = FpMode.Off;
            ClearPlay();
            // Pawns of the game being replaced: an off still waiting would name a pawn of the old game. Whatever this player
            // still controls after the reload is resumed or let go (UpdateAfterReload, WatchOrphans).
            deferredOff = null;
            lateOnPawn = null;
            orphan = null;
        }

        // Port 5: no selection bracket drawn round the played colonist's own feet.
        internal static bool HidesBracket(object thing)
        {
            return mode == FpMode.Playing && thing != null && ReferenceEquals(thing, pawn);
        }

        // Every frame (from FpInput.OnRootUpdate, every program state): arming, confirmation, auto-leave, resume, reselect.
        internal static void OnRootUpdate()
        {
            if (Current.ProgramState == ProgramState.Entry && !MpBridge.InSession)
            {
                // The main menu with no session: nothing from the last game carries over.
                afterReload = false;
                cheatSheetShown = false;
                deferredOff = null;
                lateOnPawn = null;
                armed = null;
                return;
            }
            // Nothing while the game waits for a long event (a load or a Multiplayer reload tears the old game down on the
            // loading thread; Preparing 3D, an autosave): an armed entry, an off to send and a resume all wait for its end.
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null || LongEventHandler.ShouldWaitForEvent)
            {
                return;
            }
            if (deferredOff != null && !MpBridge.Simulating)
            {
                Pawn off = deferredOff;
                deferredOff = null;
                SendOff(off);
            }
            if (lateOnPawn != null)
            {
                if (Now > lateOnUntil)
                {
                    lateOnPawn = null;
                }
                else if ((mode != FpMode.Playing || pawn != lateOnPawn) && FpState.LocalControls(lateOnPawn))
                {
                    // The on sent before the confirmation timeout was accepted after all: nobody plays that colonist here.
                    Pawn late = lateOnPawn;
                    lateOnPawn = null;
                    SendOff(late);
                }
            }
            if (armed != null)
            {
                Pawn a = armed;
                if (mode != FpMode.Off || !FirstPerson.On || FirstPerson.Pawn != a)
                {
                    armed = null;   // the camera didn't go in (or already left): nothing to start
                }
                else if (!MpBridge.Simulating)
                {
                    armed = null;
                    Start(a);
                }
                // Else Multiplayer takes no command this frame: IsoRim's toggle with the 3D view off queues "Preparing
                // 3D" as a long event, which runs in the very next frame. Play starts once it is over, so the on goes out.
            }
            if (mode == FpMode.Playing)
            {
                UpdatePlaying();
            }
            else if (afterReload)
            {
                UpdateAfterReload();
            }
            WatchOrphans();
        }

        // A pawn this player controls while not playing it (an on that landed after play gave up on it, after a reload's
        // resume ended, or any other way): nobody steers it and, without AutoNeeds, it would stand in the control job until
        // a mental break. Let go after OrphanSeconds. Reads synced state only, sends through the usual UI sender.
        private static void WatchOrphans()
        {
            Pawn found = null;
            if (mode == FpMode.Off && armed == null && !afterReload && deferredOff == null && FpRegistry.ControlledCount > 0
                && !FpTestKit.DirectControl)   // the self-test's headless host controls pawns without playing them
            {
                int local = FpIds.Local;
                if (local != FpIds.None)
                {
                    foreach (FpMapComp comp in FpMapComp.AllComps())
                    {
                        foreach (FpEntry e in comp.entries)
                        {
                            if (e.controller == local && e.pawn != null)
                            {
                                found = e.pawn;
                                break;
                            }
                        }
                        if (found != null)
                        {
                            break;
                        }
                    }
                }
            }
            if (found == null)
            {
                orphan = null;
                return;
            }
            if (found != orphan)
            {
                orphan = found;
                orphanSince = Now;
                return;
            }
            if (Now - orphanSince > OrphanSeconds)
            {
                orphan = null;
                ModLog.Note("[IsoRim] First person: letting go of " + found.LabelShort + ", controlled by this player but not played.");
                SendOff(found);
            }
        }

        private static void Start(Pawn p)
        {
            mode = FpMode.Playing;
            pawn = p;
            if (lateOnPawn == p)
            {
                lateOnPawn = null;
            }
            confirmed = false;
            pendingOn = true;
            afterReload = false;
            orphan = null;
            FpCamera.Begin(MpFacing.Drawn(p).AsAngle);   // MP: a drafted pawn's kept facing (MpFacing)
            UI.UnfocusCurrentControl();   // a stuck text focus would swallow the movement keys (Input.GetKey) and the capture
            FpInput.OnPlayStarted();
            sentFlags = FpSettings.Flags;
            SendOn(p);
            if (FpSettings.cheatSheet && !cheatSheetShown)
            {
                cheatSheetShown = true;
                FpHud.ShowCheatSheet();
            }
        }

        private static void UpdatePlaying()
        {
            Pawn p = pawn;
            if (p == null || !FirstPerson.On || FirstPerson.Pawn != p)
            {
                // The camera is no longer in this colonist without IsoRim's leave (it always calls Port 9): end play anyway.
                bool send = confirmed || pendingOn && onSent;
                mode = FpMode.Off;
                if (send && p != null)
                {
                    SendOff(p);
                }
                ClearPlay();
                return;
            }
            if (!confirmed)
            {
                if (FpState.LocalControls(p))
                {
                    confirmed = true;
                    pendingOn = false;
                }
                else if (!onSent)
                {
                    SendOn(p);   // Multiplayer took no command so far; the confirmation clock starts once it does
                }
                else if (Now - enterTime > ConfirmSeconds)
                {
                    NotTaken(p);
                    return;
                }
            }
            else if (!FpState.LocalControls(p))
            {
                // The simulation released the colonist (downed, dead, a mental break, the faction changed...).
                string reason = p.Dead ? "R3D_Fp_EndDead".Translate()
                    : p.Downed ? "R3D_Fp_EndDowned".Translate()
                    : p.InMentalState ? "R3D_Fp_EndMental".Translate()
                    : "R3D_Fp_EndOther".Translate();
                mode = FpMode.Off;
                ClearPlay();
                FirstPerson.Leave(reason);
                return;
            }

            // The float menu works on the selected pawns, and Multiplayer's map-selected sync context too: keep the played
            // colonist the only selection.
            Selector sel = Find.Selector;
            if (sel != null && (sel.NumSelected != 1 || sel.SingleSelectedThing != p))
            {
                sel.ClearSelection();
                sel.Select(p, false, false);
            }

            // autoNeeds and skillChecks reach the simulation only as FpSetControl's flags: re-send when they change.
            if (confirmed)
            {
                byte f = FpSettings.Flags;
                if (f != sentFlags && Now - lastControlSend >= ResendSeconds && FpSync.SendSetControl(p, true, f))
                {
                    sentFlags = f;   // only once it went out: a change made during a long event goes on the next frame
                    lastControlSend = Now;
                }
            }
        }

        // FpSetControl(on) with the flags of the settings, recorded only when Multiplayer took it.
        private static void SendOn(Pawn p)
        {
            onSent = FpSync.SendSetControl(p, true, sentFlags);
            if (onSent)
            {
                enterTime = Now;
                lastControlSend = Now;
            }
        }

        // No confirmation in time: back to IsoRim's ride-along on the same colonist, looking where play looked. No off is
        // sent (the simulation refused the on, or still has it to run); an on that lands within ResumeSeconds is taken
        // back by OnRootUpdate.
        private static void NotTaken(Pawn p)
        {
            int c = FpState.ControllerOf(p);
            bool other = c != FpIds.None && c != FpIds.Local;
            lateOnPawn = p;
            lateOnUntil = Now + ResumeSeconds;
            mode = FpMode.Off;
            ClearPlay();
            try
            {
                // IsoRim's own look takes over where play's camera was, so the view doesn't jump.
                FirstPerson.Look(Mathf.DeltaAngle(FirstPerson.Yaw, FpCamera.Yaw), FpCamera.Pitch - FirstPerson.Pitch);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: handing the view back to the ride-along failed: " + e, 93652201);
            }
            string text = other ? "R3D_Fp_NotTaken".Translate(FpIds.NameOf(c)) : "R3D_Fp_NotTakenNoReply".Translate(p.LabelShort);
            FpHud.Toast(text, FpHud.WarnColor, 3f);
        }

        // After a reload (spec 6.4): the entry was saved with the map, so this player still controls the colonist. Take it
        // again (resume on), or release it (resume off, or it can't be taken), so it doesn't stand in a control nobody
        // steers.
        private static void UpdateAfterReload()
        {
            if (LongEventHandler.ShouldWaitForEvent || Find.CurrentMap == null)
            {
                return;
            }
            if (!MpBridge.InSession)
            {
                afterReload = false;   // a single-player load starts with nobody in control (FpMapComp.FinalizeInit)
                return;
            }
            if (reloadUntil < 0)
            {
                reloadUntil = Now + ResumeSeconds;
            }
            bool late = Now > reloadUntil;
            Pawn p = FindPawn(Find.CurrentMap, reloadPawnId);
            if (p == null || !View3D.Active || !FpState.LocalControls(p))
            {
                // Not yet: the 3D view may still be switching on, and this player's own on may still be to run (sent just
                // before a join point, the server stamped it after the join point's tick, so it runs in the catch-up
                // ticks after the reload). Given up after ResumeSeconds; the orphan watchdog lets go of a late one.
                if (late)
                {
                    afterReload = false;
                    ReleaseIfOurs(p ?? FindPawnAnywhere(reloadPawnId));
                }
                return;
            }
            afterReload = false;
            if (!FpSettings.resume || !FpSettings.play || FirstPerson.On || FpIds.PlayOff)
            {
                ReleaseIfOurs(p);
                return;
            }
            Selector sel = Find.Selector;
            if (sel != null)
            {
                sel.ClearSelection();
                sel.Select(p, false, false);
            }
            resuming = true;
            try
            {
                FirstPerson.Toggle();   // Port 8 arms play; OnRootUpdate starts it and re-sends FpSetControl(on)
            }
            finally
            {
                resuming = false;
            }
            if (armed != p)
            {
                ReleaseIfOurs(p);
            }
        }

        private static void ReleaseIfOurs(Pawn p)
        {
            if (p != null && FpState.LocalControls(p))
            {
                SendOff(p);
            }
        }

        private static Pawn FindPawn(Map map, int id)
        {
            if (map?.mapPawns == null)
            {
                return null;
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.thingIDNumber == id)
                {
                    return p;
                }
            }
            return null;
        }

        private static Pawn FindPawnAnywhere(int id)
        {
            foreach (Map m in Find.Maps)
            {
                Pawn p = FindPawn(m, id);
                if (p != null)
                {
                    return p;
                }
            }
            return null;
        }

        // FpSetControl(off). A UI sender returns without sending while Multiplayer takes no command (a camera leave from
        // inside a tick or command, such as a gravship takeoff, or a long event), so such an off waits for a later frame.
        private static void SendOff(Pawn p)
        {
            pendingOn = false;
            if (p == null)
            {
                return;
            }
            if (MpBridge.Simulating || !FpSync.SendSetControl(p, false, 0))
            {
                deferredOff = p;
            }
        }

        // Everything about the play that just ended (mode is already Off). Plain writes only: CameraReset may run off the
        // main thread.
        private static void ClearPlay()
        {
            pawn = null;
            confirmed = false;
            pendingOn = false;
            onSent = false;
            armed = null;
        }
    }
}
