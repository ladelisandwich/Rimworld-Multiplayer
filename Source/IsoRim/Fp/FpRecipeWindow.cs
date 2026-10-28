using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace IsoRim.Fp
{
    // Track D (fp-play-spec 11.1): the recipe picker, opened by the left button on one of the colony's benches while
    // playing. "Continue" lists the bills already on the bench this colonist may take; "Make" lists what the bench can make
    // now, each with this colonist's time and skill, greyed with the reason when they can't. A choice goes out as a command
    // (FpBills.FpWorkBill or FpStartRecipe) and closes the picker; the command's result shows as the game's message.
    // Keys: 1-9 choose a row, Enter repeats the last choice, Esc closes. Being a window, it frees the cursor while open
    // (FpInput's watchdog), and the cursor is captured again when it closes.
    //
    // Interface only: it reads the game and never changes it. Bill.ShouldDoNow and Bill_ProductionWithUft.BoundWorker are
    // never called here (both write to the bill), nor anything that fills the reachability cache. The one static it
    // writes, JobFailReason (Bill.PawnAllowedToStartAnew's reason), is the float menus' and is cleared again at once.
    public class Dialog_FpRecipes : Window
    {
        private const float HeaderHeight = 46f;
        private const float CountRowHeight = 30f;
        private const float FooterHeight = 22f;
        private const float SectionHeight = 24f;
        private const float RowHeight = 40f;
        private const float IconSize = 32f;
        private const float NumberWidth = 18f;
        private const double RefreshSeconds = 1.5;

        private static readonly Color Accent = new Color(0.55f, 0.78f, 0.95f);         // IsoRimMod's accent
        private static readonly Color RefusedColour = new Color(0.878f, 0.353f, 0.353f); // #E05A5A
        private static readonly Color NoteColour = new Color(1f, 0.82f, 0.45f);          // the HUD's warning colour
        private static readonly Color DimColour = new Color(0.7f, 0.7f, 0.7f);

        // This player's choices, kept between openings: the count toggle, and the last choice for Enter (a recipe and its
        // count, or a bill by its load id: holding the bill itself would keep a finished game's map alive).
        private static int count = 1;
        private static RecipeDef lastRecipe;
        private static int lastCount = 1;
        private static string lastBillId;

        private readonly Pawn pawn;
        private readonly Building bench;
        private readonly List<Row> rows = new List<Row>();
        private int continueRows;
        private double builtAt = -1;
        private Vector2 scroll;
        private bool closeRequested;

        // Why the last choice wasn't sent, shown in the footer for a moment: the HUD's toasts draw under windows, so they
        // would be hidden behind this one.
        private string refusalText;
        private double refusalUntil = -1;
        private const double RefusalSeconds = 2.5;

        private sealed class Row
        {
            public Bill bill;            // a Continue row; null for a recipe
            public RecipeDef recipe;
            public string label;
            public string detail;        // right of the label: skill, time
            public string note;          // second line: progress, missing ingredients, "yours"
            public string refusal;       // greyed with this reason; null when it can be chosen
            public float progress = -1f; // an unfinished piece, 0..1
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(460f, 560f); }
        }

        public Dialog_FpRecipes(Pawn pawn, Building bench)
        {
            this.pawn = pawn;
            this.bench = bench;
            forcePause = false;
            preventCameraMotion = false;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            closeOnCancel = true;
            closeOnAccept = true;   // Enter reaches OnAcceptKeyPressed (repeat), not a plain close
            doCloseX = true;
        }

        // ---- self-test hooks (FpTestM2)

        // The count the next recipe choice sends (1 or 5).
        internal static int CountChoice
        {
            get { return count; }
            set { count = value == 5 ? 5 : 1; }
        }

        // A row's number (1-based, as the number keys choose it), -1 if not listed.
        internal int NumberOf(RecipeDef recipe)
        {
            EnsureRows();
            int i = rows.FindIndex(r => r.bill == null && r.recipe == recipe);
            return i >= 0 ? i + 1 : -1;
        }

        internal int NumberOf(Bill bill)
        {
            EnsureRows();
            int i = rows.FindIndex(r => r.bill == bill);
            return i >= 0 ? i + 1 : -1;
        }

        internal string RefusalOf(int number)
        {
            EnsureRows();
            return number >= 1 && number <= rows.Count ? rows[number - 1].refusal : null;
        }

        // Chooses a row as a click would; true when a command went out.
        internal bool ChooseNumber(int number)
        {
            EnsureRows();
            return number >= 1 && number <= rows.Count && Choose(rows[number - 1]);
        }

        // ---- window

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (closeRequested || !StillValid() || Find.WindowStack == null || !Find.WindowStack.GetsInput(this))
            {
                return;
            }
            // Number keys, polled: with this window absorbing input round it, the game uses up every key event before the
            // window's own contents see it (WindowStack.HandleEventsHighPriority), which also keeps 1-4 from the time
            // controls while it is open.
            for (int i = 0; i < 9; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i))
                {
                    EnsureRows();
                    if (i < rows.Count)
                    {
                        Choose(rows[i]);
                    }
                    else
                    {
                        SoundDefOf.ClickReject.PlayOneShotOnCamera();
                    }
                    break;
                }
            }
        }

        // Closing waits for the next OnGUI pass: WindowUpdate runs inside the window stack's own loop over its windows.
        public override void ExtraOnGUI()
        {
            base.ExtraOnGUI();
            if (closeRequested || !StillValid())
            {
                Close(false);
            }
        }

        // Enter: the last choice again (spec 11.1).
        public override void OnAcceptKeyPressed()
        {
            Row r = RepeatRow();
            if (r != null)
            {
                Choose(r);
            }
            else
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
            }
            Event.current.Use();
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (closeRequested || !StillValid())
            {
                return;
            }
            EnsureRows();
            IBillGiver giver = (IBillGiver)bench;
            float y = 0f;

            // Header: the bench, its state, the number of bills.
            Widgets.ThingIcon(new Rect(0f, 2f, 40f, 40f), bench);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(48f, 0f, inRect.width - 80f, 30f), bench.LabelCap);
            Text.Font = GameFont.Tiny;
            GUI.color = giver.CurrentlyUsableForBills() ? DimColour : RefusedColour;
            Widgets.Label(new Rect(48f, 28f, inRect.width - 48f, 18f), StatusLine(giver));
            GUI.color = Color.white;
            y += HeaderHeight;

            // How many: 1 or 5.
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, y, 110f, CountRowHeight - 4f), "R3D_Fp_Recipes_Count".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            if (CountButton(new Rect(112f, y, 44f, CountRowHeight - 4f), 1))
            {
                count = 1;
            }
            Rect five = new Rect(160f, y, 44f, CountRowHeight - 4f);
            if (CountButton(five, 5))
            {
                count = 5;
            }
            TooltipHandler.TipRegion(five, "R3D_Fp_Recipes_FiveTip".Translate());
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = DimColour;
            Widgets.Label(new Rect(210f, y, inRect.width - 210f, CountRowHeight - 4f), pawn.LabelShortCap);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            y += CountRowHeight;

            // The rows.
            Rect outRect = new Rect(0f, y, inRect.width, inRect.height - y - FooterHeight);
            float viewH = (continueRows > 0 ? SectionHeight + continueRows * RowHeight : 0f)
                + SectionHeight + Mathf.Max(1, rows.Count - continueRows) * RowHeight;
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, viewH);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float ry = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                if (i == 0 && continueRows > 0)
                {
                    Section(ref ry, viewRect.width, "R3D_Fp_Recipes_Continue".Translate());
                }
                if (i == continueRows)
                {
                    Section(ref ry, viewRect.width, "R3D_Fp_Recipes_Make".Translate());
                }
                if (DrawRow(new Rect(0f, ry, viewRect.width, RowHeight), rows[i], i))
                {
                    Choose(rows[i]);
                }
                ry += RowHeight;
            }
            if (rows.Count == continueRows)
            {
                Section(ref ry, viewRect.width, "R3D_Fp_Recipes_Make".Translate());
                Text.Font = GameFont.Small;
                GUI.color = DimColour;
                Widgets.Label(new Rect(NumberWidth, ry + 8f, viewRect.width - NumberWidth, RowHeight - 8f), "R3D_Fp_Recipes_None".Translate());
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();

            // Footer: the keys, or for a moment why the last choice wasn't sent.
            Text.Font = GameFont.Tiny;
            Rect footer = new Rect(0f, inRect.height - FooterHeight + 4f, inRect.width, FooterHeight);
            if (refusalText != null && Time.realtimeSinceStartupAsDouble < refusalUntil)
            {
                GUI.color = RefusedColour;
                Widgets.Label(footer, refusalText.Truncate(footer.width));
            }
            else
            {
                GUI.color = DimColour;
                Widgets.Label(footer, "R3D_Fp_Recipes_Keys".Translate());
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // ---- drawing

        private static bool CountButton(Rect r, int n)
        {
            bool on = count == n;
            if (on)
            {
                Widgets.DrawBoxSolid(r, new Color(Accent.r, Accent.g, Accent.b, 0.25f));
            }
            return Widgets.ButtonText(r, "x" + n, drawBackground: !on);
        }

        private static void Section(ref float y, float width, string label)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = Accent;
            Widgets.Label(new Rect(0f, y + 6f, width, SectionHeight - 6f), label.ToUpperInvariant());
            GUI.color = Color.white;
            y += SectionHeight;
        }

        // One row; true when clicked.
        private bool DrawRow(Rect r, Row row, int index)
        {
            bool refused = row.refusal != null;
            if (index % 2 == 0)
            {
                Widgets.DrawAltRect(r);
            }
            if (!refused)
            {
                Widgets.DrawHighlightIfMouseover(r);
            }
            if (IsLastChoice(row))
            {
                Widgets.DrawHighlightSelected(r);
            }
            float alpha = refused ? 0.45f : 1f;

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(Accent.r, Accent.g, Accent.b, alpha);
            if (index < 9)
            {
                Widgets.Label(new Rect(r.x, r.y, NumberWidth, r.height), (index + 1).ToString());
            }
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            Rect icon = new Rect(r.x + NumberWidth + 2f, r.y + (r.height - IconSize) / 2f, IconSize, IconSize);
            Widgets.DefIcon(icon, row.recipe, alpha: alpha);

            float textX = icon.xMax + 6f;
            float textW = r.xMax - textX - 4f;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(textX, r.y + 2f, textW * 0.62f, 22f), row.label.Truncate(textW * 0.62f));
            if (!row.detail.NullOrEmpty())
            {
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperRight;
                GUI.color = new Color(DimColour.r, DimColour.g, DimColour.b, alpha);
                Widgets.Label(new Rect(textX + textW * 0.62f, r.y + 4f, textW * 0.38f, 18f), row.detail.Truncate(textW * 0.38f));
                Text.Anchor = TextAnchor.UpperLeft;
            }

            Text.Font = GameFont.Tiny;
            string second = refused ? row.refusal : row.note;
            if (!second.NullOrEmpty())
            {
                GUI.color = refused ? RefusedColour : NoteColour;
                Widgets.Label(new Rect(textX, r.y + 21f, textW - (row.progress >= 0f ? 64f : 0f), 18f), second.Truncate(textW - 64f));
            }
            if (row.progress >= 0f)
            {
                GUI.color = Color.white;
                Widgets.FillableBar(new Rect(r.xMax - 64f, r.y + 25f, 58f, 8f), Mathf.Clamp01(row.progress));
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            if (refused)
            {
                TooltipHandler.TipRegion(r, row.refusal);
            }
            return Widgets.ButtonInvisible(r, !refused);
        }

        private string StatusLine(IBillGiver giver)
        {
            List<string> parts = new List<string>();
            CompPowerTrader power = bench.TryGetComp<CompPowerTrader>();
            if (power != null)
            {
                parts.Add(power.PowerOn ? "R3D_Fp_Recipes_PowerOn".Translate() : "R3D_Fp_Recipes_NoPower".Translate());
            }
            CompRefuelable fuel = bench.TryGetComp<CompRefuelable>();
            if (fuel != null)
            {
                parts.Add(fuel.HasFuel ? "R3D_Fp_Recipes_Fuel".Translate(fuel.FuelPercentOfMax.ToStringPercent()) : "R3D_Fp_Recipes_NoFuel".Translate());
            }
            CompBreakdownable breakdown = bench.TryGetComp<CompBreakdownable>();
            if (breakdown != null && breakdown.BrokenDown)
            {
                parts.Add("R3D_Fp_Recipes_Broken".Translate());
            }
            parts.Add("R3D_Fp_Recipes_Bills".Translate(giver.BillStack.Count, FpTuning.Get.maxBillStack));
            return string.Join("   ", parts);
        }

        // ---- rows

        private bool StillValid()
        {
            return FpPlay.Mode == FpMode.Playing && FpPlay.Pawn == pawn && pawn != null && pawn.Spawned && bench != null && bench.Spawned
                && bench.Map == pawn.Map && bench.Faction == MpBridge.RealPlayerFaction;
        }

        private void EnsureRows()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (builtAt >= 0 && now - builtAt < RefreshSeconds && now >= builtAt)
            {
                return;
            }
            builtAt = now;
            Rebuild();
        }

        private void Rebuild()
        {
            rows.Clear();
            BillStack stack = ((IBillGiver)bench).BillStack;
            Map map = bench.Map;

            // Continue: bills open to this colonist that the game would let them work on now.
            for (int i = 0; i < stack.Count; i++)
            {
                Bill bill = stack[i];
                if (bill == null || bill.recipe == null || bill.deleted || bill.PawnRestriction != null && bill.PawnRestriction != pawn || !DoableNow(bill))
                {
                    continue;
                }
                Row row = new Row { bill = bill, recipe = bill.recipe, label = bill.LabelCap };
                if (bill is Bill_Production bp)
                {
                    row.detail = bp.RepeatInfoText;
                }
                row.refusal = FpBills.NotAllowedReason(pawn, bill) ?? FpBills.RecipeRefusal(pawn, bench, bill.recipe, out _, out _);
                List<string> notes = new List<string>();
                if (bill.PawnRestriction == pawn)
                {
                    notes.Add("R3D_Fp_Recipes_Yours".Translate());
                }
                // An unfinished piece: its progress, and who made it (the game lets only its maker carry on with it).
                UnfinishedThing uft = (bill as Bill_ProductionWithUft)?.BoundUft;
                if (uft != null && !uft.Destroyed)
                {
                    float total = bill.GetWorkAmount(uft);
                    if (uft.Initialized && total > 0f)
                    {
                        row.progress = 1f - uft.workLeft / total;
                        notes.Add("R3D_Fp_Recipes_Done".Translate(Mathf.Clamp01(row.progress).ToStringPercent()));
                    }
                    if (row.refusal == null && uft.Creator != null && uft.Creator != pawn)
                    {
                        row.refusal = "R3D_Fp_Recipes_BoundTo".Translate(uft.Creator.LabelShort);
                    }
                }
                if (row.refusal == null && pawn.CurJob?.bill == bill)
                {
                    row.refusal = "R3D_Fp_Recipes_Working".Translate(pawn.LabelShort);
                }
                row.note = string.Join("   ", notes);
                rows.Add(row);
            }
            continueRows = rows.Count;

            // Make: what the bench can make now (the bills tab's own test, ITab_Bills).
            bool full = stack.Count >= FpTuning.Get.maxBillStack;
            List<RecipeDef> recipes = bench.def.AllRecipes;
            for (int i = 0; i < recipes.Count; i++)
            {
                RecipeDef r = recipes[i];
                if (!r.AvailableNow || !r.AvailableOnNow(bench))
                {
                    continue;
                }
                Row row = new Row { recipe = r, label = r.LabelCap };
                row.refusal = full ? (string)"R3D_Fp_BillStackFull".Translate(bench.LabelShort, stack.Count)
                    : FpBills.RecipeRefusal(pawn, bench, r, out _, out _);
                row.detail = Detail(r);
                if (row.refusal == null)
                {
                    // The skill a recipe asks for goes on the second line: in the detail it pushed the time out of its
                    // column. A greyed row's reason names the requirement when that is what is missing.
                    string needs = r.skillRequirements.NullOrEmpty() ? null
                        : (string)"R3D_Fp_Recipes_Needs".Translate(string.Join(", ", r.skillRequirements.Select(sr => sr.Summary)));
                    string missing = MissingNote(r, map);
                    row.note = needs == null ? missing : missing == null ? needs : needs + "   " + missing;
                }
                rows.Add(row);
            }
        }

        // Bill.ShouldDoNow without its side effect (Bill_Production resets or sets `paused`, game state that a call from the
        // interface would change on this machine alone). A mechanitor's bandwidth isn't tested: the command says so.
        private static bool DoableNow(Bill bill)
        {
            if (bill.suspended)
            {
                return false;
            }
            if (!(bill is Bill_Production bp))
            {
                return true;
            }
            bool ok;
            if (bp.repeatMode == BillRepeatModeDefOf.Forever)
            {
                ok = true;
            }
            else if (bp.repeatMode == BillRepeatModeDefOf.RepeatCount)
            {
                ok = bp.repeatCount > 0;
            }
            else if (bp.repeatMode == BillRepeatModeDefOf.TargetCount)
            {
                int n = bp.recipe.WorkerCounter.CountProducts(bp);
                bool paused = bp.paused;
                if (bp.pauseWhenSatisfied && n >= bp.targetCount)
                {
                    paused = true;
                }
                if (n <= bp.unpauseWhenYouHave || !bp.pauseWhenSatisfied)
                {
                    paused = false;
                }
                ok = !paused && n < bp.targetCount;
            }
            else
            {
                ok = false;
            }
            if (ok && bill is Bill_Autonomous autonomous && autonomous.State == FormingState.Forming)
            {
                ok = false;
            }
            return ok;
        }

        // "Cooking 8   25 s": this colonist's level in the recipe's skill and the time the work itself takes at normal
        // speed (the game's DoRecipeWork: the recipe's speed stat, times the bench's). The requirement is on the row's
        // second line.
        private string Detail(RecipeDef r)
        {
            List<string> parts = new List<string>();
            if (r.workSkill != null && pawn.skills != null)
            {
                SkillRecord skill = pawn.skills.GetSkill(r.workSkill);
                if (skill != null)
                {
                    parts.Add(r.workSkill.LabelCap + " " + (skill.TotallyDisabled ? "-" : skill.Level.ToString()));
                }
            }
            string time = TimeText(r);
            if (time != null)
            {
                parts.Add(time);
            }
            return string.Join("   ", parts);
        }

        private string TimeText(RecipeDef r)
        {
            if (r.workAmount < 0f && r.products.NullOrEmpty())
            {
                return null;
            }
            float speed = r.workSpeedStat == null ? 1f : pawn.GetStatValue(r.workSpeedStat);
            if (r.workTableSpeedStat != null && bench is Building_WorkTable)
            {
                speed *= bench.GetStatValue(r.workTableSpeedStat);
            }
            if (speed <= 0.001f)
            {
                return null;
            }
            float seconds = r.WorkAmountTotal(null) / speed / 60f;
            return seconds < 90f
                ? (string)"R3D_Fp_Recipes_Seconds".Translate(Mathf.Max(1, Mathf.RoundToInt(seconds)))
                : (string)"R3D_Fp_Recipes_Minutes".Translate((seconds / 60f).ToString("0.#"));
        }

        // "No raw potatoes on the map": ingredients nobody could fetch anywhere (the command's own search, within the bill's
        // radius, has the last word).
        private string MissingNote(RecipeDef r, Map map)
        {
            List<ThingDef> missing = r.PotentiallyMissingIngredients(pawn, map).ToList();
            if (missing.Count == 0)
            {
                return null;
            }
            return "R3D_Fp_Recipes_Missing".Translate(string.Join(", ", missing.Select(d => d.label)));
        }

        // ---- choosing

        private bool IsLastChoice(Row row)
        {
            return row.bill != null ? lastBillId != null && row.bill.GetUniqueLoadID() == lastBillId : lastBillId == null && row.recipe == lastRecipe;
        }

        // Sends the row's command and closes; a greyed row says why instead. True when a command went out.
        private bool Choose(Row row)
        {
            if (closeRequested)
            {
                return false;
            }
            if (row.refusal != null)
            {
                Refused(row.refusal);
                return false;
            }
            bool sent = row.bill != null ? FpBills.TrySendWorkBill(pawn, row.bill) : FpBills.TrySendStartRecipe(pawn, bench, row.recipe, count);
            if (!sent)
            {
                Refused("R3D_Fp_BillNotSent".Translate());
                return false;
            }
            if (row.bill != null)
            {
                lastBillId = row.bill.GetUniqueLoadID();
                lastRecipe = row.bill.recipe;
            }
            else
            {
                lastBillId = null;
                lastRecipe = row.recipe;
                lastCount = count;
            }
            SoundDefOf.Click.PlayOneShotOnCamera();
            // Each bill job makes one piece and control comes back after it (spec 11.2), so five are made one at a time:
            // the toast says how to carry on.
            FpHud.Toast(row.bill != null || count <= 1 ? row.label : (string)"R3D_Fp_Recipes_FiveToast".Translate(row.label, count), Color.white,
                row.bill != null || count <= 1 ? 1.5f : 4f);
            closeRequested = true;
            return true;
        }

        private void Refused(string text)
        {
            SoundDefOf.ClickReject.PlayOneShotOnCamera();
            refusalText = text;
            refusalUntil = Time.realtimeSinceStartupAsDouble + RefusalSeconds;
        }

        // Enter's row: the last bill again; after a recipe, an unfinished bill of it this colonist was given (carry on
        // rather than pile up bills), else the recipe again with its count.
        private Row RepeatRow()
        {
            EnsureRows();
            if (lastBillId != null)
            {
                Row same = rows.FirstOrDefault(r => r.bill != null && r.refusal == null && r.bill.GetUniqueLoadID() == lastBillId);
                if (same != null)
                {
                    return same;
                }
            }
            if (lastRecipe == null)
            {
                return null;
            }
            Row mine = rows.FirstOrDefault(r => r.bill != null && r.refusal == null && r.bill.recipe == lastRecipe && r.bill.PawnRestriction == pawn);
            if (mine != null)
            {
                return mine;
            }
            Row recipe = rows.FirstOrDefault(r => r.bill == null && r.recipe == lastRecipe);
            if (recipe != null && lastBillId == null)
            {
                count = lastCount;
            }
            return recipe;
        }
    }
}
