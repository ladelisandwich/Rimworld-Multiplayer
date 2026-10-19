using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Factions;

// Every player faction's storyteller is ticked on every map (FactionRepeater) and on the world. Incidents from
// storyteller comps are kept apart by the target tags (another faction's settlement isn't our player home), but
// incidents made by quest parts (QuestPart_ThreatsGenerator: "raids for N days") come from the shared quest list,
// so each faction's storyteller would fire the same quest raid in the same interval: a doubled raid on one colony.
// This is Storyteller.StorytellerTick with that filter in the loop. The filter used to live in a patch on
// Quest.PartsListForReading, a one-line getter the .NET JIT inlines into its callers, where the patch never runs.
public static class StorytellerFactionTick
{
    public static void Tick(Storyteller storyteller)
    {
        storyteller.incidentQueue.IncidentQueueTick();

        if (Find.TickManager.TicksGame % 1000 != 0 || !DebugSettings.enableStoryteller)
            return;

        foreach (FiringIncident incident in storyteller.MakeIncidentsForInterval())
            if (QuestIncidentIsOurs(incident))
                storyteller.TryFire(incident);
    }

    // True for non-quest incidents, and for quest incidents when the current faction is the one they belong to
    public static bool QuestIncidentIsOurs(FiringIncident incident)
    {
        if (incident.sourceQuestPart == null)
            return true;

        // Quest parts are walked by the world tick too; their incidents fire from the map tick only
        if (incident.parms.target is not Map map || map != Multiplayer.MapContext)
            return false;

        if (incident.parms.quest != null && incident.parms.quest.TryGetPlayerFaction(out var questFaction))
            return questFaction == Faction.OfPlayer;

        if (map.ParentFaction is { IsPlayer: true } owner)
            return owner == Faction.OfPlayer;

        // A site or an ambush: the first player faction with pawns there, else the first player faction
        Faction spectator = Multiplayer.WorldComp.spectatorFaction;
        Faction first = null;
        foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
        {
            if (!f.IsPlayer || f == spectator) continue;
            if (map.mapPawns.SpawnedPawnsInFaction(f).Count > 0)
                return f == Faction.OfPlayer;
            first ??= f;
        }

        return first == Faction.OfPlayer;
    }
}
