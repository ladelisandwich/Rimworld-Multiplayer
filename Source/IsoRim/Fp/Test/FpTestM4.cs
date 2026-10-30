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
    // Milestone 4 scenarios (fp-play-spec 16, M4): skill checks in all the other skilled work (FpAdapters: Construct, Mine,
    // PlantWork, Sow, Repair, AffectFloor, SmoothWall, RemoveBuilding; surgery through the Bill adapter) and their levers
    // (FpLevers: ConstructSuccessChance, MiningYield, PlantHarvestYield, with DrugHarvestYield through it), started the way
    // a player starts them: the left button on the target in first person (track C's float-menu choice, track D's work
    // orders), answered through the ring (track E).
    //
    // Deviations from the spec's table, to make the runs deterministic on random -quicktest colonists:
    // - The doer is teleported next to the target before play starts (solo only) instead of walking there: the left button
    //   works within arm's reach (FpPick.InReach), and the scene's targets are 3 to 7 cells from C1. The work is assigned
    //   to the doer only once it is under control (an idle colonist with the work assigned would start on the target).
    // - m4-repair registers the scene's half-broken wall with the game's repair list first: FpScene sets its hit points
    //   directly, which the list doesn't watch, so the repair work giver (and its float-menu order) wouldn't see it.
    // - The doer's level in the work's skill is set (solo: SetLevel; mp4-work: in the host's scene before hosting), so the
    //   work lasts long enough for checks at Normal, the ring's parts are wide enough for a scripted tap, and construction
    //   never fails its roll (Construction 10: ConstructSuccessChance 1.02 before the clamp, at full manipulation and sight).
    // - The lever lines are checked with a forced tally on the doer's own job (as m3-poison does), not only from the
    //   checks just answered: a finished piece takes its tally's job or thing with it.
    // - m4-surgery's patient is the other colonist, sent to bed tired (rest 0.05) so they sleep there; the operation is
    //   Biotech's hemogen extraction (500 work, medicine only), the only surgery in this game's defs that needs no item
    //   and harms no part. Without Biotech the scenario fails its setup with that reason. Anything tendable on the patient
    //   (a -quicktest colonist's asthma, an old wound) is tended first: the game puts "Prioritize tending" before the
    //   operation, and the left button takes the best option (first run: the click started a tend).
    // - mp4-work runs through four targets (three granite rocks and a stool frame next to the doer, spawned by the host
    //   before hosting) rather than for ten minutes: about three minutes of mining and building at Normal with every ring
    //   answered in a fixed pattern.
    // - m4-mine and m4-plants also check track D's work orders as D asked (the hint before the click, one command that
    //   designates, the designated jobs, a second click that sends nothing).
    // Added: m4-levers (every M4 lever's formula from forced tallies, the construction failure part's floor at a chance of
    // 1, and DrugHarvestYield taking the form once through PlantHarvestYield), m4-minehold (Direct control: mining held
    // before its last pick hit while a check waits), m4-deconstruct (RemoveBuilding on a wall of the scene's line) and
    // m4-order-refuse (D's refusals: a stale kind, an allowed area without the rock; a mining order abandoned with W).
    internal static class FpTestM4
    {
        internal static void Register(Dictionary<string, Func<IEnumerator<int>>> s)
        {
            s["m4-construct"] = Construct;
            s["m4-mine"] = Mine;
            s["m4-minehold"] = MineHold;
            s["m4-order-refuse"] = OrderRefuse;
            s["m4-plants"] = Plants;
            s["m4-repair"] = Repair;
            s["m4-surgery"] = Surgery;
            s["m4-smooth"] = Smooth;
            s["m4-deconstruct"] = Deconstruct;
            s["m4-levers"] = Levers;
            s["mp4-work"] = MpWork;
            // The host's kit is part of its scene, made before hosting (in a session it would get negative ids).
            K.AddSceneExtra("mp4-work", map => SpawnWorkKit(map, out string error) ? null : error);
        }

        private const string ControlJob = "IsoRimFp_Control";

        // ---------------------------------------------------------------- helpers (as FpTestM1 and FpTestM3)

        private static string JobOf(Pawn p)
        {
            return p?.CurJob == null ? "none" : p.CurJob.def.defName + ":" + p.CurJob.loadID + " " + p.CurJob.targetA;
        }

        private static bool InControlJob(Pawn p)
        {
            return p?.CurJob?.def?.defName == ControlJob;
        }

        // Jobs are pooled (JobMaker.ReturnToPool when a job ends; the next MakeJob, often the control job a tick later,
        // hands out the same object again), so a job is followed by its loadID, never by reference (first mp4-work run: the
        // finished stool's job object came back as the control job, and the wait for its end lasted until the colonist
        // went to eat, 7 minutes later; m4-deconstruct took 9). The last left click's job, by id:
        private static int clickedJobId = -1;

        private static bool OnJob(Pawn p, int id)
        {
            return id >= 0 && p?.CurJob != null && p.CurJob.loadID == id;
        }

        private static FpEntry Entry(Pawn p)
        {
            return FpRegistry.EntryOf(p);
        }

        private static string State(Pawn p)
        {
            JobDriver d = p?.jobs?.curDriver;
            IFpWorkAdapter a = d == null ? null : FpWork.AdapterFor(d);
            string work = a == null ? "no adapter" : a.Name + " " + a.Done(d).ToString("0.0") + "/" + a.Total(d).ToString("0.0");
            return JobOf(p) + " | " + work + " | " + FpState.Describe(p) + " | ring " + FpSkillCheck.State;
        }

        private static void SelectOnly(Thing t)
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(t, false, false);
        }

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

        private static Vector3 At(Thing t, float height)
        {
            Vector3 c = t.TrueCenter();
            return new Vector3(c.x, height, c.z);
        }

        private static Vector3 AtCell(IntVec3 c, float height)
        {
            return new Vector3(c.x + 0.5f, height, c.z + 0.5f);
        }

        private static IEnumerable<int> Click()
        {
            yield return K.Button(0, true);
            yield return K.Button(0, false);
        }

        // Solo only (game state): a colonist moved to a cell next to its target, standing, before play starts.
        private static void Teleport(Pawn p, IntVec3 c)
        {
            if (p.Position == c)
            {
                return;
            }
            p.jobs?.StopAll();
            p.pather?.StopDead();
            p.Position = c;
            p.Notify_Teleported();
        }

        // Solo only (game state): the work's skill at exactly this level (see the header).
        private static void SetLevel(Pawn p, SkillDef skill, int level)
        {
            SkillRecord r = skill == null ? null : p?.skills?.GetSkill(skill);
            if (r == null || r.TotallyDisabled || r.Level == level)
            {
                return;
            }
            K.Info(p.LabelShort + "'s " + skill.defName + " set from " + r.Level + " to " + level);
            r.Level = level;
        }

        // The first of C1 and C2 (or the given order) who can do all these kinds of work.
        private static Pawn Doer(WorkTypeDef[] work, params Pawn[] order)
        {
            return order.FirstOrDefault(c => c != null && work.All(w => !c.WorkTypeIsDisabled(w)));
        }

        // The work switched on for the doer (the scene clears every priority, and the game's own orders and the work
        // orders need the work assigned). Only once the doer is under control: an idle colonist with the work assigned
        // would start on the scene's target by itself.
        private static void Enable(Pawn p, params WorkTypeDef[] work)
        {
            foreach (WorkTypeDef w in work)
            {
                S.EnableWork(p, w);
            }
        }

        // A setup that can't run fails the scenario and ends it (the harness steps no script after Done).
        private static int Fail(string name, string why)
        {
            K.Check(name, false, why);
            return K.Done();
        }

        private static int Resolved => FpHits.Count("Check accepted") + FpHits.Count("Check stale");

        // Answers the ring now running with the wanted outcome (FpTestM3.AnswerRing: a tap timed so the press lands on the
        // great part's middle for Great, the Hit part's middle for Hit, 20 degrees before the arc for Miss; no press for a
        // timeout).
        // `shot` (optional) names a screenshot taken as the needle starts and another of the result (the ring on this work).
        private static IEnumerable<int> AnswerRing(byte want, string label, string shot = null)
        {
            yield return K.WaitUntil(() => FpSkillCheck.State == FpRingState.Needle, "for the ring's needle (" + label + ")", 3600);
            if (shot != null)
            {
                K.Shot(shot + "_ring");
            }
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
            float last = FpSkillCheck.NeedleDeg;
            while (FpSkillCheck.State == FpRingState.Needle)
            {
                float now = FpSkillCheck.NeedleDeg;
                float step = Mathf.Max(0.5f, now - last);
                last = now;
                float lead = Time.unscaledDeltaTime * rate - FpSettings.checkLatencyMs / 1000f * rate;
                if (now + lead + step / 2f >= target)
                {
                    yield return K.Tap(Key.Space);
                    break;
                }
                yield return 1;
            }
            yield return K.WaitUntil(() => FpSkillCheck.State != FpRingState.Needle, "for the ring's result", 600);
            K.Info(label + ": wanted " + want + ", judged press " + FpSkillCheck.LastPressDeg.ToString("0.0") + " -> outcome " + FpSkillCheck.LastOutcome);
            if (shot != null)
            {
                yield return 2;
                K.Shot(shot + "_result");
            }
        }

        // Answers one check the ring shows and checks the simulation applied the outcome as spec 10.4 says: the work moved
        // by the segment's share (within `slack` units: whole hit points for Mine and Repair) and the piece's tally counted
        // it. `name` prefixes the checks.
        private static IEnumerable<int> AnswerAndCheck(Pawn p, byte want, string name, float slack)
        {
            int resolved0 = Resolved;
            int accepted0 = FpHits.Count("Check accepted");
            // The first check of each work type is photographed (its ring, then its result).
            foreach (int w in AnswerRing(want, name, name.Replace(" check", "").Replace('-', '_').Replace(' ', '_'))) yield return w;
            yield return K.WaitUntil(() => Resolved > resolved0, "for the " + name + " answer to reach the simulation", 600);
            bool accepted = FpHits.Count("Check accepted") > accepted0;
            FpTuningDef T = FpTuning.Get;
            byte got = (byte)Math.Max(0, FpWork.Diag.AppliedOutcome);
            float grant = got == FpOutcome.Great ? T.grantGreat : got == FpOutcome.Hit ? T.grantHit : T.grantMiss;
            float seg = Mathf.Max(0f, FpWork.Diag.AppliedBefore - FpWork.Diag.AppliedSegStart);
            float change = FpWork.Diag.AppliedAfter - FpWork.Diag.AppliedBefore;
            K.Check(name + " accepted", accepted && FpWork.Diag.AppliedPawn == p.thingIDNumber,
                "accepted " + accepted + ", simulation got " + FpWork.Diag.AppliedOutcome + " for pawn " + FpWork.Diag.AppliedPawn + " (ring judged "
                + FpSkillCheck.LastOutcome + "); " + State(p));
            // The adapters keep the work within its bounds (FpAdapters.AddDone and the hit-point adapters' own clamps): a grant
            // never completes the piece (at most 99% of the total, or one hit point short), a miss never undoes more than
            // was done. A check raised late in the piece can ask for more than is left (second m4-construct run: 167.8
            // asked at 419.5 of 525), and then only reaches that bound.
            float asked = seg * grant;
            float eps = slack + 1e-3f * FpWork.Diag.AppliedTotal;
            bool exact = Mathf.Abs(change - asked) <= eps;
            bool capped = asked > 0f && change < asked && change >= -eps && FpWork.Diag.AppliedAfter >= 0.99f * FpWork.Diag.AppliedTotal - eps;
            bool floored = asked < 0f && change > asked && change <= eps && FpWork.Diag.AppliedAfter <= eps;
            K.Check(name + " work change", exact || capped || floored,
                "done " + FpWork.Diag.AppliedBefore.ToString("0.00") + " -> " + FpWork.Diag.AppliedAfter.ToString("0.00") + " of " + FpWork.Diag.AppliedTotal.ToString("0.00")
                + ": " + change.ToString("0.00") + " vs segment " + seg.ToString("0.00") + " x " + grant.ToString("0.00") + " = " + asked.ToString("0.00")
                + " (slack " + slack + ")" + (capped ? ", held at the top bound" : floored ? ", held at the bottom bound" : ""));
        }

        // Answers every check of the work until `done` (the job ended), outcomes taken in turn from `pattern`; returns
        // through `answered` how many it answered.
        private static IEnumerable<int> AnswerUntil(Func<bool> done, byte[] pattern, string label, int[] answered)
        {
            int n = 0;
            while (!done() && n < 16)
            {
                yield return K.WaitUntil(() => done() || FpSkillCheck.State == FpRingState.Needle, "for the next needle or the end of " + label, 36000);
                if (done())
                {
                    break;
                }
                int resolved0 = Resolved;
                foreach (int w in AnswerRing(pattern[n % pattern.Length], label + " check " + n)) yield return w;
                yield return K.WaitUntil(() => Resolved > resolved0 || done(), "for the " + label + " answer to run", 1800);
                n++;
            }
            answered[0] = n;
        }

        // Waits up to `frames` for the crosshair's hint to read `want` as an action (FpActions works the hint out at most 4
        // times a second, so a frame count alone depends on the frame rate), then checks it.
        private static IEnumerable<int> CheckHint(string name, string want, int frames = 120)
        {
            for (int i = 0; i < frames && !(FpActions.HintKind == FpHintKind.Action && FpActions.Hint == want); i++)
            {
                yield return 1;
            }
            K.Check(name + " hint", FpActions.HintKind == FpHintKind.Action && FpActions.Hint == want,
                "hint \"" + FpActions.Hint + "\" (" + FpActions.HintKind + "), expected \"" + want + "\"");
        }

        // The left button on the crosshair's target: aims, checks the pick (and the hint, when one is given), clicks and
        // waits for the job `started` names.
        private static IEnumerable<int> LeftClick(Pawn p, Vector3 aim, Func<FpHit, bool> picked, Func<Job, bool> started, string name, Job[] job, string hint = null)
        {
            foreach (int w in AimAt(aim)) yield return w;
            yield return 30;   // past the click guard after capture, and the hint
            FpHit hit = FpPick.Current;
            K.Check(name + " aimed", hit.Valid && picked(hit), "hit " + (hit.Thing?.ThingID ?? "cell " + hit.Cell) + ", in reach " + hit.InReach
                + ", hint \"" + FpActions.Hint + "\" (" + FpActions.HintKind + ")");
            if (hint != null)
            {
                foreach (int w in CheckHint(name, hint)) yield return w;
            }
            int before = p.CurJob != null ? p.CurJob.loadID : -1;
            foreach (int w in Click()) yield return w;
            yield return K.WaitUntil(() => p.CurJob != null && p.CurJob.loadID != before && started(p.CurJob), "for the " + name + " job", 900);
            job[0] = p.CurJob;
            clickedJobId = job[0] != null ? job[0].loadID : -1;
            K.Check(name + " job", job[0] != null && started(job[0]), JobOf(p));
        }

        // Waits for the work toil to be tracked and checks a plan exists; Done ends the scenario when none is planned.
        private static IEnumerable<int> WaitTracked(Pawn p, Job job, string adapter, string name, bool needChecks = true)
        {
            int id = job.loadID;   // read now: the object may be pooled and handed out again once the job ends
            yield return K.WaitUntil(() => Entry(p)?.checkJob == id || !OnJob(p, id), "for " + name + "'s work toil to be tracked", 7200);
            FpEntry e = Entry(p);
            JobDriver d = p.jobs.curDriver;
            IFpWorkAdapter a = d == null ? null : FpWork.AdapterFor(d);
            K.Check(name + " tracked by " + adapter, e != null && e.checkJob == id && a != null && a.Name == adapter,
                "adapter " + (a?.Name ?? "none") + "; " + State(p));
            if (needChecks && (e == null || e.planned == 0))
            {
                K.Check(name + " checks planned", false, "no checks planned (plan: rem " + FpWork.Diag.PlanRemTicks.ToString("0") + " ticks, f " + FpWork.Diag.PlanF + "); " + State(p));
                yield return K.Done();
            }
        }

        // ---------------------------------------------------------------- forced tallies and lever lines

        // A tally on the pawn's current job with form -4, 0 or +4 (as FpTestM3.ForceTally).
        private static FpTally ForceTally(Pawn p, int form)
        {
            FpMapComp comp = FpMapComp.For(p.Map);
            FpTally t = comp.GetOrAddTally(FpWork.TallyKeyOf(p, p.CurJob), null, p, p.CurJob.loadID);
            t.great = form > 0 ? 3 : form == 0 ? 1 : 0;
            t.hit = 0;
            t.miss = form < 0 ? 3 : form == 0 ? 1 : 0;
            t.expected = 0;   // undiluted: the table's own factor
            return t;
        }

        private static float WithForm(Pawn p, StatDef stat, int form)
        {
            FpTally t = ForceTally(p, form);
            try
            {
                return p.GetStatValue(stat);
            }
            finally
            {
                FpMapComp.For(p.Map)?.RemoveTally(t);
            }
        }

        // The value spec 10.5 expects for a form from the value with no tally (and the stat's own range).
        private static float Expected(StatDef stat, float base0, float k, bool failChance)
        {
            float v = failChance ? (base0 >= 1f ? base0 : 1f - (1f - base0) * k) : base0 * k;
            return Mathf.Clamp(v, stat.minValue, stat.maxValue);
        }

        // Spec 10.5 for one lever stat, from forced tallies on the pawn's current job: form -4 and +4 give the table's
        // factor (failure part for a success chance), form 0 changes nothing, the explanation shows the line, and no tally
        // is left behind. A value the stat's maximum already caps is reported, not judged.
        private static void LeverLine(string name, Pawn p, string statName, List<float> table, bool failChance)
        {
            StatDef stat = DefDatabase<StatDef>.GetNamedSilentFail(statName);
            StatPart_FpForm part = stat?.parts?.OfType<StatPart_FpForm>().FirstOrDefault();
            if (!K.Check(name + " " + statName + " part installed", part != null && part.failChance == failChance, part == null ? "no StatPart_FpForm" : "table " + part.table + ", failChance " + part.failChance))
            {
                return;
            }
            if (stat.Worker.IsDisabledFor(p))
            {
                K.Info(name + " " + statName + ": disabled for " + p.LabelShort + " (incapable); its lever line isn't checked");
                return;
            }
            float base0 = p.GetStatValue(stat);
            float bad = WithForm(p, stat, -4);
            float even = WithForm(p, stat, 0);
            float good = WithForm(p, stat, 4);
            float after = p.GetStatValue(stat);
            float eBad = Expected(stat, base0, FpTuningDef.Table(table, -4), failChance);
            float eGood = Expected(stat, base0, FpTuningDef.Table(table, 4), failChance);
            bool capped = base0 >= stat.maxValue - 1e-5f;
            K.Check(name + " " + statName + " form -4", capped ? bad <= base0 + 1e-5f : Mathf.Abs(bad - eBad) <= 1e-5f,
                "base " + base0.ToString("0.00000") + " -> " + bad.ToString("0.00000") + " (expected " + eBad.ToString("0.00000") + (capped ? ", base at the stat's max" : "") + ")");
            K.Check(name + " " + statName + " form +4", capped ? good >= base0 - 1e-5f : Mathf.Abs(good - eGood) <= 1e-5f,
                "base " + base0.ToString("0.00000") + " -> " + good.ToString("0.00000") + " (expected " + eGood.ToString("0.00000") + ")");
            K.Check(name + " " + statName + " form 0 and after", Mathf.Abs(even - base0) <= 1e-7f && Mathf.Abs(after - base0) <= 1e-7f,
                "form 0 " + even.ToString("0.00000") + ", after " + after.ToString("0.00000") + ", base " + base0.ToString("0.00000"));
            FpTally t = ForceTally(p, -4);
            StatRequest req = StatRequest.For(p);
            string line = part.ExplanationPart(req);
            string full = stat.Worker.GetExplanationFull(req, stat.toStringNumberSense, p.GetStatValue(stat));
            FpMapComp.For(p.Map)?.RemoveTally(t);
            K.Check(name + " " + statName + " explanation", !line.NullOrEmpty() && full.Contains(line), "line \"" + line + "\"");
        }

        // ---------------------------------------------------------------- m4-construct

        // The left button on the scene's table frame gives the game's finish-frame job; its check is answered (Great); the
        // finished table's quality went through the form offset; ConstructSuccessChance has the form line.
        private static IEnumerator<int> Construct()
        {
            Frame frame = S.TableFrame;
            Pawn p = Doer(new[] { WorkTypeDefOf.Construction }, S.C1, S.C2);
            if (frame == null || frame.Destroyed || p == null)
            {
                yield return Fail("m4-construct setup", frame == null ? "no table frame in the scene" : "neither C1 nor C2 can construct");
                yield break;
            }
            SetLevel(p, SkillDefOf.Construction, 10);
            K.SetSpeed(TimeSpeed.Normal);
            Teleport(p, S.Origin + new IntVec3(2, 0, -3));   // west of the frame at (3, -3)
            foreach (int w in EnterPlay(p)) yield return w;
            Enable(p, WorkTypeDefOf.Construction);
            IntVec3 cell = frame.Position;
            Job[] job = new Job[1];
            foreach (int w in LeftClick(p, At(frame, 0.05f), h => h.Thing == frame,
                j => j.def == JobDefOf.FinishFrame && j.targetA.Thing == frame, "m4-construct", job)) yield return w;
            foreach (int w in WaitTracked(p, job[0], "Construct", "m4-construct")) yield return w;
            int offsets0 = FpHits.Count("Quality offset");
            foreach (int w in AnswerAndCheck(p, FpOutcome.Great, "m4-construct check", 0f)) yield return w;
            FpTally tally = FpMapComp.For(p.Map)?.TallyByKey(Entry(p).tallyKey);
            K.Check("m4-construct frame tally", tally != null && tally.target == frame && tally.Count == 1, tally == null ? "no tally" : "tally on " + tally.target.ToStringSafe() + ", form " + tally.Form);
            int[] answered = { 0 };
            foreach (int w in AnswerUntil(() => !OnJob(p, clickedJobId), new[] { FpOutcome.Hit }, "m4-construct", answered)) yield return w;
            yield return K.WaitTicks(5);
            Thing built = cell.GetThingList(p.Map).FirstOrDefault(t => t.def == frame.def.entityDefToBuild);
            CompQuality q = built?.TryGetComp<CompQuality>();
            K.Check("m4-construct table built", built != null && q != null, built == null ? "no table at " + cell + " (frame " + (frame.Destroyed ? "gone" : "still there") + ")" : built + " " + q?.Quality);
            K.Check("m4-construct quality used the form", FpHits.Count("Quality offset") > offsets0, (FpHits.Count("Quality offset") - offsets0) + " offsets applied");
            yield return K.WaitUntil(() => InControlJob(p), "for the control job", 600);
            SetLevel(p, SkillDefOf.Construction, 3);   // under a chance of 1, so the failure part has something to scale
            LeverLine("m4-construct", p, "ConstructSuccessChance", FpTuning.Get.constructFail, true);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m4-mine

        // The left button on the undesignated granite gives a Mine designation and the game's mining job; a Great takes
        // the segment's share of hit points off the rock; MiningYield has the form line.
        private static IEnumerator<int> Mine()
        {
            Thing rock = S.Granite;
            Pawn p = Doer(new[] { WorkTypeDefOf.Mining }, S.C1, S.C2);
            if (rock == null || rock.Destroyed || p == null)
            {
                yield return Fail("m4-mine setup", rock == null ? "no granite in the scene" : "neither C1 nor C2 can mine");
                yield break;
            }
            SetLevel(p, SkillDefOf.Mining, 8);
            K.SetSpeed(TimeSpeed.Normal);
            Teleport(p, S.Origin + new IntVec3(4, 0, 3));   // south of the granite at (4, 4)
            foreach (int w in EnterPlay(p)) yield return w;
            Enable(p, WorkTypeDefOf.Mining);
            K.Check("m4-mine undesignated", p.Map.designationManager.DesignationOn(rock) == null && p.Map.designationManager.DesignationAt(rock.Position, DesignationDefOf.Mine) == null, "designations on the rock before the click");
            Job[] job = new Job[1];
            int ordered0 = FpHits.Count("Order work"), designated0 = FpHits.Count("Order work designated");
            foreach (int w in LeftClick(p, At(rock, 0.5f), h => h.Thing == rock, j => j.def == JobDefOf.Mine && j.targetA.Thing == rock, "m4-mine", job,
                "R3D_Fp_OrderMine".Translate(rock.LabelShort))) yield return w;
            K.Check("m4-mine designated", p.Map.designationManager.DesignationAt(rock.Position, DesignationDefOf.Mine) != null, "Mine designation at " + rock.Position);
            // Track D's work order (FpOrders): one command, which designated the rock and gave the game's mining job as a
            // "Prioritize" order would.
            K.Check("m4-mine one work order", FpHits.Count("Order work") - ordered0 == 1 && FpHits.Count("Order work designated") - designated0 == 1
                && job[0].playerForced && job[0].workGiverDef?.defName == "Mine",
                (FpHits.Count("Order work") - ordered0) + " orders, " + (FpHits.Count("Order work designated") - designated0) + " designated, forced "
                + job[0].playerForced + ", giver " + (job[0].workGiverDef?.defName ?? "none"));
            // A second click on the rock being mined sends nothing and keeps the job.
            int sent0 = FpHits.Count("Order work sent");
            foreach (int w in Click()) yield return w;
            yield return K.WaitTicks(20);
            K.Check("m4-mine second click", FpHits.Count("Order work sent") == sent0 && OnJob(p, clickedJobId),
                (FpHits.Count("Order work sent") - sent0) + " orders sent; " + JobOf(p));
            foreach (int w in WaitTracked(p, job[0], "Mine", "m4-mine")) yield return w;
            int hp0 = rock.HitPoints;
            foreach (int w in AnswerAndCheck(p, FpOutcome.Great, "m4-mine check", 1f)) yield return w;
            K.Info("m4-mine: the rock's hit points " + hp0 + " -> " + rock.HitPoints + " of " + rock.MaxHitPoints + " around the answer");
            // A click while the ring still shows its result reaches the left button (the pinned pointer is over the ring's
            // window, where Waylith marks mouse events used before the interface's pass; FpInput takes them back). On the
            // rock being mined it orders nothing ("already").
            if (FpSkillCheck.State != FpRingState.Idle)
            {
                int down0 = FpHits.Count("LMB down"), taken0 = FpHits.Count("Obscured mouse event taken"), sentOver0 = FpHits.Count("Order work sent");
                string ring = FpSkillCheck.State.ToString();
                foreach (int w in Click()) yield return w;
                yield return 3;
                K.Check("m4-mine click over the ring", FpHits.Count("LMB down") > down0 && FpHits.Count("Order work sent") == sentOver0,
                    "ring " + ring + ": LMB down +" + (FpHits.Count("LMB down") - down0) + ", obscured events taken +" + (FpHits.Count("Obscured mouse event taken") - taken0)
                    + ", orders sent +" + (FpHits.Count("Order work sent") - sentOver0));
            }
            else
            {
                K.Info("m4-mine: the ring was idle again after the answer; the click over the ring isn't checked");
            }
            int[] answered = { 0 };
            foreach (int w in AnswerUntil(() => !OnJob(p, clickedJobId), new[] { FpOutcome.Hit, FpOutcome.Miss }, "m4-mine", answered)) yield return w;
            K.Check("m4-mine mined out", rock.Destroyed, "rock " + (rock.Destroyed ? "gone" : "still there at " + rock.HitPoints + " hit points") + "; " + answered[0] + " more checks answered");
            yield return K.WaitUntil(() => InControlJob(p), "for the control job", 600);
            LeverLine("m4-mine", p, "MiningYield", FpTuning.Get.yield, false);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m4-minehold

        // Direct control (no ring): mining planned at Normal, then run at Superfast with its first check unanswered, stops
        // before the pick hit that would mine the rock out (FpMineAdapter.NearCompletion), and the rock is mined once
        // answered.
        private static IEnumerator<int> MineHold()
        {
            Thing rock = S.Granite;
            Pawn p = Doer(new[] { WorkTypeDefOf.Mining }, S.C1, S.C2);
            if (rock == null || rock.Destroyed || p == null)
            {
                yield return Fail("m4-minehold setup", rock == null ? "no granite in the scene" : "neither C1 nor C2 can mine");
                yield break;
            }
            SetLevel(p, SkillDefOf.Mining, 8);
            K.SetSpeed(TimeSpeed.Normal);
            Teleport(p, S.Origin + new IntVec3(4, 0, 3));
            foreach (int w in DirectControl(p)) yield return w;
            Enable(p, WorkTypeDefOf.Mining);
            FpOrders.FpOrderWork(p, rock, 0);   // the work order's own command (outside a session it runs at once)
            yield return K.WaitUntil(() => p.CurJob?.def == JobDefOf.Mine && p.CurJob.targetA.Thing == rock, "for the mining job", 600);
            Job job = p.CurJob;
            int jobId = job.loadID;
            foreach (int w in WaitTracked(p, job, "Mine", "m4-minehold")) yield return w;
            FpEntry e = Entry(p);
            // Superfast before the check is raised: its deadline is deadlineTicks x the speed factor of the moment it is
            // raised (3600 ticks at Superfast), which leaves the mining time to reach its last pick hit while it waits.
            K.SetSpeed(TimeSpeed.Superfast);
            yield return K.WaitUntil(() => e.pendingSeq != 0 || !OnJob(p, jobId), "for a check", 7200);
            int seq = e.pendingSeq;
            K.Check("m4-minehold check raised at Superfast", seq != 0 && e.deadlineTick - e.pendingTick == FpTuning.Get.deadlineTicks * FpMath.SpeedFactor((int)TimeSpeed.Superfast),
                "raised at " + e.pendingTick + ", deadline " + e.deadlineTick + "; " + State(p));
            yield return K.WaitUntil(() => e.held || e.pendingSeq != seq || !OnJob(p, jobId), "for the hold", 3600);
            int hit = rock.def.building.isNaturalRock ? 80 : 40;
            K.Check("m4-minehold held before the last hit", e.held && e.pendingSeq == seq && !rock.Destroyed && rock.HitPoints <= hit,
                "hit points " + rock.HitPoints + " (one hit " + hit + "); " + State(p));
            int hits0 = FpHits.Count("Hold skipped interval");
            int hp0 = rock.HitPoints;
            yield return K.WaitTicks(60);
            K.Check("m4-minehold work stopped", e.held && rock.HitPoints == hp0 && !rock.Destroyed && FpHits.Count("Hold skipped interval") > hits0,
                "hit points " + hp0 + " -> " + rock.HitPoints + " over 60 ticks, " + (FpHits.Count("Hold skipped interval") - hits0) + " intervals skipped");
            K.Direct.Result(p, e.checkJob, e.pendingSeq, FpOutcome.Hit);
            K.Check("m4-minehold released by the result", !e.held && e.pendingSeq == 0, State(p));
            yield return K.WaitUntil(() => rock.Destroyed || !OnJob(p, jobId), "for the rock to be mined out", 1800);
            K.Check("m4-minehold mined out", rock.Destroyed, State(p));
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in DirectRelease(p)) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m4-order-refuse

        // Track D's work order when it can't go through (FpOrders, spec 11.4), and what an abandoned one leaves:
        // - a command whose kind no longer fits the target is refused (R3D_Fp_OrderChanged) and designates nothing;
        // - a colonist restricted to an area without the rock gets no order hint, the area's name in the refusal, and the
        //   left button sends nothing; the command itself refuses too (R3D_Fp_OrderForbidden), designating nothing;
        // - a mining order the player walks away from (W) ends the job; the Mine designation stays, as a designator's would.
        // The colonist's work priorities are never switched on here: the order works whatever the work tab says.
        private static IEnumerator<int> OrderRefuse()
        {
            Thing rock = S.Granite;
            Pawn p = Doer(new[] { WorkTypeDefOf.Mining }, S.C1, S.C2);
            if (rock == null || rock.Destroyed || p == null)
            {
                yield return Fail("m4-order-refuse setup", rock == null ? "no granite in the scene" : "neither C1 nor C2 can mine");
                yield break;
            }
            Map map = Find.CurrentMap;
            DesignationManager dm = map.designationManager;
            K.SetSpeed(TimeSpeed.Normal);
            IntVec3 stand = S.Origin + new IntVec3(4, 0, 3);   // south of the granite at (4, 4)
            Teleport(p, stand);
            foreach (int w in EnterPlay(p)) yield return w;

            // The wrong kind (a harvest order on a rock): refused in the command.
            int job0 = p.CurJob.loadID;
            FpOrders.LastToldKey = null;
            bool sent = FpOrders.TrySendOrderWork(p, rock, FpOrders.KindHarvest);
            yield return K.WaitTicks(5);
            K.Check("m4-order-refuse wrong kind", sent && FpOrders.LastToldKey == "R3D_Fp_OrderChanged" && dm.DesignationAt(rock.Position, DesignationDefOf.Mine) == null
                && InControlJob(p) && p.CurJob.loadID == job0,
                "sent " + sent + ", told " + (FpOrders.LastToldKey ?? "nothing") + ", designation " + (dm.DesignationAt(rock.Position, DesignationDefOf.Mine) != null) + "; " + JobOf(p));

            // An allowed area around the colonist that leaves the rock out.
            if (!map.areaManager.TryMakeNewAllowed(out Area_Allowed area))
            {
                yield return Fail("m4-order-refuse area", "no new allowed area could be made");
                yield break;
            }
            foreach (IntVec3 c in CellRect.CenteredOn(stand, 1))
            {
                if (c.InBounds(map) && c != rock.Position)
                {
                    area[c] = true;
                }
            }
            p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
            yield return K.WaitUntil(() => InControlJob(p), "for the control job inside the area", 600);
            // Away from the rock first, so the crosshair's hint is worked out afresh on it with the area on (FpActions keeps
            // a hint while its target stays the same).
            Turn(180f, 8f);
            yield return 20;
            foreach (int w in AimAt(At(rock, 0.5f))) yield return w;
            yield return 30;
            FpHit hit = FpPick.Current;
            string refusal = FpOrders.RefusalHint(p, hit);
            K.Check("m4-order-refuse area hint", hit.Thing == rock && FpOrders.OrderHint(p, hit) == null && refusal != null && refusal.Contains(area.Label),
                "hit " + (hit.Thing?.ThingID ?? "none") + ", order hint \"" + FpOrders.OrderHint(p, hit) + "\", refusal \"" + refusal + "\", area " + area.Label
                + "; crosshair hint \"" + FpActions.Hint + "\" (" + FpActions.HintKind + ")");
            // The crosshair shows the same reason in red (FpActions.WorkOutHint, D's request), at most 4 hint passes a second.
            for (int i = 0; i < 120 && !(FpActions.HintKind == FpHintKind.Invalid && FpActions.Hint != null && FpActions.Hint.Contains(area.Label)); i++)
            {
                yield return 1;
            }
            K.Check("m4-order-refuse area crosshair hint", FpActions.HintKind == FpHintKind.Invalid && FpActions.Hint != null && FpActions.Hint.Contains(area.Label),
                "hint \"" + FpActions.Hint + "\" (" + FpActions.HintKind + "), area " + area.Label);
            int sent0 = FpHits.Count("Order work sent");
            foreach (int w in Click()) yield return w;
            yield return K.WaitTicks(20);
            K.Check("m4-order-refuse area click", FpHits.Count("Order work sent") == sent0 && dm.DesignationAt(rock.Position, DesignationDefOf.Mine) == null && InControlJob(p),
                (FpHits.Count("Order work sent") - sent0) + " orders sent, designation " + (dm.DesignationAt(rock.Position, DesignationDefOf.Mine) != null) + "; " + JobOf(p));
            FpOrders.LastToldKey = null;
            sent = FpOrders.TrySendOrderWork(p, rock, FpOrders.KindMine);
            yield return K.WaitTicks(5);
            K.Check("m4-order-refuse area command", sent && FpOrders.LastToldKey == "R3D_Fp_OrderForbidden" && dm.DesignationAt(rock.Position, DesignationDefOf.Mine) == null && InControlJob(p),
                "sent " + sent + ", told " + (FpOrders.LastToldKey ?? "nothing") + ", designation " + (dm.DesignationAt(rock.Position, DesignationDefOf.Mine) != null) + "; " + JobOf(p));
            p.playerSettings.AreaRestrictionInPawnCurrentMap = null;
            area.Delete();

            // Ordered for real, then abandoned with W. (No hint check: the crosshair hasn't moved, so FpActions keeps the hint
            // it worked out while the area was on; m4-mine checks the hint.)
            Job[] job = new Job[1];
            foreach (int w in LeftClick(p, At(rock, 0.5f), h => h.Thing == rock, j => j.def == JobDefOf.Mine && j.targetA.Thing == rock, "m4-order-refuse mine", job)) yield return w;
            Turn(180f, 8f);   // facing south, away from the rock: W walks off into open ground
            yield return 3;
            yield return K.HoldTicks(30, Key.W);
            yield return K.WaitUntil(() => InControlJob(p), "for W to end the mining job", 600);
            K.Check("m4-order-refuse walked away", InControlJob(p) && dm.DesignationAt(rock.Position, DesignationDefOf.Mine) != null,
                JobOf(p) + ", Mine designation " + (dm.DesignationAt(rock.Position, DesignationDefOf.Mine) != null ? "kept" : "gone"));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m4-plants

        // The left button on the grown oak chops it, on the ripe rice harvests it, and on the rice's empty field cell sows
        // it; each is tracked by its adapter with checks; PlantHarvestYield has the form line.
        private static IEnumerator<int> Plants()
        {
            Pawn p = Doer(new[] { WorkTypeDefOf.PlantCutting, WorkTypeDefOf.Growing }, S.C1, S.C2);
            if (p == null || S.Oak == null || S.Rice == null)
            {
                yield return Fail("m4-plants setup", p == null ? "neither C1 nor C2 can cut and grow plants" : "no oak or rice in the scene");
                yield break;
            }
            SetLevel(p, SkillDefOf.Plants, 6);   // plant work speed about 0.8: the rice's 200 and 170 work last over 150 ticks
            K.SetSpeed(TimeSpeed.Normal);
            byte[] pattern = { FpOutcome.Hit, FpOutcome.Great };
            int[] answered = { 0 };

            // Chop the oak.
            Thing oak = S.Oak;
            Teleport(p, S.Origin + new IntVec3(-5, 0, 2));   // east of the oak at (-6, 2)
            foreach (int w in EnterPlay(p)) yield return w;
            Enable(p, WorkTypeDefOf.PlantCutting, WorkTypeDefOf.Growing);
            Job[] job = new Job[1];
            foreach (int w in LeftClick(p, At(oak, 0.5f), h => h.Thing == oak, j => j.targetA.Thing == oak && p.jobs.curDriver is JobDriver_PlantWork, "m4-plants chop", job,
                "R3D_Fp_OrderChop".Translate(oak.LabelShort))) yield return w;
            // Track D's work order: a grown tree gets the game's "Chop wood" (a HarvestPlant designation, the designated
            // harvest job).
            K.Check("m4-plants chop order", p.Map.designationManager.DesignationOn(oak, DesignationDefOf.HarvestPlant) != null
                && job[0].def == JobDefOf.HarvestDesignated && job[0].playerForced,
                JobOf(p) + ", giver " + (job[0].workGiverDef?.defName ?? "none"));
            foreach (int w in WaitTracked(p, job[0], "PlantWork", "m4-plants chop")) yield return w;
            foreach (int w in AnswerAndCheck(p, FpOutcome.Great, "m4-plants chop check", 0f)) yield return w;
            foreach (int w in AnswerUntil(() => !OnJob(p, clickedJobId), pattern, "m4-plants chop", answered)) yield return w;
            K.Check("m4-plants oak chopped", oak.Destroyed, oak.Destroyed ? "gone" : "still there; " + State(p));

            // Harvest the rice. Moved there while still controlled (the control job comes straight back), so the colonist,
            // whose plant work is now assigned, never stands idle next to the ripe rice.
            Thing rice = S.Rice;
            IntVec3 field = rice.Position;
            yield return K.WaitUntil(() => InControlJob(p), "for the control job", 600);
            Teleport(p, S.Origin + new IntVec3(5, 0, 2));   // west of the rice at (6, 2)
            yield return K.WaitUntil(() => InControlJob(p) && !p.pather.Moving, "for the control job after the move", 600);
            yield return 10;
            foreach (int w in LeftClick(p, At(rice, 0.15f), h => h.Thing == rice, j => j.targetA.Thing == rice && p.jobs.curDriver is JobDriver_PlantWork, "m4-plants harvest", job,
                "R3D_Fp_OrderHarvest".Translate(rice.LabelShort))) yield return w;
            K.Check("m4-plants harvest order", p.Map.designationManager.DesignationOn(rice, DesignationDefOf.HarvestPlant) != null
                && job[0].def == JobDefOf.HarvestDesignated && job[0].playerForced,
                JobOf(p) + ", giver " + (job[0].workGiverDef?.defName ?? "none"));
            foreach (int w in WaitTracked(p, job[0], "PlantWork", "m4-plants harvest")) yield return w;
            foreach (int w in AnswerUntil(() => !OnJob(p, clickedJobId), pattern, "m4-plants harvest", answered)) yield return w;
            K.Check("m4-plants rice harvested", rice.Destroyed && answered[0] >= 1, "rice " + (rice.Destroyed ? "gone" : "still there") + ", " + answered[0] + " checks answered");
            yield return K.WaitUntil(() => InControlJob(p), "for the control job", 600);
            LeverLine("m4-plants", p, "PlantHarvestYield", FpTuning.Get.yield, false);

            // Sow it again, from the growing zone's own order on the empty cell (the harvested grain moved off it first, so
            // the crosshair finds the cell and not the grain).
            ThingDef riceDef = rice.def;
            foreach (Thing item in field.GetThingList(p.Map).Where(t => t.def.category == ThingCategory.Item).ToList())
            {
                item.Destroy(DestroyMode.Vanish);
            }
            if (!PlantUtility.GrowthSeasonNow(field, p.Map, riceDef))
            {
                K.Info("m4-plants sow: not rice's growing season here now; sowing skipped");
            }
            else
            {
                foreach (int w in LeftClick(p, AtCell(field, 0f), h => h.Cell == field,
                    j => j.def == JobDefOf.Sow && j.targetA.Cell == field, "m4-plants sow", job)) yield return w;
                foreach (int w in WaitTracked(p, job[0], "Sow", "m4-plants sow")) yield return w;
                Thing sown = job[0].targetA.Thing;
                foreach (int w in AnswerUntil(() => !OnJob(p, clickedJobId), pattern, "m4-plants sow", answered)) yield return w;
                FpTally t = sown == null ? null : FpMapComp.For(p.Map)?.TallyByKey(sown.thingIDNumber);
                K.Check("m4-plants sown", sown is Plant pl && !pl.Destroyed && pl.Growth > 0f && answered[0] >= 1,
                    "sown " + sown.ToStringSafe() + ", " + answered[0] + " checks answered, the plant's tally " + (t == null ? "none" : t.great + "/" + t.hit + "/" + t.miss));
            }
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m4-repair

        // The left button on the scene's half-broken wall gives the game's repair job; the answers move its hit points by
        // their share; the work is never held (Repair works in its tickAction).
        private static IEnumerator<int> Repair()
        {
            Thing wall = S.DamagedWall;
            Pawn p = Doer(new[] { WorkTypeDefOf.Construction }, S.C1, S.C2);
            if (wall == null || wall.Destroyed || p == null || !FpAdapters.Has("Repair"))
            {
                yield return Fail("m4-repair setup", wall == null ? "no damaged wall in the scene" : p == null ? "neither C1 nor C2 can construct"
                    : "the Repair adapter is off (adapters: " + FpAdapters.Names + ")");
                yield break;
            }
            SetLevel(p, SkillDefOf.Construction, 10);
            p.Map.areaManager.Home[wall.Position] = true;   // the game repairs only in the home area
            // FpScene broke the wall through its hit points, which the game's repair list doesn't watch (only damage, a
            // spawn and a repair update it), so the repair work giver wouldn't see it.
            p.Map.listerBuildingsRepairable.Notify_BuildingTookDamage((Building)wall);
            K.SetSpeed(TimeSpeed.Normal);
            Teleport(p, S.Origin + new IntVec3(-4, 0, 5));   // south of the wall at (-4, 6)
            foreach (int w in EnterPlay(p)) yield return w;
            Enable(p, WorkTypeDefOf.Construction);
            Job[] job = new Job[1];
            foreach (int w in LeftClick(p, At(wall, 0.5f), h => h.Thing == wall, j => j.def == JobDefOf.Repair && j.targetA.Thing == wall, "m4-repair", job)) yield return w;
            foreach (int w in WaitTracked(p, job[0], "Repair", "m4-repair")) yield return w;
            FpEntry e = Entry(p);
            K.Check("m4-repair counts from the start", e.startHp > 0 && e.startHp < wall.MaxHitPoints && e.startHp <= wall.HitPoints,
                "start " + e.startHp + ", now " + wall.HitPoints + " of " + wall.MaxHitPoints);
            int holds0 = FpHits.Count("Hold skipped interval");
            bool heldSeen = false;
            yield return K.WaitUntil(() => e.pendingSeq != 0 || !OnJob(p, clickedJobId), "for a check", 7200);
            foreach (int w in AnswerAndCheck(p, FpOutcome.Great, "m4-repair check", 1f)) yield return w;
            int n = 0;
            while (OnJob(p, clickedJobId) && n < 16)
            {
                heldSeen |= e.held;
                if (FpSkillCheck.State == FpRingState.Needle)
                {
                    foreach (int w in AnswerRing(n % 2 == 0 ? FpOutcome.Hit : FpOutcome.Miss, "m4-repair check " + ++n)) yield return w;
                }
                yield return 1;
            }
            K.Check("m4-repair never held", !heldSeen && FpHits.Count("Hold skipped interval") == holds0,
                "held seen " + heldSeen + ", intervals skipped " + (FpHits.Count("Hold skipped interval") - holds0));
            K.Check("m4-repair repaired", wall.HitPoints == wall.MaxHitPoints, "hit points " + wall.HitPoints + " of " + wall.MaxHitPoints + "; " + State(p));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m4-surgery

        private static readonly IntVec3 BedAt = new IntVec3(-4, 0, -4);
        private static readonly IntVec3 MedicineAt = new IntVec3(-2, 0, -4);
        private static readonly IntVec3 DoctorAt = new IntVec3(-3, 0, -3);

        // An operation (Bill_Medical) is a bill: the left button on the patient in bed gives the doctor the game's DoBill job
        // for it, tracked by the Bill adapter with a Medicine check; MedicalSurgerySuccessChance has the form line.
        private static IEnumerator<int> Surgery()
        {
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail("ExtractHemogenPack");
            Pawn doctor = Doer(new[] { WorkTypeDefOf.Doctor }, S.C1, S.C2);
            Pawn patient = doctor == S.C1 ? S.C2 : S.C1;
            if (recipe == null || doctor == null || patient == null || !patient.def.AllRecipes.Contains(recipe) || !recipe.AvailableOnNow(patient))
            {
                yield return Fail("m4-surgery setup", recipe == null ? "no ExtractHemogenPack recipe (Biotech off)" : doctor == null ? "neither C1 nor C2 can doctor"
                    : "the hemogen extraction isn't available on " + patient.LabelShort);
                yield break;
            }
            Map map = Find.CurrentMap;
            SetLevel(doctor, SkillDefOf.Medicine, 6);   // medical operation speed 0.76: the 500 work lasts over 150 ticks
            K.SetSpeed(TimeSpeed.Normal);
            Thing bedThing = ThingMaker.MakeThing(ThingDef.Named("Bed"), ThingDefOf.WoodLog);
            bedThing.SetFactionDirect(Faction.OfPlayer);
            Building_Bed bed = (Building_Bed)GenSpawn.Spawn(bedThing, S.Origin + BedAt, map, Rot4.North);
            Thing medicine = ThingMaker.MakeThing(ThingDefOf.MedicineIndustrial);
            medicine.stackCount = 5;
            GenSpawn.Spawn(medicine, S.Origin + MedicineAt, map);
            if (patient.needs?.rest != null)
            {
                patient.needs.rest.CurLevel = 0.05f;   // tired: asleep in the bed for hours
            }
            Job lie = JobMaker.MakeJob(JobDefOf.LayDown, bed);
            patient.jobs.StartJob(lie, JobCondition.InterruptForced);
            yield return K.WaitUntil(() => patient.InBed(), "for " + patient.LabelShort + " to be in bed", 1800);
            // A -quicktest colonist can start with something tendable (an old wound, a disease): the doctor's best option on
            // a patient who needs tending is then the game's "Prioritize tending" (the Doctor work type's tend giver comes
            // before its bill giver), which the left button takes. Tended first, so the operation is the left button's
            // option, as the spec's scenario means it.
            List<string> tended = new List<string>();
            foreach (Hediff h in patient.health.hediffSet.hediffs.ToList())
            {
                if (h.TendableNow())
                {
                    tended.Add(h.LabelCap);
                    h.Tended(1f, 1f);
                }
            }
            K.Check("m4-surgery patient needs no tending", !patient.health.HasHediffsNeedingTend(),
                tended.Count == 0 ? "nothing to tend" : "tended first: " + string.Join(", ", tended));
            Bill_Medical bill = HealthCardUtility.CreateSurgeryBill(patient, recipe, null, null, false);
            K.Check("m4-surgery bill", bill != null && patient.BillStack.Bills.Contains(bill), "bill " + bill?.GetUniqueLoadID() + " on " + patient.LabelShort);
            Teleport(doctor, S.Origin + DoctorAt);
            foreach (int w in EnterPlay(doctor)) yield return w;
            Enable(doctor, WorkTypeDefOf.Doctor);
            Job[] job = new Job[1];
            foreach (int w in LeftClick(doctor, At(patient, 0.2f), h => h.Thing == patient,
                j => j.def == JobDefOf.DoBill && j.bill == bill, "m4-surgery", job)) yield return w;
            foreach (int w in WaitTracked(doctor, job[0], "Bill", "m4-surgery")) yield return w;
            K.Check("m4-surgery Medicine check", FpWork.SkillOf(doctor.jobs.curDriver) == SkillDefOf.Medicine, "skill " + FpWork.SkillOf(doctor.jobs.curDriver)?.defName);
            foreach (int w in AnswerAndCheck(doctor, FpOutcome.Hit, "m4-surgery check", 0f)) yield return w;
            if (OnJob(doctor, clickedJobId))
            {
                K.Info("m4-surgery: the doctor's form during the operation is " + FpLevers.FormOf(doctor) + "; MedicalSurgerySuccessChance "
                    + doctor.GetStatValue(StatDefOf.MedicalSurgerySuccessChance).ToString("0.000"));
            }
            int[] answered = { 0 };
            foreach (int w in AnswerUntil(() => !OnJob(doctor, clickedJobId), new[] { FpOutcome.Hit }, "m4-surgery", answered)) yield return w;
            yield return K.WaitUntil(() => InControlJob(doctor), "for the control job", 900);
            LeverLine("m4-surgery", doctor, "MedicalSurgerySuccessChance", FpTuning.Get.surgery, false);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m4-smooth

        private static readonly IntVec3 FloorAt = new IntVec3(0, 0, -5);

        // A rough stone floor designated for smoothing: the left button on it gives the game's smoothing job, tracked by the
        // AffectFloor adapter (a cell: the tally is the job's) with checks.
        private static IEnumerator<int> Smooth()
        {
            Pawn p = Doer(new[] { WorkTypeDefOf.Construction }, S.C1, S.C2);
            TerrainDef rough = DefDatabase<TerrainDef>.GetNamedSilentFail("Granite_Rough")
                ?? DefDatabase<TerrainDef>.AllDefsListForReading.FirstOrDefault(t => t.smoothedTerrain != null && t.natural);
            if (p == null || rough == null)
            {
                yield return Fail("m4-smooth setup", p == null ? "neither C1 nor C2 can construct" : "no smoothable rough stone floor in the defs");
                yield break;
            }
            Map map = Find.CurrentMap;
            IntVec3 cell = S.Origin + FloorAt;
            map.terrainGrid.SetTerrain(cell, rough);
            map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.SmoothFloor));
            SetLevel(p, SkillDefOf.Construction, 10);
            K.SetSpeed(TimeSpeed.Normal);
            Teleport(p, cell + new IntVec3(0, 0, 1));
            foreach (int w in EnterPlay(p)) yield return w;
            Enable(p, WorkTypeDefOf.Construction);
            Job[] job = new Job[1];
            foreach (int w in LeftClick(p, AtCell(cell, 0f), h => h.Cell == cell && h.Thing == null,
                j => j.def == JobDefOf.SmoothFloor && j.targetA.Cell == cell, "m4-smooth", job)) yield return w;
            foreach (int w in WaitTracked(p, job[0], "AffectFloor", "m4-smooth")) yield return w;
            K.Check("m4-smooth job-keyed tally", Entry(p).tallyKey == -(job[0].loadID + 1), "key " + Entry(p).tallyKey + " for job " + job[0].loadID);
            foreach (int w in AnswerAndCheck(p, FpOutcome.Hit, "m4-smooth check", 0f)) yield return w;
            int[] answered = { 0 };
            foreach (int w in AnswerUntil(() => !OnJob(p, clickedJobId), new[] { FpOutcome.Great, FpOutcome.Miss }, "m4-smooth", answered)) yield return w;
            K.Check("m4-smooth smoothed", map.terrainGrid.TerrainAt(cell) == rough.smoothedTerrain, "terrain " + map.terrainGrid.TerrainAt(cell).defName);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m4-deconstruct

        // A wall of the scene's line designated for deconstruction: the left button gives the game's deconstruct job,
        // tracked by the RemoveBuilding adapter on the wall's tally. A granite wall is 950 work (WorkToBuild 135 x 6 + 140,
        // StatWorker applies the stuff's factor before its offset); Construction 4 (speed 0.65 x 1.7) takes about 860
        // ticks: two checks at Normal.
        private static IEnumerator<int> Deconstruct()
        {
            Pawn p = Doer(new[] { WorkTypeDefOf.Construction }, S.C1, S.C2);
            Building wall = S.WallLine.Count > 0 ? S.WallLine[0] : null;
            if (p == null || wall == null || wall.Destroyed)
            {
                yield return Fail("m4-deconstruct setup", p == null ? "neither C1 nor C2 can construct" : "no wall line in the scene");
                yield break;
            }
            Map map = Find.CurrentMap;
            map.designationManager.AddDesignation(new Designation(wall, DesignationDefOf.Deconstruct));
            SetLevel(p, SkillDefOf.Construction, 4);
            K.SetSpeed(TimeSpeed.Normal);
            Teleport(p, wall.Position + new IntVec3(0, 0, -1));
            foreach (int w in EnterPlay(p)) yield return w;
            Enable(p, WorkTypeDefOf.Construction);
            Job[] job = new Job[1];
            foreach (int w in LeftClick(p, At(wall, 0.5f), h => h.Thing == wall, j => j.def == JobDefOf.Deconstruct && j.targetA.Thing == wall, "m4-deconstruct", job)) yield return w;
            foreach (int w in WaitTracked(p, job[0], "RemoveBuilding", "m4-deconstruct")) yield return w;
            K.Check("m4-deconstruct wall tally key", Entry(p).tallyKey == wall.thingIDNumber, "key " + Entry(p).tallyKey + ", wall " + wall.thingIDNumber);
            foreach (int w in AnswerAndCheck(p, FpOutcome.Great, "m4-deconstruct check", 0f)) yield return w;
            int[] answered = { 0 };
            foreach (int w in AnswerUntil(() => !OnJob(p, clickedJobId), new[] { FpOutcome.Hit }, "m4-deconstruct", answered)) yield return w;
            K.Check("m4-deconstruct removed", wall.Destroyed, wall.Destroyed ? "gone" : "still there; " + State(p));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m4-levers

        // Every M4 lever from forced tallies on a colonist's current job (no play): ConstructSuccessChance's failure part
        // scales by the table under a chance of 1 and not at all from 1 up, MiningYield and PlantHarvestYield multiply by the
        // yield table, DrugHarvestYield gets the form once (through PlantHarvestYield, no part of its own), and no lever is
        // on a work speed or a combat stat.
        private static IEnumerator<int> Levers()
        {
            Pawn p = new[] { S.C1, S.C2 }.FirstOrDefault(c => c?.CurJob != null && c.skills != null);
            if (p == null)
            {
                yield return Fail("m4-levers pawn", "neither C1 nor C2 has a job to hang a tally on");
                yield break;
            }
            FpTuningDef T = FpTuning.Get;
            SkillRecord con = p.skills.GetSkill(SkillDefOf.Construction);
            int con0 = con.Level;
            if (!con.TotallyDisabled)
            {
                con.Level = 3;
                LeverLine("m4-levers", p, "ConstructSuccessChance", T.constructFail, true);
                con.Level = 20;
                float top = p.GetStatValue(StatDefOf.ConstructSuccessChance);
                float topBad = WithForm(p, StatDefOf.ConstructSuccessChance, -4);
                K.Check("m4-levers no failure part at a chance of 1", top < 1f || Mathf.Abs(topBad - top) <= 1e-6f,
                    "level 20: " + top.ToString("0.0000") + ", form -4 " + topBad.ToString("0.0000"));
                con.Level = con0;
            }
            else
            {
                K.Info("m4-levers: " + p.LabelShort + " can't construct; ConstructSuccessChance not checked");
            }
            SkillRecord mining = p.skills.GetSkill(SkillDefOf.Mining);
            int mining0 = mining.Level;
            mining.Level = Math.Min(mining0, 8);   // under the 1.25 cap at form +4
            LeverLine("m4-levers", p, "MiningYield", T.yield, false);
            mining.Level = mining0;
            SkillRecord plants = p.skills.GetSkill(SkillDefOf.Plants);
            int plants0 = plants.Level;
            plants.Level = Math.Min(plants0, 8);
            LeverLine("m4-levers", p, "PlantHarvestYield", T.yield, false);
            StatDef drug = StatDefOf.DrugHarvestYield;
            float drug0 = p.GetStatValue(drug);
            float drugGood = WithForm(p, drug, 4);
            float drugBad = WithForm(p, drug, -4);
            K.Check("m4-levers DrugHarvestYield has no part of its own", drug.parts == null || !drug.parts.Any(x => x is StatPart_FpForm), "parts " + (drug.parts?.Count ?? 0));
            K.Check("m4-levers DrugHarvestYield takes the form once", drug0 > 0f && Mathf.Abs(drugBad / drug0 - FpTuningDef.Table(T.yield, -4)) <= 1e-4f
                && (drug0 * FpTuningDef.Table(T.yield, 4) > drug.maxValue || Mathf.Abs(drugGood / drug0 - FpTuningDef.Table(T.yield, 4)) <= 1e-4f),
                "base " + drug0.ToString("0.0000") + ", form -4 " + drugBad.ToString("0.0000") + ", +4 " + drugGood.ToString("0.0000"));
            plants.Level = plants0;
            string[] never = { "WorkSpeedGlobal", "ConstructionSpeed", "MiningSpeed", "PlantWorkSpeed", "SmoothingSpeed", "MedicalOperationSpeed",
                "ShootingAccuracyPawn", "AimingDelayFactor", "MeleeHitChance", "MoveSpeed" };
            string touched = string.Join(", ", never.Where(n => DefDatabase<StatDef>.GetNamedSilentFail(n)?.parts?.Any(x => x is StatPart_FpForm) == true));
            K.Check("m4-levers no speed or combat lever", touched.Length == 0, touched.Length == 0 ? "none of " + never.Length + " stats" : "levers on " + touched);
            K.Check("m4-levers no tally left", FpRegistry.TallyForWorker(p) == null, FpState.Describe(p));
            K.Info("m4-levers adapters: " + FpAdapters.Names);
            K.Check("m4-levers every adapter on", new[] { "Bill", "Construct", "Mine", "PlantWork", "Sow", "Repair", "AffectFloor", "SmoothWall", "RemoveBuilding", "ExtractTree", "PlantSeed", "FixBrokenDown" }.All(FpAdapters.Has),
                FpAdapters.Names);
            yield return K.Done();
        }

        // ---------------------------------------------------------------- mp4-work

        // The kit at offsets from the doer's own cell in the scene (C1's is the origin, C2's C2At): three granite rocks west,
        // east and south of it and a stool frame (wood, everything delivered) north of it, all within arm's reach.
        private static readonly IntVec3[] RocksAt = { new IntVec3(-1, 0, 0), new IntVec3(1, 0, 0), new IntVec3(0, 0, -1) };
        private static readonly IntVec3 StoolAt = new IntVec3(0, 0, 1);

        private static readonly WorkTypeDef[] KitWork = { WorkTypeDefOf.Mining, WorkTypeDefOf.Construction };

        private static readonly List<Thing> KitRocks = new List<Thing>();
        private static Frame KitStool;

        // The doer and its cell, alike on both machines: the first of C2 and C1 who can mine and construct.
        private static Pawn KitDoer(out IntVec3 home)
        {
            Pawn p = new[] { S.C2, S.C1 }.FirstOrDefault(c => c != null && KitWork.All(w => !c.WorkTypeIsDisabled(w)));
            home = p == S.C1 ? S.Origin : S.Origin + S.C2At;
            return p;
        }

        // The host, with its scene, before hosting: the rocks, the frame, the doer's skills and its mining (all saved, so the
        // client's join carries them). Mining only: an idle colonist mines nothing undesignated, but with construction
        // assigned it would build the stool frame while the host waits for the client; the client assigns construction
        // (a synced work priority) when it gets to the frame.
        private static bool SpawnWorkKit(Map map, out string error)
        {
            error = null;
            Pawn p = KitDoer(out IntVec3 home);
            if (p == null)
            {
                error = "neither C2 nor C1 can mine and construct";
                return false;
            }
            S.EnableWork(p, WorkTypeDefOf.Mining);
            SetLevel(p, SkillDefOf.Mining, 8);
            SetLevel(p, SkillDefOf.Construction, 10);
            KitRocks.Clear();
            foreach (IntVec3 off in RocksAt)
            {
                KitRocks.Add(GenSpawn.Spawn(ThingMaker.MakeThing(ThingDef.Named("Granite")), home + off, map));
            }
            ThingDef stool = ThingDef.Named("Stool");
            Frame frame = (Frame)ThingMaker.MakeThing(stool.frameDef, ThingDefOf.WoodLog);
            frame.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(frame, home + StoolAt, map, Rot4.North);
            foreach (ThingDefCountClass need in stool.CostListAdjusted(ThingDefOf.WoodLog))
            {
                Thing res = ThingMaker.MakeThing(need.thingDef);
                res.stackCount = need.count;
                frame.resourceContainer.TryAdd(res);
            }
            KitStool = frame;
            K.Info("mp4-work kit for " + p.LabelShort + " at " + home + ": rocks " + string.Join(", ", KitRocks.Select(r => r.Position.ToString())) + ", stool frame " + frame.Position);
            return true;
        }

        // After a reload (hosting) or on the client: the kit found again by its cells.
        private static bool FindWorkKit(Map map, out Pawn doer, out string error)
        {
            error = null;
            doer = KitDoer(out IntVec3 home);
            KitRocks.Clear();
            KitStool = null;
            if (doer == null)
            {
                error = "neither C2 nor C1 can mine and construct";
                return false;
            }
            foreach (IntVec3 off in RocksAt)
            {
                Thing r = (home + off).GetThingList(map).FirstOrDefault(t => t.def.defName == "Granite");
                if (r != null)
                {
                    KitRocks.Add(r);
                }
            }
            KitStool = (home + StoolAt).GetThingList(map).OfType<Frame>().FirstOrDefault();
            if (KitRocks.Count != RocksAt.Length || KitStool == null)
            {
                error = "the kit isn't at " + home + " (" + KitRocks.Count + " rocks, stool frame " + (KitStool != null) + ")";
                return false;
            }
            return true;
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

        // The client mines the three rocks and builds the stool in first person, answering every ring in the pattern
        // Great, Hit, Miss, Hit, none; the host only watches. The runner's compare then checks both players' state lines
        // (job, work progress, check and tally fields) are equal, and no desync file appeared.
        private static IEnumerator<int> MpWork()
        {
            if (K.IsHost)
            {
                yield return K.WaitUntil(() => MpBridge.InSession, "for hosting", 3600);
                yield return K.WaitUntil(() => Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null
                    && !LongEventHandler.AnyEventNowOrWaiting, "for the reload to finish", 7200);
                K.Check("scene found again", S.Adopt(Find.CurrentMap, out string error), error ?? S.Describe());
                K.Check("mp4-work kit found again", FindWorkKit(Find.CurrentMap, out Pawn _, out string kitError), kitError ?? "kit at its cells");
                yield return K.WaitUntil(() => Players >= 2, "for the client to join", 18000);
                yield return K.WaitUntil(() => Players < 2, "for the client to finish", 90000);
                yield return 120;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                if (!FindWorkKit(Find.CurrentMap, out Pawn p, out string error))
                {
                    yield return Fail("mp4-work kit", error);
                    yield break;
                }
                foreach (int w in EnterPlay(p)) yield return w;
                yield return K.WaitUntil(() => MP.InInterface, "for Multiplayer to take commands", 1800);
                // Local: the kit is next to the doer's cell in the scene, but the colonist may have stepped off it.
                bool anyDistance0 = FpSettings.interactAnyDistance;
                FpSettings.interactAnyDistance = true;
                byte[] pattern = { FpOutcome.Great, FpOutcome.Hit, FpOutcome.Miss, FpOutcome.Hit, FpOutcome.MissTimeout };
                List<Thing> targets = new List<Thing> { KitRocks[0], KitStool, KitRocks[1], KitRocks[2] };
                int total = 0;
                int[] answered = { 0 };
                Job[] job = new Job[1];
                foreach (Thing t in targets)
                {
                    Thing target = t;
                    bool frame = target is Frame;
                    string name = "mp4-work " + (frame ? "build " : "mine ") + target.Position;
                    if (frame && p.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                    {
                        S.EnableWork(p, WorkTypeDefOf.Construction);   // in a session the game's synced SetPriority: a command
                        yield return K.WaitUntil(() => p.workSettings.GetPriority(WorkTypeDefOf.Construction) > 0, "for construction to be assigned", 1800);
                    }
                    foreach (int w in LeftClick(p, At(target, frame ? 0.05f : 0.5f), h => h.Thing == target,
                        j => j.targetA.Thing == target && (frame ? j.def == JobDefOf.FinishFrame : j.def == JobDefOf.Mine), name, job)) yield return w;
                    if (job[0] == null || job[0].targetA.Thing != target)
                    {
                        continue;
                    }
                    foreach (int w in AnswerUntil(() => !OnJob(p, clickedJobId), pattern, name, answered)) yield return w;
                    total += answered[0];
                    K.Check(name + " done", target.Destroyed, (target.Destroyed ? "gone" : "still there") + ", " + answered[0] + " checks answered; " + State(p));
                    yield return K.WaitUntil(() => InControlJob(p), "for the control job", 900);
                }
                FpSettings.interactAnyDistance = anyDistance0;
                K.Check("mp4-work checks answered", total >= 4, total + " checks answered over " + targets.Count + " pieces");
                yield return K.WaitTicks(600);   // more state lines while nothing runs
                foreach (int w in LeavePlay()) yield return w;
                yield return K.WaitTicks(120);
                yield return K.Done();
            }
            else
            {
                yield return Fail("mp4-work role", "needs --mp (host and client)");
            }
        }
    }
}
