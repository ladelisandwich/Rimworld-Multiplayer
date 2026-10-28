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
    // Milestone 2 scenarios (fp-play-spec 16, M2): the workbench (track D: the recipe picker, FpStartRecipe, FpWorkBill, the
    // bill clean-up) and AutoNeeds (track B). Solo scenarios run windowed with injected input; mp2-cook runs the host
    // headless and the client windowed, as the mp1-* ones do.
    //
    // The scene's stove is a FueledStove at +3 north of C1, filled up, with 20 raw potatoes at (+2, 0). C1 first walks
    // north until the stove stops it (its interaction cell, two cells up), so the stove is well within reach. A colonist
    // who can never cook (random -quicktest colonists) fails the scenario at once with that reason: rerun it.
    //
    // Additions to the spec's table, each a check the spec implies: m2-cook also checks that the picker frees the cursor
    // and that it is captured again after the choice; m2-15 also checks that the picker greys the recipes of a full bench
    // (the command's refusal is then tested through a direct send); m2-continue puts another bill first on the bench, so
    // "Continue" must pick the chosen bill and leave the bench's bill order as it was; m2-cancel also checks that the bill
    // leaves the first-person list once play ends but stays on the bench for the AI; m2-autoneeds also holds a movement
    // key while the autopilot eats (it must not cancel the meal). Four AutoNeeds scenarios track B asked for are added:
    // no food on the map, drafted, exhaustion, and the option off.
    internal static class FpTestM2
    {
        internal static void Register(Dictionary<string, Func<IEnumerator<int>>> s)
        {
            s["m2-cook"] = Cook;
            s["m2-noingr"] = NoIngredients;
            s["m2-15"] = Fifteen;
            s["m2-continue"] = Continue;
            s["m2-cancel"] = Cancel;
            s["m2-autoneeds"] = AutoNeeds;
            s["m2-autoneeds-nofood"] = AutoNeedsNoFood;
            s["m2-autoneeds-drafted"] = AutoNeedsDrafted;
            s["m2-autoneeds-sleep"] = AutoNeedsSleep;
            s["m2-autoneeds-off"] = AutoNeedsOff;
            s["mp2-cook"] = MpCook;
        }

        private const string ControlJob = "IsoRimFp_Control";

        // ---------------------------------------------------------------- helpers (as FpTestM1's)

        private static string JobOf(Pawn p)
        {
            Job j = p?.CurJob;
            return j == null ? "none" : j.def.defName + ":" + j.loadID + " " + j.targetA + (j.bill != null ? " bill " + j.bill.GetUniqueLoadID() : "");
        }

        private static bool InControlJob(Pawn p)
        {
            return p?.CurJob?.def?.defName == ControlJob;
        }

        private static bool Locked => Cursor.lockState == CursorLockMode.Locked && !Cursor.visible;

        private static bool Free => Cursor.lockState == CursorLockMode.None && Cursor.visible;

        private static string CursorText => "lockState " + Cursor.lockState + ", visible " + Cursor.visible;

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
            if (Picker != null)
            {
                Picker.Close(false);
                yield return 5;
            }
            if (FpPlay.Mode == FpMode.Playing || FirstPerson.On)
            {
                yield return K.Tap(Key.F5);
                yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Off && !FirstPerson.On, "for first person to end", 600);
            }
            yield return 10;
        }

        private static void Turn(float yaw, float pitch)
        {
            FpCamera.Look(FpMath.DeltaDeg(FpCamera.Yaw, yaw), pitch - FpCamera.Pitch);
        }

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

        private static List<Message> LiveMessages()
        {
            return HarmonyLib.AccessTools.Field(typeof(Messages), "liveMessages")?.GetValue(null) as List<Message> ?? new List<Message>();
        }

        // ---------------------------------------------------------------- bench helpers

        private static Dialog_FpRecipes Picker => Find.WindowStack?.WindowOfType<Dialog_FpRecipes>();

        private static BillStack StoveBills => ((IBillGiver)S.Stove).BillStack;

        private static FpMapComp Comp => FpMapComp.For(S.Stove.Map);

        private static RecipeDef SimpleMeal => DefDatabase<RecipeDef>.GetNamedSilentFail("CookMealSimple");

        private static ThingDef MealSimple => DefDatabase<ThingDef>.GetNamedSilentFail("MealSimple");

        // The first-person bill of this pawn on the stove (in the map's list), null if none.
        private static Bill FpBillOf(Pawn p)
        {
            FpMapComp comp = Comp;
            return StoveBills.Bills.FirstOrDefault(b => b.PawnRestriction == p && comp != null && comp.fpBills.Contains(b));
        }

        private static string BillText(Bill b)
        {
            if (b == null)
            {
                return "no bill";
            }
            Bill_Production bp = b as Bill_Production;
            return b.GetUniqueLoadID() + " at " + StoveBills.IndexOf(b) + ", for " + (b.PawnRestriction?.LabelShort ?? "anyone")
                + ", " + (bp != null ? bp.repeatMode.defName + " " + bp.repeatCount : b.GetType().Name) + ", radius " + b.ingredientSearchRadius
                + (b.deleted ? ", deleted" : "") + (Comp != null && Comp.fpBills.Contains(b) ? ", listed" : "");
        }

        // Fails the scenario's setup when the colonist can't cook at the stove (the command's own test: a colonist
        // incapable of cooking, or without the capacity for it).
        private static bool CanCook(Pawn p, string scenario)
        {
            string why = SimpleMeal == null || S.Stove == null ? "no CookMealSimple recipe or no stove"
                : !S.Stove.def.AllRecipes.Contains(SimpleMeal) ? "the stove has no CookMealSimple"
                : FpBills.RecipeRefusal(p, S.Stove, SimpleMeal, out _, out _);
            K.Check(scenario + " setup", why == null, why == null ? p.LabelShort + " can cook at " + S.Stove.LabelShort : why + " (random colonist: rerun)");
            return why == null;
        }

        // C1 walks north until the stove stops it (its interaction cell): the stove is then within reach.
        private static IEnumerable<int> WalkToStove(Pawn p)
        {
            Turn(0f, 8f);
            yield return 5;
            yield return K.HoldTicks(90, Key.W);
            yield return K.WaitTicks(20);
        }

        // Aims at the stove and clicks: the picker opens.
        private static IEnumerable<int> OpenPicker(string scenario)
        {
            foreach (int w in AimAt(At(S.Stove, 0.4f))) yield return w;
            yield return 30;   // past the click guard after capture, and the hint's slot
            K.Check(scenario + " aimed at the stove", FpPick.Current.Thing == S.Stove,
                "hit " + (FpPick.Current.Thing?.ThingID ?? "none") + ", in reach " + FpPick.Current.InReach + ", hint " + FpActions.Hint + " (" + FpActions.HintKind + ")");
            yield return K.Button(0, true);
            yield return K.Button(0, false);
            yield return K.WaitUntil(() => Picker != null, "for the recipe picker", 120);
            yield return 10;   // the cursor watchdog frees the cursor a frame later
        }

        // Chooses a recipe in the open picker: by its number key when it is in the first nine rows, else as a click.
        private static IEnumerable<int> ChooseRecipe(RecipeDef r, int count)
        {
            Dialog_FpRecipes picker = Picker;
            if (picker == null)
            {
                K.Check("choose " + r.defName, false, "no picker open");
                yield break;
            }
            Dialog_FpRecipes.CountChoice = count;
            int n = picker.NumberOf(r);
            K.Info("choosing " + r.defName + " x" + count + " (row " + n + (n > 0 ? ", " + (picker.RefusalOf(n) ?? "open") : "") + ")");
            if (n >= 1 && n <= 9)
            {
                yield return K.Tap((Key)((int)Key.Digit1 + n - 1));
            }
            else
            {
                picker.ChooseNumber(n);
            }
            yield return 5;
        }

        private static int MealsOnMap(Map map)
        {
            ThingDef meal = MealSimple;
            int n = map.listerThings.ThingsOfDef(meal).Sum(t => t.stackCount);
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                Thing carried = p.carryTracker?.CarriedThing;
                if (carried != null && carried.def == meal)
                {
                    n += carried.stackCount;
                }
            }
            return n;
        }

        // ---------------------------------------------------------------- M2 solo

        private static IEnumerator<int> Cook()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            if (!CanCook(S.C1, "m2-cook"))
            {
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
                yield break;
            }
            foreach (int w in WalkToStove(S.C1)) yield return w;
            foreach (int w in AimAt(At(S.Stove, 0.4f))) yield return w;
            yield return 30;
            string hint = FpBills.PickerHint(S.C1, FpPick.Current);
            K.Check("m2-cook hint", hint != null && FpActions.Hint == hint && FpActions.HintKind == FpHintKind.Action,
                "hint " + FpActions.Hint + " (" + FpActions.HintKind + "), picker hint " + hint);
            foreach (int w in OpenPicker("m2-cook")) yield return w;
            K.Check("m2-cook picker open, cursor free", Picker != null && Free, "picker " + (Picker != null) + ", " + CursorText);
            K.Shot("m2_picker");
            int tick0 = K.Ticks;
            foreach (int w in ChooseRecipe(SimpleMeal, 1)) yield return w;
            yield return K.WaitUntil(() => FpBillOf(S.C1) != null || K.Ticks > tick0 + 60, "for the bill");
            Bill bill = FpBillOf(S.C1);
            Bill_Production bp = bill as Bill_Production;
            K.Check("m2-cook bill made", bp != null && StoveBills.IndexOf(bill) == 0 && bill.PawnRestriction == S.C1
                && bp.repeatMode == BillRepeatModeDefOf.RepeatCount && bp.repeatCount == 1 && Mathf.Approximately(bill.ingredientSearchRadius, 20f),
                BillText(bill) + " after " + (K.Ticks - tick0) + " ticks");
            yield return K.WaitUntil(() => S.C1.CurJob?.def == JobDefOf.DoBill || K.Ticks > tick0 + 60, "for the bill job");
            K.Check("m2-cook bill job", bill != null && S.C1.CurJob?.def == JobDefOf.DoBill && S.C1.CurJob.bill == bill, JobOf(S.C1));
            yield return K.WaitUntil(() => Picker == null && Locked, "for the picker to close and the cursor to be captured", 120);
            K.Check("m2-cook picker closed, captured", Picker == null && Locked, CursorText);
            if (bill == null)
            {
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
                yield break;
            }

            int meals0 = MealsOnMap(S.Stove.Map);
            K.SetSpeed(TimeSpeed.Fast);
            yield return K.WaitUntil(() => MealsOnMap(S.Stove.Map) > meals0, "for the meal (at 3x)", 3600);
            K.Check("m2-cook meal made", MealsOnMap(S.Stove.Map) > meals0, "simple meals " + meals0 + " -> " + MealsOnMap(S.Stove.Map));
            yield return K.WaitUntil(() => S.C1.CurJob?.bill != bill, "for the bill job to end", 3600);
            int ended = K.Ticks;
            yield return K.WaitUntil(() => bill.deleted || K.Ticks > ended + 120, "for the bill's clean-up");
            K.Check("m2-cook bill deleted within 120 ticks", bill.deleted && StoveBills.IndexOf(bill) < 0 && !Comp.fpBills.Contains(bill),
                BillText(bill) + ", " + (K.Ticks - ended) + " ticks after the job ended, " + JobOf(S.C1));
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> NoIngredients()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            if (!CanCook(S.C1, "m2-noingr"))
            {
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
                yield break;
            }
            // Every simple-meal ingredient within reach of the bill's radius goes (the scene's potatoes and anything the
            // -quicktest colony keeps nearby).
            RecipeDef r = SimpleMeal;
            Map map = S.Stove.Map;
            int removed = 0;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).ToList())
            {
                if (!t.Destroyed && t.Spawned && t.Position.InHorDistOf(S.Stove.Position, 30f) && r.fixedIngredientFilter.Allows(t)
                    && r.ingredients.Any(i => i.filter.Allows(t)))
                {
                    t.Destroy(DestroyMode.Vanish);
                    removed++;
                }
            }
            K.Info("removed " + removed + " ingredient stacks");
            foreach (int w in WalkToStove(S.C1)) yield return w;
            foreach (int w in OpenPicker("m2-noingr")) yield return w;
            int bills0 = StoveBills.Count;
            Job job0 = S.C1.CurJob;
            FpBills.LastToldKey = null;
            int messages0 = LiveMessages().Count;
            foreach (int w in ChooseRecipe(r, 1)) yield return w;
            yield return K.WaitTicks(30);
            K.Check("m2-noingr no bill left", StoveBills.Count == bills0 && FpBillOf(S.C1) == null && Comp.fpBills.Count == 0,
                StoveBills.Count + " bills (" + bills0 + " before), listed " + Comp.fpBills.Count);
            K.Check("m2-noingr message", FpBills.LastToldKey == "R3D_Fp_NoIngredients" && LiveMessages().Count > messages0,
                "key " + (FpBills.LastToldKey ?? "none") + ", messages: " + string.Join(" | ", LiveMessages().Select(m => m.text)));
            K.Check("m2-noingr job unchanged", S.C1.CurJob == job0 && InControlJob(S.C1), JobOf(S.C1));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Fifteen()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            if (!CanCook(S.C1, "m2-15"))
            {
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
                yield break;
            }
            // Fifteen suspended bills: the bench is full, and none shows under "Continue".
            BillStack stack = StoveBills;
            while (stack.Count < 15)
            {
                Bill b = SimpleMeal.MakeNewBill();
                b.suspended = true;
                stack.AddBill(b);
            }
            foreach (int w in WalkToStove(S.C1)) yield return w;
            foreach (int w in OpenPicker("m2-15")) yield return w;
            Dialog_FpRecipes picker = Picker;
            int n = picker != null ? picker.NumberOf(SimpleMeal) : -1;
            string refusal = picker?.RefusalOf(n);
            K.Check("m2-15 picker greys the recipe", n > 0 && refusal != null, "row " + n + ": " + (refusal ?? "open"));
            FpBills.LastToldKey = null;
            foreach (int w in ChooseRecipe(SimpleMeal, 1)) yield return w;
            yield return K.WaitTicks(10);
            K.Check("m2-15 greyed row sends nothing", stack.Count == 15 && FpBills.LastToldKey == null, stack.Count + " bills, key " + (FpBills.LastToldKey ?? "none"));
            // The command's own refusal, past the picker.
            if (Picker != null)
            {
                Picker.Close(false);
                yield return 5;
            }
            FpBills.SendStartRecipe(S.C1, S.Stove, SimpleMeal, 1);
            yield return K.WaitTicks(10);
            K.Check("m2-15 refused, stack still 15", stack.Count == 15 && FpBills.LastToldKey == "R3D_Fp_BillStackFull" && FpBillOf(S.C1) == null,
                stack.Count + " bills, key " + (FpBills.LastToldKey ?? "none") + ", " + JobOf(S.C1));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Continue()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            if (!CanCook(S.C1, "m2-continue"))
            {
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
                yield break;
            }
            // An ordinary bill, as a player would add it in the bills tab, behind a suspended one: "Continue" must take the
            // chosen bill and leave the order as it was.
            BillStack stack = StoveBills;
            Bill first = SimpleMeal.MakeNewBill();
            first.suspended = true;
            stack.AddBill(first);
            Bill_Production target = (Bill_Production)SimpleMeal.MakeNewBill();
            target.repeatMode = BillRepeatModeDefOf.RepeatCount;
            target.repeatCount = 1;
            stack.AddBill(target);
            foreach (int w in WalkToStove(S.C1)) yield return w;
            foreach (int w in OpenPicker("m2-continue")) yield return w;
            K.Shot("m2_continue_picker");
            Dialog_FpRecipes picker = Picker;
            int n = picker != null ? picker.NumberOf(target) : -1;
            K.Check("m2-continue listed", n > 0 && picker.RefusalOf(n) == null, "row " + n + ": " + (n > 0 ? picker.RefusalOf(n) ?? "open" : "not listed"));
            if (n >= 1 && n <= 9)
            {
                yield return K.Tap((Key)((int)Key.Digit1 + n - 1));
            }
            else if (picker != null)
            {
                picker.ChooseNumber(n);
            }
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => S.C1.CurJob?.bill == target || K.Ticks > tick0 + 60, "for the bill job");
            K.Check("m2-continue DoBill on that bill", S.C1.CurJob?.def == JobDefOf.DoBill && S.C1.CurJob.bill == target, JobOf(S.C1));
            K.Check("m2-continue order kept", stack.IndexOf(first) == 0 && stack.IndexOf(target) == 1 && !Comp.fpBills.Contains(target),
                "first at " + stack.IndexOf(first) + ", target " + BillText(target));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        private static IEnumerator<int> Cancel()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            if (!CanCook(S.C1, "m2-cancel"))
            {
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
                yield break;
            }
            foreach (int w in WalkToStove(S.C1)) yield return w;
            foreach (int w in OpenPicker("m2-cancel")) yield return w;
            int tick0 = K.Ticks;
            foreach (int w in ChooseRecipe(SimpleMeal, 1)) yield return w;
            yield return K.WaitUntil(() => S.C1.CurJob?.def == JobDefOf.DoBill || K.Ticks > tick0 + 60, "for the bill job");
            Bill bill = FpBillOf(S.C1);
            K.Check("m2-cancel fetching", bill != null && S.C1.CurJob?.bill == bill, JobOf(S.C1));
            yield return K.WaitUntil(() => Locked, "for the cursor to be captured again", 120);
            yield return K.WaitTicks(5);
            // Walk away (south, the stove blocks north) for half a second: longer than the 300 ms that interrupts a job.
            Turn(180f, 8f);
            yield return 3;
            yield return K.HoldTicks(30, Key.W);
            yield return K.WaitUntil(() => InControlJob(S.C1) || K.Ticks > tick0 + 600, "for the control job");
            Bill_Production bp = bill as Bill_Production;
            K.Check("m2-cancel back in control", InControlJob(S.C1), JobOf(S.C1));
            K.Check("m2-cancel bill kept", bp != null && !bp.deleted && StoveBills.IndexOf(bp) >= 0 && bp.repeatCount == 1 && Comp.fpBills.Contains(bp), BillText(bill));
            yield return K.WaitTicks(130);   // two clean-up passes while controlled
            K.Check("m2-cancel bill kept while controlled", bp != null && !bp.deleted && Comp.fpBills.Contains(bp), BillText(bill));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.WaitTicks(70);
            K.Check("m2-cancel released: off the list, still on the bench", bp != null && !bp.deleted && StoveBills.IndexOf(bp) >= 0
                && bp.PawnRestriction == S.C1 && !Comp.fpBills.Contains(bp), BillText(bill));
            yield return K.Done();
        }

        // ---------------------------------------------------------------- AutoNeeds (track B, spec 5.3-5.4)

        // The jobs the game's food giver may start a hungry colonist on: eating, or fetching food from someone's inventory
        // or a plant first.
        private static bool FoodJob(Job j)
        {
            return j != null && (j.def == JobDefOf.Ingest || j.def == JobDefOf.TakeFromOtherInventory || j.def == JobDefOf.Harvest);
        }

        private static bool LyingDown(Job j)
        {
            return j?.def?.driverClass != null && typeof(JobDriver_LayDown).IsAssignableFrom(j.def.driverClass);
        }

        private static string Needs(Pawn p)
        {
            return "food " + (p.needs?.food?.CurLevelPercentage.ToString("0.00") ?? "-") + " (" + (p.needs?.food?.CurCategory.ToString() ?? "-")
                + "), rest " + (p.needs?.rest?.CurLevel.ToString("0.000") ?? "-") + " (" + (p.needs?.rest?.CurCategory.ToString() ?? "-") + ")";
        }

        // Watches the colonist for a number of ticks: true when the control job held all along.
        private static IEnumerable<int> StaysInControl(Pawn p, int ticks, Action<bool, string> result)
        {
            int end = K.Ticks + ticks;
            bool held = true;
            string lost = null;
            while (K.Ticks < end)
            {
                if (held && !InControlJob(p))
                {
                    held = false;
                    lost = JobOf(p) + " at tick " + K.Ticks;
                }
                yield return 1;
            }
            result(held, held ? JobOf(p) : lost);
        }

        // Urgent hunger hands the colonist to the autopilot, which eats (a movement key doesn't take them off it), then
        // gives control back.
        private static IEnumerator<int> AutoNeeds()
        {
            bool autoNeeds0 = FpSettings.autoNeeds;
            FpSettings.autoNeeds = true;
            foreach (int w in EnterPlay(S.C1)) yield return w;
            FpEntry e = FpRegistry.EntryOf(S.C1);
            K.Check("m2-autoneeds flag", e != null && (e.flags & FpFlags.AutoNeeds) != 0, "flags " + (e?.flags.ToString() ?? "no entry"));
            int given0 = FpHits.Count("AutoNeeds job given");
            S.C1.needs.food.CurLevelPercentage = 0.05f;
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => FoodJob(S.C1.CurJob) || K.Ticks > tick0 + 120, "for the eating job");
            K.Check("m2-autoneeds eats within 120 ticks", FoodJob(S.C1.CurJob) && FpHits.Count("AutoNeeds job given") > given0,
                JobOf(S.C1) + " after " + (K.Ticks - tick0) + " ticks, " + Needs(S.C1) + ", AutoNeeds jobs given " + (FpHits.Count("AutoNeeds job given") - given0));
            string reason = FpState.AutopilotReason(S.C1);
            K.Check("m2-autoneeds HUD shows autopilot", reason != null, reason ?? "no autopilot reason");
            yield return 20;   // the HUD rebuilds its status line at most 4 times a second (FpHud.ReportRefresh)
            K.Shot("m2_autoneeds");

            // Walking doesn't cancel the autopilot (FpSetIntent leaves its job alone).
            Job eating = S.C1.CurJob;
            yield return K.HoldTicks(30, Key.W);
            yield return K.WaitTicks(5);
            K.Check("m2-autoneeds walking waits", S.C1.CurJob == eating || FoodJob(S.C1.CurJob) && !InControlJob(S.C1),
                "was " + (eating == null ? "none" : eating.def.defName + ":" + eating.loadID) + ", now " + JobOf(S.C1));

            K.SetSpeed(TimeSpeed.Superfast);
            yield return K.WaitUntil(() => InControlJob(S.C1), "for control to come back after eating", 3600);
            K.Check("m2-autoneeds control back", InControlJob(S.C1) && FpState.AutopilotReason(S.C1) == null
                && S.C1.needs.food.CurCategory == HungerCategory.Fed, JobOf(S.C1) + ", " + Needs(S.C1));
            K.SetSpeed(TimeSpeed.Normal);
            foreach (int w in LeavePlay()) yield return w;
            FpSettings.autoNeeds = autoNeeds0;
            yield return K.Done();
        }

        // No food anywhere: the colonist stays under control (the HUD warns) instead of being handed to the autopilot.
        private static IEnumerator<int> AutoNeedsNoFood()
        {
            bool autoNeeds0 = FpSettings.autoNeeds;
            FpSettings.autoNeeds = true;
            Map map = S.C1.Map;
            int removed = 0;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).ToList())
            {
                if (!t.Destroyed)
                {
                    t.Destroy(DestroyMode.Vanish);
                    removed++;
                }
            }
            if (S.Potatoes != null && !S.Potatoes.Destroyed)
            {
                S.Potatoes.Destroy(DestroyMode.Vanish);
                removed++;
            }
            // Food carried by anyone (eaten from the own inventory, or taken from another's).
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p.inventory?.innerContainer == null)
                {
                    continue;
                }
                foreach (Thing t in p.inventory.innerContainer.ToList())
                {
                    if (t.def.IsIngestible)
                    {
                        t.Destroy(DestroyMode.Vanish);
                        removed++;
                    }
                }
            }
            K.Info("removed " + removed + " food sources");
            foreach (int w in EnterPlay(S.C1)) yield return w;
            int given0 = FpHits.Count("AutoNeeds job given");
            S.C1.needs.food.CurLevelPercentage = 0.05f;
            bool held = false;
            string detail = null;
            foreach (int w in StaysInControl(S.C1, 300, (ok, d) => { held = ok; detail = d; })) yield return w;
            K.Check("m2-autoneeds-nofood stays in control", held && FpHits.Count("AutoNeeds job given") == given0,
                detail + ", " + Needs(S.C1) + ", AutoNeeds jobs given " + (FpHits.Count("AutoNeeds job given") - given0));
            foreach (int w in LeavePlay()) yield return w;
            FpSettings.autoNeeds = autoNeeds0;
            yield return K.Done();
        }

        // Drafted, the colonist doesn't see to their needs (as the game's drafted colonists don't).
        private static IEnumerator<int> AutoNeedsDrafted()
        {
            bool autoNeeds0 = FpSettings.autoNeeds;
            FpSettings.autoNeeds = true;
            foreach (int w in EnterPlay(S.C1)) yield return w;
            S.C1.drafter.Drafted = true;
            yield return K.WaitUntil(() => InControlJob(S.C1), "for the control job while drafted", 300);
            S.C1.needs.food.CurLevelPercentage = 0.05f;
            bool held = false;
            string detail = null;
            foreach (int w in StaysInControl(S.C1, 300, (ok, d) => { held = ok; detail = d; })) yield return w;
            K.Check("m2-autoneeds-drafted stays in control", held && S.C1.Drafted, detail + ", drafted " + S.C1.Drafted + ", " + Needs(S.C1));
            S.C1.drafter.Drafted = false;
            yield return K.WaitTicks(10);
            foreach (int w in LeavePlay()) yield return w;
            FpSettings.autoNeeds = autoNeeds0;
            yield return K.Done();
        }

        // Exhaustion: the autopilot lies the colonist down; once rested, control comes back (the game's 211-tick override
        // check while lying down lets them up).
        private static IEnumerator<int> AutoNeedsSleep()
        {
            bool autoNeeds0 = FpSettings.autoNeeds;
            FpSettings.autoNeeds = true;
            foreach (int w in EnterPlay(S.C1)) yield return w;
            S.C1.needs.rest.CurLevel = 0.005f;
            int tick0 = K.Ticks;
            yield return K.WaitUntil(() => LyingDown(S.C1.CurJob) || K.Ticks > tick0 + 120, "for the sleep job");
            K.Check("m2-autoneeds-sleep lies down within 120 ticks", LyingDown(S.C1.CurJob) && FpState.AutopilotReason(S.C1) != null,
                JobOf(S.C1) + " after " + (K.Ticks - tick0) + " ticks, " + Needs(S.C1) + ", autopilot " + (FpState.AutopilotReason(S.C1) ?? "none"));
            S.C1.needs.rest.CurLevel = 1f;
            int rested = K.Ticks;
            yield return K.WaitUntil(() => InControlJob(S.C1) || K.Ticks > rested + 600, "for control to come back after resting", 1800);
            K.Check("m2-autoneeds-sleep control back", InControlJob(S.C1) && FpState.AutopilotReason(S.C1) == null,
                JobOf(S.C1) + " " + (K.Ticks - rested) + " ticks after resting, " + Needs(S.C1));
            foreach (int w in LeavePlay()) yield return w;
            FpSettings.autoNeeds = autoNeeds0;
            yield return K.Done();
        }

        // With the option off (sent as the control's flags), urgent hunger leaves the colonist under control.
        private static IEnumerator<int> AutoNeedsOff()
        {
            bool autoNeeds0 = FpSettings.autoNeeds;
            FpSettings.autoNeeds = true;
            foreach (int w in EnterPlay(S.C1)) yield return w;
            FpSettings.autoNeeds = false;   // FpPlay sends the new flags (at most once a second)
            yield return K.WaitUntil(() => FpRegistry.EntryOf(S.C1) is FpEntry e0 && (e0.flags & FpFlags.AutoNeeds) == 0,
                "for the flags without AutoNeeds to reach the simulation", 600);
            FpEntry e = FpRegistry.EntryOf(S.C1);
            K.Check("m2-autoneeds-off flag cleared", e != null && (e.flags & FpFlags.AutoNeeds) == 0 && InControlJob(S.C1),
                "flags " + (e?.flags.ToString() ?? "no entry") + ", " + JobOf(S.C1));
            S.C1.needs.food.CurLevelPercentage = 0.05f;
            bool held = false;
            string detail = null;
            foreach (int w in StaysInControl(S.C1, 300, (ok, d) => { held = ok; detail = d; })) yield return w;
            K.Check("m2-autoneeds-off stays in control", held, detail + ", " + Needs(S.C1));
            foreach (int w in LeavePlay()) yield return w;
            FpSettings.autoNeeds = autoNeeds0;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- M2 multiplayer

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

        // As FpTestM1.CompareTicks: enough FPSTATE lines for fp_compare.py while both players still run.
        private const int CompareTicks = 1900;

        private static IEnumerable<int> AfterReload()
        {
            yield return K.WaitUntil(() => Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null
                && !LongEventHandler.AnyEventNowOrWaiting, "for the reload to finish", 7200);
            K.Check("scene found again", S.Adopt(Find.CurrentMap, out string error), error ?? S.Describe());
        }

        // The cook: C2, or C1 when C2 can never cook and C1 can (-quicktest colonists are random; the scene puts colonists
        // who can cook first, so C2 can't only when at most one of the two can). Worked out alike on both machines from the
        // pawns' synced data; after a reload, from the scene adopted again.
        private static Pawn MpCookPawn
        {
            get
            {
                Pawn c2 = S.C2;
                bool c2Cooks = SimpleMeal != null && S.Stove != null && FpBills.RecipeRefusal(c2, S.Stove, SimpleMeal, out _, out _) == null;
                return c2Cooks || S.C1 == null ? c2 : S.C1;
            }
        }

        // The client cooks a simple meal on the cook (C2, see MpCookPawn) through the picker. Pass: the bill and its job are
        // the same on both machines: fp_compare.py compares the FPSTATE lines (the cook's job and its load id among them),
        // and both logs carry the command's line with the bill's load id ("FPTEST CMD ... FpStartRecipe ... -> started
        // Bill_CookMealSimple_<n>") and an INFO line with it, to be compared by eye or grep.
        private static IEnumerator<int> MpCook()
        {
            if (K.IsHost)
            {
                yield return K.WaitUntil(() => MpBridge.InSession, "for hosting", 3600);
                foreach (int w in AfterReload()) yield return w;
                yield return K.WaitUntil(() => Players >= 2, "for the client to join", 18000);
                // The client's join makes a join point, which saves and reloads the host's game: the scene's things are new
                // objects again (the stove found before would never get the bill). The client's first command runs only
                // after its join completed, so the reload is over by then.
                yield return K.WaitUntil(() => K.FirstCommandTick(false) >= 0, "for the client's first command", 36000);
                foreach (int w in AfterReload()) yield return w;
                Pawn cook = MpCookPawn;
                yield return K.WaitUntil(() => FpBillOf(cook) != null, "for the client's bill on " + cook.LabelShort, 36000);
                Bill bill = FpBillOf(cook);
                K.Info("mp2-cook bill " + BillText(bill) + ", " + cook.LabelShort + " " + JobOf(cook));
                K.Check("mp2-cook bill on the host", bill != null && bill.PawnRestriction == cook && StoveBills.IndexOf(bill) == 0, BillText(bill));
                yield return K.WaitUntil(() => Players < 2, "for the client to finish", 36000);
                yield return 120;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                Pawn cook = MpCookPawn;
                foreach (int w in EnterPlay(cook)) yield return w;
                if (!CanCook(cook, "mp2-cook"))
                {
                    foreach (int w in LeavePlay()) yield return w;
                    yield return K.Done();
                    yield break;
                }
                bool anyDistance0 = FpSettings.interactAnyDistance;
                FpSettings.interactAnyDistance = true;   // local; C2 stands south-west of the stove, C1 south of it
                Turn(0f, 8f);
                yield return K.HoldTicks(30, Key.W);
                yield return K.WaitTicks(30);
                foreach (int w in OpenPicker("mp2-cook")) yield return w;
                int tick0 = K.Ticks;
                foreach (int w in ChooseRecipe(SimpleMeal, 1)) yield return w;
                yield return K.WaitUntil(() => FpBillOf(cook) != null || K.Ticks > tick0 + 300, "for the bill");
                Bill bill = FpBillOf(cook);
                K.Info("mp2-cook bill " + BillText(bill) + ", " + cook.LabelShort + " " + JobOf(cook));
                yield return K.WaitUntil(() => cook.CurJob?.def == JobDefOf.DoBill || K.Ticks > tick0 + 300, "for the bill job");
                K.Check("mp2-cook bill job", bill != null && cook.CurJob?.def == JobDefOf.DoBill && cook.CurJob.bill == bill, JobOf(cook));
                FpSettings.interactAnyDistance = anyDistance0;
                yield return K.WaitTicks(CompareTicks);
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
            }
            else
            {
                K.Check("mp2-cook role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }
    }
}
