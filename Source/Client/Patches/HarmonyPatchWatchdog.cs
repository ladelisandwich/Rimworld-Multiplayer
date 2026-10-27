using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Patches;

// On RimWorld 1.7 (.NET 10 with tiered compilation) Harmony's detours can be dropped when the runtime
// re-JITs a hot method, so a patch runs fine for a while and then silently stops. Seen on
// TickManager.TickManagerUpdate, LoadedObjectDirectory.RegisterLoaded, Pawn_JobTracker.StartJob,
// GenSpawn.Spawn and ThingWithComps.InitializeComps. The watchdog makes that visible instead of letting it
// show up as a desync, and offers to restart the game with tiered compilation off: the game's restart
// relaunches the same executable with the same arguments and inherits this process's environment.
public static class HarmonyPatchWatchdog
{
    public const string LaunchHint =
        "Launch RimWorld with tiered compilation off: add DOTNET_TieredCompilation=0 in front of %command% " +
        "in the Steam launch options (DOTNET_TieredCompilation=0 %command%).";

    private const string TieringVariable = "DOTNET_TieredCompilation";
    private const string RestartMarkerVariable = "MultiplayerRestartedForTiering";

    // Frames the game has to run in Play, with no long event, before the tick patch is expected to have run
    private const int GraceFrames = 120;

    private static int consecutivePlayFrames;
    private static bool reported;
    private static bool restartOffered;

    public static bool TieredCompilationOff { get; private set; }

    // True when tiering is on and a restart with it off hasn't been tried yet
    public static bool ShouldOfferRestart { get; private set; }

    public static void CheckTieredCompilation()
    {
        if (Native.IsMono) return;

        var env = Environment.GetEnvironmentVariable(TieringVariable)
                  ?? Environment.GetEnvironmentVariable("COMPlus_TieredCompilation");
        var config = AppContext.GetData("System.Runtime.TieredCompilation");
        var restarted = Environment.GetEnvironmentVariable(RestartMarkerVariable) == "1";
        Environment.SetEnvironmentVariable(RestartMarkerVariable, "");

        TieredCompilationOff = IsOff(env) || config is false || config is string s && IsOff(s);

        if (TieredCompilationOff)
        {
            Log.Message("Multiplayer: tiered compilation is off, Harmony patches won't be dropped by re-JITs");
            return;
        }

        Log.Error("Multiplayer: .NET tiered compilation is on. On RimWorld 1.7 the runtime drops Harmony " +
                  "patches when it re-JITs a hot method, so Multiplayer stops ticking the game and desyncs. " +
                  LaunchHint);

        if (restarted)
            Log.Error("Multiplayer: the game was restarted with DOTNET_TieredCompilation=0 but tiered compilation " +
                      "is still on, so the environment didn't reach the new process. Set the launch option by hand.");
        else
            ShouldOfferRestart = true;

        static bool IsOff(string? value) =>
            value != null && (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    // Relaunches the game the way the mod-config restart does, with tiering off for the new process
    // Harmony 3 (0Harmony 3.0.0-rc.3) keeps its patch records by MethodBase, and a MethodInfo looked up through a
    // subclass (AccessTools.Method(typeof(Derived), "InheritedMethod")) isn't equal to the declaring type's. Two mods
    // patching one method those two ways get two records and two detours on the same code, and whichever detour went in
    // last silently replaces the other's patches. Found Oct 26: Multiplayer's arbiter patch on SectionLayer.DrawLayer
    // hid every IsoRim hook on MapDrawLayer.DrawLayer (no 3D lighting, night darkness or fog lid). Run once after the
    // mods' startup patching; logs an error per split method.
    public static void CheckSplitPatches()
    {
        try
        {
            var splits = Harmony.GetAllPatchedMethods().GroupBy(m => m.MethodHandle.Value).Where(g => g.Count() > 1).ToList();
            foreach (var g in splits)
            {
                var first = g.First();
                string records = string.Join(" | ", g.Select(m =>
                    "through " + m.ReflectedType?.Name + " by " + string.Join(", ", Harmony.GetPatchInfo(m)?.Owners ?? Enumerable.Empty<string>())));
                Log.Error($"Multiplayer: {first.DeclaringType?.FullName}.{first.Name} has Harmony patches under {g.Count()} separate " +
                          $"records ({records}). Only one record's detour is active, so the other patches never run. Every mod " +
                          $"has to patch it through its declaring type, {first.DeclaringType?.Name}.");
            }
        }
        catch (Exception e)
        {
            Log.Error("Multiplayer: checking the Harmony patches for split records failed: " + e);
        }
    }

    public static void RestartWithTieringOff()
    {
        Environment.SetEnvironmentVariable(TieringVariable, "0");
        Environment.SetEnvironmentVariable(RestartMarkerVariable, "1");
        Log.Message("Multiplayer: restarting RimWorld with tiered compilation off");
        GenCommandLine.Restart();
    }

    // Called every frame from OnMainThread.Update. TickManager.TickManagerUpdate runs every frame in Play
    // outside long events, so TickPatch.Prefix should stamp every frame too.
    public static void Update()
    {
        if (Native.IsMono) return;

        if (ShouldOfferRestart && !restartOffered && Current.ProgramState == ProgramState.Entry
            && Find.WindowStack != null && LongEventHandler.currentEvent == null)
        {
            restartOffered = true;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "Multiplayer on RimWorld 1.7 needs .NET tiered compilation turned off. With it on, the runtime " +
                "drops Harmony patches a few seconds into play and every multiplayer session desyncs.\n\n" +
                "Restart RimWorld now with tiered compilation off? The restart keeps your mod list.\n\n" +
                "To make this permanent, " + LaunchHint,
                RestartWithTieringOff,
                destructive: false,
                "Multiplayer: restart needed"));
        }

        if (reported) return;

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
