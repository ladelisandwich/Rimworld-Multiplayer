using System;
using System.Linq;
using HarmonyLib;
using Multiplayer.Client.AsyncTime;
using Multiplayer.Common;
using UnityEngine;
using Verse;
using Object = UnityEngine.Object;

namespace Multiplayer.Client.Diag
{
    // The Multiplayer side of first-person play's self-test (fp-play-spec 15.3), for the host instance that
    // scripts/fp_selftest.py --mp starts. Inert unless the FPTEST_HOST environment variable is "1" AND dev mode is on at
    // startup: then it hosts the -quicktest game once IsoRim's harness (IsoRim.Fp.FpTestKit) has built its scene, so the
    // scene is in the save the client loads, logs "FPTEST HOSTED" once the server runs (the runner starts the client on
    // that line), and keeps the session's time running at Normal until a desync happens. In a normal game nothing here
    // runs: no object is created and nothing is patched.
    [StaticConstructorOnStartup]
    public static class FpSelfTestHost
    {
        public const string Variable = "FPTEST_HOST";
        public const string DirectAddress = "0.0.0.0:30502";

        private enum Phase { Waiting, Hosting, Hosted, Over }

        private static bool active;
        private static bool headless;
        private static Phase phase;
        private static Func<bool> kitActive;
        private static Func<bool> sceneReady;
        private static bool kitBound;
        private static bool loggedWaiting;
        private static bool desyncSeen;
        private static bool wasPaused;
        private static float nextVoteAt;

        public static bool Active => active;

        static FpSelfTestHost()
        {
            if (Environment.GetEnvironmentVariable(Variable) != "1") return;

            if (!Prefs.DevMode)
            {
                Log.Warning($"Multiplayer: {Variable} is set but dev mode is off, so the first-person self-test host stays off");
                return;
            }

            if (Multiplayer.arbiterInstance) return;

            active = true;
            headless = GenCommandLine.CommandLineArgPassed("Headless") || GenCommandLine.CommandLineArgPassed("NoWindow");
            var obj = new GameObject("FpSelfTestHost");
            obj.AddComponent<FpSelfTestHostRunner>();
            Object.DontDestroyOnLoad(obj);
            Log.Message($"FPTEST host armed: hosts on {DirectAddress} once the scene is built");
        }

        // For the mp1-joinpoint scenario (IsoRim's FpTestM1 calls it by name on the host): creates a join point now, as
        // the host's "/joinpoint" chat command does. Every client then reloads from it.
        public static void ForceJoinPoint()
        {
            if (!active)
            {
                Log.Warning($"Multiplayer: FpSelfTestHost.ForceJoinPoint called without {Variable}=1 and dev mode, ignored");
                return;
            }

            var server = Multiplayer.LocalServer;
            if (server == null)
            {
                Log.Error("Multiplayer: FPTEST host can't force a join point: this instance isn't hosting");
                return;
            }

            // The server's state belongs to its own thread
            server.Enqueue(() =>
            {
                if (!server.worldData.TryStartJoinPointCreation(true))
                    ServerLog.Error("FPTEST host: a join point is already being created, none forced");
            });
            Log.Message("FPTEST host: join point requested");
        }

        // Every frame (FpSelfTestHostRunner.Update), in every program state
        internal static void Update()
        {
            try
            {
                LetLongEventsRunHeadless();
                switch (phase)
                {
                    case Phase.Waiting:
                        TryHost();
                        break;
                    case Phase.Hosting:
                        WaitForServer();
                        break;
                    case Phase.Hosted:
                        KeepTimeRunning();
                        break;
                }
            }
            catch (Exception e)
            {
                phase = Phase.Over;
                Log.Error($"Multiplayer: FPTEST host failed and stops: {e}");
            }
        }

        // A headless instance has no IMGUI (Waylith creates no ImGuiHost), so the long-event window never gets its first
        // repaint, and a synchronous long event with a text waits for that forever (QueuedLongEvent
        // ShouldWaitUntilDisplayed): hosting stalled in "MpSaving" and the server never started. Here the current event
        // counts as displayed, which is what the first repaint would have done.
        private static void LetLongEventsRunHeadless()
        {
            if (headless && LongEventHandler.currentEvent is { alreadyDisplayed: false } e)
                e.alreadyDisplayed = true;
        }

        private static bool GameReady =>
            Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null &&
            !LongEventHandler.AnyEventNowOrWaiting;

