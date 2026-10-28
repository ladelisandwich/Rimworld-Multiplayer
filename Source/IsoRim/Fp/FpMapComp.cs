using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace IsoRim.Fp
{
    // The synced side of first-person play (fp-play-spec 5, 9): which player controls which pawn, saved with each map in
    // its FpMapComp, plus a static index (FpRegistry) over every map for fast lookups. Everything here that changes state
    // runs in the simulation (commands, the map tick, loading) and reads only synced state: never Input, Time, the 3D
    // view, IsoRim or first-person settings, FpIds.Local or Multiplayer's player list.

    // Movement sectors are 0..15 (FpMath.SectorOf); Stop is no movement.
    public static class FpDir
    {
        public const byte Stop = 255;
    }

    // FpSetControl's flags.
    public static class FpFlags
    {
        public const byte AutoNeeds = 1, Checks = 2, Force = 4;
    }

    // A skill check's result (FpCheckResult's outcome).
    public static class FpOutcome
    {
        public const byte MissTimeout = 0, Miss = 1, Hit = 2, Great = 3;
    }

    // One controlled pawn (spec 9.1; B saves every field, F owns the meaning of the check fields).
    public sealed class FpEntry : IExposable
    {
        public Pawn pawn;
        public Faction faction;
        public int controller = FpIds.None;
        public byte dir = FpDir.Stop;
        public byte flags;
        public byte facing = 255;              // M6
        public int checkJob = -1;
        public int tallyKey;
        public int p0;                         // per mille
        public int planned;
        public int issued;
        public int seq;
        public int pendingSeq;
        public int pendingTick;
        public int deadlineTick;
        public int lastResolveTick = -99999;
        public float segStart;
        public bool held;                      // change only through FpRegistry.SetHeld (it keeps HeldCount)
        public byte lastOutcome = 255;
        public int lastOutcomeTick = -99999;
        public float carry;                    // repair rounding

        // The component whose list holds this entry. Not saved: set when the entry is added, after loading and by
        // FpRegistry.Rebuild. An entry only counts on its own map (a pawn that just left it is released by that map).
        internal FpMapComp comp;

        // Default values equal the field initialisers, so a field at its default isn't written.
        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref controller, "ctrl", FpIds.None);
            Scribe_Values.Look(ref dir, "dir", FpDir.Stop);
            Scribe_Values.Look(ref flags, "flags", (byte)0);
            Scribe_Values.Look(ref facing, "facing", (byte)255);
            Scribe_Values.Look(ref checkJob, "checkJob", -1);
            Scribe_Values.Look(ref tallyKey, "tallyKey", 0);
            Scribe_Values.Look(ref p0, "p0", 0);
            Scribe_Values.Look(ref planned, "planned", 0);
            Scribe_Values.Look(ref issued, "issued", 0);
            Scribe_Values.Look(ref seq, "seq", 0);
            Scribe_Values.Look(ref pendingSeq, "pendingSeq", 0);
            Scribe_Values.Look(ref pendingTick, "pendingTick", 0);
            Scribe_Values.Look(ref deadlineTick, "deadlineTick", 0);
            Scribe_Values.Look(ref lastResolveTick, "lastResolve", -99999);
            Scribe_Values.Look(ref segStart, "segStart", 0f);
            Scribe_Values.Look(ref held, "held", false);
            Scribe_Values.Look(ref lastOutcome, "lastOutcome", (byte)255);
            Scribe_Values.Look(ref lastOutcomeTick, "lastOutcomeTick", -99999);
            Scribe_Values.Look(ref carry, "carry", 0f);
        }
    }

    // A worked piece's check results (spec 10.6).
    public sealed class FpTally : IExposable
    {
        public int key;
        public Thing target;
        public Pawn owner;
        public int jobLoadId;
        public int great, hit, miss, lastTick;

        public int Count => great + hit + miss;

        public int Form => FpMath.Form(great, hit, miss);

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key", 0);
            Scribe_References.Look(ref target, "target");
            Scribe_References.Look(ref owner, "owner");
            Scribe_Values.Look(ref jobLoadId, "jobLoadId", 0);
            Scribe_Values.Look(ref great, "great", 0);
            Scribe_Values.Look(ref hit, "hit", 0);
            Scribe_Values.Look(ref miss, "miss", 0);
            Scribe_Values.Look(ref lastTick, "lastTick", 0);
        }
    }

    public sealed class FpMapComp : MapComponent
    {
        public readonly List<FpEntry> entries = new List<FpEntry>();   // sorted by pawn.thingIDNumber
        public readonly List<FpTally> tallies = new List<FpTally>();   // sorted by key
        public readonly List<Bill> fpBills = new List<Bill>();         // bills first person created on this map (D maintains)

        // Map -> its component, so For() doesn't walk the map's ~50 components on every call. Lookups only; replaced with
        // the registry for each new Game (possibly on a loading thread, hence a new instance), a removed map drops its key.
        private static Dictionary<Map, FpMapComp> byMap = new Dictionary<Map, FpMapComp>();

        public FpMapComp(Map map) : base(map)
        {
        }

        // Cached lookup, null for a null map.
        public static FpMapComp For(Map map)
        {
            if (map == null)
            {
                return null;
            }
            Dictionary<Map, FpMapComp> d = byMap;
            if (d.TryGetValue(map, out FpMapComp comp) && comp != null && comp.map == map)
            {
                return comp;
            }
            comp = map.GetComponent<FpMapComp>();
            if (comp != null)
            {
                d[map] = comp;
            }
            return comp;
        }

        internal static void ClearCache()
        {
            byMap = new Dictionary<Map, FpMapComp>();
        }

        public FpEntry Get(Pawn p)
        {
            int i = IndexOfEntry(p);
            return i >= 0 ? entries[i] : null;
        }

        // Add or update; keeps the sort; updates the registry. An existing entry only gets the new controller, faction
        // and flags: its movement and check state stay (FpSync releases an entry first when another player takes it).
        public FpEntry Control(Pawn p, int player, Faction faction, byte flags)
        {
            int i = IndexOfEntry(p);
            if (i >= 0)
            {
                FpEntry e = entries[i];
                e.controller = player;
                e.faction = faction;
                e.flags = flags;
                e.comp = this;
                return e;
            }
            FpEntry added = new FpEntry { pawn = p, faction = faction, controller = player, flags = flags, comp = this };
            entries.Insert(~i, added);
            FpRegistry.Added(added);
            return added;
        }

        // Map context only (spec 5.6): the skill-check and bill clean-up first, then the control job ends and the entry
        // goes. The entry is marked released before the job ends, so the think tree, which runs inside EndCurrentJob,
        // doesn't hand the control job straight back.
        public void Release(FpEntry e, bool countPendingAsMiss)
        {
            if (e == null)
            {
                return;
            }
            FpWork.OnRelease(this, e, countPendingAsMiss);
            FpBills.OnRelease(this, e);
            e.controller = FpIds.None;
            e.dir = FpDir.Stop;
            FpRegistry.SetHeld(e, false);
            Pawn p = e.pawn;
            // Only a pawn on this map: ending the job of a pawn that already moved to another map would change that map
            // from this one's tick. There, the control job ends itself once the entry is gone.
            if (p != null && p.Spawned && p.Map == map && FpControlJob.InControlJob(p))
            {
                p.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
            }
            if (entries.Remove(e))
            {
                FpRegistry.Removed(e);
            }
        }

        public FpTally TallyByKey(int key)
        {
            int i = IndexOfTally(key);
            return i >= 0 ? tallies[i] : null;
        }

        // The tally of key; a new one gets target, owner and jobLoadId (an existing one keeps its own).
        public FpTally GetOrAddTally(int key, Thing target, Pawn owner, int jobLoadId)
        {
            int i = IndexOfTally(key);
            if (i >= 0)
            {
                return tallies[i];
            }
            FpTally t = new FpTally { key = key, target = target, owner = owner, jobLoadId = jobLoadId };
            tallies.Insert(~i, t);
            FpRegistry.TallyAdded();
            return t;
        }

        public void RemoveTally(FpTally t)
        {
            if (t != null && tallies.Remove(t))
            {
                FpRegistry.TallyRemoved();
            }
        }

        // Data only, every map (safe in the world command context): the player's entries lose their controller and stop;
        // each map's own tick then releases them (spec 5.7). No job changes here.
        public static void ReleaseAllOf(int playerId)
        {
            foreach (FpMapComp comp in AllComps())
            {
                foreach (FpEntry e in comp.entries)
                {
                    if (e.controller == playerId)
                    {
                        e.controller = FpIds.None;
                        e.dir = FpDir.Stop;
                    }
                }
            }
        }

        // Data only: every entry loses its controller (player ids of an earlier session mean nothing in this one).
        public static void ResetAll()
        {
            foreach (FpMapComp comp in AllComps())
            {
                foreach (FpEntry e in comp.entries)
                {
                    e.controller = FpIds.None;
                    e.dir = FpDir.Stop;
                }
            }
        }

        // Every map's component in map order (Game.Maps), for the data-only sweeps and the registry.
        internal static IEnumerable<FpMapComp> AllComps()
        {
            List<Map> maps = Current.Game?.Maps;
            if (maps == null)
            {
                yield break;
            }
            for (int i = 0; i < maps.Count; i++)
            {
                FpMapComp comp = For(maps[i]);
                if (comp != null)
                {
                    yield return comp;
                }
            }
        }

        // Each tick in this map's context (spec 5.7): release every entry whose pawn can't be controlled any more, run the
        // skill-check tick of the others, then the periodic clean-ups.
        public override void MapComponentTick()
        {
            if (entries.Count > 0)
            {
                foreach (FpEntry e in entries.ToArray())   // a copy: Release removes from the list
                {
                    if (!StillControlled(e))
                    {
                        Release(e, false);
                        continue;
                    }
                    FpWork.Tick(this, e);
                }
            }
            int t = Find.TickManager.TicksGame;
            if (t % 60 == 0)
            {
                FpBills.Tick(this);
            }
            if (t % 250 == 0)
            {
                FpWork.PruneTallies(this);
                if (Prefs.DevMode)
                {
                    FpRegistry.Verify(this);   // logging only
                }
            }
            // B keeps this as the last line of its tick (spec 5.7): the self-test's state lines.
            if (FpTestKit.Active)
            {
                FpTestKit.OnMapTick(this);
            }
        }

        private bool StillControlled(FpEntry e)
        {
            Pawn p = e.pawn;
            return e.controller != FpIds.None
                && p != null && !p.Destroyed && p.Spawned && p.Map == map && !p.Dead && !p.Downed && !p.InMentalState
                && p.Faction == e.faction && !p.IsPrisoner && p.IsColonistPlayerControlled;
        }

        // Saved: entries, tallies and first-person bills (spec 9.1). The lists are readonly, so each is loaded into a
        // local and copied in whenever the game's loader hands back a new list (LoadingVars for Deep, ResolvingCrossRefs
        // for references).
        public override void ExposeData()
        {
            LookList(entries, "fpEntries", LookMode.Deep);
            LookList(tallies, "fpTallies", LookMode.Deep);
            LookList(fpBills, "fpBills", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                CleanUpAfterLoading();
            }
        }

        private static void LookList<T>(List<T> field, string label, LookMode mode)
        {
            List<T> list = field;
            Scribe_Collections.Look(ref list, label, mode);
            if (Scribe.mode != LoadSaveMode.Saving && !ReferenceEquals(list, field))
            {
                field.Clear();
                if (list != null)
                {
                    field.AddRange(list);
                }
            }
        }

        // Drops what lost its reference (a pawn or bill that no longer exists), keeps a tally whose target is gone only
        // while its owner exists (a job-keyed tally, or the piece's last moments), and restores the sort with one entry
        // per pawn and one tally per key (first one in saved order wins).
        private void CleanUpAfterLoading()
        {
            List<FpEntry> keptEntries = entries.Where(e => e != null && e.pawn != null)
                .OrderBy(e => e.pawn.thingIDNumber).ToList();
            entries.Clear();
            foreach (FpEntry e in keptEntries)
            {
                if (entries.Count == 0 || entries[entries.Count - 1].pawn.thingIDNumber != e.pawn.thingIDNumber)
                {
                    e.comp = this;
                    entries.Add(e);
                }
            }
            List<FpTally> keptTallies = tallies.Where(t => t != null && (t.target != null || t.owner != null))
                .OrderBy(t => t.key).ToList();
            tallies.Clear();
            foreach (FpTally t in keptTallies)
            {
                if (tallies.Count == 0 || tallies[tallies.Count - 1].key != t.key)
                {
                    tallies.Add(t);
                }
            }
            fpBills.RemoveAll(b => b == null);
        }

        // After loading or generating the map. Outside a session nobody can be in control after a load (single player
        // starts uncontrolled; tallies and bills stay). In a session the entries are kept: every join point reloads every
        // player's game, and control must survive that (spec 6.4).
        public override void FinalizeInit()
        {
            if (!MpBridge.InSession)
            {
                entries.Clear();
            }
            FpRegistry.Rebuild();
        }

        public override void MapRemoved()
        {
            FpRegistry.Forget(this);
            byMap.Remove(map);
        }

        // Binary searches over the sorted lists: the index, or the bitwise complement of the insertion point.
        private int IndexOfEntry(Pawn p)
        {
            if (p == null)
            {
                return ~entries.Count;
            }
            int id = p.thingIDNumber;
            int lo = 0, hi = entries.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = entries[mid].pawn.thingIDNumber.CompareTo(id);
                if (c == 0)
                {
                    return entries[mid].pawn == p ? mid : ~mid;
                }
                if (c < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return ~lo;
        }

        private int IndexOfTally(int key)
        {
            int lo = 0, hi = tallies.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = tallies[mid].key.CompareTo(key);
                if (c == 0)
                {
                    return mid;
                }
                if (c < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return ~lo;
        }
    }

    // A derived index over every map's FpMapComp; lookups only, never iterated for decisions. Never saved: cleared for
    // each new Game (constructor postfix, FpControlJob) and rebuilt from the components when a map is finalised.
    // ControlledCount counts index entries, which include an entry marked released (controller None) until its map's tick
    // removes it: it is a fast "anything to do?" test, and every caller still checks the entry's controller.
    //
    // The dictionary is replaced, never cleared in place: the Game constructor can run on a loading thread while the
    // interface still reads the old one.
    public static class FpRegistry
    {
        private static Dictionary<Pawn, FpEntry> byPawn = new Dictionary<Pawn, FpEntry>();
        private static int heldCount;
        private static int tallyCount;

        public static int ControlledCount
        {
            get { return byPawn.Count; }
        }

        public static int HeldCount
        {
            get { return heldCount; }
        }

        public static int TallyCount
        {
            get { return tallyCount; }
        }

        public static FpEntry EntryOf(Pawn p)
        {
            Dictionary<Pawn, FpEntry> d = byPawn;
            if (p == null || d.Count == 0)
            {
                return null;
            }
            return d.TryGetValue(p, out FpEntry e) ? e : null;
        }

        // Via FpWork.TallyKeyOf(p, p.CurJob), in the pawn's own map's component.
        public static FpTally TallyForWorker(Pawn p)
        {
            if (tallyCount == 0 || p?.Map == null || p.CurJob == null)
            {
                return null;
            }
            FpMapComp comp = FpMapComp.For(p.Map);
            if (comp == null || comp.tallies.Count == 0)
            {
                return null;
            }
            return comp.TallyByKey(FpWork.TallyKeyOf(p, p.CurJob));
        }

        // The only way to change FpEntry.held: it keeps HeldCount, the hold prefix's fast test, in step.
        public static void SetHeld(FpEntry e, bool held)
        {
            if (e == null || e.held == held)
            {
                return;
            }
            e.held = held;
            if (Indexed(e))
            {
                heldCount += held ? 1 : -1;
            }
        }

        // From every map's FpMapComp, in map order.
        internal static void Rebuild()
        {
            Dictionary<Pawn, FpEntry> d = new Dictionary<Pawn, FpEntry>();
            int held = 0;
            int tallies = 0;
            foreach (FpMapComp comp in FpMapComp.AllComps())
            {
                foreach (FpEntry e in comp.entries)
                {
                    e.comp = comp;
                    if (e.pawn != null && !d.ContainsKey(e.pawn))
                    {
                        d.Add(e.pawn, e);
                        if (e.held)
                        {
                            held++;
                        }
                    }
                }
                tallies += comp.tallies.Count;
            }
            byPawn = d;
            heldCount = held;
            tallyCount = tallies;
        }

        // Game constructor postfix: nothing from the previous game stays indexed.
        internal static void Clear()
        {
            byPawn = new Dictionary<Pawn, FpEntry>();
            heldCount = 0;
            tallyCount = 0;
            FpMapComp.ClearCache();
        }

        internal static void Added(FpEntry e)
        {
            if (e.pawn == null || byPawn.ContainsKey(e.pawn))
            {
                return;
            }
            byPawn.Add(e.pawn, e);
            if (e.held)
            {
                heldCount++;
            }
        }

        internal static void Removed(FpEntry e)
        {
            if (!Indexed(e))
            {
                return;
            }
            byPawn.Remove(e.pawn);
            if (e.held)
            {
                heldCount--;
            }
        }

        internal static void TallyAdded()
        {
            tallyCount++;
        }

        internal static void TallyRemoved()
        {
            tallyCount--;
        }

        // A map is gone (MapRemoved): its entries and tallies leave the index.
        internal static void Forget(FpMapComp comp)
        {
            foreach (FpEntry e in comp.entries)
            {
                Removed(e);
            }
            tallyCount -= comp.tallies.Count;
            if (tallyCount < 0)
            {
                tallyCount = 0;
            }
        }

        private static bool Indexed(FpEntry e)
        {
            return e.pawn != null && byPawn.TryGetValue(e.pawn, out FpEntry indexed) && indexed == e;
        }

        // Dev mode, every 250 ticks of each map: the index must agree with the components. Logs a mismatch once and
        // never repairs it (a repair would hide the bug that caused it).
        internal static void Verify(FpMapComp self)
        {
            int entries = 0, held = 0, tallies = 0;
            foreach (FpMapComp comp in FpMapComp.AllComps())
            {
                entries += comp.entries.Count;
                tallies += comp.tallies.Count;
                foreach (FpEntry e in comp.entries)
                {
                    if (e.held)
                    {
                        held++;
                    }
                }
            }
            string bad = null;
            foreach (FpEntry e in self.entries)
            {
                if (EntryOf(e.pawn) != e)
                {
                    bad = "entry of " + e.pawn.ToStringSafe() + " on map " + self.map.uniqueID + " isn't indexed";
                    break;
                }
            }
            if (bad == null && (entries != byPawn.Count || held != heldCount || tallies != tallyCount))
            {
                bad = "maps hold " + entries + " entries, " + held + " held, " + tallies + " tallies; the index has "
                    + byPawn.Count + ", " + heldCount + ", " + tallyCount;
            }
            if (bad != null)
            {
                Log.ErrorOnce("[IsoRim] First person: the control index disagrees with the maps (" + bad + ").", 93652301);
            }
        }
    }

    // Read-only queries for UI and simulation.
    public static class FpState
    {
        // FpIds.None if not controlled.
        public static int ControllerOf(Pawn p)
        {
            FpEntry e = FpRegistry.EntryOf(p);
            return e == null ? FpIds.None : e.controller;
        }

        // ControllerOf(p) == FpIds.Local && Local != None
        public static bool LocalControls(Pawn p)
        {
            int local = FpIds.Local;
            return local != FpIds.None && ControllerOf(p) == local;
        }

        // Null if under control, else a translated reason. UI text; the simulation tests AutopilotKey instead, which has
        // the same null and doesn't depend on the player's language.
        public static string AutopilotReason(Pawn p)
        {
            string key = AutopilotKey(p);
            return key == null ? null : key.Translate().Resolve();
        }

        // The reason's translation key, null while the player is in charge of the pawn. Synced state only.
        internal static string AutopilotKey(Pawn p)
        {
            FpEntry e = FpRegistry.EntryOf(p);
            if (p == null || e == null || e.controller == FpIds.None)
            {
                return "R3D_Fp_AutoNotControlled";
            }
            if (p.GetLord() != null)
            {
                return "R3D_Fp_AutoLord";
            }
            if (FpControlJob.NeedsAutopilot(e, p))
            {
                return "R3D_Fp_AutoNeeds";
            }
            return null;
        }

        // Spec 9.3, appended to Multiplayer's per-tick pawn digest (PawnStateTrace) and hashed into the desync traces, so
        // it holds synced fields only, formatted the same on every machine. "" when the pawn has no entry and no tally.
        public static string Describe(Pawn p)
        {
            if (p == null)
            {
                return "";
            }
            FpEntry e = FpRegistry.EntryOf(p);
            FpTally t = FpRegistry.TallyForWorker(p);
            if (e == null && t == null)
            {
                return "";
            }
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "fp c=" + (e?.controller ?? FpIds.None).ToString(inv)
                + " d=" + (e?.dir ?? FpDir.Stop).ToString(inv)
                + " f=" + (e?.flags ?? 0).ToString(inv)
                + " j=" + (e?.checkJob ?? -1).ToString(inv)
                + " q=" + (e?.issued ?? 0).ToString(inv) + "/" + (e?.planned ?? 0).ToString(inv)
                + " s=" + (e?.pendingSeq ?? 0).ToString(inv)
                + " h=" + (e != null && e.held ? "1" : "0")
                + " t=" + (t?.great ?? 0).ToString(inv) + "/" + (t?.hit ?? 0).ToString(inv) + "/" + (t?.miss ?? 0).ToString(inv);
        }
    }
}
