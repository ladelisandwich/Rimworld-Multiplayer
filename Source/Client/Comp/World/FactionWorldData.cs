using RimWorld;
using Verse;

namespace Multiplayer.Client;

public class FactionWorldData : IExposable
{
    public int factionId;

    public ResearchManager researchManager;
    public OutfitDatabase outfitDatabase;
    public DrugPolicyDatabase drugPolicyDatabase;
    public FoodRestrictionDatabase foodRestrictionDatabase;
    public PlaySettings playSettings;

    public History history;
    public Storyteller storyteller;
    public StoryWatcher storyWatcher;

    // The faction's TickManager.lastSettleTicksInt: the storyteller measures "days since settling" from it for
    // the early-game threat ramp, the adaptation grace period and every incident's earliest day, so a faction
    // created mid-game needs its own clock. -1 means "not set yet" (saves from before this field).
    public int settleTick = -1;

    public FactionWorldData() { }

    public void ExposeData()
    {
        Scribe_Values.Look(ref factionId, "factionId");
        Scribe_Values.Look(ref settleTick, "settleTick", -1);

        Scribe_Deep.Look(ref researchManager, "researchManager");
        Scribe_Deep.Look(ref drugPolicyDatabase, "drugPolicyDatabase");
        Scribe_Deep.Look(ref outfitDatabase, "outfitDatabase");
        Scribe_Deep.Look(ref foodRestrictionDatabase, "foodRestrictionDatabase");
        Scribe_Deep.Look(ref playSettings, "playSettings");

        Scribe_Deep.Look(ref history, "history");
        Scribe_Deep.Look(ref storyteller, "storyteller");
        Scribe_Deep.Look(ref storyWatcher, "storyWatcher");

        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            researchManager ??= new ResearchManager();
            drugPolicyDatabase ??= new DrugPolicyDatabase();
            outfitDatabase ??= new OutfitDatabase();
            foodRestrictionDatabase ??= new FoodRestrictionDatabase();
            playSettings ??= new PlaySettings();

            history ??= new History();
            storyteller ??= new Storyteller(Find.Storyteller.def, Find.Storyteller.difficultyDef,
                Find.Storyteller.difficulty);
            storyWatcher ??= new StoryWatcher();
        }
    }

    public void ReassignIds()
    {
        foreach (DrugPolicy p in drugPolicyDatabase.policies)
            p.id = Find.UniqueIDsManager.GetNextThingID();

        foreach (ApparelPolicy o in outfitDatabase.outfits)
            o.id = Find.UniqueIDsManager.GetNextThingID();

        foreach (FoodPolicy o in foodRestrictionDatabase.foodRestrictions)
            o.id = Find.UniqueIDsManager.GetNextThingID();
    }

    public static FactionWorldData New(int factionId)
    {
        return new FactionWorldData()
        {
            factionId = factionId,
            settleTick = Find.TickManager.TicksGame,

            researchManager = new ResearchManager(),
            drugPolicyDatabase = new DrugPolicyDatabase(),
            outfitDatabase = new OutfitDatabase(),
            foodRestrictionDatabase = new FoodRestrictionDatabase(),
            playSettings = new PlaySettings(),

            history = new History(),
            storyteller = new Storyteller(Find.Storyteller.def, Find.Storyteller.difficultyDef, Find.Storyteller.difficulty),
            storyWatcher = new StoryWatcher()
        };
    }

    public static FactionWorldData FromCurrent(int factionId)
    {
        return new FactionWorldData()
        {
            factionId = factionId == int.MinValue ? Faction.OfPlayer.loadID : factionId,
            settleTick = Find.TickManager.lastSettleTicksInt,

            researchManager = Find.ResearchManager,
            drugPolicyDatabase = Current.Game.drugPolicyDatabase,
            outfitDatabase = Current.Game.outfitDatabase,
            foodRestrictionDatabase = Current.Game.foodRestrictionDatabase,
            playSettings = Current.Game.playSettings,

            history = Find.History,
            storyteller = Find.Storyteller,
            storyWatcher = Find.StoryWatcher
        };
    }
}
