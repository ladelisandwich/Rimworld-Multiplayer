using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.InputSystem;
using Verse;
using InputKey = UnityEngine.InputSystem.Key;
using InputMouse = UnityEngine.InputSystem.Mouse;

namespace IsoRim.Fp
{
    // First-person play's self-test harness (fp-play-spec 15.1), run by scripts/fp_selftest.py. Inert unless dev mode is on
    // AND the ISORIM_FPTEST environment variable names a scenario ("<scenario>[:<role>]", role solo (default), host or
    // client): then it builds the test scene (FpScene) on the first Playing frame, runs the scenario's script one step per
    // frame, feeds synthetic keys and mouse through the engine's own input queue (UnityEngine.InputSystem, as the GLFW bridge
    // does) just before the frame's IMGUI dispatch, logs "FPTEST PASS|FAIL <name>: <detail>" lines and quits. Nothing
    // reaches the desktop. In a normal game nothing here is patched or run.
    //
    // Scripts are IEnumerator<int>: each yield is the number of frames to wait before the next step. A step that queued
    // input resumes only once all of it was delivered; WaitTicks, WaitUntil and HoldTicks also hold the script until their
    // condition is met (or the step times out, which fails the scenario).
    public static class FpTestKit
    {
        public static readonly string Spec = Environment.GetEnvironmentVariable("ISORIM_FPTEST");

        private static bool active;

        // Cached at install: dev mode and the variable. Read by FpMapComp's tick for the FPSTATE lines (logging only).
        public static bool Active
        {
            get { return active; }
        }

        public static string Scenario { get; private set; }

        public static string Role { get; private set; } = "solo";

        public static bool IsSolo => Role == "solo";

        public static bool IsHost => Role == "host";

        public static bool IsClient => Role == "client";

        // Solo and host: the scene is built. The Multiplayer-side test host (FPTEST_HOST=1) hosts only after this, so the
        // client gets the scene in the host's save.
        public static bool SceneReady { get; private set; }

        public static int Fails { get; private set; }

        public static int Ticks => Find.TickManager != null ? Find.TickManager.TicksGame : 0;

        private static readonly Dictionary<string, Func<IEnumerator<int>>> scenarios = new Dictionary<string, Func<IEnumerator<int>>>();

        // Per-scenario additions to the scene (solo and host), made with it on the first Playing frame, before
        // SceneReady lets Multiplayer's test host start the session: a thing the harness spawns once a session runs is
        // made outside any command, so Multiplayer gives it a negative id (mp3-craft's bench came out as
        // HandTailoringBench-2), which no real game's thing has. Each returns null, or why it failed.
        private static readonly Dictionary<string, Func<Map, string>> sceneExtras = new Dictionary<string, Func<Map, string>>();

        internal static void AddSceneExtra(string scenario, Func<Map, string> extra)
        {
            sceneExtras[scenario] = extra;
        }

        private static readonly Queue<Action> input = new Queue<Action>();
        private static readonly List<Hold> holds = new List<Hold>();

        private enum Phase { Waiting, Running, Finishing, ShuttingDown, Quit }

        private static Phase phase;
        private static IEnumerator<int> script;
        private static int waitFrame;
        private static Func<bool> waitCond;
        private static string waitWhat;
        private static int waitDeadline;
        private static int startFrame = -1;
        private static int runStartFrame;
        private static int quitFrame;

        // Every wait and timeout counted in frames also lasts at least that many 60 fps frames of real time: a headless
        // instance draws nothing and runs thousands of frames a second (the host of an --mp run gave up "never became
        // ready" 2 s into its map generation), while a windowed one at 60 fps or less is held by the frame count as before.
        private const double FrameSeconds = 1.0 / 60.0;
        private static double waitTime;
        private static double waitDeadlineTime;
        private static double startTime;
        private static double runStartTime;

        private const int StepTimeoutFrames = 3600;       // one WaitUntil/WaitTicks/HoldTicks: about a minute
        private const int ScenarioTimeoutFrames = 108000; // a whole scenario: about half an hour at 60 fps
        private const int ReadyTimeoutFrames = 36000;     // the game (or for a client, the session) never came up