        // The first Playing frame without a session, after IsoRim's harness built the scene when the harness runs
        private static void TryHost()
        {
            if (!GameReady || Multiplayer.Client != null) return;

            if (KitActive() && !SceneReady())
            {
                if (!loggedWaiting)
                    Log.Message("FPTEST host: waiting for the scene before hosting");
                loggedWaiting = true;
                return;
            }

            var settings = new ServerSettings
            {
                gameName = "fptest",
                direct = true,
                directAddress = DirectAddress,
                lan = false,
                steam = false,
                arbiter = false,
                pauseOnJoin = false,
                pauseOnDesync = true,
                pauseOnLetter = PauseOnLetter.Never,
                autoJoinPoint = AutoJoinPointFlags.Join,
                // A config mismatch would open JoinDataWindow on the client, which no script answers
                syncConfigs = false
            };

            // Starts the local server's listeners first: HostUtil.HostServer needs Multiplayer.LocalServer
            if (!HostWindow.HostProgrammatically(settings))
            {
                phase = Phase.Over;
                Log.Error($"Multiplayer: FPTEST host couldn't host on {DirectAddress} (port in use?)");
                return;
            }

            phase = Phase.Hosting;
            Log.Message("FPTEST host: hosting");
        }

        // Hosting reloads the game in a long event; the server thread starts at its end
        private static void WaitForServer()
        {
            if (Multiplayer.Client == null)
            {
                phase = Phase.Over;
                Log.Error("Multiplayer: FPTEST host: the session ended while hosting");
                return;
            }

            if (!GameReady || Multiplayer.LocalServer is not { FullyStarted: true }) return;

            phase = Phase.Hosted;
            Log.Message($"FPTEST HOSTED on {DirectAddress}");
        }

        // Hosting pauses every map (HostUtil.SetGameState): vote Normal whenever the world is paused, at most every
        // 2 s since a vote takes a command round trip, and never again once anyone desynced (the pause on desync stays,
        // and the runner reports the desync)
        private static void KeepTimeRunning()
        {
            var session = Multiplayer.session;
            if (Multiplayer.Client == null || session == null)
            {
                phase = Phase.Over;
                Log.Message("FPTEST host: the session ended");
                return;
            }

            if (desyncSeen) return;

            if (session.desynced || session.players.Any(p => p.status == PlayerStatus.Desynced))
            {
                desyncSeen = true;
                Log.Message("FPTEST host: a desync happened, time is left as it is from now on");
                return;
            }

            if (!GameReady || Multiplayer.reloading || TickPatch.Simulating ||
                session.client.State != ConnectionStateEnum.ClientPlaying)
                return;

            var world = Multiplayer.AsyncWorldTime;
            if (world == null || world.DesiredTimeSpeed != TimeSpeed.Paused)
            {
                wasPaused = false;
                return;
            }

            if (Time.realtimeSinceStartup < nextVoteAt) return;
            nextVoteAt = Time.realtimeSinceStartup + 2f;

            if (!wasPaused)
                Log.Message("FPTEST host: the world is paused, voting Normal");
            wasPaused = true;

            // TimeControlPatch.SendTimeVote for the world, without its OnGUI parts
            if (Multiplayer.GameComp.IsLowestWins)
                Multiplayer.Client.SendCommand(CommandType.TimeSpeedVote, ScheduledCommand.Global,
                    (byte)TimeVote.Normal, world.TickableId);
            else
                MpTimeControls.SendTimeChange(world, TimeSpeed.Normal);
        }

        // IsoRim's harness, by name (IsoRim.dll is its own assembly): runs this scenario, and has its scene built
        private static void BindKit()
        {
            kitBound = true;
            var type = GenTypes.GetTypeInAnyAssembly("IsoRim.Fp.FpTestKit", "IsoRim.Fp");
            if (type == null) return;

            try
            {
                kitActive = AccessTools.MethodDelegate<Func<bool>>(AccessTools.DeclaredPropertyGetter(type, "Active"));
                sceneReady = AccessTools.MethodDelegate<Func<bool>>(AccessTools.DeclaredPropertyGetter(type, "SceneReady"));
            }
            catch (Exception e)
            {
                kitActive = null;
                sceneReady = null;
                Log.Error($"Multiplayer: FPTEST host couldn't bind IsoRim.Fp.FpTestKit, so it hosts without waiting for the scene: {e}");
            }
        }

        private static bool KitActive()
        {
            if (!kitBound) BindKit();
            return kitActive != null && kitActive();
        }

        private static bool SceneReady() => sceneReady != null && sceneReady();
    }

    public class FpSelfTestHostRunner : MonoBehaviour
    {
        public void Update() => FpSelfTestHost.Update();
    }
}
