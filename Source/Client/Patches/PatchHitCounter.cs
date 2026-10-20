using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using LudeonTK;
using Multiplayer.Client.Util;
using MultiplayerLoader;
using Verse;

namespace Multiplayer.Client.Patches;

// Counts how often the determinism patches run, in dev mode only. On .NET the JIT can inline a small patched
// method into a caller compiled before the patch, and the patch then never runs for that caller, with no error
// anywhere. The "MP: patch hit counts" debug action shows which patches actually fire.
public static class PatchHitCounter
{
    private class Counter { public int value; }

    private static readonly ConcurrentDictionary<string, Counter> counters = new();

    // Every counter name, so the report also lists the ones that never fired
    public static readonly string[] Known =
    [
        "HashCode.Combine seed in Map.get_NextGenSeed", "HashCode.Combine seed in Gravship.get_ConstantRandSeed",
        "HashCode.Combine seed in Building_Trap.KnowsOfTrap", "HashCode.Combine seed in LayoutWorker.FillAllRooms",
        "TargetInfo.GetHashCode map combine", "WindManager.BaseWindSpeedAt map hash", "Tradeable.GetHashCode",
        "GlowGrid litGlowers comparer", "FishShadowComponent water body hash",
        "FastTileFinder single batch", "MapBrightnessTracker fixed deltaTime", "UndercaveMapComponent collapse",
        "GenTicks.GetCameraUpdateRate synced", "Thing.UpdateRateTicks synced", "Pawn.UpdateRateTicks synced",
    ];

    static PatchHitCounter()
    {
        foreach (var name in Known)
            counters.TryAdd(name, new Counter());
    }

    public static void Hit(string name)
    {
        if (!Prefs.DevMode) return;
        Interlocked.Increment(ref counters.GetOrAdd(name, _ => new Counter()).value);
    }

    public static void Reset()
    {
        foreach (var c in counters.Values)
            Interlocked.Exchange(ref c.value, 0);
    }

    [DebugAction(MpDebugActions.MultiplayerCategory, "Patch hit counts", allowedGameStates = /* all */ AllowedGameStates.Invalid)]
    public static void LogHitCounts()
    {
        var sb = new StringBuilder("Multiplayer patch hit counts (dev mode only, since startup or the last reset):\n");

        foreach (var (name, counter) in counters.OrderBy(kv => kv.Key))
            sb.AppendLine($"  {counter.value,8}  {name}");

        sb.AppendLine("Ritual sync hooks:");
        foreach (var (name, hits) in SyncRituals.GetHitCounts())
            sb.AppendLine($"  {hits,8}  {name}");

        Log.Message(sb.ToString().TrimEnd());
    }

    [DebugAction(MpDebugActions.MultiplayerCategory, "Patch hit counts: reset", allowedGameStates = /* all */ AllowedGameStates.Invalid)]
    public static void ResetHitCounts()
    {
        Reset();
        Log.Message("Multiplayer patch hit counts reset");
    }
}