        private static double Now => Time.realtimeSinceStartupAsDouble;

        // Both the frame count and the real time have passed.
        private static bool Past(int frame, int frameLimit, double timeLimit)
        {
            return frame > frameLimit && Now > timeLimit;
        }

        private sealed class Hold
        {
            public InputKey[] keys;
            public int ticks;
            public int until = int.MaxValue;   // set when the press is delivered
        }

        internal static void Install(Harmony h)
        {
            if (string.IsNullOrEmpty(Spec))
            {
                return;
            }
            if (!Prefs.DevMode)
            {
                Log.Warning("[IsoRim] ISORIM_FPTEST is set but dev mode is off: the first-person self-test stays off.");
                return;
            }
            string[] parts = Spec.Split(':');
            Scenario = parts[0].Trim();
            if (parts.Length > 1 && parts[1].Trim().Length > 0)
            {
                Role = parts[1].Trim().ToLowerInvariant();
            }
            if (Role != "solo" && Role != "host" && Role != "client")
            {
                Log.Error("[IsoRim] FPTEST: unknown role \"" + Role + "\" (solo, host or client); the self-test stays off.");
                return;
            }
            FpTestM1.Register(scenarios);
            FpTestM2.Register(scenarios);
            FpTestM3.Register(scenarios);
            FpTestM4.Register(scenarios);
            FpTestM5.Register(scenarios);
            FpTestM6.Register(scenarios);

            bool stepped = FpHooks.Patch(h, "self-test steps", AccessTools.DeclaredMethod(typeof(Root_Play), nameof(Root_Play.Update)),
                postfix: new HarmonyMethod(typeof(FpTestKit), nameof(Step)));
            Type dispatch = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImguiDispatch");
            bool fed = FpHooks.Patch(h, "self-test input", dispatch == null ? null : AccessTools.DeclaredMethod(dispatch, "DispatchInput"),
                prefix: new HarmonyMethod(typeof(FpTestKit), nameof(BeforeDispatch)));
            if (!stepped || !fed)
            {
                Log.Error("[IsoRim] FPTEST: the harness hooks are missing; the self-test can't run.");
                return;
            }
            active = true;
            FpInput.TestForceFocus = true;
            Log.Message("FPTEST INSTALLED scenario=" + Scenario + " role=" + Role + " (" + scenarios.Count + " scenarios known)");
        }

        // ---------------------------------------------------------------- the runner

        // ImguiDispatch.DispatchInput prefix: one queued input action per frame, delivered just before IMGUI sees the frame.
        private static void BeforeDispatch()
        {
            if (input.Count > 0)
            {
                FpHits.Hit("Test input");
                try
                {
                    input.Dequeue()();
                }
                catch (Exception e)
                {
                    Check("harness", false, "queuing input threw " + e);
                }
            }
        }

        // Root_Play.Update postfix: one script step per frame at most.
        private static void Step()
        {
            try
            {
                StepInner();
            }
            catch (Exception e)
            {
                Check("harness", false, "step threw " + e);
                Finish();
            }
        }

        private static void StepInner()
        {
            int frame = Time.frameCount;
            switch (phase)
            {
                case Phase.Quit:
                    return;
                case Phase.ShuttingDown:
                    if (frame >= quitFrame)
                    {
                        phase = Phase.Quit;
                        Application.Quit();
                    }
                    return;
                case Phase.Finishing:
                    phase = Phase.ShuttingDown;
                    quitFrame = frame + 600;
                    Root.Shutdown();
                    return;
            }
            if (startFrame < 0)
            {
                startFrame = frame;
                startTime = Now;
            }
            ReleaseHolds();
            if (input.Count > 0 || frame < waitFrame || Now < waitTime)
            {
                return;
            }
            if (phase == Phase.Waiting)
            {
                if (!Ready())
                {
                    if (Past(frame, startFrame + ReadyTimeoutFrames, startTime + ReadyTimeoutFrames * FrameSeconds))
                    {
                        Check("setup", false, "the game " + (IsClient ? "never joined the session" : "never became ready"));
                        Finish();
                    }
                    return;
                }
                Begin();
                return;
            }
            if (waitCond != null)
            {
                if (!waitCond())
                {
                    if (Past(frame, waitDeadline, waitDeadlineTime))
                    {
                        Check("wait", false, "timed out " + waitWhat);
                        waitCond = null;
                        Finish();
                    }
                    return;
                }
                waitCond = null;
            }
            if (Past(frame, runStartFrame + ScenarioTimeoutFrames, runStartTime + ScenarioTimeoutFrames * FrameSeconds))
            {
                Check("scenario", false, "ran out of time");
                Finish();
                return;
            }
            if (!script.MoveNext())
            {
                Finish();
                return;
            }
            int wait = Math.Max(1, script.Current);
            waitFrame = frame + wait;
            waitTime = Now + wait * FrameSeconds;
        }

