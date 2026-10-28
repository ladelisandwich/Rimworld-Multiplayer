using System.Globalization;
using System.Text;
using Verse;

namespace Multiplayer.Client.Desyncs;

// With slow desync tracing on, every map tick adds one desync log item holding a digest of every spawned pawn's
// simulation state. The item's hash joins the trace hash list like a stack trace does, so when two clients'
// pawns differ before any random call does (Oct 19 Desync-11: a hediff present on one side only), the report's
// first differing trace is this digest on both sides and a diff of the two names the pawn and the field.
public static class PawnStateTrace
{
    private static readonly StringBuilder builder = new();

    public static void RecordMapTick(Map map, int mapTicks)
    {
        if (!ManagedStackTracing.Enabled || !DeferredStackTracing.ShouldAddStackTraceForDesyncLog())
            return;

        // Runs in the map tick's finally block: an exception here would skip the map's random state entry and leave
        // the map marked as ticking on this player's side only
        try
        {
            builder.Clear();
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Describe(pawns[i], builder);
                builder.Append('\n');
            }

            Multiplayer.game.sync.TryAddInfoForDesyncLog(
                $"pawn state map {map.uniqueID} tick {mapTicks} pawns {pawns.Count}", builder.ToString());
        }
        catch (System.Exception e)
        {
            ManagedStackTracing.Disable($"the pawn state digest of map {map.uniqueID}", e);
        }
    }

    private static void Describe(Pawn pawn, StringBuilder b)
    {
        var inv = CultureInfo.InvariantCulture;

        b.Append(pawn.ThingID).Append(' ').Append(pawn.Position.ToString());
        b.Append(" td").Append(pawn.tickDelta);
        if (pawn.Downed) b.Append(" downed");
        if (pawn.Drafted) b.Append(" drafted");

        var pather = pawn.pather;
        if (pather != null)
            b.Append(" mv").Append(pather.Moving ? 1 : 0).Append(' ').Append(pather.nextCell.ToString())
                .Append(' ').Append(pather.nextCellCostLeft.ToString("F2", inv))
                .Append('/').Append(pather.nextCellCostTotal.ToString("F2", inv));

        var job = pawn.CurJob;
        if (job != null)
            b.Append(" job ").Append(job.def.defName).Append(':').Append(pawn.jobs.curDriver?.CurToilIndex ?? -1)
                .Append(' ').Append(job.targetA.ToString());

        if (pawn.stances?.curStance != null)
            b.Append(" st ").Append(pawn.stances.curStance.GetType().Name);

        b.Append(" age ").Append(pawn.ageTracker.AgeBiologicalTicks);

        var needs = pawn.needs;
        if (needs != null)
        {
            if (needs.food != null) b.Append(" food ").Append(needs.food.CurLevel.ToString("F3", inv));
            if (needs.rest != null) b.Append(" rest ").Append(needs.rest.CurLevel.ToString("F3", inv));
            if (needs.joy != null) b.Append(" joy ").Append(needs.joy.CurLevel.ToString("F3", inv));
            if (needs.mood != null) b.Append(" mood ").Append(needs.mood.CurLevel.ToString("F3", inv));
        }

        b.Append(" hd");
        var hediffs = pawn.health.hediffSet.hediffs;
        for (int i = 0; i < hediffs.Count; i++)
        {
            var h = hediffs[i];
            b.Append(' ').Append(h.def.defName).Append(':').Append(h.CurStageIndex).Append(':')
                .Append(h.Severity.ToString("F3", inv));
        }

        // First-person play's synced state of this pawn (controller, intent, pending skill check), when it has any
        IsoRimFp.Describe(pawn, b);
    }
}
