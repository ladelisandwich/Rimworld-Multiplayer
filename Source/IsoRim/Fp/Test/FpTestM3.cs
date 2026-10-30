using System;
using System.Collections.Generic;
using System.Linq;
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
    // Milestone 3 scenarios (fp-play-spec 16, M3): skill checks on bills. The simulation side (FpWork: plan, raise, hold,
    // deadline, results; FpLevers: quality and stat levers) and the ring (FpSkillCheck) through the left-hand path a player
    // takes: a bill started with FpBills (M2) on the scene's stove or on a hand tailor bench this file adds.
    //
    // Deviations from the spec's table, all to make the runs deterministic:
    // - The long recipe is tribalwear (Make_Apparel_TribalA, 1800 work) on a hand tailor bench, not the duster: the duster
    //   needs the Complex clothing research, which a -quicktest colony may not have, and the scene has no tailoring bench.
    //   At the hand bench's half speed (less outdoors) it gets 8 or 9 checks at Normal.
    // - m3-hold, m3-deadline and m3-stale take control through Direct (K.Direct.SetControl, no play mode), so no ring is
    //   shown and nothing answers the check unless the script does (the ring would answer a timeout by itself after one
    //   turn, and freezing it by dropping focus can't be relied on in a windowed test copy). They answer with
    //   K.Direct.Result, the ring's own sender.
    // - The doer is C1 when C1 can do the work and C2 otherwise (-quicktest colonists are random; one may never craft or
    //   cook).
    // - mp3-craft's bench and cloth are spawned by the host with its scene, before hosting (FpTestKit.AddSceneExtra: spawned
    //   in a session they would get negative ids, as anything made outside a command does); the join point made for the
    //   client's join (autoJoinPoint Join) carries them, so both games start equal.
    // - m3-outcomes raises the doer's Crafting to 10 (Widen), so the Hit and Great parts are wide enough for a scripted
    //   tap at any test frame rate.
    // Added by the integration (the review's no-free-void fixes): m3-freeze (the game's pause freezes a running needle
    // and Space then unpauses; a free cursor holds only the warning, for a few seconds, then the needle runs out into a
    // miss) and m3-forfeit (walking away from a shown check counts it as missed, work and tally; drafting does too, on
    // the tally).
    internal static class FpTestM3
    {
        internal static void Register(Dictionary<string, Func<IEnumerator<int>>> s)
        {
            s["m3-plan"] = Plan;
            s["m3-outcomes"] = Outcomes;
            s["m3-hold"] = Hold;
            s["m3-deadline"] = Deadline;
            s["m3-stale"] = Stale;
            s["m3-quality"] = Quality;
            s["m3-poison"] = Poison;
            s["m3-space"] = Space;
            s["m3-freeze"] = Freeze;
            s["m3-forfeit"] = Forfeit;
            s["mp3-craft"] = MpCraft;
            // The host's kit is part of its scene, made before hosting (in a session it would get negative ids).
            K.AddSceneExtra("mp3-craft", map => SpawnKit(map, out string error) ? null : error);
        }

        private const string ControlJob = "IsoRimFp_Control";

        // The tailoring kit, at offsets from the scene's origin (north = +z), in the ground the scene clears south of C1:
        // the 3x1 bench faces north, its work spot one cell south of its middle.
        private static readonly IntVec3 BenchAt = new IntVec3(-4, 0, -6);
        private static readonly IntVec3 ClothAt = new IntVec3(-2, 0, -6);
        private const int ClothCount = 75;

        private static Building Bench;
        private static Thing Cloth;

        private static RecipeDef Tribalwear => DefDatabase<RecipeDef>.GetNamedSilentFail("Make_Apparel_TribalA");

        private static RecipeDef SimpleMeal => DefDatabase<RecipeDef>.GetNamedSilentFail("CookMealSimple");

        // ---------------------------------------------------------------- helpers

        private static string JobOf(Pawn p)
        {
            return p?.CurJob == null ? "none" : p.CurJob.def.defName + ":" + p.CurJob.loadID + " " + p.CurJob.targetA;
        }

        private static bool InControlJob(Pawn p)
        {
            return p?.CurJob?.def?.defName == ControlJob;
        }

        private static FpEntry Entry(Pawn p)
        {
            return FpRegistry.EntryOf(p);
        }

        private static string State(Pawn p)
        {
            return JobOf(p) + " | " + FpState.Describe(p) + " | ring " + FpSkillCheck.State;
        }

        private static void SelectOnly(Thing t)
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(t, false, false);
        }

        // F5 on a pawn, then wait for play to be confirmed and the control job (as FpTestM1.EnterPlay).
        private static IEnumerable<int> EnterPlay(Pawn p)
        {
            SelectOnly(p);
            yield return K.Tap(Key.F5);
            yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Playing && FpPlay.Confirmed, "for play to be confirmed on " + p.LabelShort, 600);
            yield return K.WaitUntil(() => InControlJob(p), "for " + p.LabelShort + " to be in the control job", 600);
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

        // Control without play (no ring): the self-test's Direct path.
        private static IEnumerable<int> DirectControl(Pawn p)
        {
            K.Direct.SetControl(p, true, FpSettings.Flags);
            yield return K.WaitUntil(() => FpState.ControllerOf(p) == FpIds.Local && InControlJob(p), "for Direct control of " + p.LabelShort, 600);
            K.Check("checks flag on", (Entry(p).flags & FpFlags.Checks) != 0, "flags " + Entry(p).flags + " (FpSettings.skillChecks " + FpSettings.skillChecks + ")");
        }

        private static IEnumerable<int> DirectRelease(Pawn p)
        {
            K.Direct.SetControl(p, false, 0);
            yield return K.WaitUntil(() => FpRegistry.EntryOf(p) == null, "for the release of " + p.LabelShort, 600);
        }

        // The bill work giver for a bench and recipe, picked as FpBills does it (spec 11.2 step 2).
        private static WorkGiverDef GiverFor(Thing bench, RecipeDef recipe)
        {
            if (bench == null || recipe == null)
            {
                return null;
            }
            return DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(wg => wg.Worker is WorkGiver_DoBill
                && wg.fixedBillGiverDefs != null && wg.fixedBillGiverDefs.Contains(bench.def)
                && (recipe.requiredGiverWorkType == null || wg.workType == recipe.requiredGiverWorkType));
        }

        private static bool CanDo(Pawn p, Thing bench, RecipeDef recipe)
        {
            WorkGiverDef wg = GiverFor(bench, recipe);
            return p != null && wg != null && recipe.PawnSatisfiesSkillRequirements(p) && !p.WorkTypeIsDisabled(wg.workType)
                && wg.Worker.MissingRequiredCapacity(p) == null;
        }

        // The first of the pawns who can do the recipe at the bench, null if neither can. Its work type is switched on
        // (the scene clears every priority), so the game treats the piece as this worker's.
        private static Pawn Doer(Thing bench, RecipeDef recipe, params Pawn[] order)
        {
            Pawn p = order.FirstOrDefault(c => CanDo(c, bench, recipe));
            if (p != null)
            {
                S.EnableWork(p, GiverFor(bench, recipe).workType);
            }
            return p;
        }

        // Solo, or the host before anyone joins: the tailor bench and cloth.
        private static bool SpawnKit(Map map, out string error)
        {
            error = null;
            ThingDef benchDef = DefDatabase<ThingDef>.GetNamedSilentFail("HandTailoringBench");
            if (benchDef == null || Tribalwear == null || ThingDefOf.Cloth == null)
            {
                error = "HandTailoringBench, Make_Apparel_TribalA or Cloth is missing";
                return false;
            }
            Thing bench = ThingMaker.MakeThing(benchDef, ThingDefOf.Steel);
            bench.SetFactionDirect(Faction.OfPlayer);
            Bench = (Building)GenSpawn.Spawn(bench, S.Origin + BenchAt, map, Rot4.North);
            Thing cloth = ThingMaker.MakeThing(ThingDefOf.Cloth);
            cloth.stackCount = ClothCount;
            Cloth = GenSpawn.Spawn(cloth, S.Origin + ClothAt, map);
            K.Info("kit spawned: " + Bench + " at " + Bench.Position + " (work spot " + Bench.InteractionCell + "), " + Cloth + " x" + Cloth.stackCount);
            return Bench.def.AllRecipes.Contains(Tribalwear) || Fail(out error, "tribalwear isn't a recipe of " + Bench);
        }

        // The client: the kit came with the host's join point.
        private static bool FindKit(Map map, out string error)
        {
            error = null;
            Bench = null;
            Cloth = null;
            Bench = (S.Origin + BenchAt).GetThingList(map).OfType<Building>().FirstOrDefault(b => b.def.defName == "HandTailoringBench");
            Cloth = (S.Origin + ClothAt).GetThingList(map).FirstOrDefault(t => t.def == ThingDefOf.Cloth);
            if (Bench == null || Cloth == null)
            {
                error = "no tailor bench or cloth at the kit's cells (" + (S.Origin + BenchAt) + ", " + (S.Origin + ClothAt) + ")";
                return false;
            }
            return true;
        }

        private static bool Fail(out string error, string text)
        {
            error = text;
            return false;
        }

        // Solo only (game state): the doer's level in the work's skill raised to at least `level`, so the ring's arc and
        // great part are wide enough that a tap a frame off still lands in the part aimed at (a random -quicktest
        // colonist may have level 0: arc 34, great 6 degrees). Set before the bill starts, so the plan uses its speed.
        private static void Widen(Pawn p, SkillDef skill, int level)
        {
            SkillRecord r = skill == null ? null : p?.skills?.GetSkill(skill);
            if (r == null || r.TotallyDisabled || r.Level >= level)
            {
                return;
            }
            K.Info(p.LabelShort + "'s " + skill.defName + " raised from " + r.Level + " to " + level + " for exact ring outcomes");
            r.Level = level;
        }

        // Starts a one-off bill through first person's own command and waits for the bill job's work toil.
        private static IEnumerable<int> StartBill(Pawn p, Thing bench, RecipeDef recipe, Action<Job> started)
        {
            int jobs0 = p.CurJob?.loadID ?? -1;
            FpBills.SendStartRecipe(p, bench, recipe, 1);
            yield return K.WaitUntil(() => p.CurJob?.def == JobDefOf.DoBill && p.CurJob.loadID != jobs0, "for " + p.LabelShort + "'s bill job (" + recipe.defName + ")", 1200);
            Job job = p.CurJob;
            K.Check("bill job", job?.def == JobDefOf.DoBill && job.bill?.recipe == recipe, State(p));
            started(job);
            // Fetching, then the work toil: tracking starts there (spec 10.2).
            yield return K.WaitUntil(() => Entry(p)?.checkJob == job.loadID || p.CurJob != job, "for the work toil to be tracked", 7200);
            FpEntry e = Entry(p);
            if (e == null || e.checkJob != job.loadID || e.planned == 0)
            {
                // Nothing to test without a check; Done stops the scenario here (the harness steps no script after it).
                K.Check("checks planned", false, "no checks planned on " + recipe.defName + ": " + State(p));
                yield return K.Done();
            }
        }

        private static IEnumerable<int> WaitPending(Pawn p, string what, int timeoutFrames = 7200)
        {
            yield return K.WaitUntil(() => Entry(p) != null && Entry(p).pendingSeq != 0, "for a check (" + what + ")", timeoutFrames);
        }

        private static int Resolved => FpHits.Count("Check accepted") + FpHits.Count("Check stale");

        // Answers the ring now running with the wanted outcome by tapping the check key so the press lands on a target
        // angle: Great the great part's middle, Hit the middle of the arc's part before it, Miss 20 degrees before the arc,
        // MissTimeout no press. The press is judged at the input time of the frame the key arrives in (the next one), less
        // the latency setting, so the tap goes when the needle is one frame's travel less the latency short of the target.
        // FpMath.Judge widens the great part by half a frame's travel (hf) each side, so the Hit part judged before it is
        // [arc start - hf, great start - hf): its middle is the Hit target. A tap lands within half a step of its target;
        // when that is wider than the Hit part's half, the outcome can come out Great or Miss, and an INFO line says so.
        private static IEnumerable<int> AnswerRing(byte want, string label)
        {
            yield return K.WaitUntil(() => FpSkillCheck.State == FpRingState.Needle, "for the ring's needle (" + label + ")", 3600);
            float arc0 = FpSkillCheck.ArcStartDeg, arc = FpSkillCheck.ArcWidthDeg, great = FpSkillCheck.GreatWidthDeg;
            if (want == FpOutcome.MissTimeout)
            {
                K.Info(label + ": no press (arc " + arc0.ToString("0.0") + "+" + arc.ToString("0.0") + ", great " + great.ToString("0.0") + ")");
                yield return K.WaitUntil(() => FpSkillCheck.State != FpRingState.Needle, "for the needle to run out", 600);
                yield break;
            }
            float rate = 360f / FpTuning.Get.ringPeriod;
            float hf = Mathf.Min(6f, 0.5f * Time.unscaledDeltaTime * rate);
            float target = want == FpOutcome.Great ? arc0 + arc / 2f
                : want == FpOutcome.Hit ? arc0 + (arc - great) / 4f - hf
                : arc0 - 20f;
            if (want == FpOutcome.Hit && (arc - great) / 4f < Time.unscaledDeltaTime * rate / 2f + hf)
            {
                K.Info(label + ": the Hit part (" + ((arc - great) / 2f).ToString("0.0") + " degrees) is narrow for this frame rate ("
                    + (Time.unscaledDeltaTime * rate).ToString("0.0") + " degrees a frame): the outcome may differ");
            }
            float last = FpSkillCheck.NeedleDeg;
            while (FpSkillCheck.State == FpRingState.Needle)
            {
                float now = FpSkillCheck.NeedleDeg;
                float step = Mathf.Max(0.5f, now - last);
                last = now;
                float lead = Time.unscaledDeltaTime * rate - FpSettings.checkLatencyMs / 1000f * rate;
                if (now + lead + step / 2f >= target)
                {
                    K.Info(label + ": tap at needle " + now.ToString("0.0") + " for target " + target.ToString("0.0") + " (lead " + lead.ToString("0.0")
                        + ", step " + step.ToString("0.0") + "; arc " + arc0.ToString("0.0") + "+" + arc.ToString("0.0") + ", great " + great.ToString("0.0") + ")");
                    yield return K.Tap(Key.Space);
                    break;
                }
                yield return 1;
            }
            yield return K.WaitUntil(() => FpSkillCheck.State != FpRingState.Needle, "for the ring's result", 600);
            K.Info(label + ": judged press " + FpSkillCheck.LastPressDeg.ToString("0.0") + " -> outcome " + FpSkillCheck.LastOutcome);
        }

        // ---------------------------------------------------------------- m3-plan

        // Tribalwear at Normal: the plan matches PlanChecks on its own inputs, the first check is raised at its due point
        // (within a quarter span of its span's middle), and the ring warns, then runs.
        private static IEnumerator<int> Plan()
        {
            if (!SpawnKit(Find.CurrentMap, out string error))
            {
                K.Check("m3-plan kit", false, error);
                yield return K.Done();
                yield break;
            }
            Pawn p = Doer(Bench, Tribalwear, S.C1, S.C2);
            if (p == null)
            {
                K.Check("m3-plan doer", false, "neither C1 nor C2 can make tribalwear");
                yield return K.Done();
                yield break;
            }
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in EnterPlay(p)) yield return w;
            Job job = null;
            foreach (int w in StartBill(p, Bench, Tribalwear, j => job = j)) yield return w;
            FpEntry e = Entry(p);
            FpTuningDef T = FpTuning.Get;
            K.Check("m3-plan tracking", e != null && e.checkJob == job.loadID, State(p));
            K.Check("m3-plan speed factor", FpWork.Diag.PlanJob == job.loadID && FpWork.Diag.PlanF == 1, "f " + FpWork.Diag.PlanF + " for job " + FpWork.Diag.PlanJob);
            int expect = FpMath.PlanChecks(FpWork.Diag.PlanRemTicks, FpWork.Diag.PlanF, e.p0, T.checkSpacingTicks, T.minRemainingTicks, T.endDuePermille, T.maxChecks);
            K.Check("m3-plan planned", e.planned == expect && e.planned >= 2,
                "planned " + e.planned + ", PlanChecks(rem " + FpWork.Diag.PlanRemTicks.ToString("0") + ", f " + FpWork.Diag.PlanF + ", p0 " + e.p0 + ") = " + expect);
            JobDriver d = p.jobs.curDriver;
            IFpWorkAdapter a = FpWork.AdapterFor(d);
            if (a != null)
            {
                float rem = (a.Total(d) - a.Total(d) * e.p0 / 1000f) / a.UnitsPerTick(d);
                K.Check("m3-plan remaining ticks", Mathf.Abs(rem - FpWork.Diag.PlanRemTicks) <= 0.05f * rem + 5f,
                    "plan " + FpWork.Diag.PlanRemTicks.ToString("0") + " vs " + rem.ToString("0") + " (total " + a.Total(d).ToString("0") + ", " + a.UnitsPerTick(d).ToString("0.000") + "/tick)");
            }
            foreach (int w in WaitPending(p, "the first")) yield return w;
            int span = (T.endDuePermille - e.p0) / Math.Max(1, e.planned);
            int due = FpMath.DuePermille(0, e.planned, e.p0, T.endDuePermille, Gen.HashCombineInt(job.loadID, 0));
            K.Check("m3-plan due point", FpWork.Diag.IssueDue == due && Math.Abs(due - (e.p0 + span / 2)) <= span / 4,
                "due " + due + " (raised with " + FpWork.Diag.IssueDue + "), span " + span + " from p0 " + e.p0);
            K.Check("m3-plan raised at its due point", FpWork.Diag.IssueP >= due && FpWork.Diag.IssueP <= due + 15,
                "raised at " + FpWork.Diag.IssueP + "‰, due " + due + "‰");
            bool warned = false;
            int until = Time.frameCount + 1800;
            while (FpSkillCheck.State != FpRingState.Needle && Time.frameCount < until)
            {
                if (!warned && FpSkillCheck.State == FpRingState.Warn)
                {
                    K.Shot("m3_plan_warn");
                }
                warned |= FpSkillCheck.State == FpRingState.Warn;
                yield return 1;
            }
            K.Check("m3-plan ring warns, then runs", warned && FpSkillCheck.State == FpRingState.Needle, "warn seen " + warned + ", now " + FpSkillCheck.State);
            K.Shot("m3_plan_ring");
            foreach (int w in AnswerRing(FpOutcome.Hit, "m3-plan")) yield return w;
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m3-outcomes

        // Four checks on one piece answered Great, Hit, Miss (20 degrees early) and not at all: those outcomes reach the
        // simulation, each moves the work by its share of its segment, and the piece's tally reads 1/1/2.
        private static IEnumerator<int> Outcomes()
        {
            if (!SpawnKit(Find.CurrentMap, out string error))
            {
                K.Check("m3-outcomes kit", false, error);
                yield return K.Done();
                yield break;
            }
            Pawn p = Doer(Bench, Tribalwear, S.C1, S.C2);
            if (p == null)
            {
                K.Check("m3-outcomes doer", false, "neither C1 nor C2 can make tribalwear");
                yield return K.Done();
                yield break;
            }
            Widen(p, Tribalwear.workSkill, 10);
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in EnterPlay(p)) yield return w;
            Job job = null;
            foreach (int w in StartBill(p, Bench, Tribalwear, j => job = j)) yield return w;
            FpEntry e = Entry(p);
            K.Check("m3-outcomes enough checks", e != null && e.planned >= 4, State(p));
            byte[] wants = { FpOutcome.Great, FpOutcome.Hit, FpOutcome.Miss, FpOutcome.MissTimeout };
            FpTuningDef T = FpTuning.Get;
            foreach (byte want in wants)
            {
                int resolved0 = Resolved;
                int accepted0 = FpHits.Count("Check accepted");
                foreach (int w in AnswerRing(want, "m3-outcomes " + want)) yield return w;
                K.Shot("m3_result_" + want);   // the result text, the progress bar's ghost and the tally's pips
                yield return K.WaitUntil(() => Resolved > resolved0, "for outcome " + want + " to reach the simulation", 600);
                bool accepted = FpHits.Count("Check accepted") > accepted0;
                float seg = Mathf.Max(0f, FpWork.Diag.AppliedBefore - FpWork.Diag.AppliedSegStart);
                float grant = want == FpOutcome.Great ? T.grantGreat : want == FpOutcome.Hit ? T.grantHit : T.grantMiss;
                float change = FpWork.Diag.AppliedAfter - FpWork.Diag.AppliedBefore;
                K.Check("m3-outcomes outcome " + want, accepted && FpWork.Diag.AppliedOutcome == want,
                    "accepted " + accepted + ", simulation got " + FpWork.Diag.AppliedOutcome + " (ring judged " + FpSkillCheck.LastOutcome + " at " + FpSkillCheck.LastPressDeg.ToString("0.0") + ")");
                K.Check("m3-outcomes work change " + want, Mathf.Abs(change - seg * grant) <= 1e-3f * FpWork.Diag.AppliedTotal,
                    "done " + FpWork.Diag.AppliedBefore.ToString("0.00") + " -> " + FpWork.Diag.AppliedAfter.ToString("0.00") + ": " + change.ToString("0.00")
                    + " vs segment " + seg.ToString("0.00") + " x " + FpWork.Diag.AppliedGrant.ToString("0.00") + " = " + (seg * grant).ToString("0.00"));
            }
            FpTally t = FpMapComp.For(p.Map)?.TallyByKey(e.tallyKey);
            K.Check("m3-outcomes tally 1/1/2", t != null && t.great == 1 && t.hit == 1 && t.miss == 2 && t.target is UnfinishedThing,
                t == null ? "no tally for key " + e.tallyKey : "tally " + t.great + "/" + t.hit + "/" + t.miss + " on " + t.target.ToStringSafe() + ", form " + t.Form);
            K.Info("m3-outcomes state: " + State(p));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m3-hold

        // A simple meal planned at Normal (one check), then cooked at Superfast with the check unanswered: the work stops
        // short of completion while the check waits, and the meal is made once it is answered.
        private static IEnumerator<int> Hold()
        {
            Pawn p = Doer(S.Stove, SimpleMeal, S.C1, S.C2);
            if (p == null)
            {
                K.Check("m3-hold cook", false, "neither C1 nor C2 can cook a simple meal");
                yield return K.Done();
                yield break;
            }
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in DirectControl(p)) yield return w;
            Job job = null;
            foreach (int w in StartBill(p, S.Stove, SimpleMeal, j => job = j)) yield return w;
            Bill bill = job.bill;
            FpEntry e = Entry(p);
            K.Check("m3-hold one check planned", e != null && e.checkJob == job.loadID && e.planned >= 1, State(p));
            K.SetSpeed(TimeSpeed.Superfast);
            foreach (int w in WaitPending(p, "the meal's")) yield return w;
            int seq = e.pendingSeq;
            yield return K.WaitUntil(() => e.held || e.pendingSeq != seq, "for the hold", 1800);
            JobDriver_DoBill d = p.jobs.curDriver as JobDriver_DoBill;
            K.Check("m3-hold held", e.held && e.pendingSeq == seq && d != null, State(p));
            int hits0 = FpHits.Count("Hold skipped interval");
            float w0 = d?.workLeft ?? -1f;
            yield return K.WaitTicks(30);
            float w1 = (p.jobs.curDriver as JobDriver_DoBill)?.workLeft ?? -2f;
            K.Check("m3-hold work stopped", e.held && e.pendingSeq == seq && Mathf.Abs(w1 - w0) < 1e-4f,
                "workLeft " + w0.ToString("0.000") + " -> " + w1.ToString("0.000") + " over 30 ticks; " + State(p));
            K.Check("m3-hold intervals skipped", FpHits.Count("Hold skipped interval") > hits0, FpHits.Count("Hold skipped interval") - hits0 + " in 30 ticks");
            K.Direct.Result(p, e.checkJob, e.pendingSeq, FpOutcome.Hit);
            K.Check("m3-hold released by the result", !e.held && e.pendingSeq == 0, State(p));
            yield return K.WaitUntil(() => p.CurJob != job, "for the meal to be finished", 1800);
            K.Check("m3-hold meal made", bill is Bill_Production bp && bp.repeatCount == 0, "bill " + bill.GetUniqueLoadID() + " repeat count "
                + ((bill as Bill_Production)?.repeatCount.ToString() ?? "?") + "; " + State(p));
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in DirectRelease(p)) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m3-deadline

        // A check nobody answers is voided at its deadline: no tally, one check used up, the hold let go.
        private static IEnumerator<int> Deadline()
        {
            Pawn p = Doer(S.Stove, SimpleMeal, S.C1, S.C2);
            if (p == null)
            {
                K.Check("m3-deadline cook", false, "neither C1 nor C2 can cook a simple meal");
                yield return K.Done();
                yield break;
            }
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in DirectControl(p)) yield return w;
            Job job = null;
            foreach (int w in StartBill(p, S.Stove, SimpleMeal, j => job = j)) yield return w;
            foreach (int w in WaitPending(p, "the meal's")) yield return w;
            FpEntry e = Entry(p);
            int deadline = e.deadlineTick;
            int issued0 = e.issued;
            int seq = e.pendingSeq;
            int key = e.tallyKey;
            FpTuningDef T = FpTuning.Get;
            K.Check("m3-deadline deadline", deadline - e.pendingTick == T.deadlineTicks * FpMath.SpeedFactor((int)Find.TickManager.CurTimeSpeed),
                "raised at " + e.pendingTick + ", deadline " + deadline);
            bool heldSeen = false;
            while (K.Ticks <= deadline && e.pendingSeq == seq)
            {
                heldSeen |= e.held;
                yield return 1;
            }
            yield return K.WaitTicks(2);
            FpTally t = FpMapComp.For(p.Map)?.TallyByKey(key);
            // The void uses up one check. When it lets go of work held at the hold point and more checks were planned, the
            // next tick gives up the rest as too late (spec 10.2: P >= holdPermille sets issued = planned).
            bool usedUp = e.issued == issued0 + 1 || issued0 + 1 < e.planned && e.issued == e.planned && heldSeen;
            K.Check("m3-deadline voided", e.pendingSeq == 0 && usedUp && e.lastResolveTick >= deadline,
                "at tick " + K.Ticks + " (deadline " + deadline + "), issued " + issued0 + " -> " + e.issued + " of " + e.planned + ": " + State(p));
            K.Check("m3-deadline no tally change", t == null || t.Count == 0, t == null ? "no tally" : "tally " + t.great + "/" + t.hit + "/" + t.miss);
            K.Check("m3-deadline hold released", !e.held, "held while pending: " + heldSeen + "; now " + e.held);
            K.Info("m3-deadline: the work was " + (heldSeen ? "" : "not ") + "held before the deadline (a meal reaches 97% about 150 ticks after its check)");
            yield return K.WaitUntil(() => p.CurJob != job, "for the meal to be finished", 1800);
            foreach (int w in DirectRelease(p)) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m3-stale

        // Results for the wrong check or the wrong job change nothing and count as stale; the right one is taken.
        private static IEnumerator<int> Stale()
        {
            Pawn p = Doer(S.Stove, SimpleMeal, S.C1, S.C2);
            if (p == null)
            {
                K.Check("m3-stale cook", false, "neither C1 nor C2 can cook a simple meal");
                yield return K.Done();
                yield break;
            }
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in DirectControl(p)) yield return w;
            Job job = null;
            foreach (int w in StartBill(p, S.Stove, SimpleMeal, j => job = j)) yield return w;
            foreach (int w in WaitPending(p, "the meal's")) yield return w;
            FpEntry e = Entry(p);
            int seq = e.pendingSeq;
            int stale0 = FpHits.Count("Check stale");
            int accepted0 = FpHits.Count("Check accepted");
            K.Direct.Result(p, e.checkJob, seq + 1, FpOutcome.Great);
            K.Direct.Result(p, e.checkJob + 1, seq, FpOutcome.Great);
            K.Check("m3-stale ignored", FpHits.Count("Check stale") - stale0 == 2 && FpHits.Count("Check accepted") == accepted0 && e.pendingSeq == seq,
                "stale +" + (FpHits.Count("Check stale") - stale0) + ", accepted +" + (FpHits.Count("Check accepted") - accepted0) + "; " + State(p));
            K.Direct.Result(p, e.checkJob, seq, FpOutcome.Hit);
            FpTally t = FpMapComp.For(p.Map)?.TallyByKey(e.tallyKey);
            K.Check("m3-stale the right one is taken", FpHits.Count("Check accepted") == accepted0 + 1 && e.pendingSeq == 0 && t != null && t.hit == 1 && t.Count == 1,
                (t == null ? "no tally" : "tally " + t.great + "/" + t.hit + "/" + t.miss) + "; " + State(p));
            yield return K.WaitUntil(() => p.CurJob != job, "for the meal to be finished", 3600);
            foreach (int w in DirectRelease(p)) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- forced tallies (m3-quality, m3-poison)

        // A tally on the pawn's current job with the given form (-4, 0 or +4), as if they had worked it in first person.
        private static FpTally ForceTally(Pawn p, int form)
        {
            FpMapComp comp = FpMapComp.For(p.Map);
            int key = FpWork.TallyKeyOf(p, p.CurJob);
            FpTally t = comp.GetOrAddTally(key, null, p, p.CurJob.loadID);
            t.great = form > 0 ? 3 : form == 0 ? 1 : 0;
            t.hit = 0;
            t.miss = form < 0 ? 3 : form == 0 ? 1 : 0;
            return t;
        }

        private static void DropTally(Pawn p, FpTally t)
        {
            FpMapComp.For(p.Map)?.RemoveTally(t);
        }

        // 400 quality rolls of the pawn's crafting from one random state, with a forced form (null: no tally).
        private static List<int> Rolls(Pawn p, int? form)
        {
            FpTally t = form.HasValue ? ForceTally(p, form.Value) : null;
            List<int> rolls = new List<int>(400);
            Rand.PushState(42);
            try
            {
                for (int i = 0; i < 400; i++)
                {
                    rolls.Add((int)QualityUtility.GenerateQualityCreatedByPawn(p, SkillDefOf.Crafting, false));
                }
            }
            finally
            {
                Rand.PopState();
                if (t != null)
                {
                    DropTally(p, t);
                }
            }
            return rolls;
        }

        // Form -4, 0 and +4 move the crafted quality down and up as skill levels; form 0 rolls exactly as no tally does.
        private static IEnumerator<int> Quality()
        {
            Pawn p = new[] { S.C1, S.C2 }.FirstOrDefault(c => c?.skills?.GetSkill(SkillDefOf.Crafting) is SkillRecord r && !r.TotallyDisabled && c.CurJob != null);
            if (p == null)
            {
                K.Check("m3-quality crafter", false, "neither C1 nor C2 can craft (or has a job to hang a tally on)");
                yield return K.Done();
                yield break;
            }
            SkillRecord skill = p.skills.GetSkill(SkillDefOf.Crafting);
            int level0 = skill.Level;
            skill.Level = 10;   // room both ways for the +-4 offset
            int offsets0 = FpHits.Count("Quality offset");
            List<int> none = Rolls(p, null);
            List<int> bad = Rolls(p, -4);
            List<int> even = Rolls(p, 0);
            List<int> good = Rolls(p, 4);
            skill.Level = level0;
            double mBad = bad.Average(), mEven = even.Average(), mGood = good.Average(), mNone = none.Average();
            K.Check("m3-quality mean rises with form", mBad < mEven && mEven < mGood,
                "means: form -4 " + mBad.ToString("0.000") + ", 0 " + mEven.ToString("0.000") + ", +4 " + mGood.ToString("0.000") + " (no tally " + mNone.ToString("0.000") + ")");
            K.Check("m3-quality form 0 is no change", even.SequenceEqual(none), "first differing roll at " + FirstDiff(even, none));
            K.Check("m3-quality offset counted", FpHits.Count("Quality offset") - offsets0 == 800, (FpHits.Count("Quality offset") - offsets0) + " offsets applied (expected 800)");
            K.Check("m3-quality no tally left", FpRegistry.TallyForWorker(p) == null, FpState.Describe(p));
            yield return K.Done();
        }

        private static int FirstDiff(List<int> a, List<int> b)
        {
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                if (a[i] != b[i])
                {
                    return i;
                }
            }
            return a.Count == b.Count ? -1 : Math.Min(a.Count, b.Count);
        }

        // ---------------------------------------------------------------- m3-poison

        // Form -4 doubles the cook's food poison chance (+4 halves it), and the stat's explanation shows the line.
        private static IEnumerator<int> Poison()
        {
            Pawn p = new[] { S.C1, S.C2 }.FirstOrDefault(c => c?.CurJob != null);
            if (p == null)
            {
                K.Check("m3-poison pawn", false, "neither C1 nor C2 has a job to hang a tally on");
                yield return K.Done();
                yield break;
            }
            StatDef stat = StatDefOf.FoodPoisonChance;
            StatPart_FpForm part = stat.parts?.OfType<StatPart_FpForm>().FirstOrDefault();
            K.Check("m3-poison part installed", part != null, part == null ? "no StatPart_FpForm on FoodPoisonChance" : "table " + part.table);
            int applied0 = FpHits.Count("StatPart applied");
            float base0 = p.GetStatValue(stat);
            FpTally t = ForceTally(p, -4);
            float bad = p.GetStatValue(stat);
            StatRequest req = StatRequest.For(p);
            string line = part?.ExplanationPart(req);
            string full = stat.Worker.GetExplanationFull(req, stat.toStringNumberSense, bad);
            DropTally(p, t);
            t = ForceTally(p, 4);
            float good = p.GetStatValue(stat);
            DropTally(p, t);
            float after = p.GetStatValue(stat);
            FpTuningDef T = FpTuning.Get;
            K.Check("m3-poison form -4", Mathf.Abs(bad - base0 * FpTuningDef.Table(T.foodPoison, -4)) <= 1e-5f,
                "base " + base0.ToString("0.000000") + " -> " + bad.ToString("0.000000") + " (x" + FpTuningDef.Table(T.foodPoison, -4) + ")");
            K.Check("m3-poison form +4", Mathf.Abs(good - base0 * FpTuningDef.Table(T.foodPoison, 4)) <= 1e-5f,
                "base " + base0.ToString("0.000000") + " -> " + good.ToString("0.000000") + " (x" + FpTuningDef.Table(T.foodPoison, 4) + ")");
            K.Check("m3-poison explanation", !line.NullOrEmpty() && full.Contains(line), "line \"" + line + "\" in:\n" + full);
            K.Check("m3-poison back to base", Mathf.Abs(after - base0) <= 1e-7f, after.ToString("0.000000"));
            K.Check("m3-poison counted", FpHits.Count("StatPart applied") > applied0, (FpHits.Count("StatPart applied") - applied0) + " applications");
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m3-space

        // The check key answers the ring while it runs (no pause); 2 s after the result it pauses the game as in vanilla.
        private static IEnumerator<int> Space()
        {
            Pawn p = Doer(S.Stove, SimpleMeal, S.C1, S.C2);
            if (p == null)
            {
                K.Check("m3-space cook", false, "neither C1 nor C2 can cook a simple meal");
                yield return K.Done();
                yield break;
            }
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in EnterPlay(p)) yield return w;
            Job job = null;
            foreach (int w in StartBill(p, S.Stove, SimpleMeal, j => job = j)) yield return w;
            int resolved0 = Resolved;
            foreach (int w in AnswerRing(FpOutcome.Hit, "m3-space")) yield return w;
            yield return 5;
            K.Check("m3-space answer doesn't pause", !Find.TickManager.Paused && FpSkillCheck.State == FpRingState.Result,
                "paused " + Find.TickManager.Paused + ", ring " + FpSkillCheck.State);
            yield return K.WaitUntil(() => Resolved > resolved0, "for the answer to reach the simulation", 600);
            yield return 120;   // 2 s at least (every frame wait lasts at least a 60 fps frame)
            K.Check("m3-space key is free again", !FpSkillCheck.WantsKey, "ring " + FpSkillCheck.State);
            yield return K.Tap(Key.Space);
            yield return 5;
            K.Check("m3-space pauses after the grace", Find.TickManager.Paused, "paused " + Find.TickManager.Paused);
            K.SetSpeed(TimeSpeed.Normal);
            yield return 5;
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m3-freeze

        // What stops the ring's clock (spec 10.7 as changed by the integration): the game's pause holds a running needle,
        // and Space then unpauses instead of answering; a free cursor (Alt held) holds only the warning, and only for
        // FpSkillCheck.CursorGrace seconds, after which the needle runs and, unanswerable, runs out into a miss that
        // reaches the simulation (so freeing the cursor can't hold a check off until its deadline voids it).
        private static IEnumerator<int> Freeze()
        {
            if (!SpawnKit(Find.CurrentMap, out string error))
            {
                K.Check("m3-freeze kit", false, error);
                yield return K.Done();
                yield break;
            }
            Pawn p = Doer(Bench, Tribalwear, S.C1, S.C2);
            if (p == null)
            {
                K.Check("m3-freeze doer", false, "neither C1 nor C2 can make tribalwear");
                yield return K.Done();
                yield break;
            }
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in EnterPlay(p)) yield return w;
            Job job = null;
            foreach (int w in StartBill(p, Bench, Tribalwear, j => job = j)) yield return w;
            FpEntry e = Entry(p);
            K.Check("m3-freeze two checks", e != null && e.planned >= 2, State(p));

            // The pause.
            yield return K.WaitUntil(() => FpSkillCheck.State == FpRingState.Needle, "for the first needle", 7200);
            yield return 10;
            byte outcome0 = FpSkillCheck.LastOutcome;
            K.SetSpeed(TimeSpeed.Paused);
            yield return 2;
            float deg0 = FpSkillCheck.NeedleDeg;
            yield return 30;
            K.Check("m3-freeze pause holds the needle", FpSkillCheck.State == FpRingState.Needle && Mathf.Abs(FpSkillCheck.NeedleDeg - deg0) < 0.01f
                && !FpSkillCheck.WantsKey, "needle " + deg0.ToString("0.0") + " -> " + FpSkillCheck.NeedleDeg.ToString("0.0") + " over 30 paused frames, ring "
                + FpSkillCheck.State + ", wants the key " + FpSkillCheck.WantsKey);
            yield return K.Tap(Key.Space);
            yield return 5;
            K.Check("m3-freeze Space unpauses without answering", !Find.TickManager.Paused && FpSkillCheck.LastOutcome == outcome0
                && FpSkillCheck.State == FpRingState.Needle, "paused " + Find.TickManager.Paused + ", ring " + FpSkillCheck.State + ", last outcome "
                + FpSkillCheck.LastOutcome + " (was " + outcome0 + ")");
            K.SetSpeed(TimeSpeed.Normal);
            int resolved0 = Resolved;
            foreach (int w in AnswerRing(FpOutcome.Hit, "m3-freeze first")) yield return w;
            yield return K.WaitUntil(() => Resolved > resolved0, "for the first answer to reach the simulation", 600);

            // The free cursor, from the next ring's warning on.
            yield return K.WaitUntil(() => FpSkillCheck.State == FpRingState.Warn, "for the second ring", 7200);
            resolved0 = Resolved;
            int accepted0 = FpHits.Count("Check accepted");
            yield return K.Key(Key.LeftAlt, true);
            double t0 = Time.realtimeSinceStartupAsDouble;
            yield return 60;
            bool waiting = FpSkillCheck.State == FpRingState.Warn && !FpInput.Captured;
            string waitingText = "after 1 s: ring " + FpSkillCheck.State + ", captured " + FpInput.Captured;
            yield return K.WaitUntil(() => FpSkillCheck.State != FpRingState.Warn && FpSkillCheck.State != FpRingState.Needle,
                "for the needle to run out under the free cursor", 900);
            double waited = Time.realtimeSinceStartupAsDouble - t0;
            K.Check("m3-freeze the warning waits for the cursor", waiting, waitingText);
            double most = FpSkillCheck.CursorGrace + FpTuning.Get.ringWarn + FpTuning.Get.ringPeriod;
            K.Check("m3-freeze then the needle runs out", FpSkillCheck.LastOutcome == FpOutcome.MissTimeout && waited > FpSkillCheck.CursorGrace - 0.5
                && waited < most + 1.0, "outcome " + FpSkillCheck.LastOutcome + " after " + waited.ToString("0.00") + " s with the cursor free (expected about "
                + most.ToString("0.00") + " s at most)");
            yield return K.Key(Key.LeftAlt, false);
            yield return K.WaitUntil(() => Resolved > resolved0, "for the miss to reach the simulation", 600);
            K.Check("m3-freeze the miss counts", FpHits.Count("Check accepted") > accepted0 && FpWork.Diag.AppliedOutcome == FpOutcome.MissTimeout,
                "accepted +" + (FpHits.Count("Check accepted") - accepted0) + ", simulation got " + FpWork.Diag.AppliedOutcome + "; " + State(p));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m3-forfeit

        // Leaving the work while the ring shows a check doesn't escape it (spec 10.4). Walking away counts it as missed, work
        // and tally (FpWork.ForfeitPending from FpSetIntent), and the ring shows the miss; drafting ends the job too, and
        // the check counts on the piece's tally (FpWork.LeftJob).
        private static IEnumerator<int> Forfeit()
        {
            if (!SpawnKit(Find.CurrentMap, out string error))
            {
                K.Check("m3-forfeit kit", false, error);
                yield return K.Done();
                yield break;
            }
            Pawn p = Doer(Bench, Tribalwear, S.C1, S.C2);
            if (p == null)
            {
                K.Check("m3-forfeit doer", false, "neither C1 nor C2 can make tribalwear");
                yield return K.Done();
                yield break;
            }
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in EnterPlay(p)) yield return w;
            Job job = null;
            foreach (int w in StartBill(p, Bench, Tribalwear, j => job = j)) yield return w;
            FpEntry e = Entry(p);
            Bill bill = job.bill;
            FpTuningDef T = FpTuning.Get;

            // Walking away.
            yield return K.WaitUntil(() => FpSkillCheck.State == FpRingState.Needle, "for a needle", 7200);
            int key = e.tallyKey;
            int forfeits0 = FpHits.Count("Check forfeited");
            int shown0 = FpHits.Count("Check ring forfeited");
            yield return K.HoldTicks(30, Key.S);
            yield return K.WaitTicks(5);
            FpTally t = FpMapComp.For(p.Map)?.TallyByKey(key);
            float seg = Mathf.Max(0f, FpWork.Diag.AppliedBefore - FpWork.Diag.AppliedSegStart);
            float change = FpWork.Diag.AppliedAfter - FpWork.Diag.AppliedBefore;
            K.Check("m3-forfeit walking away counts a miss", FpHits.Count("Check forfeited") == forfeits0 + 1 && FpWork.Diag.AppliedOutcome == FpOutcome.MissTimeout
                && t != null && t.miss == 1 && t.Count == 1, "forfeited +" + (FpHits.Count("Check forfeited") - forfeits0) + ", applied " + FpWork.Diag.AppliedOutcome
                + ", tally " + (t == null ? "none" : t.great + "/" + t.hit + "/" + t.miss) + "; " + State(p));
            K.Check("m3-forfeit work taken", Mathf.Abs(change - seg * T.grantMiss) <= 1e-3f * FpWork.Diag.AppliedTotal,
                "done " + FpWork.Diag.AppliedBefore.ToString("0.00") + " -> " + FpWork.Diag.AppliedAfter.ToString("0.00") + " vs segment " + seg.ToString("0.00")
                + " x " + T.grantMiss.ToString("0.00"));
            K.Check("m3-forfeit the ring shows the miss", FpHits.Count("Check ring forfeited") == shown0 + 1, "ring " + FpSkillCheck.State);
            K.Check("m3-forfeit off the work", p.CurJob != job, State(p));

            // Drafting, on the same piece carried on.
            yield return K.WaitUntil(() => InControlJob(p), "for the control job", 600);
            FpBills.SendWorkBill(p, bill);
            yield return K.WaitUntil(() => p.CurJob?.def == JobDefOf.DoBill && p.CurJob.bill == bill, "for the bill job again", 600);
            Job job2 = p.CurJob;
            yield return K.WaitUntil(() => FpSkillCheck.State == FpRingState.Needle || p.CurJob != job2, "for a needle on the piece carried on", 7200);
            yield return 10;   // well past the warning's ringWarn game seconds (FpWork counts a check from then on)
            int abandoned0 = FpHits.Count("Check abandoned");
            int miss0 = t?.miss ?? 0;
            p.drafter.Drafted = true;
            yield return K.WaitTicks(3);
            t = FpMapComp.For(p.Map)?.TallyByKey(key);
            K.Check("m3-forfeit drafting counts a miss on the piece", p.CurJob != job2 && FpHits.Count("Check abandoned") == abandoned0 + 1
                && t != null && t.miss == miss0 + 1 && e.pendingSeq == 0, "abandoned +" + (FpHits.Count("Check abandoned") - abandoned0) + ", tally "
                + (t == null ? "none" : t.great + "/" + t.hit + "/" + t.miss) + "; " + State(p));
            p.drafter.Drafted = false;
            yield return K.WaitTicks(5);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- mp3-craft

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

        // Hosting reloads the game: the scene's things are new objects, found again by their cells (as FpTestM1), the kit
        // too.
        private static IEnumerable<int> AfterReload()
        {
            yield return K.WaitUntil(() => Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null
                && !LongEventHandler.AnyEventNowOrWaiting, "for the reload to finish", 7200);
            K.Check("scene found again", S.Adopt(Find.CurrentMap, out string error), error ?? S.Describe());
            K.Check("mp3-craft kit found again", FindKit(Find.CurrentMap, out string kitError) && Bench.thingIDNumber >= 0 && Cloth.thingIDNumber >= 0,
                kitError ?? Bench + " at " + Bench.Position + ", " + Cloth + " x" + Cloth.stackCount);
        }

        // The client crafts tribalwear with the pattern Great, Hit, Miss, Hit; the host only watches. The runner's compare
        // then checks both players' state lines (the work's progress and the tally included) are equal.
        private static IEnumerator<int> MpCraft()
        {
            if (K.IsHost)
            {
                yield return K.WaitUntil(() => MpBridge.InSession, "for hosting", 3600);
                // The kit was spawned with the scene, before hosting (FpTestKit.AddSceneExtra); the join point made for the
                // client's join carries it to the client.
                foreach (int w in AfterReload()) yield return w;
                yield return K.WaitUntil(() => Players >= 2, "for the client to join", 18000);
                yield return K.WaitUntil(() => Players < 2, "for the client to finish", 54000);
                yield return 120;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                if (!FindKit(Find.CurrentMap, out string error))
                {
                    K.Check("mp3-craft kit", false, error);
                    yield return K.Done();
                    yield break;
                }
                Pawn p = Doer(Bench, Tribalwear, S.C2, S.C1);
                if (p == null)
                {
                    K.Check("mp3-craft doer", false, "neither C2 nor C1 can make tribalwear");
                    yield return K.Done();
                    yield break;
                }
                // The doer and the colonists as the client found them (the M4-M6 test-copy run: twice the client's scene
                // differed from the host's after the half minute between hosting and the join, once the kit's cloth gone,
                // once another C1 and no control taken on the doer).
                K.Info("mp3-craft doer " + p.ThingID + " " + p.LabelShort + ": mental state " + (p.MentalStateDef?.defName ?? "none") + ", downed "
                    + p.Downed + ", drafted " + p.Drafted + ", " + JobOf(p) + "; colonists " + string.Join(", ", Find.CurrentMap.mapPawns.FreeColonistsSpawned
                    .Select(c => c.ThingID + (c.InMentalState ? " (" + c.MentalStateDef.defName + ")" : "") + (c.Downed ? " (downed)" : ""))));
                foreach (int w in EnterPlay(p)) yield return w;
                // The bill command only goes out from the interface (not during a long event): the sender's own test.
                yield return K.WaitUntil(() => MP.InInterface, "for Multiplayer to take commands", 1800);
                Job job = null;
                foreach (int w in StartBill(p, Bench, Tribalwear, j => job = j)) yield return w;
                FpEntry e = Entry(p);
                K.Check("mp3-craft checks planned", e != null && e.planned >= 4, State(p));
                byte[] pattern = { FpOutcome.Great, FpOutcome.Hit, FpOutcome.Miss, FpOutcome.Hit };
                int i = 0;
                foreach (byte want in pattern)
                {
                    int resolved0 = Resolved;
                    foreach (int w in AnswerRing(want, "mp3-craft " + i++ + " (" + want + ")")) yield return w;
                    yield return K.WaitUntil(() => Resolved > resolved0, "for the result's command to run", 1800);
                    K.Info("mp3-craft result " + want + ": simulation got " + FpWork.Diag.AppliedOutcome + "; " + State(p));
                }
                FpTally t = FpMapComp.For(p.Map)?.TallyByKey(e.tallyKey);
                K.Check("mp3-craft tally counts four", t != null && t.Count == 4, t == null ? "no tally" : "tally " + t.great + "/" + t.hit + "/" + t.miss);
                yield return K.WaitTicks(600);   // more state lines while the work goes on
                foreach (int w in LeavePlay()) yield return w;
                yield return K.WaitTicks(120);
                yield return K.Done();
            }
            else
            {
                K.Check("mp3-craft role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }
    }
}