        private static int playingSince = -1;

        private static bool Ready()
        {
            // Any queued event, not only a running one: a joining client is Playing with its map loaded while Multiplayer's
            // "MpSimulating" event (Loader.PostLoad: this player's faction, the command queues) is still queued, and the
            // scene adopted then was looked up under another faction (no colony stove: "no scene in this game").
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.CurrentMap == null
                || LongEventHandler.AnyEventNowOrWaiting)
            {
                playingSince = -1;
                return false;
            }
            if (playingSince < 0)
            {
                playingSince = Time.frameCount;
            }
            if (IsClient)
            {
                return MpBridge.InSession;
            }
            // Solo: the 3D view from -isorim3d is up (given up waiting 20 s into play: the scenario's own checks then say why).
            return !IsSolo || View3D.Ready || Time.frameCount - playingSince > 1200;
        }

        private static void Begin()
        {
            Map map = Find.CurrentMap;
            if (!scenarios.TryGetValue(Scenario, out Func<IEnumerator<int>> make))
            {
                Check("setup", false, "unknown scenario \"" + Scenario + "\"; known: " + string.Join(", ", scenarios.Keys.OrderBy(k => k)));
                Finish();
                return;
            }
            string error;
            bool built = IsClient ? FpScene.Adopt(map, out error) : FpScene.Build(map, out error);
            if (built && !IsClient && sceneExtras.TryGetValue(Scenario, out Func<Map, string> extra))
            {
                error = extra(map);
                built = error == null;
            }
            if (!built)
            {
                Check("scene", false, error);
                Finish();
                return;
            }
            SceneReady = true;
            Log.Message("FPTEST START scenario=" + Scenario + " role=" + Role + " " + FpScene.Describe()
                + (MpBridge.InSession ? " session: hooks " + (FpMpHooks.Bound ? "bound" : "unbound") + ", play off " + FpIds.PlayOff
                    + ", local id " + FpIds.Local : ""));
            phase = Phase.Running;
            script = make();
            runStartFrame = Time.frameCount;
            runStartTime = Now;
            waitFrame = Time.frameCount + 30;
            waitTime = Now + 30 * FrameSeconds;
        }

        private static void Finish()
        {
            if (phase >= Phase.Finishing)
            {
                return;
            }
            // Keys still held would stay down in the engine's input state for whatever runs next.
            foreach (Hold hold in holds)
            {
                foreach (InputKey k in hold.keys)
                {
                    InputSystem.QueueKeyChange(k, false);
                }
            }
            holds.Clear();
            input.Clear();
            Log.Message("FPTEST DONE fails=" + Fails + " scenario=" + Scenario + " role=" + Role);
            phase = Phase.Finishing;
        }

        private static void ReleaseHolds()
        {
            for (int i = holds.Count - 1; i >= 0; i--)
            {
                Hold hold = holds[i];
                if (Ticks >= hold.until)
                {
                    InputKey[] keys = hold.keys;
                    input.Enqueue(() =>
                    {
                        foreach (InputKey k in keys)
                        {
                            InputSystem.QueueKeyChange(k, false);
                        }
                    });
                    holds.RemoveAt(i);
                }
            }
        }

        // ---------------------------------------------------------------- script API

        // A key change, delivered on its own frame.
        public static int Key(InputKey k, bool down)
        {
            input.Enqueue(() => InputSystem.QueueKeyChange(k, down));
            return 0;
        }

