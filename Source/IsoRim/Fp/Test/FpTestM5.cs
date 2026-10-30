using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
    // Milestone 5 scenarios (fp-play-spec 16, M5): firing in first person (FpCombat) with the game's own odds. Solo
    // (windowed, injected input) except mp5-fire (host headless, client windowed).
    //
    // Deviations from the spec's table, to make the runs deterministic and to answer the user's requirement (no better hit
    // chance than vanilla) with a measurement:
    // - The shooter is C1 (the scene's rifle) unless C1 is incapable of violence (-quicktest colonists are random); then
    //   C2, who gets the rifle.
    // - The solo scenarios that shoot make the scene's downed pirate take no damage (a test-only prefix on
    //   Pawn.PreApplyDamage, Harmony id isorim.fptest.m5, installed by the scenario and removed at its end): a head or heart
    //   hit could kill him mid-scenario. The shots and their hits are unchanged (the hit is decided before the damage).
    // - m5-hold holds the button until a second attack has started (the spec's 3 s is borderline for the assault rifle's
    //   cycle: about 1 s warmup, the burst, 1.7 s cooldown) and reports how long that took.
    // - m5-odds does more than compare two estimates: (1) a Harmony audit of every method on the shot's path and of the
    //   combat stats' parts, before the test's own hooks go in; (2) the first-person job and the game's own "Fire at"
    //   order compared field by field, and the verb, warmup and cooldown each used (recorded by a test-only SetStance
    //   postfix); (3) the hit rates measured: alternating blocks of first-person clicks and the game's own order from the
    //   same cell at the same target, every bullet's impact counted (a test-only Bullet.Impact postfix), compared with a
    //   two-proportion z test.
    // - Every scenario that aims at the pirates first clears the cells between the scene's square and them (x +11..+13,
    //   z -3..+3: things, roof, fog; pawns and terrain untouched): the scene clears only up to x +10 and the pirate's own
    //   cell, so a rock or a tree at (+11, 0) would block both the crosshair's ray and the line of fire. The map's wild
    //   animals are taken off too, and m5-odds clicks only with the crosshair on the pirate: in the first runs a pawn that
    //   wandered into the line of sight took one click of 48 ("Not hostile"), which a player wouldn't have made.
    // - m5-odds measures 4 blocks of 12 first-person clicks against 4 blocks of 12 attacks of the game's order (about 144
    //   bullets each); every bullet at the downed pirate from 12 cells has the game's 50% lying-target miss on top of the
    //   aim, so the rates are around a third and the z test catches a gap of about 0.2.
    // - Added: m5-reclick (a click between the attacks of a running attack gets exactly one more attack).
    // - Added at integration: m5-wide (review finding: the aim's wider radius beside a standing target's chest, where the
    //   crosshair's ray rises over his shoulder and finds nothing); m5-undrafted also checks the hint's look (no "[LMB]"
    //   undrafted, red on a colonist while drafted).
    // - mp5-fire's digest of the targets' health rides on the FpFire/FpStopFire command lines (FPTEST CMD), which
    //   fp_compare.py compares between host and client; it fires at a row of five downed pirates (one would die), spawned
    //   with the host's scene before hosting (FpTestKit.AddSceneExtra), and the shooter gets a rifle there too.
    internal static class FpTestM5
    {
        internal static void Register(Dictionary<string, Func<IEnumerator<int>>> s)
        {
            s["m5-click"] = Click;
            s["m5-hold"] = Hold;
            s["m5-reclick"] = Reclick;
            s["m5-cell"] = Cell;
            s["m5-odds"] = Odds;
            s["m5-undrafted"] = Undrafted;
            s["m5-wasd"] = Wasd;
            s["m5-wide"] = Wide;
            s["mp5-fire"] = MpFire;
            K.AddSceneExtra("mp5-fire", MpKit);
        }

        private const string ControlJob = "IsoRimFp_Control";

        // ---------------------------------------------------------------- helpers

        private static string JobOf(Pawn p)
        {
            return p?.CurJob == null ? "none" : p.CurJob.def.defName + ":" + p.CurJob.loadID + " " + p.CurJob.targetA;
        }

        private static bool InControlJob(Pawn p)
        {
            return p?.CurJob?.def?.defName == ControlJob;
        }

        private static bool Attacking(Pawn p)
        {
            JobDef d = p?.CurJob?.def;
            return d == JobDefOf.AttackStatic || d == JobDefOf.AttackMelee;
        }

        private static bool AttackingOn(Pawn p, Thing t)
        {
            return Attacking(p) && p.CurJob.targetA.Thing == t;
        }

        // Jobs are pooled (JobMaker.ReturnToPool when a job ends; the next MakeJob, often the control job a tick later,
        // hands out the same object again), so a job is followed by its loadID, never by reference, and its fields are read
        // only while it is still the pawn's job.
        private static int JobId(Pawn p)
        {
            return p?.CurJob != null ? p.CurJob.loadID : -1;
        }

        private static bool IsJob(Pawn p, int id)
        {
            return id >= 0 && p?.CurJob != null && p.CurJob.loadID == id;
        }

        private static int Made(Pawn p)
        {
            return FpCombat.AttacksMade(p?.jobs?.curDriver);
        }

        private static string Mode => "mode " + FpPlay.Mode + ", confirmed " + FpPlay.Confirmed + ", first person " + FirstPerson.On;

        private static void SelectOnly(Thing t)
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(t, false, false);
        }

        // F5 on a pawn, then wait for play to be confirmed and the pawn to stand in the control job (as FpTestM1.EnterPlay).
        private static IEnumerable<int> EnterPlay(Pawn p)
        {
            SelectOnly(p);
            yield return K.Tap(Key.F5);
            yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Playing && FpPlay.Confirmed, "for play to be confirmed on " + p.LabelShort, 600);
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

        // The draft key while playing (FpInput toggles Drafted, a synced call), then wait for it to show.
        private static IEnumerable<int> SetDrafted(Pawn p, bool on)
        {
            if (p.Drafted != on)
            {
                yield return K.Tap(Key.R);
                yield return K.WaitUntil(() => p.Drafted == on, "for " + p.LabelShort + " to be " + (on ? "drafted" : "undrafted"), 600);
            }
            yield return 10;
        }

        // Turns the play camera to an absolute yaw and pitch (FpCamera only takes deltas).
        private static void Turn(float yaw, float pitch)
        {
            FpCamera.Look(FpMath.DeltaDeg(FpCamera.Yaw, yaw), pitch - FpCamera.Pitch);
        }

        // Points the crosshair at a true-space point (x, height, z), twice a few frames apart (the eye moves with the yaw).
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

        // A lying pawn's body, a little over the ground.
        private static IEnumerable<int> AimAtPawn(Pawn t)
        {
            Vector3 dp = t.DrawPos;
            foreach (int w in AimAt(new Vector3(dp.x, 0.15f, dp.z))) yield return w;
        }

        private static IEnumerable<int> PressRelease()
        {
            yield return K.Button(0, true);
            yield return K.Button(0, false);
        }

        // Whom the solo scenarios shoot with: C1 (the scene's rifle), or C2 with that rifle when C1 can't do violence.
        private static Pawn Shooter(bool giveRifle)
        {
            Pawn p = !S.C1.WorkTagIsDisabled(WorkTags.Violent) ? S.C1 : S.C2;
            if (giveRifle && p != S.C1 && p.equipment != null && (p.equipment.Primary == null || p.equipment.Primary.def.defName != "Gun_AssaultRifle"))
            {
                ThingWithComps rifle = S.C1.equipment?.Primary;
                if (rifle != null)
                {
                    S.C1.equipment.Remove(rifle);
                }
                else
                {
                    rifle = (ThingWithComps)ThingMaker.MakeThing(ThingDef.Named("Gun_AssaultRifle"));
                }
                p.equipment.DestroyAllEquipment();
                p.equipment.AddEquipment(rifle);
            }
            return p;
        }

        // The pirate is there, downed (anaesthetised again, so he can't wake up mid-scenario) and hostile, with nothing
        // between him and the cleared square (ClearLane).
        private static bool PirateReady(string scenario)
        {
            Pawn t = S.Pirate;
            string lane = t != null && t.Spawned ? ClearLane(t.Map) : null;
            if (t != null && t.Spawned && !t.Dead)
            {
                HealthUtility.TryAnesthetize(t);
            }
            return K.Check(scenario + " pirate ready", t != null && t.Spawned && !t.Dead && t.Downed && t.HostileTo(S.C1) && lane == null,
                t == null ? "no pirate in the scene" : t.ThingID + " spawned " + t.Spawned + ", dead " + t.Dead + ", downed " + t.Downed + (lane != null ? ", " + lane : ""));
        }

        // The scene clears a 21 x 21 square round C1 (x up to +10) and the pirate's own cell (+12, 0); the cells between
        // and round the pirates keep whatever the map had there, and a rock, a tree or fog in them would stop the
        // crosshair's ray and the line of fire. Cleared as the scene clears (pawns untouched; terrain kept).
        private static string ClearLane(Map map)
        {
            for (int x = 11; x <= 13; x++)
            {
                for (int z = -3; z <= 3; z++)
                {
                    IntVec3 c = S.Origin + new IntVec3(x, 0, z);
                    if (!c.InBounds(map))
                    {
                        return "lane cell " + c + " out of bounds";
                    }
                    // A steam geyser in the lane isn't destroyable: the guard is lifted as FpScene does (and the game's own
                    // colony maker) while clearing ground (a run had one at (+11..+12, ...): four "Tried to destroy
                    // non-destroyable thing" errors, and the geyser left under the crosshair took the click).
                    Thing.allowDestroyNonDestroyable = true;
                    try
                    {
                        foreach (Thing t in c.GetThingList(map).ToList())
                        {
                            if (!(t is Pawn) && !t.Destroyed)
                            {
                                t.Destroy(DestroyMode.Vanish);
                            }
                        }
                    }
                    finally
                    {
                        Thing.allowDestroyNonDestroyable = false;
                    }
                    map.roofGrid.SetRoof(c, null);
                    map.fogGrid.Unfog(c);
                }
            }
            // Wild animals wander through the lane: in the first two m5-odds runs a click met one between the shooter and the
            // pirate ("Not hostile: the right button has every order", nothing fired), and one in the line of fire takes
            // bullets of either arm. Taken off the map, so the clicks and the bullets meet only the pirates.
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(x => x.Faction == null && x.RaceProps != null && x.RaceProps.Animal).ToList())
            {
                a.Destroy(DestroyMode.Vanish);
            }
            return null;
        }

        private static IEnumerable<int> Finish(Pawn p)
        {
            if (p != null && FpPlay.Mode == FpMode.Playing && p.Drafted)
            {
                foreach (int w in SetDrafted(p, false)) yield return w;
            }
            foreach (int w in LeavePlay()) yield return w;
            RemoveTestHooks();
            yield return K.Done();
        }

        // ---------------------------------------------------------------- test-only hooks (solo scenarios that shoot)

        private const string TestHarmonyId = "isorim.fptest.m5";
        private static Harmony testHarmony;
        private static Pawn shieldedPawn;     // takes no damage
        private static Pawn countShooter;     // whose bullets and stances are counted...
        private static Thing countTarget;     // ...at this target
        private static int arm = -1;          // 0 first person, 1 the game's order, -1 not counting
        private static readonly int[] shots = new int[2];
        private static readonly int[] hits = new int[2];
        private static readonly List<int>[] warmups = { new List<int>(), new List<int>() };
        private static readonly List<int>[] cooldowns = { new List<int>(), new List<int>() };
        private static readonly List<Verb>[] verbs = { new List<Verb>(), new List<Verb>() };

        private static bool InstallTestHooks(string scenario, Pawn shooter, Pawn target)
        {
            shieldedPawn = target;
            countShooter = shooter;
            countTarget = target;
            arm = -1;
            for (int i = 0; i < 2; i++)
            {
                shots[i] = hits[i] = 0;
                warmups[i].Clear();
                cooldowns[i].Clear();
                verbs[i].Clear();
            }
            if (testHarmony != null)
            {
                return true;
            }
            try
            {
                Harmony h = new Harmony(TestHarmonyId);
                h.Patch(AccessTools.DeclaredMethod(typeof(Pawn), nameof(Pawn.PreApplyDamage)),
                    prefix: new HarmonyMethod(typeof(FpTestM5), nameof(PreApplyDamage_Prefix)));
                h.Patch(AccessTools.DeclaredMethod(typeof(Bullet), "Impact"),
                    postfix: new HarmonyMethod(typeof(FpTestM5), nameof(BulletImpact_Postfix)));
                h.Patch(AccessTools.DeclaredMethod(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.SetStance)),
                    postfix: new HarmonyMethod(typeof(FpTestM5), nameof(SetStance_Postfix)));
                testHarmony = h;
                return true;
            }
            catch (Exception e)
            {
                K.Check(scenario + " test hooks", false, e.ToString());
                return false;
            }
        }

        private static void RemoveTestHooks()
        {
            arm = -1;
            shieldedPawn = null;
            if (testHarmony != null)
            {
                testHarmony.UnpatchAll(TestHarmonyId);
                testHarmony = null;
            }
        }

        private static bool PreApplyDamage_Prefix(Pawn __instance, ref bool absorbed)
        {
            if (shieldedPawn == null || __instance != shieldedPawn)
            {
                return true;
            }
            FpHits.Hit("M5 test: damage absorbed");
            absorbed = true;
            return false;
        }

        // Every bullet of the shooter aimed at the target, when it lands: a hit if it hit the target itself.
        private static void BulletImpact_Postfix(Bullet __instance, Thing hitThing)
        {
            if (arm < 0 || __instance == null || __instance.Launcher != countShooter || __instance.intendedTarget.Thing != countTarget)
            {
                return;
            }
            shots[arm]++;
            if (hitThing == countTarget)
            {
                hits[arm]++;
            }
        }

        // The shooter's warmups (their full length and verb) and cooldowns, as the game starts them.
        private static void SetStance_Postfix(Pawn_StanceTracker __instance, Stance newStance)
        {
            if (arm < 0 || __instance?.pawn != countShooter)
            {
                return;
            }
            if (newStance is Stance_Warmup w)
            {
                warmups[arm].Add(w.ticksLeft);
                verbs[arm].Add(w.verb);
            }
            else if (newStance is Stance_Cooldown c)
            {
                cooldowns[arm].Add(c.ticksLeft);
            }
        }

        // ---------------------------------------------------------------- m5-click

        // One click on the pirate: the game's AttackStatic, capped at one attack by the release, which is exactly one attack.
        private static IEnumerator<int> Click()
        {
            Pawn p = Shooter(true);
            if (!PirateReady("m5-click") || !InstallTestHooks("m5-click", p, S.Pirate))
            {
                yield return K.Done();
                yield break;
            }
            foreach (int w in EnterPlay(p)) yield return w;
            foreach (int w in SetDrafted(p, true)) yield return w;
            foreach (int w in AimAtPawn(S.Pirate)) yield return w;
            yield return 30;   // past the click guard after capture, and the hint
            K.Info("m5-click hint: " + FpActions.Hint + " (" + FpActions.HintKind + "), crosshair on " + (FpPick.Current.Thing?.ThingID ?? "nothing"));
            K.Shot("m5_click_reticle");   // drafted, on the pirate: the brackets, the hit chance, the weapon
            int stop0 = FpHits.Count("StopFire");
            arm = 0;
            foreach (int w in PressRelease()) yield return w;
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => AttackingOn(p, S.Pirate) || K.Ticks > tick0 + 60, "for the attack job");
            Job job = p.CurJob;
            JobDriver d = p.jobs.curDriver;
            K.Check("m5-click attack job", job != null && job.def == JobDefOf.AttackStatic && job.targetA.Thing == S.Pirate && job.playerForced && job.verbToUse == null,
                JobOf(p));
            if (job == null || job.def != JobDefOf.AttackStatic)
            {
                foreach (int w in Finish(p)) yield return w;
                yield break;
            }
            int jid = job.loadID;
            K.Shot("m5_click_attack");   // the attack running: the warmup or cooldown arc
            yield return K.WaitUntil(() => FpHits.Count("StopFire") > stop0, "for the release's FpStopFire", 600);
            bool still = IsJob(p, jid);
            int cap = still ? job.maxNumStaticAttacks : -1;
            K.Check("m5-click capped at one attack", still && cap == 1, still ? "maxNumStaticAttacks " + cap : "the attack had already ended: " + JobOf(p));
            int last = 0;
            yield return K.WaitUntil(() =>
            {
                if (IsJob(p, jid))
                {
                    last = FpCombat.AttacksMade(d);
                }
                return !IsJob(p, jid);
            }, "for the attack to end", 1800);
            K.Check("m5-click one attack made", last == 1, "numAttacksMade " + last + " when the job ended");
            yield return K.WaitUntil(() => InControlJob(p), "for the control job", 600);
            yield return K.WaitTicks(30);
            arm = -1;
            K.Info("m5-click bullets " + shots[0] + " (burst " + (p.equipment?.PrimaryEq?.PrimaryVerb?.BurstShotCount ?? -1) + "), hits " + hits[0]);
            K.Check("m5-click back in control", InControlJob(p), JobOf(p));
            foreach (int w in Finish(p)) yield return w;
        }

        // ---------------------------------------------------------------- m5-hold

        // The button held: attacks go on (at least two start); the release lets the attack in progress finish and starts no
        // other.
        private static IEnumerator<int> Hold()
        {
            Pawn p = Shooter(true);
            if (!PirateReady("m5-hold") || !InstallTestHooks("m5-hold", p, S.Pirate))
            {
                yield return K.Done();
                yield break;
            }
            foreach (int w in EnterPlay(p)) yield return w;
            foreach (int w in SetDrafted(p, true)) yield return w;
            foreach (int w in AimAtPawn(S.Pirate)) yield return w;
            yield return 30;
            yield return K.Button(0, true);
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => AttackingOn(p, S.Pirate) || K.Ticks > tick0 + 60, "for the attack job");
            Job job = p.CurJob;
            JobDriver d = p.jobs.curDriver;
            if (!K.Check("m5-hold attack job", job != null && job.def == JobDefOf.AttackStatic && job.targetA.Thing == S.Pirate, JobOf(p)))
            {
                yield return K.Button(0, false);
                foreach (int w in Finish(p)) yield return w;
                yield break;
            }
            int jid = job.loadID;
            int start = K.Ticks;
            yield return K.WaitUntil(() => FpCombat.AttacksMade(d) >= 2 || !IsJob(p, jid) || K.Ticks > start + 900, "for a second attack while held", 3000);
            bool held = IsJob(p, jid);
            int made = held ? FpCombat.AttacksMade(d) : -1;
            int heldCap = held ? job.maxNumStaticAttacks : -1;
            K.Check("m5-hold attacks go on while held", made >= 2 && heldCap == int.MaxValue,
                "numAttacksMade " + made + " after " + (K.Ticks - start) + " ticks held, maxNumStaticAttacks " + heldCap
                + (held ? "" : "; the attack ended: " + JobOf(p)));
            int stop0 = FpHits.Count("StopFire");
            yield return K.Button(0, false);
            yield return K.WaitUntil(() => FpHits.Count("StopFire") > stop0, "for the release's FpStopFire", 600);
            bool still = IsJob(p, jid);
            int cap = still ? job.maxNumStaticAttacks : -1;
            int madeAtStop = FpCombat.AttacksMade(d);
            K.Check("m5-hold release caps at the attacks started", still && cap == Math.Max(1, madeAtStop),
                "maxNumStaticAttacks " + cap + ", numAttacksMade " + madeAtStop + (still ? "" : "; the attack had already ended: " + JobOf(p)));
            int last = madeAtStop;
            yield return K.WaitUntil(() =>
            {
                if (IsJob(p, jid))
                {
                    last = FpCombat.AttacksMade(d);
                }
                return !IsJob(p, jid);
            }, "for the attack to end after the burst in progress", 1800);
            K.Check("m5-hold no attack after the release", last == cap, "numAttacksMade " + last + " at the end, cap " + cap);
            yield return K.WaitUntil(() => InControlJob(p), "for the control job", 600);
            foreach (int w in Finish(p)) yield return w;
        }

        // ---------------------------------------------------------------- m5-reclick

        // A click on the pirate, then a second click while the first attack cools down: exactly two attacks (the second
        // click's release waits until its attack has started, FpCombat's owed release).
        private static IEnumerator<int> Reclick()
        {
            Pawn p = Shooter(true);
            if (!PirateReady("m5-reclick") || !InstallTestHooks("m5-reclick", p, S.Pirate))
            {
                yield return K.Done();
                yield break;
            }
            foreach (int w in EnterPlay(p)) yield return w;
            foreach (int w in SetDrafted(p, true)) yield return w;
            foreach (int w in AimAtPawn(S.Pirate)) yield return w;
            yield return 30;
            foreach (int w in PressRelease()) yield return w;
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => AttackingOn(p, S.Pirate) || K.Ticks > tick0 + 60, "for the attack job");
            Job job = p.CurJob;
            JobDriver d = p.jobs.curDriver;
            if (!K.Check("m5-reclick attack job", job != null && job.def == JobDefOf.AttackStatic, JobOf(p)))
            {
                foreach (int w in Finish(p)) yield return w;
                yield break;
            }
            int jid = job.loadID;
            // The cooldown after the burst (the gaps between a burst's shots are cooldowns too, a few ticks long).
            yield return K.WaitUntil(() => !IsJob(p, jid) || p.stances.curStance is Stance_Cooldown c && c.ticksLeft > 20,
                "for the first attack's cooldown", 1800);
            K.Check("m5-reclick still the first attack", IsJob(p, jid) && FpCombat.AttacksMade(d) == 1,
                JobOf(p) + ", numAttacksMade " + FpCombat.AttacksMade(d));
            int stop0 = FpHits.Count("StopFire");
            foreach (int w in PressRelease()) yield return w;
            int last = 0;
            yield return K.WaitUntil(() =>
            {
                if (IsJob(p, jid))
                {
                    last = FpCombat.AttacksMade(d);
                }
                return !IsJob(p, jid);
            }, "for the attack to end", 3600);
            K.Check("m5-reclick release sent once the second attack started", FpHits.Count("StopFire") > stop0,
                "StopFire " + (FpHits.Count("StopFire") - stop0) + " after the second click");
            K.Check("m5-reclick two attacks", last == 2, "numAttacksMade " + last + " when the job ended");
            yield return K.WaitUntil(() => InControlJob(p), "for the control job", 600);
            foreach (int w in Finish(p)) yield return w;
        }

        // ---------------------------------------------------------------- m5-cell

        // The rifle aimed at bare ground: no attack (a gun's cell shot would skip the cover roll). Frag grenades: thrown at
        // the cell.
        private static IEnumerator<int> Cell()
        {
            Pawn p = Shooter(true);
            foreach (int w in EnterPlay(p)) yield return w;
            foreach (int w in SetDrafted(p, true)) yield return w;
            // Seven cells south of the scene's origin: cleared ground, out of reach, away from C2 and the table frame.
            IntVec3 ground = S.Origin + new IntVec3(0, 0, -7);
            foreach (int w in AimAt(new Vector3(ground.x + 0.5f, 0f, ground.z + 0.5f))) yield return w;
            yield return 30;
            FpHit h = FpPick.Current;
            K.Check("m5-cell aimed at bare ground", h.Valid && h.Thing == null, "hit " + (h.Thing?.ThingID ?? "ground") + " cell " + h.Cell);
            K.Check("m5-cell rifle can't target places", !FpCombat.CanTargetGround(p), p.equipment?.Primary?.def.defName ?? "unarmed");
            int sent0 = FpHits.Count("Fire sent");
            foreach (int w in PressRelease()) yield return w;
            yield return K.WaitTicks(60);
            K.Check("m5-cell rifle: no attack", !Attacking(p) && FpHits.Count("Fire sent") == sent0, JobOf(p) + ", fire sent " + (FpHits.Count("Fire sent") - sent0));

            ThingWithComps rifle = p.equipment.Primary;
            if (rifle != null)
            {
                p.equipment.Remove(rifle);
            }
            ThingWithComps nades = (ThingWithComps)ThingMaker.MakeThing(ThingDef.Named("Weapon_GrenadeFrag"));
            p.equipment.AddEquipment(nades);
            yield return 15;
            K.Check("m5-cell grenades can target places", FpCombat.CanTargetGround(p), p.equipment.Primary?.def.defName ?? "unarmed");
            foreach (int w in AimAt(new Vector3(ground.x + 0.5f, 0f, ground.z + 0.5f))) yield return w;
            yield return 30;
            h = FpPick.Current;
            // The same cell as the rifle's: the hint is worked out again for the new weapon (it kept the rifle's TooFar).
            for (int i = 0; i < 120 && FpActions.HintKind != FpHintKind.Action; i++)
            {
                yield return 1;
            }
            K.Check("m5-cell grenade hint", FpActions.HintKind == FpHintKind.Action && !FpActions.Hint.NullOrEmpty(),
                "hint \"" + FpActions.Hint + "\" (" + FpActions.HintKind + ")");
            foreach (int w in PressRelease()) yield return w;
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => Attacking(p) || K.Ticks > tick0 + 60, "for the throw");
            Job job = p.CurJob;
            K.Check("m5-cell grenade thrown at the cell", job != null && job.def == JobDefOf.AttackStatic && !job.targetA.HasThing && job.targetA.Cell == h.Cell,
                JobOf(p) + ", aimed cell " + h.Cell);
            yield return K.WaitUntil(() => !Attacking(p), "for the throw to end", 1800);
            yield return K.WaitTicks(60);
            p.equipment.Remove(nades);
            nades.Destroy();
            if (rifle != null && !rifle.Destroyed)
            {
                p.equipment.AddEquipment(rifle);
            }
            foreach (int w in Finish(p)) yield return w;
        }

        // ---------------------------------------------------------------- m5-undrafted

        // Undrafted, the pirate under the crosshair: the hint names the draft key, a click attacks nothing.
        private static IEnumerator<int> Undrafted()
        {
            Pawn p = Shooter(true);
            if (!PirateReady("m5-undrafted"))
            {
                yield return K.Done();
                yield break;
            }
            foreach (int w in EnterPlay(p)) yield return w;
            foreach (int w in SetDrafted(p, false)) yield return w;
            foreach (int w in AimAtPawn(S.Pirate)) yield return w;
            yield return 40;
            string key = FpHud.KeyLabel(KeyBindingDefOf.Command_ColonistDraft);
            string hint = FpActions.Hint;
            string own = FpCombat.Hint(p, FpPick.Current);
            K.Check("m5-undrafted hint names the draft key", hint != null && hint.Contains(key) && own != null && own.Contains(key),
                "hint \"" + hint + "\" (" + FpActions.HintKind + "), combat hint \"" + own + "\", key " + key + ", crosshair on " + (FpPick.Current.Thing?.ThingID ?? "nothing"));
            // The left button doesn't draft: the HUD shows the hint without "[LMB]" and a white crosshair (FpActions.HintNoClick).
            K.Check("m5-undrafted hint isn't a click", FpActions.HintKind == FpHintKind.Action && FpActions.HintNoClick,
                "kind " + FpActions.HintKind + ", no click " + FpActions.HintNoClick);
            K.Shot("m5_undrafted_hint");
            int sent0 = FpHits.Count("Fire sent");
            foreach (int w in PressRelease()) yield return w;
            yield return K.WaitTicks(60);
            K.Check("m5-undrafted no attack", !Attacking(p) && FpHits.Count("Fire sent") == sent0, JobOf(p));

            // Drafted, on another colonist: the combat hint refuses, in red (spec 14: Invalid).
            Pawn friend = p == S.C1 ? S.C2 : S.C1;
            if (friend != null && friend.Spawned && !friend.Downed)
            {
                foreach (int w in SetDrafted(p, true)) yield return w;
                Vector3 fp = friend.DrawPos;
                foreach (int w in AimAt(new Vector3(fp.x, 0.7f, fp.z))) yield return w;
                for (int i = 0; i < 120 && FpActions.HintKind != FpHintKind.Invalid; i++)
                {
                    yield return 1;
                }
                if (FpPick.Current.Thing == friend)
                {
                    K.Check("m5-undrafted drafted on a colonist: red refusal", FpActions.HintKind == FpHintKind.Invalid && FpActions.Hint != null,
                        "hint \"" + FpActions.Hint + "\" (" + FpActions.HintKind + ")");
                }
                else
                {
                    K.Info("m5-undrafted: the crosshair isn't on " + friend.LabelShort + " (" + (FpPick.Current.Thing?.ThingID ?? "nothing") + "); red refusal not checked");
                }
            }
            foreach (int w in Finish(p)) yield return w;
        }

        // ---------------------------------------------------------------- m5-wide

        // Review finding (M5): the aim's wider radius (1.3 times the picker's) also beside a standing target's chest, where
        // the crosshair's ray rises over his shoulder and lands on nothing (an invalid hit): the hint names the attack and a
        // click fires at him. A second pirate stands at (+8, -1), stunned (he neither fights nor walks) and shielded from
        // damage; the crosshair goes half a cell to his side at chest height (outside the picker's 0.45, inside 0.585).
        private static IEnumerator<int> Wide()
        {
            Pawn p = Shooter(true);
            Pawn first = S.Pirate;
            Map map = Find.CurrentMap;
            IntVec3 c = S.Origin + new IntVec3(8, 0, -1);
            if (first == null || map == null || !c.InBounds(map))
            {
                K.Check("m5-wide setup", false, first == null ? "no pirate in the scene to copy" : "no cell for the standing pirate");
                yield return K.Done();
                yield break;
            }
            foreach (Thing th in c.GetThingList(map).ToList())
            {
                if (!(th is Pawn) && !th.Destroyed)
                {
                    th.Destroy(DestroyMode.Vanish);
                }
            }
            map.roofGrid.SetRoof(c, null);
            map.fogGrid.Unfog(c);
            Pawn t = PawnGenerator.GeneratePawn(first.kindDef, first.Faction);
            t.equipment?.DestroyAllEquipment();
            t.inventory?.DestroyAll();
            GenSpawn.Spawn(t, c, map);
            t.stances.stunner.StunFor(20000, null, addBattleLog: false, showMote: false);
            if (!InstallTestHooks("m5-wide", p, t))
            {
                yield return K.Done();
                yield break;
            }
            foreach (int w in EnterPlay(p)) yield return w;
            foreach (int w in SetDrafted(p, true)) yield return w;
            K.Check("m5-wide target standing", t.Spawned && !t.Downed && t.HostileTo(p), t.ThingID + " downed " + t.Downed + ", hostile " + t.HostileTo(p));
            Vector3 dp = t.DrawPos;
            Vector3 dir = new Vector3(dp.x - p.DrawPos.x, 0f, dp.z - p.DrawPos.z).normalized;
            Vector3 side = new Vector3(dir.z, 0f, -dir.x);
            foreach (int w in AimAt(new Vector3(dp.x, 0.9f, dp.z) + side * 0.5f)) yield return w;
            yield return 30;
            FpHit hit = FpPick.Current;
            K.Info("m5-wide crosshair: " + (hit.Valid ? hit.Thing?.ThingID ?? "cell " + hit.Cell : "nothing (an invalid hit)") + "; hint \""
                + FpActions.Hint + "\" (" + FpActions.HintKind + ")");
            K.Check("m5-wide not under the narrow pick", hit.Thing != t, "the crosshair's pick took " + t.ThingID + " directly; the aim point is too close");
            // The invalid hit itself (whatever the ray found beyond him): the wider radius still names him.
            string direct = FpCombat.Hint(p, default(FpHit));
            K.Check("m5-wide hint on an invalid hit", direct != null && !FpCombat.LastHintBlocked, "\"" + direct + "\", blocked " + FpCombat.LastHintBlocked);
            for (int i = 0; i < 120 && FpActions.HintKind != FpHintKind.Action; i++)
            {
                yield return 1;
            }
            K.Check("m5-wide crosshair hint", FpActions.HintKind == FpHintKind.Action && FpActions.Hint != null, "\"" + FpActions.Hint + "\" (" + FpActions.HintKind + ")");
            yield return K.Button(0, true);
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => AttackingOn(p, t) || K.Ticks > tick0 + 60, "for the attack job");
            K.Check("m5-wide click fires at him", AttackingOn(p, t), JobOf(p));
            yield return K.Button(0, false);
            yield return K.WaitTicks(30);
            foreach (int w in Finish(p)) yield return w;
        }

        // ---------------------------------------------------------------- m5-wasd

        // Holding fire, a movement key: the attack ends (the game's own order would be cancelled by a move order the same
        // way) and control comes back; still holding, no new attack starts on the same target.
        private static IEnumerator<int> Wasd()
        {
            Pawn p = Shooter(true);
            if (!PirateReady("m5-wasd") || !InstallTestHooks("m5-wasd", p, S.Pirate))
            {
                yield return K.Done();
                yield break;
            }
            foreach (int w in EnterPlay(p)) yield return w;
            foreach (int w in SetDrafted(p, true)) yield return w;
            foreach (int w in AimAtPawn(S.Pirate)) yield return w;
            yield return 30;
            yield return K.Button(0, true);
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => AttackingOn(p, S.Pirate) || K.Ticks > tick0 + 60, "for the attack job");
            Job job = p.CurJob;
            int jid = JobId(p);
            K.Check("m5-wasd attack job", job != null && job.def == JobDefOf.AttackStatic, JobOf(p));
            // A colonist on another job takes a movement key only after FpSettings.moveInterruptMs (300 ms) held.
            yield return K.HoldTicks(60, Key.W);
            K.Check("m5-wasd attack ended by moving", !IsJob(p, jid), JobOf(p));
            yield return K.WaitUntil(() => InControlJob(p), "for the control job", 600);
            K.Check("m5-wasd control", InControlJob(p), JobOf(p));
            yield return K.WaitTicks(60);
            K.Check("m5-wasd no new attack while still held", InControlJob(p), JobOf(p));
            yield return K.Button(0, false);
            yield return K.WaitTicks(30);
            foreach (int w in Finish(p)) yield return w;
        }

        // ---------------------------------------------------------------- m5-odds

        private const int OddsBlocks = 8;          // alternating: first person, the game's order, first person...
        private const int OddsAttacksPerBlock = 12;
        private const int OddsMinShots = 100;      // per arm (4 blocks x 12 attacks x a burst of 3 = 144 expected)

        // The user's requirement: shooting in first person has no better hit chance than the game's own order. Checked three
        // ways: nothing from first-person play patches the shot's path or the combat stats; the job, verb, warmup and
        // cooldown are the game's; the measured hit rates of first-person clicks and the game's "Fire at" order from the same
        // cell at the same target agree.
        private static IEnumerator<int> Odds()
        {
            AuditPatches();
            AuditStats();
            Pawn p = Shooter(true);
            if (!PirateReady("m5-odds") || !InstallTestHooks("m5-odds", p, S.Pirate))
            {
                yield return K.Done();
                yield break;
            }
            Pawn target = S.Pirate;
            IntVec3 cell = p.Position;
            string fpJob = null;         // the first job of each arm, described while it ran (jobs are pooled)
            JobDriver fpDriver = null;
            string gameJob = null;
            int[] attacks = new int[2];
            for (int b = 0; b < OddsBlocks; b++)
            {
                // The same skill level all along: shooting a downed pawn teaches nothing (Verb_Shoot.WarmupComplete), but a
                // level of 10 or more decays every 200 ticks, which could cross a level during the run and change the aim
                // and the warmup for one arm only.
                SkillRecord shooting = p.skills?.GetSkill(SkillDefOf.Shooting);
                if (shooting != null && !shooting.TotallyDisabled)
                {
                    shooting.xpSinceLastLevel = shooting.XpRequiredForLevelUp / 2f;
                }
                if (b % 2 == 0)
                {
                    // First person: one click per attack, each its own job, ended by the release's cap.
                    foreach (int w in EnterPlay(p)) yield return w;
                    foreach (int w in SetDrafted(p, true)) yield return w;
                    p.drafter.FireAtWill = false;   // drafting sets it; the drafted wait between the game's orders mustn't shoot
                    foreach (int w in AimAtPawn(target)) yield return w;
                    yield return 30;
                    arm = 0;
                    for (int i = 0; i < OddsAttacksPerBlock; i++)
                    {
                        // As a player would: the click when the crosshair is on the pirate (a visitor or an animal that
                        // wandered into the line of sight gets the "not hostile" refusal instead).
                        for (int f = 0; f < 600 && FpPick.Current.Thing != target; f++)
                        {
                            yield return 1;
                        }
                        foreach (int w in PressRelease()) yield return w;
                        int tick0 = K.Ticks;
                        yield return K.WaitUntil(() => AttackingOn(p, target) || K.Ticks > tick0 + 120, "for the first-person attack");
                        int j = JobId(p);
                        if (!AttackingOn(p, target))
                        {
                            K.Check("m5-odds first-person attack " + b + "." + i, false, JobOf(p) + "; hint " + FpActions.Hint + "; " + ClickFacts());
                            break;
                        }
                        if (fpJob == null)
                        {
                            fpJob = Facts(p.CurJob, false);
                            fpDriver = p.jobs.curDriver;
                            K.Info("m5-odds first-person job: " + Facts(p.CurJob, true));
                        }
                        yield return K.WaitUntil(() => !IsJob(p, j), "for the first-person attack to end", 3600);
                        attacks[0]++;
                        yield return K.WaitUntil(() => InControlJob(p), "for the control job", 600);
                    }
                    yield return K.WaitTicks(30);   // the last bullets land
                    arm = -1;
                    foreach (int w in LeavePlay()) yield return w;
                }
                else
                {
                    // The game's own drafted "Fire at" order (the float menu's action), not under first-person control.
                    yield return K.WaitUntil(() => !InControlJob(p) && FpRegistry.EntryOf(p) == null, "for the release", 600);
                    K.Check("m5-odds same cell", p.Position == cell && p.Drafted, "at " + p.Position + " (first person at " + cell + "), drafted " + p.Drafted);
                    p.drafter.FireAtWill = false;
                    Action order = FloatMenuUtility.GetRangedAttackAction(p, target, out string fail);
                    if (!K.Check("m5-odds game order " + b, order != null, fail))
                    {
                        break;
                    }
                    arm = 1;
                    order();
                    int j = JobId(p);
                    if (gameJob == null)
                    {
                        gameJob = Facts(p.CurJob, false);
                        K.Info("m5-odds the game's job: " + Facts(p.CurJob, true));
                    }
                    JobDriver d = p.jobs.curDriver;
                    // The order keeps attacking: ended once the next attack after the block's last has started (its warmup is
                    // cut, so it fires nothing).
                    yield return K.WaitUntil(() => !IsJob(p, j) || FpCombat.AttacksMade(d) > OddsAttacksPerBlock, "for the game's order's attacks", 36000);
                    attacks[1] += Mathf.Min(OddsAttacksPerBlock, FpCombat.AttacksMade(d));
                    if (IsJob(p, j))
                    {
                        p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                    yield return K.WaitTicks(30);
                    arm = -1;
                }
                PirateReady("m5-odds block " + b);
            }
            arm = -1;

            // The jobs.
            K.Check("m5-odds same job", fpJob != null && fpJob == gameJob && fpJob.StartsWith("AttackStatic ", StringComparison.Ordinal), "first person {" + fpJob + "}; game {" + gameJob + "}");
            // First person's one patch on every job driver (FpWork's hold, a prefix on JobDriver.DriverTickInterval) never
            // touches an attack: it acts only on drivers with a skill-check adapter, and attacks have none.
            K.Check("m5-odds no skill-check hold on attacks", fpDriver != null && FpWork.AdapterFor(fpDriver) == null,
                (fpDriver?.GetType().Name ?? "no first-person attack") + " adapter " + (fpDriver != null ? FpWork.AdapterFor(fpDriver)?.Name ?? "none" : "-"));
            // The verb, warmup and cooldown the game started for each.
            Verb primary = p.equipment?.PrimaryEq?.PrimaryVerb;
            List<Verb> vf = verbs[0].Distinct().ToList();
            List<Verb> vg = verbs[1].Distinct().ToList();
            K.Check("m5-odds same verb", vf.Count == 1 && vg.Count == 1 && vf[0] == vg[0] && vf[0] == primary,
                "first person " + vf.Count + " verb(s), game " + vg.Count + ", primary " + primary);
            string wf = Distinct(warmups[0]);
            string wg = Distinct(warmups[1]);
            K.Check("m5-odds same warmup", warmups[0].Count > 0 && wf == wg, "warmup ticks: first person {" + wf + "}, game {" + wg + "}");
            string cf = Distinct(cooldowns[0]);
            string cg = Distinct(cooldowns[1]);
            K.Check("m5-odds same cooldown", cooldowns[0].Count > 0 && cf == cg, "cooldown ticks: first person {" + cf + "}, game {" + cg + "}");
            // The game's estimate for each verb, in the same tick.
            if (vf.Count > 0 && vg.Count > 0)
            {
                float ef = ShotReport.HitReportFor(p, vf[0], target).TotalEstimatedHitChance;
                float eg = ShotReport.HitReportFor(p, vg[0], target).TotalEstimatedHitChance;
                K.Check("m5-odds same estimate", ef == eg, "TotalEstimatedHitChance first person " + ef.ToString("F6", CultureInfo.InvariantCulture)
                    + ", game " + eg.ToString("F6", CultureInfo.InvariantCulture));
            }
            // The measured hit rates.
            int sf = shots[0], hf = hits[0], sg = shots[1], hg = hits[1];
            double rf = sf > 0 ? (double)hf / sf : 0.0;
            double rg = sg > 0 ? (double)hg / sg : 0.0;
            double pooled = sf + sg > 0 ? (double)(hf + hg) / (sf + sg) : 0.0;
            double se = sf > 0 && sg > 0 ? Math.Sqrt(pooled * (1.0 - pooled) * (1.0 / sf + 1.0 / sg)) : 0.0;
            double z = se > 0.0 ? (rf - rg) / se : 0.0;
            string rates = "first person " + hf + "/" + sf + " = " + rf.ToString("P1", CultureInfo.InvariantCulture) + " (" + attacks[0] + " attacks), game "
                + hg + "/" + sg + " = " + rg.ToString("P1", CultureInfo.InvariantCulture) + " (" + attacks[1] + " attacks), z " + z.ToString("F2", CultureInfo.InvariantCulture);
            K.Info("m5-odds rates: " + rates + "; the game's estimate now "
                + (primary != null ? ShotReport.HitReportFor(p, primary, target).TotalEstimatedHitChance.ToStringPercent() : "-"));
            K.Check("m5-odds enough shots", sf >= OddsMinShots && sg >= OddsMinShots, rates);
            // |z| <= 3.29: the two rates differ by no more than chance explains (two-sided, p = 0.001).
            K.Check("m5-odds hit rates agree", sf > 0 && sg > 0 && Math.Abs(z) <= 3.29, rates);
            RemoveTestHooks();
            p.drafter.FireAtWill = true;
            if (p.Drafted)
            {
                p.drafter.Drafted = false;
            }
            yield return K.Done();
        }

        // What became of the last clicks (for a click that started nothing): the input path's and the trigger's counters.
        private static string ClickFacts()
        {
            string[] names = { "LMB down", "Click swallowed after capture", "Cursor captured", "Cursor released", "Fire sent", "Fire not resent (in flight)",
                "StopFire sent", "Fire refused" };
            Thing on = FpPick.Current.Thing;
            return string.Join(", ", names.Select(n => n + " " + FpHits.Count(n))) + "; crosshair on " + (on == null ? "nothing" : on.ThingID + " (" + on.def.defName
                + ", faction " + (on.Faction?.Name ?? "none") + ")") + "; captured " + FpInput.Captured + ", stance "
                + (Shooter(false)?.stances?.curStance?.GetType().Name ?? "-") + ", last toast \"" + FpHud.LastToast + "\"";
        }

        // What decides a shot in a job (the attack's kind, its target, the verb it names, whether the player forced it),
        // as text; `all` adds the fields first person sets on purpose (the attack cap, the line-of-fire end).
        private static string Facts(Job j, bool all)
        {
            if (j == null)
            {
                return "none";
            }
            string s = j.def.defName + " target " + j.targetA + " verbToUse " + (j.verbToUse?.ToString() ?? "null") + " playerForced " + j.playerForced;
            return all ? s + " maxNumStaticAttacks " + j.maxNumStaticAttacks + " endIfCantShoot " + j.endIfCantShootTargetFromCurPos : s;
        }

        private static string Distinct(List<int> values)
        {
            return string.Join(",", values.Distinct().OrderBy(v => v).Select(v => v.ToString(CultureInfo.InvariantCulture)));
        }

        // The methods a shot goes through (aim, warmup, burst, hit roll, cover, impact, cooldown), by declaring type and name.
        private static readonly (Type type, string name)[] ShotPath =
        {
            (typeof(ShotReport), "HitReportFor"), (typeof(ShotReport), "HitFactorFromShooter"),
            (typeof(ShotReport), "get_AimOnTargetChance_StandardTarget"), (typeof(ShotReport), "get_AimOnTargetChance_IgnoringPosture"),
            (typeof(ShotReport), "get_AimOnTargetChance"), (typeof(ShotReport), "get_PassCoverChance"),
            (typeof(ShotReport), "get_TotalEstimatedHitChance"), (typeof(ShotReport), "get_FactorFromPosture"),
            (typeof(ShotReport), "get_FactorFromExecution"), (typeof(ShotReport), "GetRandomCoverToMissInto"),
            (typeof(Verb), "get_WarmupTime"), (typeof(Verb), "TryStartCastOn"), (typeof(Verb), "WarmupComplete"),
            (typeof(Verb), "TryCastNextBurstShot"), (typeof(Verb), "get_BurstShotCount"), (typeof(Verb), "CanHitTarget"),
            (typeof(Verb), "CanHitTargetFrom"), (typeof(Verb), "TryFindShootLineFromTo"),
            (typeof(Verb_LaunchProjectile), "TryCastShot"), (typeof(Verb_Shoot), "TryCastShot"), (typeof(Verb_Shoot), "WarmupComplete"),
            (typeof(VerbProperties), "GetHitChanceFactor"), (typeof(VerbProperties), "AdjustedCooldown"), (typeof(VerbProperties), "AdjustedCooldownTicks"),
            (typeof(VerbProperties), "GetForceMissFactorFor"),
            (typeof(Projectile), "Launch"), (typeof(Projectile), "ImpactSomething"), (typeof(Projectile), "CanHit"), (typeof(Bullet), "Impact"),
            (typeof(CoverUtility), "CalculateOverallBlockChance"), (typeof(CoverUtility), "CalculateCoverGiverSet"),
            (typeof(Pawn), "TryStartAttack"), (typeof(Pawn), "TryGetAttackVerb"),
            (typeof(Pawn_StanceTracker), "SetStance"), (typeof(Pawn_StanceTracker), "get_FullBodyBusy"),
            (typeof(Stance_Busy), "StanceTick"), (typeof(Stance_Warmup), "StanceTick"), (typeof(Stance_Warmup), "Expire"),
            (typeof(Stance_Warmup), "InitEffects"),
            (typeof(Pawn_PathFollower), "PatherTick"),
            (typeof(StatWorker), "GetValue"), (typeof(StatWorker), "GetValueUnfinalized"), (typeof(StatWorker), "FinalizeValue"),
            (typeof(StatExtension), "GetStatValue"),
        };

        // Spec 16, M5: Harmony's patch records. The reads the spec names (the accuracy stat's worker, Verb.WarmupTime, where
        // AimingDelayFactor is read) carry no IsoRim patch at all; nothing on the rest of the shot's path, nor any method of
        // the game's attack drivers (their toil lambdas included), carries a first-person (IsoRim.Fp) patch. Every patch
        // found is listed, with its owner.
        private static void AuditPatches()
        {
            Assembly iso = typeof(FpCombat).Assembly;
            List<string> missing = new List<string>();

            List<MethodBase> named = new List<MethodBase>();
            StatWorker accWorker = StatDefOf.ShootingAccuracyPawn?.Worker;
            if (accWorker != null)
            {
                for (Type t = accWorker.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    named.AddRange(AccessTools.GetDeclaredMethods(t));
                }
            }
            else
            {
                missing.Add("ShootingAccuracyPawn's worker");
            }
            AddNamed(named, missing, typeof(Verb), "get_WarmupTime");
            AddNamed(named, missing, typeof(Verb), "TryStartCastOn");          // AimingDelayFactor: the warmup's length
            AddNamed(named, missing, typeof(Stance_Warmup), "InitEffects");   // AimingDelayFactor: the aiming sound
            AddNamed(named, missing, typeof(StatExtension), "GetStatValue");
            string namedBad = Patched(named, (m, patch) => IsIsoRim(patch, iso), out string namedAll);
            K.Check("m5-odds named reads: no IsoRim patch", namedBad.Length == 0 && missing.Count == 0,
                (namedBad.Length == 0 ? "none of " + named.Count + " methods" : namedBad) + (missing.Count > 0 ? "; not found: " + string.Join(", ", missing) : ""));
            if (namedAll.Length > 0)
            {
                K.Info("m5-odds patches on the named reads (others): " + namedAll);
            }

            List<MethodBase> path = new List<MethodBase>();
            foreach ((Type type, string name) in ShotPath)
            {
                AddNamed(path, missing, type, name);
            }
            foreach (Type driver in new[] { typeof(JobDriver_AttackStatic), typeof(JobDriver_AttackMelee) })
            {
                path.AddRange(AccessTools.GetDeclaredMethods(driver));
                foreach (Type nested in driver.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    path.AddRange(AccessTools.GetDeclaredMethods(nested));
                }
            }
            string pathBad = Patched(path, (m, patch) => IsFirstPerson(patch), out string pathAll);
            K.Check("m5-odds shot path: no first-person patch", pathBad.Length == 0 && missing.Count == 0,
                (pathBad.Length == 0 ? "none of " + path.Count + " methods" : pathBad) + (missing.Count > 0 ? "; not found: " + string.Join(", ", missing) : ""));
            K.Info("m5-odds every patch on the shot path: " + (pathAll.Length > 0 ? pathAll : "none"));
        }

        private static void AddNamed(List<MethodBase> list, List<string> missing, Type type, string name)
        {
            int n = 0;
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
            {
                if (m.Name == name)
                {
                    list.Add(m);
                    n++;
                }
            }
            if (n == 0)
            {
                missing.Add(type.Name + "." + name);
            }
        }

        // The patches on these methods: those `bad` says are bad, and every one (owner and patch method), as text.
        private static string Patched(List<MethodBase> methods, Func<MethodBase, Patch, bool> bad, out string all)
        {
            List<string> badList = new List<string>();
            List<string> allList = new List<string>();
            foreach (MethodBase m in methods.Distinct())
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
                if (info == null)
                {
                    continue;
                }
                IEnumerable<Patch>[] kinds = { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers, info.InnerPrefixes, info.InnerPostfixes, info.InnerFinalizers };
                foreach (IEnumerable<Patch> kind in kinds)
                {
                    if (kind == null)
                    {
                        continue;
                    }
                    foreach (Patch patch in kind)
                    {
                        string text = m.DeclaringType?.Name + "." + m.Name + " <- " + patch.owner + " " + patch.PatchMethod?.DeclaringType?.FullName + "." + patch.PatchMethod?.Name;
                        allList.Add(text);
                        if (bad(m, patch))
                        {
                            badList.Add(text);
                        }
                    }
                }
            }
            all = string.Join("; ", allList);
            return string.Join("; ", badList);
        }

        private static bool IsIsoRim(Patch patch, Assembly iso)
        {
            string owner = patch.owner ?? "";
            return owner.StartsWith("isorim", StringComparison.OrdinalIgnoreCase) || owner.StartsWith("peren.isorim", StringComparison.OrdinalIgnoreCase)
                || patch.PatchMethod?.DeclaringType?.Assembly == iso;
        }

        private static bool IsFirstPerson(Patch patch)
        {
            string ns = patch.PatchMethod?.DeclaringType?.Namespace ?? "";
            return ns == "IsoRim.Fp" || ns.StartsWith("IsoRim.Fp.", StringComparison.Ordinal);
        }

        // The combat stats carry no part from IsoRim (StatPart_FpForm is only for the work stats, spec 10.5) and no IsoRim
        // worker.
        private static void AuditStats()
        {
            Assembly iso = typeof(FpCombat).Assembly;
            string[] names =
            {
                "ShootingAccuracyPawn", "AimingDelayFactor", "ShootingAccuracyFactor_Touch", "ShootingAccuracyFactor_Short",
                "ShootingAccuracyFactor_Medium", "ShootingAccuracyFactor_Long", "AccuracyTouch", "AccuracyShort", "AccuracyMedium",
                "AccuracyLong", "RangedWeapon_Cooldown", "MoveSpeed", "MeleeHitChance", "MeleeDodgeChance", "MeleeDamageFactor",
                "ShootingAccuracyOutdoorsDarkOffset", "ShootingAccuracyOutdoorsLitOffset", "ShootingAccuracyIndoorsDarkOffset",
                "ShootingAccuracyIndoorsLitOffset",
            };
            List<string> bad = new List<string>();
            List<string> missing = new List<string>();
            int seen = 0;
            foreach (string name in names)
            {
                StatDef stat = DefDatabase<StatDef>.GetNamedSilentFail(name);
                if (stat == null)
                {
                    missing.Add(name);
                    continue;
                }
                seen++;
                if (stat.parts != null)
                {
                    foreach (StatPart part in stat.parts)
                    {
                        if (part != null && part.GetType().Assembly == iso)
                        {
                            bad.Add(name + " part " + part.GetType().Name);
                        }
                    }
                }
                if (stat.Worker != null && stat.Worker.GetType().Assembly == iso)
                {
                    bad.Add(name + " worker " + stat.Worker.GetType().Name);
                }
            }
            K.Check("m5-odds combat stats: no IsoRim part or worker", bad.Count == 0 && seen >= 12,
                (bad.Count == 0 ? seen + " stats clean" : string.Join("; ", bad)) + (missing.Count > 0 ? "; not in this game: " + string.Join(", ", missing) : ""));
        }

        // ---------------------------------------------------------------- mp5-fire

        // Five downed pirates in a row east of the scene, the scene's first at (+12, 0) among them.
        private static readonly IntVec3[] PirateRow = { new IntVec3(12, 0, 0), new IntVec3(12, 0, 2), new IntVec3(12, 0, -2), new IntVec3(13, 0, 1), new IntVec3(13, 0, -1) };
        private const int MpBursts = 20;

        // Host, with its scene, before hosting: the extra pirates, and a rifle for the client's shooter.
        private static string MpKit(Map map)
        {
            Pawn shooter = MpShooter();
            if (shooter.equipment == null)
            {
                return "the shooter has no equipment tracker";
            }
            if (shooter.equipment.Primary == null || shooter.equipment.Primary.def.defName != "Gun_AssaultRifle")
            {
                shooter.equipment.DestroyAllEquipment();
                shooter.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDef.Named("Gun_AssaultRifle")));
            }
            Pawn first = S.Pirate;
            if (first == null)
            {
                return "no pirate in the scene";
            }
            string lane = ClearLane(map);
            if (lane != null)
            {
                return lane;
            }
            for (int i = 1; i < PirateRow.Length; i++)
            {
                IntVec3 c = S.Origin + PirateRow[i];
                if (!c.InBounds(map))
                {
                    return "pirate cell " + c + " out of bounds";
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (!(t is Pawn) && !t.Destroyed)
                    {
                        t.Destroy(DestroyMode.Vanish);
                    }
                }
                map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
                Pawn p = PawnGenerator.GeneratePawn(first.kindDef, first.Faction);
                p.equipment?.DestroyAllEquipment();
                p.inventory?.DestroyAll();
                GenSpawn.Spawn(p, c, map);
                HealthUtility.TryAnesthetize(p);
            }
            return null;
        }

        // The client's shooter: C2 (the host plays nobody here), or C1 when C2 can't do violence. The same on both machines.
        private static Pawn MpShooter()
        {
            return !S.C2.WorkTagIsDisabled(WorkTags.Violent) ? S.C2 : S.C1;
        }

        // The row's living pirates, by thingIDNumber.
        private static List<Pawn> Pirates(Map map)
        {
            List<Pawn> list = new List<Pawn>();
            foreach (IntVec3 off in PirateRow)
            {
                IntVec3 c = S.Origin + off;
                if (!c.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map))
                {
                    if (t is Pawn p && !p.Dead && p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer))
                    {
                        list.Add(p);
                    }
                }
            }
            list.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            return list;
        }

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

        // Hosting reloads the game: the scene's things are new objects, found again by their cells.
        private static IEnumerable<int> AfterReload()
        {
            yield return K.WaitUntil(() => Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null
                && !LongEventHandler.AnyEventNowOrWaiting, "for the reload to finish", 7200);
            K.Check("scene found again", S.Adopt(Find.CurrentMap, out string error), error ?? S.Describe());
        }

        // The client fires 20 bursts at the row (one click each, the next pirate when one dies) while the host watches. Every
        // FpFire and FpStopFire line (FPTEST CMD) carries the target's health digest, and fp_compare.py compares both
        // players' command lines and state lines.
        private static IEnumerator<int> MpFire()
        {
            if (K.IsHost)
            {
                yield return K.WaitUntil(() => MpBridge.InSession, "for hosting", 3600);
                foreach (int w in AfterReload()) yield return w;
                K.Info("mp5-fire pirates: " + string.Join(", ", Pirates(Find.CurrentMap).Select(x => FpCombat.Digest(x))));
                yield return K.WaitUntil(() => Players >= 2, "for the client to join", 18000);
                yield return K.WaitUntil(() => Players < 2, "for the client to finish", 54000);
                K.Info("mp5-fire pirates after: " + string.Join(", ", Pirates(Find.CurrentMap).Select(x => FpCombat.Digest(x))));
                yield return 120;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                Pawn p = MpShooter();
                List<Pawn> row = Pirates(Find.CurrentMap);
                K.Check("mp5-fire kit", row.Count == PirateRow.Length && p.equipment?.Primary?.def.defName == "Gun_AssaultRifle",
                    row.Count + " pirates, shooter " + p.LabelShort + " with " + (p.equipment?.Primary?.def.defName ?? "nothing"));
                foreach (int w in EnterPlay(p)) yield return w;
                yield return K.WaitUntil(() => MP.InInterface, "for Multiplayer to take commands", 1800);
                foreach (int w in SetDrafted(p, true)) yield return w;
                int bursts = 0;
                int tries = 0;
                while (bursts < MpBursts && tries < MpBursts * 2)
                {
                    tries++;
                    Pawn target = Pirates(Find.CurrentMap).FirstOrDefault(x => x.Spawned);
                    if (target == null)
                    {
                        K.Info("mp5-fire every pirate is dead after " + bursts + " bursts");
                        break;
                    }
                    foreach (int w in AimAtPawn(target)) yield return w;
                    yield return 10;
                    foreach (int w in PressRelease()) yield return w;
                    int tick0 = K.Ticks;
                    yield return K.WaitUntil(() => AttackingOn(p, target) || K.Ticks > tick0 + 300, "for the attack (a round trip)", 1800);
                    if (!AttackingOn(p, target))
                    {
                        K.Info("mp5-fire burst " + tries + ": no attack on " + target.ThingID + " (" + JobOf(p) + ", hint " + FpActions.Hint + ")");
                        yield return K.WaitTicks(60);
                        continue;
                    }
                    int j = JobId(p);
                    yield return K.WaitUntil(() => !IsJob(p, j), "for the burst to end", 3600);
                    bursts++;
                    yield return K.WaitUntil(() => InControlJob(p), "for the control job", 900);
                }
                K.Check("mp5-fire bursts", bursts >= MpBursts, bursts + " bursts in " + tries + " tries");
                yield return K.WaitTicks(120);
                foreach (int w in SetDrafted(p, false)) yield return w;
                foreach (int w in LeavePlay()) yield return w;
                yield return K.WaitTicks(120);
                yield return K.Done();
            }
            else
            {
                K.Check("mp5-fire role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }
    }
}
