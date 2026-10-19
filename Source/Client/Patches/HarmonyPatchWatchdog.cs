using System;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Patches;

// On RimWorld 1.7 (.NET 10 with tiered compilation) Harmony's detours can be dropped when the runtime
// re-JITs a hot method, so a patch runs fine for a while and then silently stops. Seen on
// TickManager.TickManagerUpdate, LoadedObjectDirectory.RegisterLoaded, Pawn_JobTracker.StartJob,
// GenSpawn.Spawn and ThingWithComps.InitializeComps. The watchdog makes that visible instead of letting it
// show up as a desync.
public static class HarmonyPatchWatchdog
{
    public const string LaunchHint =
        "Launch RimWorld with tiered compilation off: add DOTNET_TieredCompilation=0 in front of %command% " +
        "in the Steam launch options (DOTNET_TieredCompilation=0 %command%).";

    // Frames the game has to run in Play, with no long event, before the tick patch is expected to have run
    private const int GraceFrames = 120;

    private static int consecutivePlayFrames;
    private static bool reported;

    public static bool TieredCompilationOff { get; private set; }

    public static void CheckTieredCompilation()
    {
        if (Native.IsMono) return;

        var env = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")
                  ?? Environment.GetEnvironmentVariable("COMPlus_TieredCompilation");
        var config = AppContext.GetData("System.Runtime.TieredCompilation");

        TieredCompilationOff = IsOff(env) || config is false || config is string s && IsOff(s);

        if (TieredCompilationOff)
            Log.Message("Multiplayer: tiered compilation is off, Harmony patches won't be dropped by re-JITs");
        else
            Log.Error("Multiplayer: .NET tiered compilation is on. On RimWorld 1.7 the runtime drops Harmony " +
                      "patches when it re-JITs a hot method, so Multiplayer stops ticking the game and desyncs. " +
                      LaunchHint);

        static bool IsOff(string? value) =>
            value != null && (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    // Called every frame from OnMainThread.Update. TickManager.TickManagerUpdate runs every frame in Play
    // outside long events, so TickPatch.Prefix should stamp every frame too.
    public static void Update()
    {
        if (reported || Native.IsMono) return;

        if (Current.ProgramState != ProgramState.Playing || Current.Game == null
            || LongEventHandler.currentEvent != null || LongEventHandler.eventQueue.Count > 0)
        {
            consecutivePlayFrames = 0;
            return;
        }

        consecutivePlayFrames++;
        if (consecutivePlayFrames <= GraceFrames) return;

        int missed = TickPatch.lastPrefixFrame < 0 ? consecutivePlayFrames : Time.frameCount - TickPatch.lastPrefixFrame;
        if (missed <= GraceFrames) return;

        reported = true;
        Log.Error($"Multiplayer: the Harmony patch on TickManager.TickManagerUpdate hasn't run for {missed} frames " +
                  "while the game is playing. The runtime re-JIT'ed the method and dropped the patch, so Multiplayer " +
                  "isn't ticking the game and any session will desync. " + LaunchHint);
    }
}