        // A press and a release on two frames.
        public static int Tap(InputKey k)
        {
            Key(k, true);
            Key(k, false);
            return 0;
        }

        // A press with modifier keys held round it ("Shift+F5").
        public static int Chord(InputKey modifier, InputKey k)
        {
            Key(modifier, true);
            Key(k, true);
            Key(k, false);
            Key(modifier, false);
            return 0;
        }

        // Keys held down together for a number of game ticks, then released together; the script waits for the release.
        public static int HoldTicks(int gameTicks, params InputKey[] keys)
        {
            Hold hold = new Hold { keys = keys, ticks = gameTicks };
            input.Enqueue(() =>
            {
                foreach (InputKey k in keys)
                {
                    InputSystem.QueueKeyChange(k, true);
                }
                hold.until = Ticks + gameTicks;
            });
            holds.Add(hold);
            return WaitUntil(() => !holds.Contains(hold), "holding " + string.Join("+", keys) + " for " + gameTicks + " ticks");
        }

        // Keys held down for a number of frames (real time), then released.
        public static int HoldFrames(int frames, params InputKey[] keys)
        {
            input.Enqueue(() =>
            {
                foreach (InputKey k in keys)
                {
                    InputSystem.QueueKeyChange(k, true);
                }
            });
            for (int i = 1; i < frames; i++)
            {
                input.Enqueue(() => { });
            }
            input.Enqueue(() =>
            {
                foreach (InputKey k in keys)
                {
                    InputSystem.QueueKeyChange(k, false);
                }
            });
            return 0;
        }

