using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim.Fp
{
    // Track D (fp-play-spec 11.1-11.3): work at a bench from first person. The left button on one of the colony's benches
    // opens the recipe picker (Dialog_FpRecipes, local); a choice there goes out as one of two commands, which run on every
    // machine at the same tick with the issuer's faction pushed:
    // - FpStartRecipe makes a bill for the played colonist alone (restricted to them, ingredients within 20 cells, 1 or 5
    //   pieces), puts it first on the bench, and gives the colonist the game's own bill job for exactly that bill. Every
    //   bill made this way is listed in the map's FpMapComp.fpBills (saved), and cleaned up here: a finished one is
    //   deleted once its worker is off it, an unfinished one stays as an ordinary restricted bill when its colonist is
    //   released (the AI finishes it).
    // - FpWorkBill gives the colonist the job for a bill already on the bench ("Continue").
    // The ingredient search, the job and every id are worked out inside the command on every machine.
    //
    // Command bodies and the clean-up are simulation code: synced state only, no Rand, no settings (spec 2). The picker
    // test and hint (TryOpenPicker, PickerHint) and the senders are interface code: they never change the game.
    public static class FpBills
    {
        // The issuer's last refusal key, for the self-test only (never read by the simulation).
        internal static string LastToldKey;

        private static readonly Color RefusedColour = new Color(0.878f, 0.353f, 0.353f);   // #E05A5A, the HUD's

        // ---------------------------------------------------------------- interface

        // The left button on an own bill giver (spec 11.1, 8.3): opens the picker. False leaves the click to the next
        // handler (work orders, the float menu).
        public static bool TryOpenPicker(Pawn p, FpHit hit)
        {
            Building bench = PickerBench(p, hit);
            if (bench == null || Find.WindowStack == null)
            {
                return false;
            }
            AcceptanceReport allowed = FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(p);
            if (!allowed.Accepted)
            {
                // The game's own gate for orders (a lord that forbids them, deathrest): its reason, as FpActions shows it.
                if (!allowed.Reason.NullOrEmpty())
                {
                    FpHud.Toast(allowed.Reason, RefusedColour);
                }
                return true;
            }
            Find.WindowStack.Add(new Dialog_FpRecipes(p, bench));
            FpHits.Hit("Recipe picker opened");
            return true;
        }

        // "Use electric stove…" or null.
        public static string PickerHint(Pawn p, FpHit hit)
        {
            Building bench = PickerBench(p, hit);
            return bench == null ? null : (string)"R3D_Fp_UseBench".Translate(bench.LabelShort);
        }

        // The bench the left button would open the picker on (spec 11.1): a spawned building of this player's faction
        // that takes bills and has recipes, within reach (or anywhere with the "any distance" option). A drafted colonist
        // gets the left button's other uses (firing, the float menu): the game gives drafted colonists no bench work.
        internal static Building PickerBench(Pawn p, FpHit hit)
        {
            if (p == null || !hit.Valid || !(hit.Thing is Building b) || !(b is IBillGiver) || !b.Spawned || b.Map != p.Map)
            {
                return null;
            }
            if (b.def.AllRecipes.NullOrEmpty() || b.Faction == null || b.Faction != MpBridge.RealPlayerFaction || p.Drafted)
            {
                return null;
            }
            if (!hit.InReach && !FpSettings.interactAnyDistance)
            {
                return null;
            }
            return b;
        }

        public static void SendStartRecipe(Pawn p, Thing bench, RecipeDef r, int count)
        {
            TrySendStartRecipe(p, bench, r, count);
        }

        public static void SendWorkBill(Pawn p, Bill bill)
        {
            TrySendWorkBill(p, bill);
        }

        // The senders proper: false when the command didn't go out (Multiplayer takes no command during a long event, or
        // this runs in a tick), so the picker can say so instead of closing on nothing.
        internal static bool TrySendStartRecipe(Pawn p, Thing bench, RecipeDef r, int count)
        {
            if (MpBridge.InSession && !MP.InInterface)
            {
                return false;
            }
            if (p == null || bench == null || r == null || count != 1 && count != 5)
            {
                return false;
            }
            FpStartRecipe(p, bench, r, count);
            return true;
        }

        internal static bool TrySendWorkBill(Pawn p, Bill bill)
        {
            if (MpBridge.InSession && !MP.InInterface)
            {
                return false;
            }
            if (p == null || bill == null)
            {
                return false;
            }
            FpWorkBill(p, bill);
            return true;
        }

        // ---------------------------------------------------------------- commands

        // Spec 11.2. The queue key's state travels with the command (QueueOrder_Down): the game's TryTakeOrderedJob reads
        // it, and read live in a tick it would be whatever this machine's player holds at that moment.
        [SyncMethod(SyncContext.QueueOrder_Down, cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpStartRecipe(Pawn p, Thing bench, RecipeDef recipe, int count)
        {
            int me = FpIds.Executing;
            string result = "-";
            try
            {
                result = StartRecipe(p, bench, recipe, count, me);
            }
            finally
            {
                if (FpTestKit.Active)
                {
                    FpTestKit.OnCommand(me, "FpStartRecipe p=" + p.thingIDNumber + " bench=" + bench.thingIDNumber + " recipe=" + recipe.defName
                        + " count=" + count + " by=" + me + " -> " + result);
                }
            }
        }

        // The colonist works on a bill already on a bench ("Continue" in the picker).
        [SyncMethod(SyncContext.QueueOrder_Down, cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpWorkBill(Pawn p, Bill bill)
        {
            int me = FpIds.Executing;
            string result = "-";
            try
            {
                result = WorkBill(p, bill, me);
            }
            finally
            {
                if (FpTestKit.Active)
                {
                    FpTestKit.OnCommand(me, "FpWorkBill p=" + p.thingIDNumber + " bill=" + bill.GetUniqueLoadID() + " by=" + me + " -> " + result);
                }
            }
        }

        // FpStartRecipe's body; returns what happened, for the self-test's command line.
        private static string StartRecipe(Pawn p, Thing bench, RecipeDef recipe, int count, int me)
        {
            if (!Authorized(p, me))
            {
                FpHits.Hit("Bill refused");
                return "unauthorized";
            }
            if (count != 1 && count != 5)
            {
                FpHits.Hit("Bill refused");
                return "bad count";
            }
            if (!(bench is IBillGiver giver) || !bench.Spawned || bench.Map != p.Map || bench.Faction != Faction.OfPlayer)
            {
                return Refuse(p, bench, "R3D_Fp_BillBenchGone", bench.LabelShort);
            }
            if (!bench.def.AllRecipes.Contains(recipe) || !recipe.AvailableNow || !recipe.AvailableOnNow(bench))
            {
                return Refuse(p, bench, "R3D_Fp_BillUnavailable", recipe.LabelCap, bench.LabelShort);
            }
            if (giver.BillStack.Count >= FpTuning.Get.maxBillStack)
            {
                return Refuse(p, bench, "R3D_Fp_BillStackFull", bench.LabelShort, giver.BillStack.Count);
            }
            string why = RecipeRefusal(p, bench, recipe, out WorkGiverDef wg, out string whyKey) ?? ReachRefusal(p, bench, wg, out whyKey);
            if (why != null)
            {
                return RefuseText(p, bench, whyKey, why);
            }

            // The bill: this colonist only, its ingredients near the bench, first on the stack so the work giver below
            // takes it before any other.
            if (!(recipe.MakeNewBill() is Bill_Production bill))
            {
                return Refuse(p, bench, "R3D_Fp_BillUnavailable", recipe.LabelCap, bench.LabelShort);
            }
            bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
            bill.repeatCount = count;
            bill.SetPawnRestriction(p);
            bill.ingredientSearchRadius = FpTuning.Get.ingredientRadius;
            BillStack stack = giver.BillStack;
            stack.AddBill(bill);
            int at = stack.IndexOf(bill);
            if (at > 0)
            {
                stack.Reorder(bill, -at);
            }
            FpMapComp comp = FpMapComp.For(p.Map);
            comp?.fpBills.Add(bill);

            Job job = BillJob(p, bench, wg, out string reason);
            if (job == null || job.bill != bill)
            {
                // Worked out before the bill goes: the message names its radius.
                string text = NoJobText(p, bench, bill, job, reason, out string key);
                Discard(job);
                stack.Delete(bill);
                comp?.fpBills.Remove(bill);
                return RefuseText(p, bench, key, text);
            }
            ForfeitBefore(p, job);
            if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc))
            {
                stack.Delete(bill);
                comp?.fpBills.Remove(bill);
                return Refuse(p, bench, "R3D_Fp_BillCantStart", p.LabelShort, recipe.label);
            }
            FpHits.Hit("Bill started");
            return "started " + bill.GetUniqueLoadID() + " job " + job.loadID;
        }

        // FpWorkBill's body (spec 9.2): the bill must still be on its bench, open to this colonist, within their skill.
        private static string WorkBill(Pawn p, Bill bill, int me)
        {
            if (!Authorized(p, me))
            {
                FpHits.Hit("Bill refused");
                return "unauthorized";
            }
            BillStack stack = bill.billStack;
            Thing bench = stack?.billGiver as Thing;
            if (bench == null || !(bench is IBillGiver giver) || giver.BillStack != stack || stack.IndexOf(bill) < 0 || bill.DeletedOrDereferenced)
            {
                return Refuse(p, null, "R3D_Fp_BillGone");
            }
            if (!bench.Spawned || bench.Map != p.Map || bench.Faction != Faction.OfPlayer)
            {
                return Refuse(p, bench, "R3D_Fp_BillBenchGone", bench.LabelShort);
            }
            if (bill.PawnRestriction != null && bill.PawnRestriction != p)
            {
                return Refuse(p, bench, "R3D_Fp_BillRestricted", bill.PawnRestriction.LabelShort);
            }
            string notAllowed = NotAllowedReason(p, bill);
            if (notAllowed != null)
            {
                return RefuseText(p, bench, "R3D_Fp_BillNotAllowed", notAllowed);
            }
            string why = RecipeRefusal(p, bench, bill.recipe, out WorkGiverDef wg, out string whyKey) ?? ReachRefusal(p, bench, wg, out whyKey);
            if (why != null)
            {
                return RefuseText(p, bench, whyKey, why);
            }

            // The work giver takes the first bill on the stack it can do: this one is moved first for the call and put back
            // where it was after (only the job is wanted, not a new order of the bills).
            if (bill is Bill_ProductionWithUft uftBill && uftBill.BoundUft != null)
            {
                // Settled before the work giver looks: the game unbinds an unfinished piece whose maker no longer has the
                // work type active (BoundWorker's getter does it) and then passes over the bill once, so "Continue" on a
                // piece this colonist started would fail the first time and work the second. Unbound first, the work
                // giver finds the colonist's own piece by its maker (ClosestUnfinishedThingForBill) in the same call.
                _ = uftBill.BoundWorker;
            }
            int was = stack.IndexOf(bill);
            if (was > 0)
            {
                stack.Reorder(bill, -was);
            }
            Job job;
            string reason;
            try
            {
                job = BillJob(p, bench, wg, out reason);
            }
            finally
            {
                int now = stack.IndexOf(bill);
                int back = System.Math.Min(was, stack.Count - 1);
                if (now >= 0 && back >= 0 && now != back)
                {
                    stack.Reorder(bill, back - now);
                }
            }
            if (job == null || job.bill != bill)
            {
                string text = NoJobText(p, bench, bill, job, reason, out string key);
                Discard(job);
                return RefuseText(p, bench, key, text);
            }
            ForfeitBefore(p, job);
            if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc))
            {
                return Refuse(p, bench, "R3D_Fp_BillCantStart", p.LabelShort, bill.recipe.label);
            }
            FpHits.Hit("Bill continued");
            return "working " + bill.GetUniqueLoadID() + " job " + job.loadID;
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

        // The work giver's job on the bench, worked out as the game's own "Prioritize" order does it (FloatMenuMakerMap
        // sets makingFor round JobOnThing): with makingFor set, WorkGiver_DoBill records why a bill can't be done
        // (missing materials) and doesn't push back the AI's next ingredient search of the bills it passes over. The
        // reason comes back for the issuer's message.
        private static Job BillJob(Pawn p, Thing bench, WorkGiverDef wg, out string reason)
        {
            Pawn makingFor = FloatMenuMakerMap.makingFor;
            JobFailReason.Clear();
            Job job;
            try
            {
                FloatMenuMakerMap.makingFor = p;
                job = ((WorkGiver_Scanner)wg.Worker).JobOnThing(p, bench, forced: true);
            }
            finally
            {
                FloatMenuMakerMap.makingFor = makingFor;
            }
            reason = JobFailReason.HaveReason ? JobFailReason.Reason : null;
            JobFailReason.Clear();
            return job;
        }

        // Why the work giver gave no job for the bill (spec 11.2 step 4): no ingredients within the radius, unless the game
        // wants another job done first (refuelling, clearing the bench) or the bench is taken. A DoBill job for another
        // bill on the bench means this one was passed over, nearly always for its ingredients (the work giver tries every
        // bill in turn; JobFailReason keeps why this one failed). Core job defs have no label, only a report string.
        private static string NoJobText(Pawn p, Thing bench, Bill bill, Job job, string reason, out string key)
        {
            if (job != null && job.def != null && job.def != JobDefOf.DoBill)
            {
                if (job.def == JobDefOf.Refuel || job.def == JobDefOf.RefuelAtomic)
                {
                    key = "R3D_Fp_BillNeedsFuel";
                    return key.Translate(bench.LabelShort);
                }
                if (job.def == JobDefOf.HaulToCell)
                {
                    key = "R3D_Fp_BillClearBench";
                    return key.Translate(bench.LabelShort);
                }
                key = "R3D_Fp_BillOtherJob";
                return key.Translate(p.LabelShort, JobText(job));
            }
            if (!p.CanReserve(bench, 1, -1, null, true))
            {
                Pawn other = p.Map.reservationManager.FirstRespectedReserver(bench, p);
                key = "R3D_Fp_BillBenchInUse";
                return key.Translate(bench.LabelShort, other != null ? other.LabelShort : "?");
            }
            key = "R3D_Fp_NoIngredients";
            string text = key.Translate(p.LabelShort, bill.recipe.label,
                bill.ingredientSearchRadius.ToString("0", CultureInfo.InvariantCulture), bench.LabelShort);
            return reason.NullOrEmpty() ? text : text + " (" + reason + ")";
        }

        // "hauling steel": a job's report string ("hauling TargetA.") with its targets named, for the message only.
        private static string JobText(Job job)
        {
            string s = job.def.reportString;
            if (s.NullOrEmpty())
            {
                return job.def.defName;
            }
            s = s.Replace("TargetA", job.targetA.Thing?.LabelShort ?? job.targetA.ToString())
                .Replace("TargetB", job.targetB.Thing?.LabelShort ?? job.targetB.ToString());
            return s.TrimEnd('.', ' ');
        }

        // A job the work giver made and the command doesn't use goes back to the game's pool (as unused think-tree jobs
        // do). Its id was taken all the same, on every machine alike.
        private static void Discard(Job job)
        {
            if (job != null)
            {
                JobMaker.ReturnToPool(job);
            }
        }

        // Spec 10.4: the colonist's new job replaces one with a skill check showing, which then counts as missed (taking
        // another bill doesn't escape a bad check). Not when the game would keep the current job (the same job again).
        private static void ForfeitBefore(Pawn p, Job job)
        {
            if (p.CurJob != null && !p.CurJob.JobIsSameAs(p, job))
            {
                FpWork.ForfeitPending(FpRegistry.EntryOf(p));
            }
        }

        // Why the game wouldn't let this colonist start the bill anew (Bill.PawnAllowedToStartAnew: slaves or mechs only,
        // the bill's allowed skill range, a mechanitor's recipe), or null. A restricted bill isn't asked: the restriction
        // itself is tested by the caller. The work giver passes over a bill that fails this, so the command and the
        // picker test it first. The game's own reason (JobFailReason) is read and cleared again.
        internal static string NotAllowedReason(Pawn p, Bill bill)
        {
            if (bill.PawnRestriction != null)
            {
                return null;
            }
            JobFailReason.Clear();
            bool allowed = bill.PawnAllowedToStartAnew(p);
            string reason = JobFailReason.HaveReason ? JobFailReason.Reason : null;
            JobFailReason.Clear();
            if (allowed)
            {
                return null;
            }
            return reason.NullOrEmpty() ? (string)"R3D_Fp_BillNotAllowed".Translate(p.LabelShort)
                : (string)"R3D_Fp_BillNotAllowedBecause".Translate(p.LabelShort, reason);
        }

        // ---------------------------------------------------------------- shared tests (simulation and picker)

        // Why this colonist can't make this recipe at this bench now, or null; also the work giver that would do it. The
        // picker greys a recipe for the same reasons the command refuses it. Reads only; no reachability (the picker
        // must not fill the game's reachability cache, so the command checks that on its own).
        internal static string RecipeRefusal(Pawn p, Thing bench, RecipeDef recipe, out WorkGiverDef wg, out string key)
        {
            wg = null;
            key = null;
            if (p.Drafted)
            {
                key = "R3D_Fp_BillDrafted";
                return key.Translate(p.LabelShort);
            }
            if (bench is IBillGiver giver && !giver.UsableForBillsAfterFueling())
            {
                key = "R3D_Fp_BillUnusable";
                return key.Translate(bench.LabelShort);
            }
            SkillRequirement skill = recipe.FirstSkillRequirementPawnDoesntSatisfy(p);
            if (skill != null)
            {
                key = "R3D_Fp_BillSkill";
                return key.Translate(p.LabelShort, skill.Summary);
            }
            if (ModsConfig.BiotechActive && recipe.mechanitorOnlyRecipe && !MechanitorUtility.IsMechanitor(p))
            {
                // A bill restricted to one colonist skips the game's own mechanitor test (Bill.PawnAllowedToStartAnew).
                key = "R3D_Fp_BillMechanitor";
                return key.Translate(p.LabelShort);
            }
            wg = GiverFor(p, bench.def, recipe, out string refusal, out key);
            return wg == null ? refusal : null;
        }

        // Spec 11.2 step 2: a bill work giver for this bench and the recipe's work type, in def order. The first one this
        // colonist can do is taken (the spec takes the first and refuses when the colonist can't do it; for one giver per
        // bench, as in the Core defs, that is the same). Null, and the first one's reason, when none fits.
        internal static WorkGiverDef GiverFor(Pawn p, ThingDef benchDef, RecipeDef recipe, out string refusal, out string key)
        {
            refusal = null;
            key = null;
            List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                WorkGiverDef wg = all[i];
                if (wg.fixedBillGiverDefs == null || !wg.fixedBillGiverDefs.Contains(benchDef) || !(wg.Worker is WorkGiver_DoBill))
                {
                    continue;
                }
                if (recipe.requiredGiverWorkType != null && wg.workType != recipe.requiredGiverWorkType)
                {
                    continue;
                }
                string whyKey = null;
                string why = null;
                if (wg.workType != null && p.WorkTypeIsDisabled(wg.workType))
                {
                    whyKey = "R3D_Fp_BillIncapable";
                    why = whyKey.Translate(p.LabelShort, wg.workType.gerundLabel);
                }
                else
                {
                    PawnCapacityDef cap = wg.Worker.MissingRequiredCapacity(p);
                    if (cap != null)
                    {
                        whyKey = "R3D_Fp_BillCapacity";
                        why = whyKey.Translate(p.LabelShort, cap.label);
                    }
                }
                if (why == null)
                {
                    key = null;
                    refusal = null;
                    return wg;
                }
                if (refusal == null)
                {
                    refusal = why;
                    key = whyKey;
                }
            }
            if (refusal == null)
            {
                key = "R3D_Fp_BillNoGiver";
                refusal = key.Translate(benchDef.label);
            }
            return null;
        }

        // Simulation only: the colonist can walk to the bench.
        private static string ReachRefusal(Pawn p, Thing bench, WorkGiverDef wg, out string key)
        {
            PathEndMode mode = wg?.Worker is WorkGiver_Scanner scanner ? scanner.PathEndMode : PathEndMode.InteractionCell;
            if (p.CanReach(bench, mode, Danger.Deadly))
            {
                key = null;
                return null;
            }
            key = "R3D_Fp_BillNoPath";
            return key.Translate(p.LabelShort, bench.LabelShort);
        }

        // ---------------------------------------------------------------- clean-up (spec 11.3)

        // Every 60 ticks of the map, in the map's context, the list in order.
        internal static void Tick(FpMapComp comp)
        {
            if (comp == null || comp.fpBills.Count == 0)
            {
                return;
            }
            foreach (Bill bill in comp.fpBills.ToArray())   // a copy: the clean-up removes from the list
            {
                if (CleanUp(comp, bill, null))
                {
                    comp.fpBills.Remove(bill);
                }
            }
        }

        // The same rules at once for the bills of a colonist being released (before its entry goes): that colonist counts
        // as uncontrolled already.
        internal static void OnRelease(FpMapComp comp, FpEntry e)
        {
            Pawn p = e?.pawn;
            if (comp == null || p == null || comp.fpBills.Count == 0)
            {
                return;
            }
            foreach (Bill bill in comp.fpBills.ToArray())
            {
                if (bill != null && bill.PawnRestriction == p && CleanUp(comp, bill, p))
                {
                    comp.fpBills.Remove(bill);
                }
            }
        }

        // True when the bill leaves the list. A finished bill (no pieces left) is deleted from its bench once its worker
        // is off it; an unfinished one leaves the list only, once its colonist is no longer controlled: it stays on the
        // bench as an ordinary restricted bill, which the AI finishes. A bill that stopped being first person's (another
        // player changed its repeat mode, the bench went to another map) leaves the list as it is.
        private static bool CleanUp(FpMapComp comp, Bill bill, Pawn releasing)
        {
            if (bill == null || bill.billStack == null || bill.DeletedOrDereferenced)
            {
                return true;
            }
            Thing bench = bill.billStack.billGiver as Thing;
            if (bench == null || !bench.Spawned || bench.Map != comp.map)
            {
                return true;   // not this map's to change any more
            }
            if (!(bill is Bill_Production bp) || bp.repeatMode != BillRepeatModeDefOf.RepeatCount)
            {
                return true;
            }
            Pawn owner = bill.PawnRestriction;
            if (bp.repeatCount <= 0)
            {
                if (owner?.CurJob?.bill == bill)
                {
                    return false;   // the last piece is still being finished
                }
                bill.billStack.Delete(bill);
                FpHits.Hit("Bill cleaned up");
                return true;
            }
            if (owner == null || owner == releasing)
            {
                return true;
            }
            FpEntry e = FpRegistry.EntryOf(owner);
            return e == null || e.controller == FpIds.None;
        }

        // ---------------------------------------------------------------- messages

        private static string Refuse(Pawn p, Thing bench, string key, params NamedArgument[] args)
        {
            TellIssuer(key, key.Translate(args), bench ?? (Thing)p);
            FpHits.Hit("Bill refused");
            return "refused " + key;
        }

        private static string RefuseText(Pawn p, Thing bench, string key, string text)
        {
            TellIssuer(key, text, bench ?? (Thing)p);
            FpHits.Hit("Bill refused");
            return "refused " + key;
        }

        // Only the player who sent the command sees why (historical false: the message archive is saved game state).
        private static void TellIssuer(string key, string text, Thing at)
        {
            if (MpBridge.InSession && !MP.IsExecutingSyncCommandIssuedBySelf)
            {
                return;
            }
            LastToldKey = key;
            Messages.Message(text, at != null && at.Spawned ? new LookTargets(at) : LookTargets.Invalid, MessageTypeDefOf.RejectInput, historical: false);
        }
    }
}
