using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim.Fp
{
    // The outcome levers of skill checks (fp-play-spec 10.5): how well a piece was worked in first person (its tally's form
    // score F, -4..+4) shifts the quality it comes out at and the stats read while it is finished (food poisoning, surgery
    // success, butchery yield), for better or worse: risk and reward. F is read from the tally of the job the worker is on
    // (FpRegistry.TallyForWorker), so it also applies to a colonist on autopilot or an AI colonist who finishes a piece
    // someone worked in first person. No lever touches shooting, melee, warmup, move speed or work speed.
    //
    // M3 has the bill levers: quality (crafting and art through bills) and the stats read while a bill finishes. M4 adds the
    // construction, mining and harvest stats (ConstructSuccessChance, MiningYield, PlantHarvestYield; DrugHarvestYield
    // through PlantHarvestYield) with their adapters, and the quality patch covers buildings (Frame.CompleteConstruction).
    // Everything here runs inside the game's own stat and quality code, so it reads only synced state.

    // Which FpTuningDef table a lever uses.
    public enum FpLeverTable : byte
    {
        ConstructFail,
        Surgery,
        FoodPoison,
        Yield,
    }

    // A form factor on one pawn stat, added to the stat's parts in code at startup (FpLevers.Install). Parts run before
    // the stat's own clamp (StatWorker.FinalizeValue), so a stat's min and max still hold.
    public class StatPart_FpForm : StatPart
    {
        public FpLeverTable table;

        // A success chance whose failure part scales: val = 1 - (1 - val) * k, for a chance under 1 (a worker whose chance
        // is 1 or more before the stat's clamp can't fail, and stays so). Otherwise val *= k.
        public bool failChance;

        public override void TransformValue(StatRequest req, ref float val)
        {
            if (FpRegistry.TallyCount == 0 || !(req.Thing is Pawn p))
            {
                return;
            }
            int form = FpLevers.FormOf(p);
            if (form == 0)
            {
                return;
            }
            float k = Factor(form);
            if (k == 1f)
            {
                return;
            }
            if (failChance)
            {
                if (val >= 1f)
                {
                    return;
                }
                val = 1f - (1f - val) * k;
            }
            else
            {
                val *= k;
            }
            FpHits.Hit("StatPart applied");
        }

        // "First-person skill checks (form +2): ×0.80", only while it changes the value.
        public override string ExplanationPart(StatRequest req)
        {
            if (FpRegistry.TallyCount == 0 || !(req.Thing is Pawn p))
            {
                return null;
            }
            int form = FpLevers.FormOf(p);
            float k = form == 0 ? 1f : Factor(form);
            if (k == 1f)
            {
                return null;
            }
            string factor = "×" + k.ToString("0.00", CultureInfo.InvariantCulture);
            return (failChance ? "R3D_Fp_StatFormFail" : "R3D_Fp_StatForm").Translate(FpLevers.FormLabel(form), factor);
        }

        // The table's factor for a form (-4..+4).
        internal float Factor(int form)
        {
            FpTuningDef T = FpTuning.Get;
            List<float> t;
            switch (table)
            {
                case FpLeverTable.ConstructFail:
                    t = T.constructFail;
                    break;
                case FpLeverTable.Surgery:
                    t = T.surgery;
                    break;
                case FpLeverTable.FoodPoison:
                    t = T.foodPoison;
                    break;
                default:
                    t = T.yield;
                    break;
            }
            return FpTuningDef.Table(t, form);
        }
    }

    public static class FpLevers
    {
        // The quality offset in skill levels while GenerateQualityCreatedByPawn(Pawn, ...) runs: set by its prefix, used
        // by the prefix of the level overload it calls, cleared by its finalizer.
        private static int pendingOffset;

        // The stats a bill reads from its worker while it finishes (M3): food poisoning when a meal is made
        // (CompFoodPoisonable.Notify_RecipeProduced), surgery success when an operation completes (Recipe_Surgery), and the
        // butchering yield (the recipe's efficiencyStat, GenRecipe.MakeRecipeProducts).
        private static readonly LeverStat[] BillStats =
        {
            new LeverStat("FoodPoisonChance", FpLeverTable.FoodPoison, false),
            new LeverStat("MedicalSurgerySuccessChance", FpLeverTable.Surgery, false),
            new LeverStat("ButcheryFleshEfficiency", FpLeverTable.Yield, false),
            new LeverStat("ButcheryMechanoidEfficiency", FpLeverTable.Yield, false),
        };

        // The stats the M4 work reads from its worker (spec 10.5): the construction failure roll every interval of a frame's
        // build toil (JobDriver_ConstructFinishFrame.cs:61), the yield booked with each mining hit (Mineable.cs:88-96) and
        // the harvest roll when a plant is collected (JobDriver_PlantWork.cs:68-80). Not DrugHarvestYield: it takes
        // PlantHarvestYield as a stat factor (Stats_Pawns_WorkGeneral.xml), so a part on both would apply the form twice.
        private static readonly LeverStat[] WorkStats =
        {
            new LeverStat("ConstructSuccessChance", FpLeverTable.ConstructFail, true),
            new LeverStat("MiningYield", FpLeverTable.Yield, false),
            new LeverStat("PlantHarvestYield", FpLeverTable.Yield, false),
        };

        private struct LeverStat
        {
            public readonly string defName;
            public readonly FpLeverTable table;
            public readonly bool failChance;

            public LeverStat(string defName, FpLeverTable table, bool failChance)
            {
                this.defName = defName;
                this.table = table;
                this.failChance = failChance;
            }
        }

        // F of the tally the worker's current job counts in, 0 without one. Simulation and UI (stat explanations).
        internal static int FormOf(Pawn p)
        {
            if (p == null)
            {
                return 0;
            }
            FpTally t = FpRegistry.TallyForWorker(p);
            return t == null ? 0 : t.Form;
        }

        internal static string FormLabel(int form)
        {
            return form > 0 ? "+" + form.ToString(CultureInfo.InvariantCulture) : form.ToString(CultureInfo.InvariantCulture);
        }

        // Both quality targets are large (the level overload is a 21-way switch, the pawn overload reads the ideoligion's
        // role), so the JIT can't inline them into a caller compiled before the patch; both are QualityUtility's own.
        internal static void Install(Harmony h)
        {
            FpHooks.Patch(h, "work levers: quality from form",
                AccessTools.DeclaredMethod(typeof(QualityUtility), nameof(QualityUtility.GenerateQualityCreatedByPawn),
                    new[] { typeof(Pawn), typeof(SkillDef), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(FpLevers), nameof(QualityByPawn_Prefix)),
                finalizer: new HarmonyMethod(typeof(FpLevers), nameof(QualityByPawn_Finalizer)));
            FpHooks.Patch(h, "work levers: quality skill offset",
                AccessTools.DeclaredMethod(typeof(QualityUtility), nameof(QualityUtility.GenerateQualityCreatedByPawn),
                    new[] { typeof(int), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(FpLevers), nameof(QualityByLevel_Prefix)));
            foreach (LeverStat s in BillStats)
            {
                AddStatPart(s);
            }
            foreach (LeverStat s in WorkStats)
            {
                AddStatPart(s);
            }
        }

        private static void AddStatPart(LeverStat s)
        {
            StatDef stat = DefDatabase<StatDef>.GetNamedSilentFail(s.defName);
            if (stat == null)
            {
                Log.Warning("[IsoRim] First person: stat " + s.defName + " not found; its skill-check lever is off.");
                return;
            }
            if (stat.parts != null && stat.parts.Exists(p => p is StatPart_FpForm))
            {
                return;
            }
            StatPart_FpForm part = new StatPart_FpForm { table = s.table, failChance = s.failChance, parentStat = stat };
            if (stat.parts == null)
            {
                stat.parts = new List<StatPart>();
            }
            stat.parts.Add(part);
            // The game decided which stats never change (StatDef.SetImmutability) before static constructors run, and caches
            // such a stat per thing for good: one without parts until now would keep its first value and never see this.
            if (stat.immutable)
            {
                stat.immutable = false;
                stat.Worker.SetCacheability(false);
            }
        }

        private static void QualityByPawn_Prefix(Pawn pawn)
        {
            pendingOffset = FormOf(pawn);
        }

        private static void QualityByPawn_Finalizer()
        {
            pendingOffset = 0;
        }

        // F levels on the worker's skill for this roll only (spec 10.5); the game's Gaussian, inspiration and role offset
        // stay as they are.
        private static void QualityByLevel_Prefix(ref int relevantSkillLevel)
        {
            if (pendingOffset == 0)
            {
                return;
            }
            relevantSkillLevel = Mathf.Clamp(relevantSkillLevel + pendingOffset, 0, 20);
            FpHits.Hit("Quality offset");
        }
    }
}
