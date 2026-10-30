using System.Runtime.CompilerServices;
using LudeonTK;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim.Fp
{
    // The control, intent and facing commands and their UI senders (fp-play-spec 5.1, 5.5, 9.2). The commands take the
    // pawn first, so Multiplayer runs them in that pawn's map queue, at the same tick on every machine, with the issuer's
    // faction pushed and the server-stamped issuer in FpIds.Executing. Outside a session a call runs at once.
    //
    // Command bodies are simulation code: synced state only, no Rand, no settings. Each sender returns at once when it is
    // called from a tick or a command in a session (it would run on this machine alone there).
    public static class FpSync
    {
        // A request equal to the last one this player sent is dropped for this long, so a request in flight isn't
        // stacked (spec 9.2). A different request goes at once; one client's commands run in the order sent.
        private const float ResendSeconds = 1f;

        private static int lastControlPawn = -1;
        private static bool lastControlOn;
        private static byte lastControlFlags;
        private static float lastControlAt = -999f;

        // UI senders: in a session outside the interface they return at once. Each returns true when the command went out
        // (or ran, outside a session) and false when it didn't, so the caller keeps the request and tries again on a later
        // frame: Multiplayer takes no command during any long event (Preparing 3D after IsoRim's toggle, an autosave, a
        // join point), and a request recorded as sent there would be lost.
        public static bool SendSetControl(Pawn p, bool on, byte flags)
        {
            if (p == null || MpBridge.InSession && !MP.InInterface)
            {
                return false;
            }
            if (MpBridge.InSession)
            {
                float now = Time.realtimeSinceStartup;
                if (p.thingIDNumber == lastControlPawn && on == lastControlOn && flags == lastControlFlags && now - lastControlAt < ResendSeconds)
                {
                    return true;   // the same request is already in flight
                }
                lastControlPawn = p.thingIDNumber;
                lastControlOn = on;
                lastControlFlags = flags;
                lastControlAt = now;
            }
            FpSetControl(p, on, flags);
            return true;
        }

        // The rate (on change, at least 100 ms apart, Stop never dropped) is FpInput's; this only guards and checks.
        public static bool SendIntent(Pawn p, byte dir)
        {
            if (p == null || MpBridge.InSession && !MP.InInterface || dir > 15 && dir != FpDir.Stop)
            {
                return false;
            }
            FpSetIntent(p, dir);
            return true;
        }

        // A UI sender would send now: outside a session always, in one only from the interface (not from a tick, a command
        // or a long event). For callers that must know whether a void sender's command went out (FpInput's facing).
        internal static bool CanSend
        {
            get { return !MpBridge.InSession || MP.InInterface; }
        }

        // Spec 9.2: the idle facing the controller's camera asks for (Rot4 as a byte: 0 north, 1 east, 2 south, 3 west).
        // The rate (a quadrant change with 10 degrees of hysteresis, at least 250 ms apart) is FpInput's; this only guards.
        public static void SendFacing(Pawn p, byte rot)
        {
            if (p == null || !CanSend || rot > 3)
            {
                return;
            }
            FpSetFacing(p, rot);
        }

        // Take control (on) or let go (off) of a pawn. Spec 5.1.
        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpSetControl(Pawn p, bool on, byte flags)
        {
            int me = FpIds.Executing;
            if (FpTestKit.Active)
            {
                FpTestKit.OnCommand(me, "FpSetControl p=" + p.thingIDNumber + " on=" + on + " flags=" + flags + " by=" + me
                    + " refuse=" + (on && me != FpIds.None ? RefuseKey(p, me) ?? "-" : "-"));
            }
            if (me == FpIds.None)
            {
                return;   // a session whose Multiplayer didn't bind the hooks: nobody can be told apart
            }
            if (on)
            {
                TakeControl(p, me, flags);
            }
            else
            {
                LetGo(p, me, flags);
            }
        }

        // The pawn's movement sector (0..15) or Stop, from its controller only. A direction also takes the pawn off an
        // interruptible job it was given (an order, a haul), unless a lord or its needs hold it: walking away is how a
        // player cancels.
        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpSetIntent(Pawn p, byte dir)
        {
            int me = FpIds.Executing;
            FpEntry e = FpRegistry.EntryOf(p);
            if (FpTestKit.Active)
            {
                FpTestKit.OnCommand(me, "FpSetIntent p=" + p.thingIDNumber + " dir=" + dir + " by=" + me + " controller=" + (e?.controller ?? FpIds.None));
            }
            if (me == FpIds.None || e == null || e.controller != me || dir > 15 && dir != FpDir.Stop)
            {
                return;
            }
            e.dir = dir;
            if (dir != FpDir.Stop && p.Spawned && e.comp != null && e.comp.map == p.Map && !p.Downed && !p.InMentalState
                && !FpControlJob.InControlJob(p) && p.jobs.IsCurrentJobPlayerInterruptible() && FpState.AutopilotKey(p) == null)
            {
                FpWork.ForfeitPending(e);   // walking away from skilled work with a check showing counts as missing it
                p.jobs.ClearQueuedJobs();
                p.jobs.EndCurrentJob(JobCondition.InterruptForced);   // the think tree hands out the control job
            }
        }

        // The facing a controlled pawn turns to while it stands in its control job (spec 5.4 step 4, 9.2), from its
        // controller only. The control job's tick does the turn (through the rotation tracker, so IsoRim's drawn facing
        // follows); a pawn walking, busy (aiming, a full-body stance) or on another job isn't turned.
        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpSetFacing(Pawn p, byte rot)
        {
            int me = FpIds.Executing;
            FpEntry e = FpRegistry.EntryOf(p);
            if (FpTestKit.Active)
            {
                FpTestKit.OnCommand(me, "FpSetFacing p=" + p.thingIDNumber + " rot=" + rot + " by=" + me + " controller=" + (e?.controller ?? FpIds.None));
            }
            if (me == FpIds.None || e == null || e.controller != me || rot > 3)
            {
                return;
            }
            e.facing = rot;
        }

        private static void TakeControl(Pawn p, int me, byte flags)
        {
            string refused = RefuseKey(p, me);
            if (refused != null)
            {
                TellIssuer(refused, p);
                return;
            }
            FpMapComp comp = FpMapComp.For(p.Map);
            if (comp == null)
            {
                return;
            }
            // One pawn per player: any other pawn this player controls (any map) is marked released; its own map's tick
            // releases it. Map order, then each list's thingID order.
            foreach (FpMapComp other in FpMapComp.AllComps())
            {
                foreach (FpEntry o in other.entries)
                {
                    if (o.controller == me && o.pawn != p)
                    {
                        o.controller = FpIds.None;
                        o.dir = FpDir.Stop;
                    }
                }
            }
            FpEntry existing = comp.Get(p);
            bool isNew = existing == null || existing.controller != me;
            if (existing != null && existing.controller != me)
            {
                // Released (by a player leaving) but its map hasn't ticked since: finish that release now, so the new
                // controller starts without the old one's movement or pending check.
                comp.Release(existing, false);
            }
            else if (existing != null && (existing.flags & FpFlags.Checks) != 0 && (flags & FpFlags.Checks) == 0)
            {
                // The controller turns skill checks off with one showing: it counts as missed (no escape from a bad
                // check through the settings), while the driver is still tracked under the old flags.
                FpWork.ForfeitPending(existing);
            }
            comp.Control(p, me, p.Faction, (byte)(flags & (FpFlags.AutoNeeds | FpFlags.Checks)));
            if (isNew)
            {
                // A job the AI chose ends now; a player-forced one (an order) finishes first, and so does one the game never
                // lets a player interrupt (vomiting, an uninterruptible cast, deathrest's last toil, carrying a baby to
                // safety), by the same test as the game's own orders (TryTakeOrderedJob). The think tree then gives the
                // control job when the job ends.
                p.jobs.ClearQueuedJobs();
                Job cur = p.CurJob;
                if (cur != null && !cur.playerForced && p.jobs.IsCurrentJobPlayerInterruptible() && !cur.def.forceCompleteBeforeNextJob)
                {
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }
        }

        // Spec 5.1, off: the controller, or with the Force flag any player of the pawn's faction (the issuer's faction is
        // pushed during a map command): a way to take a colonist back from a player who walked away from the keyboard.
        // Leaving with a check pending counts it as missed (FpWork), so leaving doesn't escape a bad check; a forced
        // release by another player voids it instead (the controller didn't choose to leave). The controller's own forced
        // release (the dev action on their own colonist) counts like any other leaving (review finding, M6).
        private static void LetGo(Pawn p, int me, byte flags)
        {
            FpEntry e = FpRegistry.EntryOf(p);
            if (e == null)
            {
                return;
            }
            bool force = (flags & FpFlags.Force) != 0;
            if (e.controller != me && !(force && e.controller != FpIds.None && p.Faction == Faction.OfPlayer))
            {
                return;
            }
            if (e.comp == null || e.comp.map != p.Map)
            {
                // The pawn left the entry's map: data only here; that map's own tick finishes the release.
                e.controller = FpIds.None;
                e.dir = FpDir.Stop;
                return;
            }
            e.comp.Release(e, (flags & FpFlags.Force) == 0 || e.controller == me);
        }

        // Spec 5.1: the translation key of the reason the pawn can't be taken, null if it can.
        private static string RefuseKey(Pawn p, int me)
        {
            if (!p.Spawned || p.Dead)
            {
                return "R3D_Fp_RefuseGone";
            }
            if (p.Downed)
            {
                return "R3D_Fp_RefuseDowned";
            }
            if (p.InMentalState)
            {
                return "R3D_Fp_RefuseMental";
            }
            if (!p.RaceProps.Humanlike || !p.IsColonistPlayerControlled || p.Faction != Faction.OfPlayer || p.IsPrisoner)
            {
                return "R3D_Fp_RefuseNotColonist";
            }
            if (FpControlJob.Def == null || !FpControlJob.HasControlSlot(p))
            {
                return "R3D_Fp_RefuseNoSlot";
            }
            FpEntry e = FpRegistry.EntryOf(p);
            if (e != null && (e.comp == null || e.comp.map != p.Map))
            {
                return "R3D_Fp_RefuseBusy";   // still listed on the map it just left, until that map ticks
            }
            if (e != null && e.controller != FpIds.None && e.controller != me)
            {
                return "R3D_Fp_RefuseTaken";
            }
            return null;
        }

        // Dev mode: take the selected colonist back from whoever controls it in first person (FpSetControl off with Force).
        // Local to the player who uses it (Multiplayer runs the IsoRim category's dev actions locally); the release itself
        // goes out as the synced command.
        [DebugAction("IsoRim", "FP: force release selected colonist", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ForceReleaseSelected()
        {
            Pawn p = Find.Selector?.SingleSelectedThing as Pawn;
            int c = FpState.ControllerOf(p);
            if (p == null || c == FpIds.None)
            {
                Messages.Message("R3D_Fp_ForceNone".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (!SendSetControl(p, false, FpFlags.Force))
            {
                Messages.Message("R3D_Fp_ForceLater".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Messages.Message("R3D_Fp_ForceSent".Translate(p.LabelShort, FpIds.NameOf(c)), MessageTypeDefOf.NeutralEvent, false);
        }

        // Only the player who sent the command sees why (historical false: the message archive is saved game state).
        private static void TellIssuer(string key, Pawn p)
        {
            if (MpBridge.InSession && !MP.IsExecutingSyncCommandIssuedBySelf)
            {
                return;
            }
            Messages.Message(key.Translate(p.LabelShort), new LookTargets(p), MessageTypeDefOf.RejectInput, historical: false);
        }
    }
}
