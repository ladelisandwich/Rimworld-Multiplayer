using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim.Fp
{
    // Track G (fp-play-spec 12): firing while drafted, with the game's own odds. The left button on a hostile under the
    // crosshair sends FpFire(hold: true), its release FpStopFire, which caps the attack at what has already started, so a
    // click always gets its burst and a held button keeps firing. The commands give the colonist the game's own attack job
    // (AttackStatic, or AttackMelee with a melee weapon or a hostile in reach), validated by the game's own drafted
    // float-menu checks (FloatMenuUtility.GetRangedAttackAction / GetMeleeAttackAction) and taken through TryTakeOrderedJob,
    // as the right-button menu's "Fire at" does.
    //
    // Vanilla odds only: nothing here touches a stat, a ShotReport, a warmup, a cooldown, a burst or a roll. The job runs
    // the game's JobDriver_AttackStatic / JobDriver_AttackMelee with the colonist's own verb (verbToUse stays null, as in
    // the game's order), so warmup (a busy stance that stops movement: no run-and-gun), aim, cover, burst and cooldown are
    // the game's in the tick. The hit chance shown (reticle, hint) is read from ShotReport.HitReportFor, never written. The
    // only aid is where the click lands: a hostile pawn within 1.3 times the picker's radius of the crosshair counts as
    // aimed at (FpPick.Ray, pawnRadiusScale), which only picks the target a vanilla player clicks on directly.
    //
    // Interface code (the press, the release, the hint, the reticle) writes no game state: it sends the two commands. The
    // command bodies are simulation code (spec 2): synced state and the commands' arguments only, no Rand, no settings.
    public static class FpCombat
    {
        // ---------------------------------------------------------------- tuning (interface)

        private const float WideScale = 1.3f;            // spec 12: the aim's pawn radius scale
        private const double ResendSeconds = 1.5;        // spec 12: no new FpFire for the same target until its job changes or this passes
        private const double RetargetSeconds = 0.25;     // spec 9.2: while held, a changed target at most 4 times a second
        private const int StopIdleTicks = 60;            // a release owed until the pressed attack starts: given up after the
                                                         // colonist has been free to attack (not aiming, firing, cooling down
                                                         // or stunned) this many game ticks without starting one
        private const double ReticleRefresh = 0.1;       // spec 12: the reticle's numbers at most 10 times a second

        private static readonly Color RefusedColour = new Color32(0xE0, 0x5A, 0x5A, 0xFF);   // the HUD's #E05A5A
        private static readonly Color ReadyColour = new Color(1f, 1f, 1f, 0.9f);
        private static readonly Color IdleColour = new Color(1f, 1f, 1f, 0.35f);
        private static readonly Color WarmupColour = new Color32(0xFF, 0xC2, 0x4A, 0xFF);
        private static readonly Color CooldownColour = new Color(0.62f, 0.72f, 0.82f, 0.75f);
        private static readonly Color OutlineColour = new Color(0f, 0f, 0f, 0.55f);

        // ---------------------------------------------------------------- the press in progress (interface, local)

        private static Pawn firePawn;                  // the colonist of the press (or of an owed release)
        private static bool pressHeld;                 // the left button is down on a press that went to combat
        private static LocalTargetInfo sentTarget = LocalTargetInfo.Invalid;   // the target of the last FpFire sent
        private static int sentJobId = -2;             // the colonist's job when it went out (the in-flight test)
        private static double sentAt = -1000.0;
        private static bool stopOwed;                  // the release still has to send FpStopFire
        private static int stopNeeds;                  // ... once the job has started this many attacks
        private static int stopJob = -1;               // ... on this job
        private static int stopIdle;                   // game ticks the colonist has been free to attack since the release
        private static int stopLastTick;
        private static int updatedFrame = -1;
        private static Game fireGame;                  // the game firePawn belongs to (a Multiplayer reload replaces it)
        private static double reloadSeenAt = -1.0;     // when Update first saw the game replaced under a press or a release
        private const double ReloadWaitSeconds = 15.0; // FpPlay's resume waits 10 s for the colonist; then the release is given up

        // The aim with the wider pawn radius, once per frame and camera (as FpPick.Current).
        private static int wideFrame = -1;
        private static Pawn widePawn;
        private static Vector3 wideEye;
        private static Vector3 wideFwd;
        private static Pawn wideResult;

        // The reticle's numbers.
        private static double reticleAt = -1000.0;
        private static Pawn reticlePawn;
        private static AimKind reticleKind;
        private static bool reticleCanHit;
        private static string reticleChance;
        private static string reticleWeapon;
        private static Stance_Busy trackedStance;      // the busy stance whose full length is known
        private static int trackedTotal;

        // The last hint couldn't be acted on (a friendly, out of range, no line of fire): the HUD's crosshair can show it
        // red (spec 14) where it reads this.
        internal static bool LastHintBlocked { get; private set; }

        // The last hint names a key, not what the left button does (undrafted on a hostile: "R: raise your weapon"): the
        // HUD can leave out its "[LMB]" there.
        internal static bool LastHintNoClick { get; private set; }

        // The private attack counters of the game's attack drivers (looked up once; a missing one logs one error and reads 0,
        // which only makes a release end held fire one attack sooner).
        private static AccessTools.FieldRef<JobDriver_AttackStatic, int> staticMade;
        private static AccessTools.FieldRef<JobDriver_AttackMelee, int> meleeMade;
        private static bool refsLooked;

        private static double Now
        {
            get { return Time.realtimeSinceStartupAsDouble; }
        }

        private enum AimKind : byte { None, Fire, Friendly }

        private struct Aim
        {
            public AimKind kind;
            public LocalTargetInfo target;
            public bool melee;
        }

        // ================================================================ interface

        // Drafted: true if it handled the click (spec 12, 8.3). Not drafted, or nothing to attack under the crosshair (an
        // own item, a ground cell the weapon can't target): false, and the click goes on to the normal left-button action.
        public static bool TryPrimaryDown(Pawn p, FpHit hit)
        {
            if (p == null || !p.Drafted || !p.Spawned || MpBridge.Simulating)
            {
                return false;
            }
            Update();
            Aim a = Resolve(p, hit);
            if (a.kind == AimKind.None)
            {
                return false;
            }
            if (a.kind == AimKind.Friendly)
            {
                FpHud.Toast("R3D_Fp_Combat_NotHostile".Translate(), RefusedColour);
                return true;
            }
            string why = VanillaRefusal(p, a.target, ref a.melee);
            if (why != null)
            {
                FpHud.Toast(Label(p, a) + ": " + why, RefusedColour);
                return true;
            }
            Press(p, a);
            return true;
        }

        // The left button's release (FpActions.PrimaryUp; FpInput also calls it when the cursor stops being captured with the
        // button down). Sends FpStopFire for a press that fired; a press on an attack already running, made between its
        // attacks, waits until the next attack has started so that click gets its burst too. A release because the cursor
        // was freed (a menu, a window, the world map, focus lost) isn't a click: it stops at once.
        public static void PrimaryUp()
        {
            if (!pressHeld)
            {
                return;
            }
            pressHeld = false;
            Pawn p = firePawn;
            if (p == null)
            {
                return;
            }
            if (stopNeeds > 0 && FpInput.Captured && !StopReady(p))
            {
                stopOwed = true;
                stopIdle = 0;
                stopLastTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
                return;
            }
            SendStop(p);
        }

        // Drafted: "Fire at raider 63%" (the hit chance only with FpSettings.showHitChance), "Melee attack raider", the game's
        // reason a shot can't be taken ("Fire at raider: Out of range"), or "not hostile" for another pawn. Undrafted, on a
        // hostile: the draft key. Null when the left button does nothing combat-related there (FpActions goes on).
        public static string Hint(Pawn p, FpHit hit)
        {
            LastHintBlocked = false;
            LastHintNoClick = false;
            // An invalid hit (the ray rose past the pick height over a standing pawn's shoulder, or ran out) still gets the
            // aim's wider radius (review finding, M5: the aim help worked beside a target's legs, where the ray lands on
            // the ground, and not beside his chest or head).
            if (p == null || !p.Spawned)
            {
                return null;
            }
            if (!p.Drafted)
            {
                Thing t = hit.Thing;
                if (!(t is Pawn))
                {
                    Pawn near = WidePawn(p);
                    if (near != null && Attackable(p, near))
                    {
                        t = near;
                    }
                }
                if (t == null || !Attackable(p, t))
                {
                    return null;
                }
                // A downed hostile within the left button's reach: its own action shows (capture, rescue...), which is what
                // the undrafted left button does there (FpActions); the draft key is the hint everywhere else.
                if (t is Pawn tp && tp.Downed && (FpSettings.interactAnyDistance || FpPick.InReach(p, t, t.Position)))
                {
                    return null;
                }
                LastHintNoClick = true;
                return "R3D_Fp_Combat_Raise".Translate(FpHud.KeyLabel(KeyBindingDefOf.Command_ColonistDraft));
            }
            Aim a = Resolve(p, hit);
            if (a.kind == AimKind.None)
            {
                return null;
            }
            if (a.kind == AimKind.Friendly)
            {
                LastHintBlocked = true;
                return "R3D_Fp_Combat_NotHostile".Translate();
            }
            string why = VanillaRefusal(p, a.target, ref a.melee);
            string label = Label(p, a);
            if (why != null)
            {
                LastHintBlocked = true;
                return label + ": " + why;
            }
            if (!a.melee && FpSettings.showHitChance)
            {
                float chance = HitChance(p, a.target);
                if (chance >= 0f)
                {
                    label += " " + chance.ToStringPercent();
                }
            }
            return label;
        }

        // Called by FpHud on Repaint while drafted (spec 12): brackets round the crosshair (red when the weapon can't hit the
        // target from here), the hit chance, a warmup arc, a thinner cooldown arc and the weapon's name.
        public static void ReticleOnGUI(Vector2 centre)
        {
            Update();
            Event e = Event.current;
            Pawn p = FpPlay.Pawn;
            if (e == null || e.type != EventType.Repaint || p == null || !p.Spawned || !p.Drafted)
            {
                return;
            }
            float s = Mathf.Clamp(FpSettings.hudScale, 0.75f, 1.5f);
            RefreshReticle(p);
            GameFont font = Text.Font;
            TextAnchor anchor = Text.Anchor;
            try
            {
                FpHits.Hit("Reticle drawn");
                DrawReticle(p, centre, s);
            }
            finally
            {
                Text.Font = font;
                Text.Anchor = anchor;
                GUI.color = Color.white;
            }
        }

        // Every frame of a game (FpInput.OnRootUpdate, in every play state; also from the reticle and the press): a release
        // owed until its attack started, and a changed target while the button is held. Once a frame at most. From the
        // frame loop rather than only the reticle (review finding, M5): the reticle isn't drawn undrafted or over the world
        // map, and a release held back until the next attack would never go out there.
        internal static void Update()
        {
            int frame = Time.frameCount;
            if (frame == updatedFrame)
            {
                return;
            }
            updatedFrame = frame;
            Pawn p = firePawn;
            if (p == null || MpBridge.Simulating)
            {
                return;
            }
            try
            {
                if (Current.Game != fireGame)
                {
                    // A Multiplayer reload (a join point, a resync, an autosave's join point) replaced the game: the same
                    // colonist comes back as a new object, found again by its id once FpPlay has resumed play on it, and
                    // FpInput starts the resumed play with the button up, so a held press is released here (review
                    // finding, M5: the saved attack job, held, kept firing after the button went up). Not resumed in time
                    // (resume off, the colonist gone): given up, and the attack ends as the game's ordered attack does.
                    if (!pressHeld && !stopOwed)
                    {
                        Forget();
                        return;
                    }
                    if (reloadSeenAt < 0)
                    {
                        reloadSeenAt = Now;
                    }
                    Pawn again = FpPlay.Mode == FpMode.Playing ? FpPlay.Pawn : null;
                    if (again == null || again.thingIDNumber != p.thingIDNumber || !FpState.LocalControls(again))
                    {
                        if (Now - reloadSeenAt > ReloadWaitSeconds)
                        {
                            Forget();
                        }
                        return;
                    }
                    firePawn = p = again;
                    fireGame = Current.Game;
                    reloadSeenAt = -1.0;
                    pressHeld = false;
                    stopOwed = true;
                    stopNeeds = 0;   // the attack already started finishes; no new one
                    stopIdle = 0;
                    stopLastTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
                    FpHits.Hit("Fire released after a reload");
                }
                if (FpPlay.Mode != FpMode.Playing || FpPlay.Pawn != p || p.Destroyed)
                {
                    // Play ended (a release from now on would come after the off and be refused): the attack ends as the
                    // game's own ordered attack does, when its target is down, gone or out of sight.
                    Forget();
                    return;
                }
                if (!p.Drafted)
                {
                    Forget();   // undrafting ended the attack (the game's Drafted setter): nothing left to stop
                    return;
                }
                double now = Now;
                if (stopOwed && (StopReady(p) || StopStalled(p)))
                {
                    SendStop(p);
                }
                if (pressHeld && p.Drafted && FpInput.Captured && now - sentAt >= RetargetSeconds)
                {
                    Retarget(p);
                }
                if (!pressHeld && !stopOwed)
                {
                    firePawn = null;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[IsoRim] First person: updating the trigger failed: " + ex, 93652601);
                Forget();
            }
        }

        // ---- the press

        private static void Press(Pawn p, Aim a)
        {
            double now = Now;
            Job cur = p.CurJob;
            int curId = cur != null ? cur.loadID : -1;
            pressHeld = true;
            firePawn = p;
            fireGame = Current.Game;
            reloadSeenAt = -1.0;
            stopOwed = false;
            stopNeeds = 0;
            stopJob = curId;
            if (IsAttackOn(cur, a.target))
            {
                // The same attack is already running: this press asks for one more attack, unless the current one is still
                // aiming (its warmup counts as started).
                int made = AttacksMade(p.jobs.curDriver);
                stopNeeds = p.stances?.curStance is Stance_Warmup ? made : made + 1;
            }
            // Spec 12: one FpFire per press; none while the last one for this target hasn't changed the job yet (in flight),
            // for at most 1.5 s.
            if (a.target == sentTarget && curId == sentJobId && now - sentAt < ResendSeconds)
            {
                FpHits.Hit("Fire not resent (in flight)");
                return;
            }
            Send(p, a.target);
        }

        // While held: the crosshair moved onto another hostile (spec 9.2: the snapped thing changed), at most 4 times a
        // second. Not onto a downed pawn (sweeping over one doesn't execute it; a click does), not onto ground cells.
        private static void Retarget(Pawn p)
        {
            Aim a = Resolve(p, FpPick.Current);
            if (a.kind != AimKind.Fire || !a.target.HasThing || a.target == sentTarget || a.target.Thing is Pawn tp && tp.Downed)
            {
                return;
            }
            if (VanillaRefusal(p, a.target, ref a.melee) != null)
            {
                return;
            }
            stopNeeds = 0;
            stopJob = p.CurJob != null ? p.CurJob.loadID : -1;
            if (Send(p, a.target))
            {
                FpHits.Hit("Fire retarget");
            }
        }

        private static bool Send(Pawn p, LocalTargetInfo target)
        {
            if (MpBridge.InSession && !MP.InInterface)
            {
                return false;
            }
            sentTarget = target;
            sentJobId = p.CurJob != null ? p.CurJob.loadID : -1;
            sentAt = Now;
            FpHits.Hit("Fire sent");
            FpFire(p, target, true);
            return true;
        }

        // The pressed attack has started (or the job it was pressed on has gone): the release can go.
        private static bool StopReady(Pawn p)
        {
            Job cur = p.CurJob;
            return !p.Spawned || !p.Drafted || cur == null || cur.loadID != stopJob || AttacksMade(p.jobs.curDriver) >= stopNeeds;
        }

        // The pressed attack isn't coming: the colonist has been free to start it (no warmup, burst, cooldown or stun) for
        // StopIdleTicks game ticks and hasn't. Counted in game time, so a pause or a slow frame rate never gives up early
        // (the attack can't start while the game stands still); at most 15 ticks a frame (a frame of catching up).
        private static bool StopStalled(Pawn p)
        {
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            int dt = Mathf.Clamp(now - stopLastTick, 0, 15);
            stopLastTick = now;
            if (p.stances != null && !p.stances.FullBodyBusy)
            {
                stopIdle += dt;
            }
            return stopIdle >= StopIdleTicks;
        }

        private static void SendStop(Pawn p)
        {
            if (MpBridge.InSession && !MP.InInterface)
            {
                stopOwed = true;   // Multiplayer takes no command now (a long event): next frame
                return;
            }
            stopOwed = false;
            stopNeeds = 0;
            FpHits.Hit("StopFire sent");
            FpStopFire(p);
        }

        private static void Forget()
        {
            firePawn = null;
            fireGame = null;
            reloadSeenAt = -1.0;
            pressHeld = false;
            stopOwed = false;
            stopNeeds = 0;
            stopIdle = 0;
            sentTarget = LocalTargetInfo.Invalid;
            sentJobId = -2;
        }

        // ---- the aim (interface; reads only)

        // What a click would do (spec 12, aim): the pawn under the crosshair (a hostile is fired at, anyone else refused); a
        // hostile pawn just off it (the wider radius); a hostile thing under it; the ground cell under it when the weapon
        // can target places. Anything else: nothing (the click isn't combat).
        private static Aim Resolve(Pawn p, FpHit hit)
        {
            Aim a = default(Aim);
            if (p == null || !p.Spawned)
            {
                return a;
            }
            if (!hit.Valid)
            {
                // Nothing under the narrow pick (a ray over a standing pawn's shoulder rises past the pick height): a
                // hostile within the wider radius is still the target, as beside his legs.
                Pawn wide = WidePawn(p);
                return wide != null && Attackable(p, wide) ? FireAt(p, wide) : a;
            }
            if (hit.Thing is Pawn direct && direct != p)
            {
                if (!Attackable(p, direct))
                {
                    a.kind = AimKind.Friendly;
                    a.target = direct;
                    return a;
                }
                return FireAt(p, direct);
            }
            Pawn near = WidePawn(p);
            if (near != null && Attackable(p, near))
            {
                return FireAt(p, near);
            }
            if (hit.Thing != null)
            {
                return Attackable(p, hit.Thing) ? FireAt(p, hit.Thing) : a;
            }
            if (hit.Cell.IsValid && hit.Cell.InBounds(p.Map) && CanTargetGround(p))
            {
                return FireAt(p, hit.Cell);
            }
            return a;
        }

        private static Aim FireAt(Pawn p, LocalTargetInfo target)
        {
            return new Aim { kind = AimKind.Fire, target = target, melee = UseMelee(p, target) };
        }

        // A hostile pawn within the aim's wider radius of the crosshair's ray (FpActions: an invalid hit still gets the
        // combat hint then).
        internal static bool NearHostile(Pawn p)
        {
            Pawn w = WidePawn(p);
            return w != null && Attackable(p, w);
        }

        // The first pawn the crosshair's ray passes within 1.3 times the picker's radius of, from the played colonist's eye.
        private static Pawn WidePawn(Pawn p)
        {
            if (FpPlay.Mode != FpMode.Playing || FpPlay.Pawn != p || FirstPerson.Pawn != p || !View3D.Ready)
            {
                return null;
            }
            Map map = Find.CurrentMap;
            if (map == null || p.Map != map)
            {
                return null;
            }
            Vector3 eye = View3D.EyePosition;
            Vector3 fwd = View3D.Forward;
            int frame = Time.frameCount;
            if (frame == wideFrame && p == widePawn && eye == wideEye && fwd == wideFwd)
            {
                return wideResult;
            }
            wideFrame = frame;
            widePawn = p;
            wideEye = eye;
            wideFwd = fwd;
            wideResult = null;
            try
            {
                FpHit h = FpPick.Ray(map, eye, fwd, FpTuning.Get.pickMaxCells, p, WideScale);
                wideResult = h.Valid ? h.Thing as Pawn : null;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the aim's ray failed: " + e, 93652602);
            }
            return wideResult;
        }

        // The game's own validation of the order (the drafted float menu's: range, line of fire, violence, ideology, reach
        // for melee): null when allowed, else the game's reason. The returned order itself is never run. A hostile in reach
        // that can't be fought hand to hand (no path round a corner) is shot instead when the colonist has a gun: melee
        // turns false then. Shared by the interface (hint, press, reticle) and FpFire, so both decide alike.
        internal static string VanillaRefusal(Pawn p, LocalTargetInfo target, ref bool melee)
        {
            string fail;
            Action order = melee ? FloatMenuUtility.GetMeleeAttackAction(p, target, out fail) : FloatMenuUtility.GetRangedAttackAction(p, target, out fail);
            if (order == null && melee && RangedVerb(p) != null)
            {
                string meleeFail = fail;
                if (FloatMenuUtility.GetRangedAttackAction(p, target, out fail) != null)
                {
                    melee = false;
                    return null;
                }
                fail = meleeFail.NullOrEmpty() ? fail : meleeFail;
            }
            if (order != null)
            {
                return null;
            }
            return fail.NullOrEmpty() ? (string)"CannotHitTarget".Translate().CapitalizeFirst() : fail;
        }

        private static string Label(Pawn p, Aim a)
        {
            Thing t = a.target.Thing;
            if (a.melee)
            {
                return t is Pawn tp && tp.Downed ? "MeleeAttackToDeath".Translate(t.Label, t).CapitalizeFirst() : "MeleeAttack".Translate(t.Label, t).CapitalizeFirst();
            }
            if (t != null)
            {
                return "FireAt".Translate(t.Label, t).CapitalizeFirst();
            }
            string verb = RangedVerb(p)?.verbProps?.label;
            return verb.NullOrEmpty() ? (string)"R3D_Fp_Combat_FireGround".Translate() : verb.CapitalizeFirst();
        }

        // The game's estimate for the colonist's ranged verb on the target (read-only), -1 without one.
        private static float HitChance(Pawn p, LocalTargetInfo target)
        {
            Verb v = RangedVerb(p);
            if (v == null || !target.IsValid)
            {
                return -1f;
            }
            return ShotReport.HitReportFor(p, v, target).TotalEstimatedHitChance;
        }

        // ================================================================ shared rules (synced state only: interface and simulation)

        // A target the left button attacks: a hostile pawn (to this colonist: GenHostility, mental states included) that is
        // alive, or a hostile thing the game's drafted attack menu offers (FloatMenuOptionProvider_DraftedAttack.CanTarget,
        // its hostile cases). Neutral and friendly things and pawns are left to the right-button menu, where the game's
        // confirmations are.
        internal static bool Attackable(Pawn p, Thing t)
        {
            if (p == null || t == null || t == p || !t.Spawned || t.Map != p.Map)
            {
                return false;
            }
            if (t is Pawn tp)
            {
                return !tp.Dead && tp.HostileTo(p);
            }
            ThingDef def = t.def;
            return def != null && !def.noRightClickDraftAttack && (def.destroyable || def.IsNonDeconstructibleAttackableBuilding) && t.HostileTo(p);
        }

        // The colonist's primary weapon's verb when it is ranged, else null (fists, a melee weapon).
        internal static Verb RangedVerb(Pawn p)
        {
            Verb v = p?.equipment?.PrimaryEq?.PrimaryVerb;
            return v != null && v.verbProps != null && !v.verbProps.IsMeleeAttack ? v : null;
        }

        // The weapon can be aimed at a place (grenades, launchers: TargetingParameters.canTargetLocations). A gun can't: a
        // cell shot would skip the cover roll (Verb_LaunchProjectile.TryCastShot only rolls cover for a thing).
        internal static bool CanTargetGround(Pawn p)
        {
            Verb v = RangedVerb(p);
            return v != null && v.verbProps.targetParams != null && v.verbProps.targetParams.canTargetLocations;
        }

        // Spec 12: melee without a ranged primary, or on a pawn within 1.5 cells.
        internal static bool UseMelee(Pawn p, LocalTargetInfo target)
        {
            if (RangedVerb(p) == null)
            {
                return true;
            }
            return target.Thing is Pawn && p.Position.InHorDistOf(target.Cell, 1.5f);
        }

        private static bool IsAttackOn(Job j, LocalTargetInfo target)
        {
            return j != null && j.verbToUse == null && j.targetA == target && (j.def == JobDefOf.AttackStatic || j.def == JobDefOf.AttackMelee);
        }

        // Attacks the colonist's current attack driver has started (its warmup counts), 0 for any other driver.
        internal static int AttacksMade(JobDriver d)
        {
            LookRefs();
            if (d is JobDriver_AttackStatic s && staticMade != null)
            {
                return staticMade(s);
            }
            if (d is JobDriver_AttackMelee m && meleeMade != null)
            {
                return meleeMade(m);
            }
            return 0;
        }

        private static void LookRefs()
        {
            if (refsLooked)
            {
                return;
            }
            refsLooked = true;
            try
            {
                staticMade = AccessTools.FieldRefAccess<JobDriver_AttackStatic, int>("numAttacksMade");
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] First person: JobDriver_AttackStatic.numAttacksMade not found (" + e.Message + "); a release caps held fire at one attack.");
            }
            try
            {
                meleeMade = AccessTools.FieldRefAccess<JobDriver_AttackMelee, int>("numMeleeAttacksMade");
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] First person: JobDriver_AttackMelee.numMeleeAttacksMade not found (" + e.Message + "); a release caps held melee at one attack.");
            }
        }

        // ================================================================ commands

        // Spec 12: attack the target with the game's own job. hold true: keep attacking until FpStopFire (or the job ends as
        // the game's attack order does); false: one attack. The queue key's state travels with the command
        // (QueueOrder_Down), because TryTakeOrderedJob reads it; a queued attack is one attack (it can't be held).
        [SyncMethod(SyncContext.QueueOrder_Down, cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpFire(Pawn p, LocalTargetInfo target, bool hold)
        {
            int me = FpIds.Executing;
            string result = "-";
            try
            {
                result = Fire(p, target, hold, me);
            }
            finally
            {
                if (FpTestKit.Active)
                {
                    FpTestKit.OnCommand(me, "FpFire p=" + p.thingIDNumber + " target=" + Describe(target) + " hold=" + hold + " by=" + me
                        + " -> " + result + " | " + Digest(target));
                }
            }
        }

        // Spec 12: the attack already started finishes, no new one starts.
        [SyncMethod(cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpStopFire(Pawn p)
        {
            int me = FpIds.Executing;
            string result = "-";
            LocalTargetInfo was = p.CurJob != null ? p.CurJob.targetA : LocalTargetInfo.Invalid;
            try
            {
                result = StopFire(p, me);
            }
            finally
            {
                if (FpTestKit.Active)
                {
                    FpTestKit.OnCommand(me, "FpStopFire p=" + p.thingIDNumber + " by=" + me + " -> " + result + " | " + Digest(was));
                }
            }
        }

        // FpFire's body; returns what happened (the self-test's command line).
        private static string Fire(Pawn p, LocalTargetInfo target, bool hold, int me)
        {
            if (!Authorized(p, me))
            {
                FpHits.Hit("Fire refused");
                return "unauthorized";
            }
            if (!p.Drafted)
            {
                return Refuse(p, target, "R3D_Fp_Combat_NotDrafted", p.LabelShort);
            }
            if (!target.IsValid)
            {
                return Refuse(p, target, "R3D_Fp_Combat_TargetGone");
            }
            Thing t = target.Thing;
            if (target.HasThing)
            {
                if (t.Destroyed || !t.Spawned || t.Map != p.Map)
                {
                    return Refuse(p, target, "R3D_Fp_Combat_TargetGone");
                }
                if (!Attackable(p, t))
                {
                    return Refuse(p, target, "R3D_Fp_Combat_NotHostileMsg", t.LabelShort);
                }
            }
            else if (!target.Cell.InBounds(p.Map) || !CanTargetGround(p))
            {
                return Refuse(p, target, "R3D_Fp_Combat_NoGround", p.LabelShort);
            }
            bool melee = UseMelee(p, target);
            if (melee && !target.HasThing)
            {
                return Refuse(p, target, "R3D_Fp_Combat_NoGround", p.LabelShort);
            }
            // The game's own checks for the order (the drafted menu's; melee in reach falls back to shooting as the hint
            // said); the order it returns isn't run. Inside the map's random state push and pop (review finding, M5): the
            // melee check picks the colonist's melee verb for a new target (Pawn_MeleeVerbs.ChooseMeleeVerb draws from
            // Rand), and spec 2 allows no draw in a command; every machine runs the same, so this keeps the map's sequence
            // as the game's own order would leave it until the strike.
            string why;
            Rand.PushState();
            try
            {
                why = VanillaRefusal(p, target, ref melee);
            }
            finally
            {
                Rand.PopState();
            }
            if (why != null)
            {
                return RefuseText(p, target, "R3D_Fp_Combat_Cant", "R3D_Fp_Combat_Cant".Translate(p.LabelShort, why.UncapitalizeFirst()));
            }
            JobDef def = melee ? JobDefOf.AttackMelee : JobDefOf.AttackStatic;
            // A queued order (the queue key held) is one attack: a held button can't reach a job that hasn't started.
            bool queued = KeyBindingDefOf.QueueOrder.IsDownEvent;
            bool keep = hold && !queued;
            Job cur = p.CurJob;
            if (cur != null && cur.def == def && cur.targetA == target && cur.verbToUse == null && p.jobs.curDriver != null && !p.jobs.curDriver.ended)
            {
                // The same attack is running (a press between its attacks): held now, not restarted (a restart would throw
                // away the aim in progress).
                if (keep)
                {
                    if (melee)
                    {
                        cur.maxNumMeleeAttacks = int.MaxValue;
                    }
                    else
                    {
                        cur.maxNumStaticAttacks = int.MaxValue;
                    }
                }
                FpHits.Hit("Fire extended");
                return "extended job " + cur.loadID + " " + def.defName + " made " + AttacksMade(p.jobs.curDriver);
            }
            Job job = JobMaker.MakeJob(def, target);
            if (melee)
            {
                job.maxNumMeleeAttacks = keep ? int.MaxValue : 1;
                if (t is Pawn tp)
                {
                    job.killIncappedTarget = tp.Downed;   // as the game's melee order
                }
            }
            else
            {
                job.maxNumStaticAttacks = keep ? int.MaxValue : 1;
                job.endIfCantShootTargetFromCurPos = true;   // the target out of sight ends the job rather than aiming on
            }
            // As every first-person order: a skill check the ring has shown counts as missed when the work is left (10.2).
            // Only when the attack replaces the current job (TryTakeOrderedJob's own rule): a queued attack, or a job the
            // game won't let a player interrupt, leaves the work running.
            if (!queued && cur != null && p.jobs.IsCurrentJobPlayerInterruptible() && !cur.def.forceCompleteBeforeNextJob)
            {
                FpWork.ForfeitPending(FpRegistry.EntryOf(p));
            }
            if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc))
            {
                return Refuse(p, target, "R3D_Fp_Combat_CantStart", p.LabelShort);
            }
            FpHits.Hit("Fire");
            return "job " + job.loadID + " " + def.defName + " max=" + (keep ? "all" : "1") + (p.CurJob == job ? "" : " queued");
        }

        // FpStopFire's body.
        private static string StopFire(Pawn p, int me)
        {
            if (!Authorized(p, me))
            {
                return "unauthorized";
            }
            FpHits.Hit("StopFire");
            Job cur = p.CurJob;
            JobDriver d = p.jobs.curDriver;
            if (cur == null || d == null || d.ended)
            {
                return "no job";
            }
            int made = AttacksMade(d);
            if (cur.def == JobDefOf.AttackStatic && d is JobDriver_AttackStatic)
            {
                // The driver ends the job once this many attacks have started and the colonist isn't busy (aiming, firing,
                // cooling down), so the attack in progress finishes.
                cur.maxNumStaticAttacks = Math.Max(1, made);
                return "static made " + made + " max " + cur.maxNumStaticAttacks;
            }
            if (cur.def == JobDefOf.AttackMelee && d is JobDriver_AttackMelee)
            {
                cur.maxNumMeleeAttacks = Math.Max(1, made);
                // The melee driver checks its count only after an attack, so with attacks made it would strike once more:
                // it ends now instead (each strike counted is over; a warmup in progress would be cut, so not then).
                if (made >= 1 && !(p.stances.curStance is Stance_Warmup))
                {
                    p.jobs.EndCurrentJob(JobCondition.Succeeded);
                    return "melee made " + made + ", ended";
                }
                return "melee made " + made + " max " + cur.maxNumMeleeAttacks;
            }
            return "not attacking (" + cur.def.defName + ")";
        }

        // The issuer controls the pawn on its own map (spec 9.2: authorized).
        private static bool Authorized(Pawn p, int me)
        {
            if (me == FpIds.None || !p.Spawned || p.Dead)
            {
                return false;
            }
            FpEntry e = FpRegistry.EntryOf(p);
            return e != null && e.controller == me && e.comp != null && e.comp.map == p.Map;
        }

        private static string Refuse(Pawn p, LocalTargetInfo target, string key, params NamedArgument[] args)
        {
            return RefuseText(p, target, key, key.Translate(args));
        }

        private static string RefuseText(Pawn p, LocalTargetInfo target, string key, string text)
        {
            FpHits.Hit("Fire refused");
            TellIssuer(text, target.HasThing && target.Thing.Spawned ? target.Thing : p);
            return "refused " + key;
        }

        // Only the player who sent the command sees why (historical false: the message archive is saved game state).
        private static void TellIssuer(string text, Thing at)
        {
            if (MpBridge.InSession && !MP.IsExecutingSyncCommandIssuedBySelf)
            {
                return;
            }
            Messages.Message(text, at != null && at.Spawned ? new LookTargets(at) : LookTargets.Invalid, MessageTypeDefOf.RejectInput, historical: false);
        }

        // ---- self-test lines (only with FpTestKit.Active; synced state, formatted alike everywhere)

        private static string Describe(LocalTargetInfo t)
        {
            if (t.HasThing)
            {
                return "T" + t.Thing.thingIDNumber.ToString(CultureInfo.InvariantCulture);
            }
            return t.Cell.IsValid ? "c" + t.Cell.x.ToString(CultureInfo.InvariantCulture) + "," + t.Cell.z.ToString(CultureInfo.InvariantCulture) : "none";
        }

        // The target's health: injuries and their total severity for a pawn (with dead and downed), hit points for a thing.
        internal static string Digest(LocalTargetInfo t)
        {
            Thing th = t.Thing;
            if (th == null)
            {
                return "-";
            }
            CultureInfo inv = CultureInfo.InvariantCulture;
            if (th is Pawn tp)
            {
                int n = 0;
                float sev = 0f;
                if (tp.health?.hediffSet != null)
                {
                    foreach (Hediff h in tp.health.hediffSet.hediffs)
                    {
                        if (h is Hediff_Injury)
                        {
                            n++;
                            sev += h.Severity;
                        }
                    }
                }
                return "T" + tp.thingIDNumber.ToString(inv) + " inj=" + n.ToString(inv) + " sev=" + sev.ToString("F3", inv)
                    + " down=" + (tp.Downed ? "1" : "0") + " dead=" + (tp.Dead ? "1" : "0");
            }
            return "T" + th.thingIDNumber.ToString(inv) + " hp=" + th.HitPoints.ToString(inv) + "/" + th.MaxHitPoints.ToString(inv);
        }

        // ================================================================ the reticle (interface)

        private static void RefreshReticle(Pawn p)
        {
            double now = Now;
            if (p == reticlePawn && now - reticleAt < ReticleRefresh && now >= reticleAt)
            {
                return;
            }
            reticleAt = now;
            reticlePawn = p;
            ThingWithComps weapon = p.equipment?.Primary;
            reticleWeapon = weapon != null ? weapon.LabelShortCap : (string)"R3D_Fp_Combat_Unarmed".Translate();
            reticleChance = null;
            reticleCanHit = false;
            Aim a = Resolve(p, FpPick.Current);
            reticleKind = a.kind;
            if (a.kind != AimKind.Fire)
            {
                return;
            }
            reticleCanHit = VanillaRefusal(p, a.target, ref a.melee) == null;
            if (a.melee)
            {
                return;
            }
            Verb v = RangedVerb(p);
            reticleCanHit = reticleCanHit && v != null && v.CanHitTarget(a.target);   // spec 12: red when the verb can't hit it
            if (reticleCanHit && FpSettings.showHitChance)
            {
                reticleChance = ShotReport.HitReportFor(p, v, a.target).TotalEstimatedHitChance.ToStringPercent();
            }
        }

        private static void DrawReticle(Pawn p, Vector2 c, float s)
        {
            float r = 13f * s;
            float thick = Mathf.Max(1f, Mathf.Round(2f * s));
            Color col = reticleKind == AimKind.None ? IdleColour
                : reticleKind == AimKind.Friendly || !reticleCanHit ? RefusedColour
                : ReadyColour;
            GUI.color = Color.white;   // Widgets.DrawLine tints with its colour on top of GUI.color under UnityStubs (CLAUDE.md)
            // Four brackets at the diagonals, each 50 degrees of the ring, with a dark outline.
            for (int k = 0; k < 4; k++)
            {
                float mid = 45f + 90f * k;
                Arc(c, r, mid - 25f, mid + 25f, OutlineColour, thick + 2f);
                Arc(c, r, mid - 25f, mid + 25f, col, thick);
            }
            // Warmup: an arc filling clockwise from 12 o'clock; cooldown: a thinner one emptying.
            Stance stance = p.stances?.curStance;
            if (stance is Stance_Busy busy)
            {
                float frac = BusyFraction(busy);
                if (busy is Stance_Warmup)
                {
                    Arc(c, r + 5f * s, 0f, 360f * frac, WarmupColour, thick);
                }
                else if (busy is Stance_Cooldown && trackedTotal >= 10)
                {
                    Arc(c, r + 5f * s, 0f, 360f * (1f - frac), CooldownColour, Mathf.Max(1f, thick - 1f));
                }
            }
            // The hit chance and the weapon, right of the crosshair.
            float x = c.x + r + 9f * s;
            Text.Font = s >= 1.3f ? GameFont.Small : GameFont.Tiny;
            float lh = Text.LineHeight;
            if (reticleChance != null)
            {
                Label(new Rect(x, c.y - lh, 120f * s, lh), reticleChance, col);
            }
            Text.Font = GameFont.Tiny;
            Label(new Rect(x, c.y + 1f, 200f * s, Text.LineHeight), reticleWeapon, new Color(1f, 1f, 1f, 0.7f));
        }

        // How far the busy stance has run, 0..1 (its full length remembered from the first frame it was seen).
        private static float BusyFraction(Stance_Busy busy)
        {
            if (busy != trackedStance)
            {
                trackedStance = busy;
                // The interface's TicksGame is the world's clock, which with Multiplayer's async time isn't the map's (the
                // stance's startedTick): a difference no stance could have run is ignored. The reticle runs every frame
                // while drafted, so a stance is normally first seen within a frame of its start anyway.
                int elapsed = Find.TickManager != null ? Find.TickManager.TicksGame - busy.startedTick : 0;
                if (elapsed < 0 || elapsed > 600)
                {
                    elapsed = 0;
                }
                trackedTotal = Mathf.Max(1, busy.ticksLeft + elapsed);
            }
            trackedTotal = Mathf.Max(trackedTotal, busy.ticksLeft);
            return Mathf.Clamp01(1f - busy.ticksLeft / (float)trackedTotal);
        }

        // An arc of the ring, clockwise from 12 o'clock, in segments of at most 10 degrees.
        private static void Arc(Vector2 c, float r, float fromDeg, float toDeg, Color col, float width)
        {
            if (toDeg - fromDeg < 0.5f)
            {
                return;
            }
            int n = Mathf.Max(1, Mathf.CeilToInt((toDeg - fromDeg) / 10f));
            float step = (toDeg - fromDeg) / n;
            for (int i = 0; i < n; i++)
            {
                Widgets.DrawLine(Rim(c, r, fromDeg + i * step), Rim(c, r, fromDeg + (i + 1) * step), col, width);
            }
        }

        // The point of the ring at an angle clockwise from 12 o'clock (UI y grows down).
        private static Vector2 Rim(Vector2 c, float r, float deg)
        {
            float a = deg * Mathf.Deg2Rad;
            return new Vector2(c.x + Mathf.Sin(a) * r, c.y - Mathf.Cos(a) * r);
        }

        private static void Label(Rect rect, string text, Color col)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = new Color(0f, 0f, 0f, 0.8f * col.a);
            Widgets.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text);
            GUI.color = col;
            Widgets.Label(rect, text);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
