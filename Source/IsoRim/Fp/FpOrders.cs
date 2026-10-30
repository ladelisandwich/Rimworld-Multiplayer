using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim.Fp
{
    // Track D (fp-play-spec 11.4, M4): work orders from first person on what the crosshair points at, without picking a
    // designator first. The left button on a rock mines it, on a ripe crop or a grown tree harvests or chops it, on a young
    // tree cuts it. The order goes out as FpOrderWork, which runs on every machine at the same tick with the issuer's
    // faction pushed: it adds the game's own designation if the target has none (Mine, HarvestPlant or CutPlant, exactly
    // what the game's designators would add), then gives the colonist the game's own work-giver job for that one target,
    // as the game's "Prioritize ..." order does (TryTakeOrderedJobPrioritizedWork). The skill checks (track F) follow from
    // the job like any other skilled work.
    //
    // Which order a target gets (KindOf), shared by the hint, the click and the command:
    // - a natural rock or ore (def.mineable, not smoothed) with no designation other than Mine or MineVein: Mine;
    // - a plant whose cutting isn't prevented, with no designation other than HarvestPlant or CutPlant:
    //   - HarvestPlant already on it, or none and ripe (HarvestableNow) with the game's "Standard" harvest (crops,
    //     berries, healroot) or a tree's wood: Harvest ("Harvest rice plant", "Chop oak tree"), as the game's Harvest and
    //     Chop wood designators;
    //   - CutPlant already on it, or none and a tree that isn't ripe (a young tree, a stump): Cut, as the Cut designator;
    // - anything else is left to the next handler (the float menu): grass, unripe crops, a smoothed wall (mining it would
    //   open the base to a stray click), a tree marked for extraction, a wall marked for smoothing.
    //
    // Command bodies are simulation code: synced state only, no Rand, no settings (spec 2). OrderHint, TryOrderWork and the
    // sender are interface code: they never change the game.
    public static class FpOrders
    {
        // kind 0 Mine, 1 Harvest, 2 Cut (spec 9.2); None: not an order target.
        internal const byte KindMine = 0, KindHarvest = 1, KindCut = 2, KindNone = 255;

        // A request equal to the last one sent is dropped for this long, so an order in flight isn't stacked (spec 9.2: at
        // most one in flight). A different order goes at once; one client's commands run in the order sent.
        private const float ResendSeconds = 1f;

        private static int lastPawn = -1;
        private static int lastTarget = -1;
        private static byte lastKind = KindNone;
        private static float lastAt = -999f;

        // The issuer's last refusal key, for the self-test only (never read by the simulation).
        internal static string LastToldKey;

        private static readonly Color RefusedColour = new Color(0.878f, 0.353f, 0.353f);   // #E05A5A, the HUD's

        // ---------------------------------------------------------------- interface

        // The left button (spec 8.3, 11.4): a rock or plant this colonist can work on, in reach, sends FpOrderWork and
        // returns true. A target this colonist can't work on (incapable, no manipulation, outside the allowed area) or is
        // already working on is also taken, with a message and no command. False leaves the click to the float menu: not
        // an order target, or the colonist drafted (the float menu has the drafted orders).
        public static bool TryOrderWork(Pawn p, FpHit hit)
        {
            Thing t = Target(p, hit, out byte kind);
            if (t == null)
            {
                return false;
            }
            if (AlreadyOn(p, t))
            {
                FpHud.Toast("R3D_Fp_OrderAlready".Translate(p.LabelShort), Color.white);
                return true;
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
            string why = CapabilityRefusal(p, t, kind, out string _);
            if (why != null)
            {
                FpHud.Toast(why, RefusedColour);
                return true;
            }
            if (!TrySendOrderWork(p, t, kind))
            {
                FpHud.Toast("R3D_Fp_BillNotSent".Translate(), RefusedColour);
                return true;
            }
            FpHud.Toast(Label(t, kind), Color.white);
            return true;
        }

        // "Mine granite", "Chop oak tree", "Harvest rice plant", "Cut young oak tree", or null: not an order target, or one
        // this colonist can't work on (then the float menu's label, or RefusalHint, says why). Null too while the game
        // allows the colonist no orders at all (a lord, deathrest): FpActions shows the game's reason then.
        public static string OrderHint(Pawn p, FpHit hit)
        {
            Thing t = Target(p, hit, out byte kind);
            if (t == null || !FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(p).Accepted || CapabilityRefusal(p, t, kind, out string _) != null)
            {
                return null;
            }
            return Label(t, kind);
        }

        // Why the left button can't order work on the crosshair's rock or plant (incapable, no manipulation, outside the
        // allowed area), or null. For the hint with HintKind Invalid (FpActions, after OrderHint returned null).
        internal static string RefusalHint(Pawn p, FpHit hit)
        {
            Thing t = Target(p, hit, out byte kind);
            return t == null ? null : CapabilityRefusal(p, t, kind, out string _);
        }

        // The crosshair's thing when it is an order target for this colonist now (spec 11.4, UI rule), and its kind.
        private static Thing Target(Pawn p, FpHit hit, out byte kind)
        {
            kind = KindNone;
            if (p == null || !p.Spawned || !hit.Valid || hit.Thing == null)
            {
                return null;
            }
            Thing t = hit.Thing;
            if (!t.Spawned || t.Map != p.Map || !hit.InReach && !FpSettings.interactAnyDistance)
            {
                return null;
            }
            kind = KindOf(t, p.Map);
            if (kind == KindNone || p.Drafted && !AnyGiverWhileDrafted(kind))
            {
                kind = KindNone;
                return null;
            }
            return t;
        }

        // The colonist is already working on this target (the command would keep the job as it is): the hint stays, a
        // click only says so.
        private static bool AlreadyOn(Pawn p, Thing t)
        {
            Job cur = p.CurJob;
            return cur != null && cur.targetA.Thing == t && (cur.def == JobDefOf.Mine || p.jobs.curDriver is JobDriver_PlantWork);
        }

        private static string Label(Thing t, byte kind)
        {
            switch (kind)
            {
                case KindMine:
                    return "R3D_Fp_OrderMine".Translate(t.LabelShort);
                case KindHarvest:
                    return (t.def.plant != null && t.def.plant.IsTree ? "R3D_Fp_OrderChop" : "R3D_Fp_OrderHarvest").Translate(t.LabelShort);
                default:
                    return "R3D_Fp_OrderCut".Translate(t.LabelShort);
            }
        }

        // The sender: false when the command didn't go out (Multiplayer takes no command during a long event, or this runs
        // in a tick).
        internal static bool TrySendOrderWork(Pawn p, Thing t, byte kind)
        {
            if (MpBridge.InSession && !MP.InInterface)
            {
                return false;
            }
            if (p == null || t == null || kind > KindCut)
            {
                return false;
            }
            if (MpBridge.InSession)
            {
                float now = Time.realtimeSinceStartup;
                if (p.thingIDNumber == lastPawn && t.thingIDNumber == lastTarget && kind == lastKind && now - lastAt < ResendSeconds && now >= lastAt)
                {
                    return true;   // the same order is already in flight
                }
                lastPawn = p.thingIDNumber;
                lastTarget = t.thingIDNumber;
                lastKind = kind;
                lastAt = now;
            }
            FpHits.Hit("Order work sent");
            FpOrderWork(p, t, kind);
            return true;
        }

        // ---------------------------------------------------------------- command

        // Spec 11.4. The queue key's state travels with the command (QueueOrder_Down), as with the bill commands: the game's
        // TryTakeOrderedJob reads it, and read live in a tick it would be whatever this machine's player holds then.
        [SyncMethod(SyncContext.QueueOrder_Down, cancelIfAnyArgNull = true)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FpOrderWork(Pawn p, Thing target, byte kind)
        {
            int me = FpIds.Executing;
            string result = "-";
            try
            {
                result = OrderWork(p, target, kind, me);
            }
            finally
            {
                if (FpTestKit.Active)
                {
                    FpTestKit.OnCommand(me, "FpOrderWork p=" + p.thingIDNumber + " target=" + target.thingIDNumber + " kind=" + kind
                        + " by=" + me + " -> " + result);
                }
            }
        }

        // FpOrderWork's body; returns what happened, for the self-test's command line (the same on every machine).
        private static string OrderWork(Pawn p, Thing target, byte kind, int me)
        {
            if (!Authorized(p, me))
            {
                FpHits.Hit("Order work refused");
                return "unauthorized";
            }
            if (kind > KindCut)
            {
                FpHits.Hit("Order work refused");
                return "bad kind";
            }
            Map map = p.Map;
            if (!target.Spawned || target.Map != map || target.Position.Fogged(map))
            {
                return Refuse(p, null, "R3D_Fp_OrderGone", target.LabelShort);
            }
            if (KindOf(target, map) != kind)
            {
                // Changed since the click (someone designated it otherwise, it ripened, it was smoothed).
                return Refuse(p, target, "R3D_Fp_OrderChanged", target.LabelShort);
            }
            if (p.Drafted && !AnyGiverWhileDrafted(kind))
            {
                return Refuse(p, target, "R3D_Fp_OrderDrafted", p.LabelShort);
            }
            string why = CapabilityRefusal(p, target, kind, out string whyKey);
            if (why != null)
            {
                return RefuseText(p, target, whyKey, why);
            }
            if (!p.CanReach(target, PathEndMode.Touch, Danger.Deadly))
            {
                return Refuse(p, target, "R3D_Fp_OrderNoPath", p.LabelShort, target.LabelShort);
            }

            // Spec 11.4 step 1: the game's own designation, which its work givers look for. Taken back below if no job
            // comes of it, so a refused order leaves nothing behind for the colony's AI.
            DesignationManager dm = map.designationManager;
            Designation added = null;
            if (!HasOwnDesignation(dm, target, kind))
            {
                added = kind == KindMine ? new Designation(target.Position, DesignationDefOf.Mine)
                    : new Designation(target, kind == KindHarvest ? DesignationDefOf.HarvestPlant : DesignationDefOf.CutPlant);
                dm.AddDesignation(added);
            }

            // Steps 2 and 3: the work giver's job on this target, ordered as "Prioritize".
            Job job = WorkJob(p, target, kind, out WorkGiver_Scanner giver, out string reason);
            if (job == null)
            {
                TakeBack(dm, added);
                string text = "R3D_Fp_OrderNoJob".Translate(p.LabelShort, target.LabelShort);
                return RefuseText(p, target, "R3D_Fp_OrderNoJob", reason.NullOrEmpty() ? text : text + " (" + reason + ")");
            }
            if (p.CurJob != null && p.CurJob.JobIsSameAs(p, job))
            {
                // Already doing exactly this (the order arrived twice): the game would keep the current job anyway.
                JobMaker.ReturnToPool(job);
                return "same job " + p.CurJob.loadID;
            }
            ForfeitBefore(p, job);
            job.workGiverDef = giver.def;   // as the game's own "Prioritize" option sets it, before the job starts
            if (!p.jobs.TryTakeOrderedJobPrioritizedWork(job, giver, target.Position))
            {
                TakeBack(dm, added);
                return Refuse(p, target, "R3D_Fp_OrderCantStart", p.LabelShort, target.LabelShort);
            }
            if (added != null)
            {
                WarnIssuer(target, kind);
                FpHits.Hit("Order work designated");
            }
            FpHits.Hit("Order work");
            return "ordered " + job.def.defName + " job " + job.loadID + " giver " + giver.def.defName + (added != null ? " designated" : "");
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

        // The first work giver (work types in def order, then each type's givers by priority, as the game's float menu lists
        // them) that takes this target and gives this colonist a job on it. The colonist's work priorities aren't asked:
        // a player in first person works on what they point at, as with bills, whatever the work tab says; only a work
        // type the colonist is incapable of is out (spec 11.4: "enabled"). The game's reason for the first refusal comes
        // back for the message.
        private static Job WorkJob(Pawn p, Thing t, byte kind, out WorkGiver_Scanner giver, out string reason)
        {
            giver = null;
            reason = null;
            foreach (WorkGiverDef wg in Givers(kind))
            {
                if (GiverRefusal(p, wg, out string _) != null)
                {
                    continue;
                }
                WorkGiver_Scanner s = (WorkGiver_Scanner)wg.Worker;
                if (!Takes(p, s, t) || s.ShouldSkip(p, forced: true))
                {
                    continue;
                }
                JobFailReason.Clear();
                Job job = s.HasJobOnThing(p, t, forced: true) ? s.JobOnThing(p, t, forced: true) : null;
                if (reason == null && JobFailReason.HaveReason)
                {
                    reason = JobFailReason.Reason;
                }
                JobFailReason.Clear();
                if (job != null && !Fits(job, kind))
                {
                    // Another use of the same plant (pruning a Gauranlen tree): not what the left button offered.
                    JobMaker.ReturnToPool(job);
                    job = null;
                }
                if (job != null)
                {
                    giver = s;
                    return job;
                }
            }
            return null;
        }

        // A harvest or cut order must end in the game's plant work on it (JobDriver_PlantWork: harvest, chop, cut). A mine
        // order takes whatever the mining giver gives: the mine job, or first hauling aside a thing that blocks the only
        // standing spot (MineAIUtility).
        private static bool Fits(Job job, byte kind)
        {
            if (kind == KindMine)
            {
                return true;
            }
            System.Type driver = job.def?.driverClass;
            return driver != null && typeof(JobDriver_PlantWork).IsAssignableFrom(driver);
        }

        // The scanner looks at this thing: its thing request, or its own list (the game's float menu asks the same).
        private static bool Takes(Pawn p, WorkGiver_Scanner s, Thing t)
        {
            if (s.PotentialWorkThingRequest.Accepts(t))
            {
                return true;
            }
            IEnumerable<Thing> things = s.PotentialWorkThingsGlobal(p);
            if (things == null)
            {
                return false;
            }
            foreach (Thing x in things)
            {
                if (x == t)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasOwnDesignation(DesignationManager dm, Thing t, byte kind)
        {
            if (kind == KindMine)
            {
                return dm.DesignationAt(t.Position, DesignationDefOf.Mine) != null || dm.DesignationAt(t.Position, DesignationDefOf.MineVein) != null;
            }
            return dm.DesignationOn(t, kind == KindHarvest ? DesignationDefOf.HarvestPlant : DesignationDefOf.CutPlant) != null;
        }

        private static void TakeBack(DesignationManager dm, Designation added)
        {
            if (added != null)
            {
                dm.RemoveDesignation(added);
            }
        }

        // Spec 10.2 (integration changes), as the bill commands do: the colonist's new job replaces one with a skill check
        // showing, which then counts as missed (another order doesn't escape a bad check). Not when the game would keep
        // the current job (the same job again).
        private static void ForfeitBefore(Pawn p, Job job)
        {
            if (p.CurJob != null && !p.CurJob.JobIsSameAs(p, job))
            {
                FpWork.ForfeitPending(FpRegistry.EntryOf(p));
            }
        }

        // The warnings the game's designators give when they add these designations (precepts against mining or tree
        // cutting, an important plant), to the issuer only.
        private static void WarnIssuer(Thing t, byte kind)
        {
            if (MpBridge.InSession && !MP.IsExecutingSyncCommandIssuedBySelf)
            {
                return;
            }
            if (kind == KindMine)
            {
                if (!ModsConfig.IdeologyActive)
                {
                    return;
                }
                // Designator_Mine.PossiblyWarnPlayerOnDesignatingMining (protected there).
                List<string> names = new List<string>();
                foreach (Ideo ideo in Faction.OfPlayer.ideos.AllIdeos)
                {
                    if (ideo.WarnPlayerOnDesignateMine)
                    {
                        names.Add(Find.ActiveLanguageWorker.Pluralize(ideo.memberName));
                    }
                }
                if (names.Count > 0)
                {
                    Messages.Message("MessageWarningPlayerDesignatedMining".Translate(names.ToCommaList(useAnd: true)), MessageTypeDefOf.CautionInput, historical: false);
                }
                return;
            }
            Designator_PlantsHarvestWood.PossiblyWarnPlayerImportantPlantDesignateCut(t);
            if (ModsConfig.IdeologyActive && t.def.plant.IsTree && t.def.plant.treeLoversCareIfChopped)
            {
                Designator_PlantsHarvestWood.PossiblyWarnPlayerOnDesignatingTreeCut();
            }
        }

        // ---------------------------------------------------------------- shared tests (simulation and interface)

        // The order a thing gets (see the top of the file), or KindNone. Reads the designations of the faction in context:
        // the local player's in the interface, the issuer's in the command (Multiplayer swaps the map's designation manager
        // per faction), the same faction for a colonist this player controls.
        internal static byte KindOf(Thing t, Map map)
        {
            if (t == null || map == null)
            {
                return KindNone;
            }
            DesignationManager dm = map.designationManager;
            if (t.def.mineable)
            {
                if (t.def.IsSmoothed)
                {
                    return KindNone;
                }
                List<Designation> at = dm.AllDesignationsAt(t.Position);
                for (int i = 0; i < at.Count; i++)
                {
                    DesignationDef d = at[i].def;
                    if (d != DesignationDefOf.Mine && d != DesignationDefOf.MineVein)
                    {
                        return KindNone;   // marked for something else (smoothing): the float menu has that order
                    }
                }
                return KindMine;
            }
            if (!(t is Plant plant) || t.def.plant == null)
            {
                return KindNone;
            }
            CompPlantPreventCutting prevent = t.TryGetComp<CompPlantPreventCutting>();
            if (prevent != null && prevent.PreventCutting)
            {
                return KindNone;
            }
            bool harvestDes = false;
            bool cutDes = false;
            List<Designation> on = dm.AllDesignationsOn(t);
            for (int i = 0; i < on.Count; i++)
            {
                DesignationDef d = on[i].def;
                if (d == DesignationDefOf.HarvestPlant)
                {
                    harvestDes = true;
                }
                else if (d == DesignationDefOf.CutPlant)
                {
                    cutDes = true;
                }
                else
                {
                    return KindNone;   // marked for something else (extraction): the float menu has that order
                }
            }
            PlantProperties pp = t.def.plant;
            bool ripe = plant.HarvestableNow;
            if (harvestDes)
            {
                return ripe ? KindHarvest : KindNone;   // the work giver harvests only a ripe plant
            }
            if (cutDes)
            {
                return KindCut;
            }
            if (ripe && (pp.harvestTag == "Standard" || pp.IsTree))
            {
                return KindHarvest;
            }
            return pp.IsTree ? KindCut : KindNone;
        }

        // Why this colonist can't take an order of this kind on this thing, or null. Reads only (no reachability, no
        // reservations: the command checks those), so the hint and the click can ask it every time.
        internal static string CapabilityRefusal(Pawn p, Thing t, byte kind, out string key)
        {
            key = null;
            string first = null;
            string firstKey = null;
            bool any = false;
            foreach (WorkGiverDef wg in Givers(kind))
            {
                if (p.Drafted && !wg.canBeDoneWhileDrafted)
                {
                    continue;   // a drafted colonist isn't an order target (Target), and the command refuses it first
                }
                string why = GiverRefusal(p, wg, out string whyKey);
                if (why == null)
                {
                    any = true;
                    break;
                }
                if (first == null)
                {
                    first = why;
                    firstKey = whyKey;
                }
            }
            if (!any)
            {
                key = firstKey ?? "R3D_Fp_OrderNoGiver";
                return first ?? (string)key.Translate(t.LabelShort);
            }
            if (t.IsForbidden(p))
            {
                // The game's own refusal for a "Prioritize" order on a forbidden thing or one outside the allowed area.
                key = "R3D_Fp_OrderForbidden";
                string what;
                if (t.Position.InAllowedArea(p))
                {
                    what = "CannotPrioritizeForbidden".Translate(t.Label, t);
                }
                else
                {
                    Area area = p.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap;
                    what = ((string)"CannotPrioritizeForbiddenOutsideAllowedArea".Translate()).UncapitalizeFirst()
                        + (area != null ? " (" + area.Label + ")" : "");
                }
                return key.Translate(p.LabelShort, t.LabelShort, what);
            }
            return null;
        }

        // Why this colonist can't use this work giver at all, or null: incapable of its work type or tags, or a missing
        // capacity (no manipulation).
        private static string GiverRefusal(Pawn p, WorkGiverDef wg, out string key)
        {
            key = null;
            if (p.Drafted && !wg.canBeDoneWhileDrafted)
            {
                key = "R3D_Fp_OrderDrafted";
                return key.Translate(p.LabelShort);
            }
            if (wg.workType != null && p.WorkTypeIsDisabled(wg.workType) || p.WorkTagIsDisabled(wg.workTags))
            {
                key = "R3D_Fp_OrderIncapable";
                return key.Translate(p.LabelShort, wg.workType != null ? wg.workType.gerundLabel : wg.gerund);
            }
            PawnCapacityDef cap = wg.Worker.MissingRequiredCapacity(p);
            if (cap != null)
            {
                key = "R3D_Fp_OrderCapacity";
                return key.Translate(p.LabelShort, cap.label);
            }
            return null;
        }

        private static bool AnyGiverWhileDrafted(byte kind)
        {
            foreach (WorkGiverDef wg in Givers(kind))
            {
                if (wg.canBeDoneWhileDrafted)
                {
                    return true;
                }
            }
            return false;
        }

        // The work givers that may take an order of this kind (spec 11.4: Mining for a rock, Growing or PlantCutting for a
        // harvest, PlantCutting for a cut), in the game's float-menu order: work types in def order, then each type's
        // givers by priority. Only directly orderable scanners that look at things (a cell scanner such as the growing
        // zone's harvest giver works on a whole zone, not on the plant pointed at).
        private static IEnumerable<WorkGiverDef> Givers(byte kind)
        {
            List<WorkTypeDef> types = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            for (int i = 0; i < types.Count; i++)
            {
                WorkTypeDef wt = types[i];
                bool relevant = kind == KindMine ? wt == WorkTypeDefOf.Mining
                    : kind == KindHarvest ? wt == WorkTypeDefOf.PlantCutting || wt == WorkTypeDefOf.Growing
                    : kind == KindCut && wt == WorkTypeDefOf.PlantCutting;
                if (!relevant || wt.workGiversByPriority == null)
                {
                    continue;
                }
                for (int j = 0; j < wt.workGiversByPriority.Count; j++)
                {
                    WorkGiverDef wg = wt.workGiversByPriority[j];
                    if (wg != null && wg.directOrderable && wg.scanThings && wg.Worker is WorkGiver_Scanner)
                    {
                        yield return wg;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- messages

        private static string Refuse(Pawn p, Thing at, string key, params NamedArgument[] args)
        {
            TellIssuer(key, key.Translate(args), at ?? p);
            FpHits.Hit("Order work refused");
            return "refused " + key;
        }

        private static string RefuseText(Pawn p, Thing at, string key, string text)
        {
            TellIssuer(key, text, at ?? p);
            FpHits.Hit("Order work refused");
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