        // Mouse motion: (dx, dy) pixels on each of `frames` frames, from wherever the (virtual) pointer is.
        public static int MouseDelta(float dx, float dy, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                input.Enqueue(() => InputSystem.QueueMouseMove(InputMouse.current.position.ReadValue() + new Vector2(dx, dy)));
            }
            return 0;
        }

        // A mouse button change at the pointer's position (0 left, 1 right, 2 middle).
        public static int Button(int b, bool down)
        {
            input.Enqueue(() => InputSystem.QueueMouseButton(b, down, InputMouse.current.position.ReadValue()));
            return 0;
        }

        // Moves the (virtual, while captured) pointer to a screen point in engine pixels.
        public static int VirtualPointer(Vector2 screenPx)
        {
            input.Enqueue(() => InputSystem.QueueMouseMove(screenPx));
            return 0;
        }

        // Holds the script until `n` more game ticks have passed.
        public static int WaitTicks(int n)
        {
            int target = Ticks + n;
            return WaitUntil(() => Ticks >= target, "for " + n + " ticks (until tick " + target + ")");
        }

        // Holds the script until cond is true; fails the scenario after timeoutFrames.
        public static int WaitUntil(Func<bool> cond, string what, int timeoutFrames = StepTimeoutFrames)
        {
            waitCond = cond;
            waitWhat = what;
            waitDeadline = Time.frameCount + timeoutFrames;
            waitDeadlineTime = Now + timeoutFrames * FrameSeconds;
            return 0;
        }

        // Solo only: in a session the speed is a vote.
        public static void SetSpeed(TimeSpeed speed)
        {
            if (MpBridge.InSession)
            {
                Check("setup", false, "SetSpeed used in a session");
                return;
            }
            Find.TickManager.CurTimeSpeed = speed;
        }

        public static bool Check(string name, bool ok, string detail)
        {
            if (!ok)
            {
                Fails++;
            }
            Log.Message("FPTEST " + (ok ? "PASS" : "FAIL") + " " + name + ": " + detail);
            return ok;
        }

        public static void Info(string text)
        {
            Log.Message("FPTEST INFO " + text);
        }

        // A screenshot into <save data>/renders/fptest/<scenario>/ (skipped headless: nothing is drawn).
        public static void Shot(string name)
        {
            if (Headless)
            {
                Info("shot " + name + " skipped (headless)");
                return;
            }
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", "fptest", Scenario ?? "none");
            Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png"));
            Info("shot " + name);
        }

        public static bool Headless =>
            GenCommandLine.CommandLineArgPassed("Headless") || GenCommandLine.CommandLineArgPassed("headless")
            || GenCommandLine.CommandLineArgPassed("NoWindow") || GenCommandLine.CommandLineArgPassed("nowindow");

        // Ends the scenario after this step: "FPTEST DONE fails=<n>", then Root.Shutdown, then Application.Quit 600 frames on.
        public static int Done()
        {
            Finish();
            return 0;
        }

        // A scenario took control through Direct: its pawns are controlled without being played, so FpPlay's orphan
        // watchdog leaves this player's controlled pawns alone for the rest of the run.
        internal static bool DirectControl { get; private set; }

        // The headless host has no input: it drives the same senders the UI uses.
        public static class Direct
        {
            public static void SetControl(Pawn p, bool on, byte flags)
            {
                DirectControl = true;
                FpSync.SendSetControl(p, on, flags);
            }

            // False when Multiplayer took no command this frame (send again on a later one).
            public static bool Intent(Pawn p, byte dir)
            {
                return FpSync.SendIntent(p, dir);
            }

            public static void Result(Pawn p, int jobLoadId, int seq, byte outcome)
            {
                FpWork.SendResult(p, jobLoadId, seq, outcome);
            }
        }

        // A first-person command as it ran on this machine (logging only, in the command's own context: synced state and
        // the command's arguments; callers test Active first). Lets two players' logs be compared command by command when
        // their state lines differ.
        public static void OnCommand(int by, string text)
        {
            if (!firstCommandTick.ContainsKey(by))
            {
                firstCommandTick[by] = Ticks;
            }
            Log.Message("FPTEST CMD t=" + Ticks + " " + text);
        }

        // Player id -> the game tick its first first-person command ran here (the same tick on every machine).
        private static readonly Dictionary<int, int> firstCommandTick = new Dictionary<int, int>();

        // The tick the first command from this player (local) or from anyone else ran here; -1 before one has. A
        // client's first command can only run once its join is complete, so the host waits for one before acting
        // (see FpTestM1.ClientReady).
        public static int FirstCommandTick(bool local)
        {
            int me = FpIds.Local;
            int first = -1;
            foreach (KeyValuePair<int, int> kv in firstCommandTick)
            {
                if ((kv.Key == me) == local && (first < 0 || kv.Value < first))
                {
                    first = kv.Value;
                }
            }
            return first;
        }

        // ---------------------------------------------------------------- state lines

        // From FpMapComp.MapComponentTick (map context), every 60 map ticks: one FPSTATE line per controlled pawn and per
        // pawn owning a tally, in thingIDNumber order. Logging only; fp_compare.py compares host and client lines.
        public static void OnMapTick(FpMapComp comp)
        {
            int t = Find.TickManager.TicksGame;
            if (t % 60 != 0)
            {
                return;
            }
            try
            {
                List<Pawn> pawns = new List<Pawn>();
                foreach (FpEntry e in comp.entries)
                {
                    if (e?.pawn != null && !pawns.Contains(e.pawn))
                    {
                        pawns.Add(e.pawn);
                    }
                }
                foreach (FpTally tally in comp.tallies)
                {
                    if (tally?.owner != null && !pawns.Contains(tally.owner))
                    {
                        pawns.Add(tally.owner);
                    }
                }
                pawns.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
                foreach (Pawn p in pawns)
                {
                    Log.Message(StateLine(comp.map, t, p));
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] FPTEST: writing the state lines failed: " + e, 93652201);
            }
        }

        private static string StateLine(Map map, int t, Pawn p)
        {
            Verse.AI.Job job = p.CurJob;
            string jobText = job == null ? "none:-1" : job.def.defName + ":" + job.loadID;
            int wl = -1;
            Verse.AI.JobDriver d = p.jobs?.curDriver;
            IFpWorkAdapter a = d == null ? null : FpWork.AdapterFor(d);
            if (a != null)
            {
                float total = a.Total(d);
                wl = total > 0f ? (int)(1000f * a.Done(d) / total) : -1;
            }
            IntVec3 pos = p.Position;
            return "FPSTATE m=" + map.uniqueID + " t=" + t + " p=" + p.thingIDNumber + " pos=" + pos.x + "," + pos.z
                + " job=" + jobText + " wl=" + wl + " " + FpState.Describe(p);
        }
    }
}
