using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using K = IsoRim.Fp.FpTestKit;
using Key = UnityEngine.InputSystem.Key;
using S = IsoRim.Fp.FpScene;

namespace IsoRim.Fp
{
    // Milestone 6 scenarios (fp-play-spec 16, M6: hardening and polish), tracks A and B.
    //
    // Solo (windowed, injected input):
    // - m6-facing: the idle facing follows the camera's quadrant (FpSetFacing) within 300 ms, with hysteresis; a walk faces
    //   the way walked and the colonist turns back to the camera's quadrant when it stops; drafted, it doesn't turn south.
    // - m6-lead: the eye's lead stays within 0.35 cells and on walkable cells, fades once walking and after stopping, stays
    //   at nothing against a blocked step and with the option off; the head bob stays within its amplitude.
    // - m6-hover: while captured the interface's passes see the pointer at the screen's centre (the hover leak), wherever
    //   the virtual pointer is, and the mouse look still turns by exactly the counts (the pin adds no delta).
    // - m6-status: the autopilot status at the test copy's 1280 x 720 isn't cut and stays clear of the inspect pane (its real
    //   width: 72 px per visible tab) and the time controls; the needs strip shows above the pane's tab row.
    // - m6-cheatsheet: the controls line on the first entry never overlaps a message or the colonist bar.
    // - m6-flash: damage to the played colonist flashes the screen's edges.
    // - m6-wheel (added at integration, with the wheel cycling of spec 7.1): on the scene's grenades the wheel moves the
    //   left button's option off the best one, the hint follows, a click takes the option shown, and another target starts
    //   at its best option again.
    //
    // Multiplayer (--mp; host headless with Direct calls, client windowed):
    // - mp6-guard: the host's vanilla order and draft for the client's controlled colonist are refused on both machines
    //   (the issuer told), an order for an uncontrolled colonist is taken; then the host's forced release ends the client's
    //   play.
    // - mp6-facing: the client's camera turns reach the host as the colonist's facing; the eye's lead shows over the
    //   command's latency.
    // - mp6-tags: the client sees the host's controlled colonist tagged with the host's name, flat and in 3D, and not with
    //   the option off.
    // - mp6-both: both players in first person for 5 minutes (the host walks, drafts and fires; the client crafts with skill
    //   checks); a join point forced while the client's check is pending brings the same check's ring back.
    // - mp6-soak: both players walking in first person with slow desync tracing on for 25 minutes: no desync.
    //
    // Deviations from spec 16's M6 table: m6-facing is solo (the host-side check of the same is mp6-facing); mp6-soak runs
    // 25 minutes, not 30 (FpTestKit's whole-scenario timeout is 30 minutes of real time from the host's start, which
    // includes hosting and the client's join); the rest are additions for the M6 items the table has no scenario for.
    // m6-wheel takes the grenades' cell out of the scene's stockpile first: on a home map the game offers no "Pick up",
    // so a stored weapon has "Equip" alone; out of storage it also gets "Prioritize hauling" (spec 16 assumed several
    // options). The host scenarios find the scene again after the client's join, whose join point reloads the host's game.
    internal static class FpTestM6
    {
        internal static void Register(Dictionary<string, Func<IEnumerator<int>>> s)
        {
            s["m6-facing"] = Facing;
            s["m6-lead"] = Lead;
            s["m6-hover"] = Hover;
            s["m6-status"] = Status;
            s["m6-cheatsheet"] = CheatSheet;
            s["m6-flash"] = Flash;
            s["m6-wheel"] = Wheel;
            s["mp6-guard"] = MpGuard;
            s["mp6-facing"] = MpFacing6;
            s["mp6-tags"] = MpTags;
            s["mp6-both"] = MpBoth;
            s["mp6-soak"] = MpSoak;
            K.AddSceneExtra("mp6-both", map => SpawnKit(map, out string error) ? null : error);
        }

        private const string ControlJob = "IsoRimFp_Control";

        // fp_compare.py needs at least 30 FPSTATE lines both players logged (one per controlled pawn every 60 ticks).
        private const int CompareTicks = 1900;

        // ---------------------------------------------------------------- helpers (as FpTestM1 and FpTestM3)

        private static string JobOf(Pawn p)
        {
            return p?.CurJob == null ? "none" : p.CurJob.def.defName + " " + p.CurJob.targetA;
        }

        private static bool InControlJob(Pawn p)
        {
            return p?.CurJob?.def?.defName == ControlJob;
        }

        private static FpEntry Entry(Pawn p)
        {
            return FpRegistry.EntryOf(p);
        }

        private static string State(Pawn p)
        {
            return JobOf(p) + " | rot " + (p?.Rotation.ToStringHuman() ?? "-") + " | " + FpState.Describe(p) + " | facing "
                + (Entry(p)?.facing.ToString() ?? "-") + " | ring " + FpSkillCheck.State;
        }

        private static string Mode => "mode " + FpPlay.Mode + ", confirmed " + FpPlay.Confirmed + ", first person " + FirstPerson.On
            + " (" + (FirstPerson.Pawn?.LabelShort ?? "-") + ")";

        private static void SelectOnly(Thing t)
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(t, false, false);
        }

        private static void CloseMainTab()
        {
            if (Find.MainTabsRoot.OpenTab != null)
            {
                Find.MainTabsRoot.EscapeCurrentTab(false);
            }
        }

        private static IEnumerable<int> EnterPlay(Pawn p)
        {
            SelectOnly(p);
            yield return K.Tap(Key.F5);
            yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Playing && FpPlay.Confirmed, "for play to be confirmed on " + p.LabelShort, 900);
            yield return K.WaitUntil(() => InControlJob(p) && !p.pather.Moving, "for " + p.LabelShort + " to stand in the control job", 600);
            yield return 30;
        }

        private static IEnumerable<int> LeavePlay()
        {
            if (FpPlay.Mode == FpMode.Playing || FirstPerson.On)
            {
                yield return K.Tap(Key.F5);
                yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Off && !FirstPerson.On, "for first person to end", 600);
            }
            yield return 10;
        }

        // Turns the play camera to an absolute yaw and pitch (FpCamera only takes deltas).
        private static void Turn(float yaw, float pitch)
        {
            FpCamera.Look(FpMath.DeltaDeg(FpCamera.Yaw, yaw), pitch - FpCamera.Pitch);
        }

        // Points the crosshair at a true-space point (x, height, z), twice (the eye moves a little with the first turn).
        private static IEnumerable<int> AimAt(Vector3 target)
        {
            for (int i = 0; i < 2; i++)
            {
                Vector3 eye = View3D.EyePosition;
                Vector3 d = target - eye;
                float flat = Mathf.Sqrt(d.x * d.x + d.z * d.z);
                Turn(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, Mathf.Atan2(-d.y, Mathf.Max(flat, 0.01f)) * Mathf.Rad2Deg);
                yield return 3;
            }
        }

        private static Vector3 At(Thing t, float height)
        {
            Vector3 c = t.TrueCenter();
            return new Vector3(c.x, height, c.z);
        }

        private static double Now => Time.realtimeSinceStartupAsDouble;

        private static int Players
        {
            get
            {
                try
                {
                    return MpBridge.InSession ? MP.GetPlayers().Count : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        // The live messages' texts (Verse.Messages keeps them private).
        private static List<string> LiveMessages()
        {
            List<string> texts = new List<string>();
            try
            {
                if (AccessTools.Field(typeof(Messages), "liveMessages")?.GetValue(null) is IEnumerable list)
                {
                    foreach (object m in list)
                    {
                        if (m is Message msg && msg.text != null)
                        {
                            texts.Add(msg.text);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                K.Info("reading the live messages failed: " + e.Message);
            }
            return texts;
        }

        // The fixed middle of a two-argument translation ("{0} is controlled by {1} ..." -> " is controlled by ").
        private static string Between(string key)
        {
            string t = key.Translate("\u0001", "\u0002").ToString();
            int a = t.IndexOf('\u0001'), b = t.IndexOf('\u0002');
            return a >= 0 && b > a ? t.Substring(a + 1, b - a - 1) : t;
        }

        private static IEnumerable<int> AfterReload()
        {
            yield return K.WaitUntil(() => Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null
                && !LongEventHandler.AnyEventNowOrWaiting, "for the reload to finish", 7200);
            K.Check("scene found again", S.Adopt(Find.CurrentMap, out string error), error ?? S.Describe());
        }

        private static IEnumerable<int> HostWaitForClient()
        {
            yield return K.WaitUntil(() => MpBridge.InSession, "for hosting", 3600);
            foreach (int w in AfterReload()) yield return w;
            Game hosted = Current.Game;
            yield return K.WaitUntil(() => Players >= 2, "for the client to join", 18000);
            // The client's join makes a join point (autoJoinPoint Join), which reloads the host's game as well: every scene
            // object is a new one after it (the first mp6-guard and mp6-facing runs read the old C2, still on the scene's
            // Wait job and facing south, and timed out). Found again once that reload is over. (M1-M3's host scenarios only
            // look pawns up by id, which survives the reload.)
            for (int i = 0; i < 1800 && Current.Game == hosted; i++)
            {
                yield return 1;
            }
            K.Info("join point " + (Current.Game == hosted ? "reload not seen; the scene is looked up again anyway" : "reloaded the host's game"));
            foreach (int w in AfterReload()) yield return w;
            yield return 600;
        }

        // A first-person command of the client's has run here: its join is complete (see FpTestM1.HostWaitForClientCommand).
        private static IEnumerable<int> HostWaitForClientCommand()
        {
            yield return K.WaitUntil(() => K.FirstCommandTick(false) >= 0, "for the client's first command", 36000);
        }

        private static IEnumerable<int> ClientReady()
        {
            while (!K.Direct.Intent(S.C2, FpDir.Stop))
            {
                yield return 1;
            }
            yield return K.WaitUntil(() => K.FirstCommandTick(true) >= 0, "for the ready signal to run", 1800);
        }

        private static IEnumerable<int> HostWaitForClientDone()
        {
            yield return K.WaitUntil(() => Players < 2, "for the client to finish", 36000);
            yield return 120;
        }

        // Long waits in steps of a few hundred ticks (one WaitTicks may last at most about a minute of real time).
        private static IEnumerable<int> WaitLong(int ticks)
        {
            int until = K.Ticks + ticks;
            while (K.Ticks < until)
            {
                yield return K.WaitTicks(Math.Min(600, until - K.Ticks));
            }
        }

        // The other player controls p (not this one, not nobody).
        private static bool ControlledByOther(Pawn p)
        {
            FpEntry e = Entry(p);
            return e != null && e.controller >= 0 && e.controller != FpIds.Local;
        }

        private static IEnumerable<int> HostSquare(Pawn p, int ticks)
        {
            foreach (byte dir in new byte[] { 0, 4, 8, 12 })
            {
                K.Direct.Intent(p, dir);
                yield return K.WaitTicks(ticks);
            }
            K.Direct.Intent(p, FpDir.Stop);
            yield return K.WaitTicks(30);
        }

        private static IEnumerable<int> ClientSquare(int ticks)
        {
            foreach (float yaw in new[] { 0f, 90f, 180f, 270f })
            {
                Turn(yaw, 8f);
                yield return 5;
                yield return K.HoldTicks(ticks, Key.W);
            }
        }

        // ---------------------------------------------------------------- m6-facing

        private static IEnumerator<int> Facing()
        {
            Pawn p = S.C1;
            foreach (int w in EnterPlay(p)) yield return w;
            // The first facing goes out once control is confirmed: the camera's quadrant, which play started on.
            yield return K.WaitUntil(() => Entry(p) != null && Entry(p).facing <= 3, "for the first facing", 300);
            byte first = Entry(p).facing;
            K.Check("m6-facing first facing is the camera's", first == FpInput.Quadrant(FpCamera.Yaw, 255), "yaw " + FpCamera.Yaw.ToString("0.0") + ", " + State(p));
            yield return K.WaitTicks(10);
            K.Check("m6-facing colonist faces it", p.Rotation.AsByte == first, State(p));

            // Within the quadrant and its 10 degrees of hysteresis nothing is sent.
            int sent0 = FpHits.Count("Facing sent");
            Turn(first * 90f + 50f, 8f);
            yield return 30;
            K.Check("m6-facing hysteresis holds at 50 degrees", FpHits.Count("Facing sent") == sent0 && p.Rotation.AsByte == first,
                "sent " + (FpHits.Count("Facing sent") - sent0) + ", " + State(p));

            // A quarter turn: the colonist turns within 300 ms (spec 16, m6-facing).
            byte want = (byte)((first + 1) & 3);
            Turn(want * 90f, 8f);
            double t0 = Now;
            yield return K.WaitUntil(() => p.Rotation.AsByte == want, "for the colonist to turn to " + new Rot4(want).ToStringHuman(), 120);
            double took = Now - t0;
            K.Check("m6-facing turned within 300 ms", p.Rotation.AsByte == want && took <= 0.3, "after " + (took * 1000).ToString("0") + " ms, " + State(p));
            K.Check("m6-facing synced facing", Entry(p)?.facing == want, State(p));

            // Walking backwards faces the way walked; standing again, the colonist turns back to the camera's quadrant.
            Turn(90f, 8f);
            yield return K.WaitUntil(() => p.Rotation == Rot4.East, "to face east", 120);
            yield return K.HoldTicks(45, Key.S);
            bool walkedWest = p.Rotation == Rot4.West;
            K.Check("m6-facing walking faces the way walked", walkedWest, State(p));
            yield return K.WaitUntil(() => !p.pather.Moving, "to stop", 300);
            yield return K.WaitTicks(20);
            K.Check("m6-facing standing faces the camera again", p.Rotation == Rot4.East, State(p));

            // Drafted and standing: no turn south (the control job handles facing).
            yield return K.Tap(Key.R);
            yield return K.WaitTicks(60);
            K.Check("m6-facing drafted keeps the facing", p.Drafted && p.Rotation == Rot4.East && InControlJob(p), "drafted " + p.Drafted + ", " + State(p));
            yield return K.Tap(Key.R);
            yield return K.WaitTicks(10);
            K.Shot("m6_facing");
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m6-lead

        private static IEnumerator<int> Lead()
        {
            Pawn p = S.C1;
            bool lead0 = FpSettings.eyeLead;
            float bob0 = FpSettings.headBob;
            foreach (int w in EnterPlay(p)) yield return w;
            FpSettings.eyeLead = true;
            FpSettings.headBob = 1f;

            // South, into the cleared ground: bounds and walkability every frame, the bob within its amplitude.
            Turn(180f, 8f);
            yield return 10;
            float maxLead = 0f, maxBob = 0f, walkingLead = 0f;
            float eyeLow = float.MaxValue, eyeHigh = float.MinValue;   // the eye's height in the picture while walking
            bool walkable = true;
            string bad = "";
            yield return K.Key(Key.W, true);
            for (int i = 0; i < 150; i++)
            {
                yield return 1;
                Vector3 o = FpCamera.EyeOffset;
                float l = new Vector2(o.x, o.z).magnitude;
                maxLead = Mathf.Max(maxLead, l);
                maxBob = Mathf.Max(maxBob, Mathf.Abs(o.y));
                if (i >= 90)
                {
                    walkingLead = Mathf.Max(walkingLead, l);
                }
                if (i >= 30 && p.pather.Moving)
                {
                    float y = View3D.EyePosition.y;
                    eyeLow = Mathf.Min(eyeLow, y);
                    eyeHigh = Mathf.Max(eyeHigh, y);
                }
                Vector3 at = p.DrawPos + new Vector3(o.x, 0f, o.z);
                IntVec3 c = new IntVec3(Mathf.FloorToInt(at.x), 0, Mathf.FloorToInt(at.z));
                if (!c.InBounds(p.Map) || !c.Walkable(p.Map))
                {
                    walkable = false;
                    bad = c.ToString();
                }
            }
            yield return K.Key(Key.W, false);
            K.Info("m6-lead walk south: max lead " + maxLead.ToString("0.000") + " (solo: the walk starts a tick after the key, so little lead), "
                + "lead while walking " + walkingLead.ToString("0.000") + ", max bob " + maxBob.ToString("0.0000"));
            K.Check("m6-lead within 0.35 cells", maxLead <= FpCamera.LeadMax + 0.001f, maxLead.ToString("0.000"));
            K.Check("m6-lead on walkable cells", walkable, walkable ? "all frames" : "eye over " + bad);
            K.Check("m6-lead fades while walking that way", walkingLead < 0.05f, walkingLead.ToString("0.000"));
            K.Check("m6-lead head bob within its amplitude", maxBob <= FpCamera.BobAmplitude + 1e-4f && maxBob > 0.25f * FpCamera.BobAmplitude,
                maxBob.ToString("0.0000"));
            // The bob reaches the picture (FirstPerson.Eye keeps the offset's y since the integration's Port line).
            float eyeSpan = eyeHigh >= eyeLow ? eyeHigh - eyeLow : 0f;
            K.Check("m6-lead head bob moves the eye", eyeSpan > 0.25f * FpCamera.BobAmplitude && eyeSpan <= 2f * FpCamera.BobAmplitude + 1e-3f,
                "eye height " + (eyeHigh >= eyeLow ? eyeLow.ToString("0.0000") + " .. " + eyeHigh.ToString("0.0000") : "never sampled walking"));
            yield return K.WaitUntil(() => !p.pather.Moving, "to stop", 300);
            yield return 30;
            K.Check("m6-lead gone after stopping", FpCamera.EyeOffset.magnitude < 0.01f, FpCamera.EyeOffset.ToString("F4"));

            // Blocked: back north, the stove 3 cells north of the start stops the walk; the key held on doesn't lean the eye.
            Turn(0f, 8f);
            yield return 5;
            yield return K.Key(Key.W, true);
            yield return K.WaitUntil(() => p.pather.Moving, "to walk north", 120);
            yield return K.WaitUntil(() => !p.pather.Moving, "to be stopped by the stove", 3600);
            float blocked = 0f;
            for (int i = 0; i < 60; i++)
            {
                yield return 1;
                blocked = Mathf.Max(blocked, new Vector2(FpCamera.EyeOffset.x, FpCamera.EyeOffset.z).magnitude);
            }
            yield return K.Key(Key.W, false);
            K.Check("m6-lead none against a blocked step", blocked < 0.02f, blocked.ToString("0.000") + " at " + p.Position);

            // Options off: no lead, no bob.
            FpSettings.eyeLead = false;
            FpSettings.headBob = 0f;
            Turn(180f, 8f);
            yield return 5;
            float any = 0f;
            yield return K.Key(Key.W, true);
            for (int i = 0; i < 60; i++)
            {
                yield return 1;
                any = Mathf.Max(any, FpCamera.EyeOffset.magnitude);
            }
            yield return K.Key(Key.W, false);
            K.Check("m6-lead off: no offset", any == 0f, any.ToString("0.0000"));
            K.Info("m6-lead eye height with the bob off is " + (View3D.EyePosition.y).ToString("0.000"));
            FpSettings.eyeLead = lead0;
            FpSettings.headBob = bob0;
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m6-wheel

        private static IEnumerator<int> Wheel()
        {
            Pawn p = S.C1;
            Thing nade = S.Grenades;
            if (nade == null || !nade.Spawned || p == null)
            {
                K.Check("m6-wheel setup", false, "no grenades in the scene");
                yield return K.Done();
                yield break;
            }
            foreach (int w in EnterPlay(p)) yield return w;
            CloseMainTab();
            // The scene keeps the grenades in a stockpile, and on a home map the game offers no "Pick up" for them
            // (FloatMenuOptionProvider_PickUpItem), so "Equip" was their only option (first run: "1 options to cycle").
            // Their cell taken out of the stockpile, they also get the game's "Prioritize hauling" (to the stockpile's
            // spare cell; C1 hauls, FpScene). C2 stands meanwhile, so nobody hauls them first.
            if (nade.Position.GetZone(nade.Map) is Zone_Stockpile store)
            {
                store.RemoveCell(nade.Position);
            }
            foreach (int w in AimAt(At(nade, 0.05f))) yield return w;
            for (int i = 0; i < 120 && !(FpActions.HintKind == FpHintKind.Action && FpPick.Current.Thing == nade); i++)
            {
                yield return 1;
            }
            yield return 20;   // past the hint's next slot
            string hint0 = FpActions.Hint;
            int n = FpActions.CycleCount;
            K.Info("m6-wheel on " + nade.LabelShort + ": hint \"" + hint0 + "\" (" + FpActions.HintKind + "), " + n + " options to cycle");
            if (!K.Check("m6-wheel options to cycle", FpPick.Current.Thing == nade && FpActions.HintKind == FpHintKind.Action && n >= 2,
                "crosshair on " + (FpPick.Current.Thing?.ThingID ?? "nothing") + ", " + n + " options"))
            {
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
                yield break;
            }
            int cycled0 = FpHits.Count("LMB option cycled");
            yield return K.Scroll(-1f);
            yield return 5;
            K.Check("m6-wheel cycled", FpActions.CycleIndex != 0 && FpActions.Hint != hint0 && FpHits.Count("LMB option cycled") == cycled0 + 1,
                "index " + FpActions.CycleIndex + ", hint \"" + FpActions.Hint + "\", cycled +" + (FpHits.Count("LMB option cycled") - cycled0));
            string want = FpActions.CycledLabel;
            int chosen0 = FpHits.Count("LMB option chosen");
            yield return K.Button(0, true);
            yield return K.Button(0, false);
            yield return K.WaitTicks(10);
            K.Check("m6-wheel click takes the option shown", FpHits.Count("LMB option chosen") == chosen0 + 1 && want != null && FpActions.LastChosen == want,
                "chose \"" + FpActions.LastChosen + "\", shown \"" + want + "\"; " + JobOf(p));
            // Another target: back to its best option.
            Turn(FpCamera.Yaw + 180f, 8f);
            yield return 40;
            K.Check("m6-wheel new target starts at its best", FpActions.CycleIndex == 0, "index " + FpActions.CycleIndex + ", hint \"" + FpActions.Hint + "\"");
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m6-hover

        private static IEnumerator<int> Hover()
        {
            foreach (int w in EnterPlay(S.C1)) yield return w;
            CloseMainTab();
            int pins0 = FpHits.Count("Pointer pinned");
            int tips0 = FpHits.Count("Thing tooltips skipped");
            yield return 20;
            Vector2 centre = new Vector2(UI.screenWidth / 2f, UI.screenHeight / 2f);
            K.Check("m6-hover passes pinned", FpHits.Count("Pointer pinned") > pins0, (FpHits.Count("Pointer pinned") - pins0) + " pinned passes in 20 frames");
            // The map's thing tooltips (at the pinned pointer: the crosshair) are skipped while captured.
            K.Check("m6-hover thing tooltips skipped", FpHits.Count("Thing tooltips skipped") > tips0,
                (FpHits.Count("Thing tooltips skipped") - tips0) + " passes skipped in 20 frames");
            K.Check("m6-hover interface pointer at the centre", Time.frameCount - FpHud.LastHudPointerFrame < 5 && (FpHud.LastHudPointer - centre).magnitude < 1.5f,
                "HUD saw " + FpHud.LastHudPointer + ", centre " + centre);
            // The virtual pointer over the first main button, then the inspect pane: the interface still sees the centre.
            float s = Prefs.UIScale;
            foreach (Vector2 px in new[] { new Vector2(40f * s, 15f * s), new Vector2(150f * s, 120f * s) })
            {
                yield return K.VirtualPointer(px);
                yield return 4;
                K.Check("m6-hover pinned with the pointer at " + px, (FpHud.LastHudPointer - centre).magnitude < 1.5f, "HUD saw " + FpHud.LastHudPointer);
            }
            // The look turns by exactly the counts (m1-look's numbers): the pin's two moves add no delta.
            float yaw0 = FpCamera.Yaw, pitch0 = FpCamera.Pitch;
            yield return K.MouseDelta(20f, 0f, 10);
            yield return 10;
            float dYaw = FpMath.DeltaDeg(yaw0, FpCamera.Yaw);
            float want = 200f * FpSettings.sensitivity;
            K.Check("m6-hover look unchanged (yaw)", Mathf.Abs(dYaw - want) <= 1.5f, "turned " + dYaw.ToString("0.00") + ", wanted " + want.ToString("0.00"));
            yield return K.MouseDelta(0f, 20f, 10);
            yield return 10;
            float dPitch = FpCamera.Pitch - pitch0;
            float wantPitch = (IsoRimMod.Settings != null && IsoRimMod.Settings.invertY ? 1f : -1f) * want;
            K.Check("m6-hover look unchanged (pitch)", Mathf.Abs(dPitch - wantPitch) <= 1.5f, "tilted " + dPitch.ToString("0.00") + ", wanted " + wantPitch.ToString("0.00"));
            // The free cursor: no pin, the interface sees the real pointer.
            yield return K.VirtualPointer(new Vector2(40f * s, 15f * s));
            yield return K.Key(Key.LeftAlt, true);
            yield return 6;
            bool freeNotPinned = !FpInput.Captured && (FpHud.LastHudPointer - centre).magnitude > 50f;
            K.Check("m6-hover free cursor not pinned", freeNotPinned, "captured " + FpInput.Captured + ", HUD saw " + FpHud.LastHudPointer);
            int tipsFree = FpHits.Count("Thing tooltips skipped");
            yield return 4;
            K.Check("m6-hover free cursor keeps the thing tooltips", FpHits.Count("Thing tooltips skipped") == tipsFree,
                (FpHits.Count("Thing tooltips skipped") - tipsFree) + " passes skipped with the cursor free");
            yield return K.Key(Key.LeftAlt, false);
            yield return 10;
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m6-status

        private static IEnumerator<int> Status()
        {
            Pawn p = S.C1;
            foreach (int w in EnterPlay(p)) yield return w;
            K.Info("screen " + UI.screenWidth + " x " + UI.screenHeight + " UI pixels (UI scale " + Prefs.UIScale + ")");
            // On autopilot: hungry with AutoNeeds (as m2-autoneeds), the longest status the HUD shows.
            bool needs0 = FpSettings.autoNeeds;
            FpSettings.autoNeeds = true;
            p.needs.food.CurLevelPercentage = 0.05f;
            yield return K.WaitUntil(() => FpState.AutopilotKey(p) != null, "for the autopilot", 1800);
            string reason = FpState.AutopilotReason(p);
            // Case aside: the game's formatting capitalises the reason after "Autopilot: " (first run: "Autopilot: Looking after
            // their needs" for "looking after their needs").
            Func<bool> shown = () => Time.frameCount - FpHud.LastStatusFrame < 3
                && FpHud.LastStatusLines.Any(l => l.text.IndexOf(reason, StringComparison.OrdinalIgnoreCase) >= 0);
            for (int i = 0; i < 300 && !shown(); i++)
            {
                yield return 1;
            }
            if (!K.Check("m6-status shows the autopilot", shown(), "reason \"" + reason + "\", now " + (FpState.AutopilotReason(p) ?? "no autopilot")
                + ", status drawn " + (Time.frameCount - FpHud.LastStatusFrame) + " frames ago: " + string.Join(" | ", FpHud.LastStatusLines.Select(l => "\"" + l.text + "\""))
                + "; " + JobOf(p)))
            {
                p.needs.food.CurLevelPercentage = 0.9f;
                FpSettings.autoNeeds = needs0;
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
                yield break;
            }
            string lines = string.Join(" | ", FpHud.LastStatusLines.Select(l => "\"" + l.text + "\" at " + l.rect));
            // The inspect pane is 72 px per visible tab, at least 432 (a colonist with the DLCs' tabs gets a wider one).
            Window pane = Find.WindowStack.WindowOfType<MainTabWindow_Inspect>();
            float paneRight = pane != null ? pane.windowRect.xMax : 432f;
            K.Info("m6-status lines: " + lines + "; inspect pane " + (pane != null ? pane.windowRect.ToString() : "closed"));
            K.Check("m6-status nothing cut", !FpHud.LastStatusCut, lines);
            K.Check("m6-status clear of the inspect pane", FpHud.LastStatusLines.All(l => l.rect.xMin >= paneRight - 0.5f), lines + "; pane right " + paneRight);
            K.Check("m6-status clear of the time controls", FpHud.LastStatusLines.All(l => l.rect.xMax <= UI.screenWidth - 200f + 0.5f), lines);
            K.Check("m6-status on screen", FpHud.LastStatusLines.All(l => l.rect.yMin >= 0f && l.rect.yMax <= UI.screenHeight), lines);
            // The needs strip (spec 14, M6): shown, on screen, above the inspect pane and its tab row, apart from the status.
            Rect needs = FpHud.LastNeedsRect;
            Rect paneAndTabs = pane != null ? new Rect(pane.windowRect.x, pane.windowRect.y - InspectPaneUtility.TabHeight, pane.windowRect.width,
                pane.windowRect.height + InspectPaneUtility.TabHeight) : default(Rect);
            K.Check("m6-status needs strip shown", needs.width > 0f && needs.xMin >= 0f && needs.yMin >= 0f && needs.yMax <= UI.screenHeight, needs.ToString());
            K.Check("m6-status needs strip clear of the inspect pane", pane == null || !needs.Overlaps(paneAndTabs), needs + " vs " + paneAndTabs);
            K.Check("m6-status needs strip clear of the status", !FpHud.LastStatusLines.Any(l => l.rect.Overlaps(needs)), needs + " vs " + lines);
            K.Shot("m6_status_autopilot");
            yield return 2;   // the capture is of this frame's picture: still hungry when it is drawn
            p.needs.food.CurLevelPercentage = 0.9f;
            FpSettings.autoNeeds = needs0;
            yield return K.WaitTicks(30);
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m6-cheatsheet

        private static IEnumerator<int> CheatSheet()
        {
            bool sheet0 = FpSettings.cheatSheet;
            FpSettings.cheatSheet = true;
            foreach (int w in EnterPlay(S.C1)) yield return w;   // the session's first entry: IsoRim's message and the sheet
            int frames = 0, overMessage = 0, overBar = 0;
            string where = "";
            for (int round = 0; round < 2; round++)
            {
                if (round == 1)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        Messages.Message("FPTEST message " + i + ": a line of the game's message stack", MessageTypeDefOf.SilentInput, false);
                    }
                }
                for (int i = 0; i < 90; i++)
                {
                    yield return 1;
                    if (FpHud.LastCheatSheetFrame < Time.frameCount - 2 || FpHud.LastCheatSheetRect.width <= 0f)
                    {
                        continue;
                    }
                    frames++;
                    Rect r = FpHud.LastCheatSheetRect;
                    where = r.ToString();
                    if (Messages.CollidesWithAnyMessage(r, out _))
                    {
                        overMessage++;
                    }
                    ColonistBar bar = Find.ColonistBar;
                    if (bar?.DrawLocs != null && bar.DrawLocs.Any(l => new Rect(l, bar.Size).Overlaps(r)))
                    {
                        overBar++;
                    }
                }
                if (round == 1)
                {
                    K.Shot("m6_cheatsheet_messages");
                }
            }
            K.Check("m6-cheatsheet shown", frames > 60, frames + " frames drawn, last at " + where);
            K.Check("m6-cheatsheet never over a message", overMessage == 0, overMessage + " frames over a message (last at " + where + ")");
            K.Check("m6-cheatsheet never over the colonist bar", overBar == 0, overBar + " frames over a portrait");
            FpSettings.cheatSheet = sheet0;
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- m6-flash

        private static IEnumerator<int> Flash()
        {
            Pawn p = S.C1;
            foreach (int w in EnterPlay(p)) yield return w;
            yield return 20;
            K.Check("m6-flash none before", FpHud.LastFlashAlpha == 0f, FpHud.LastFlashAlpha.ToString("0.00"));
            float hp0 = p.health.summaryHealth.SummaryHealthPercent;
            p.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 3f));
            float seen = 0f;
            for (int i = 0; i < 10; i++)
            {
                yield return 1;
                seen = Mathf.Max(seen, FpHud.LastFlashAlpha);
            }
            K.Check("m6-flash on damage", seen > 0.1f, "health " + hp0.ToString("0.000") + " -> " + p.health.summaryHealth.SummaryHealthPercent.ToString("0.000")
                + ", flash " + seen.ToString("0.00"));
            yield return 60;
            K.Check("m6-flash fades", FpHud.LastFlashAlpha == 0f, FpHud.LastFlashAlpha.ToString("0.00"));
            foreach (int w in LeavePlay()) yield return w;
            yield return K.Done();
        }

        // ---------------------------------------------------------------- mp6-guard

        private static IEnumerator<int> MpGuard()
        {
            if (K.IsHost)
            {
                foreach (int w in HostWaitForClient()) yield return w;
                yield return K.WaitUntil(() => ControlledByOther(S.C2) && InControlJob(S.C2), "for the client's control of C2", 3600);
                yield return K.WaitTicks(120);
                int refused0 = FpHits.Count("Order refused");
                bool drafted0 = S.C2.Drafted;
                int controller = Entry(S.C2).controller;
                yield return K.WaitUntil(() => MP.InInterface, "for Multiplayer to take commands", 1800);
                // Vanilla synced calls, as a click on the colonist would make them (Multiplayer's SyncMethods).
                S.C2.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, S.Origin + new IntVec3(-2, 0, -6)));
                S.C2.drafter.Drafted = !drafted0;
                S.C1.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, S.Origin + new IntVec3(2, 0, -6)));
                yield return K.WaitUntil(() => FpHits.Count("Order refused") >= refused0 + 2, "for the refusals", 900);
                yield return K.WaitTicks(30);
                K.Check("mp6-guard refused here", FpHits.Count("Order refused") - refused0 >= 2, (FpHits.Count("Order refused") - refused0) + " refused");
                K.Check("mp6-guard C2 kept its control", InControlJob(S.C2) && S.C2.Drafted == drafted0 && Entry(S.C2)?.controller == controller,
                    "drafted " + S.C2.Drafted + ", " + State(S.C2));
                K.Check("mp6-guard C1 took its order", S.C1.CurJob?.def == JobDefOf.Goto || S.C1.Position.z < S.Origin.z, JobOf(S.C1) + " at " + S.C1.Position);
                string mid = Between("R3D_Fp_ControlledBy");
                K.Check("mp6-guard issuer told", LiveMessages().Any(m => m.Contains(mid) && m.Contains(S.C2.LabelShort)),
                    string.Join(" / ", LiveMessages()));
                foreach (int w in WaitLong(CompareTicks)) yield return w;
                // Then the host takes C2 back (FpSetControl off with Force): the client's play ends.
                yield return K.WaitUntil(() => FpSync.SendSetControl(S.C2, false, FpFlags.Force), "for the forced release to go out", 600);
                yield return K.WaitUntil(() => Entry(S.C2) == null, "for C2's release", 900);
                K.Check("mp6-guard forced release", Entry(S.C2) == null && !InControlJob(S.C2), State(S.C2));
                foreach (int w in HostWaitForClientDone()) yield return w;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                foreach (int w in EnterPlay(S.C2)) yield return w;
                bool drafted0 = S.C2.Drafted;
                yield return K.WaitUntil(() => FpHits.Count("Order refused") >= 2, "for the host's orders to be refused", 7200);
                yield return K.WaitTicks(30);
                K.Check("mp6-guard refused here too", FpHits.Count("Order refused") >= 2, FpHits.Count("Order refused") + " refused");
                K.Check("mp6-guard still playing C2", FpPlay.Mode == FpMode.Playing && FpPlay.Confirmed && InControlJob(S.C2) && S.C2.Drafted == drafted0,
                    Mode + ", drafted " + S.C2.Drafted + ", " + State(S.C2));
                string mid = Between("R3D_Fp_ControlledBy");
                K.Check("mp6-guard only the issuer told", !LiveMessages().Any(m => m.Contains(mid)), string.Join(" / ", LiveMessages()));
                // Walk a little meanwhile (state lines with movement for the compare).
                Turn(180f, 8f);
                yield return K.HoldTicks(60, Key.W);
                yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Off, "for the host's forced release", 36000);
                K.Check("mp6-guard play ended by the forced release", FpPlay.Mode == FpMode.Off && Entry(S.C2) == null, Mode + ", " + State(S.C2));
                yield return 60;
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
            }
            else
            {
                K.Check("mp6-guard role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }

        // ---------------------------------------------------------------- mp6-facing

        private static IEnumerator<int> MpFacing6()
        {
            if (K.IsHost)
            {
                foreach (int w in HostWaitForClient()) yield return w;
                yield return K.WaitUntil(() => ControlledByOther(S.C2) && Entry(S.C2).facing <= 3, "for the client's control of C2 and its first facing", 3600);
                int changes = 0;
                byte last = Entry(S.C2).facing;
                while (changes < 3 && Players >= 2)
                {
                    byte from = last;
                    yield return K.WaitUntil(() => Entry(S.C2) == null || Entry(S.C2).facing != from || Players < 2, "for the client's next facing", 7200);
                    if (Entry(S.C2) == null || Players < 2)
                    {
                        break;
                    }
                    last = Entry(S.C2).facing;
                    byte want = last;
                    yield return K.WaitUntil(() => S.C2.Rotation.AsByte == want || Entry(S.C2)?.facing != want, "for C2 to turn here", 300);
                    changes++;
                    K.Check("mp6-facing turn " + changes + " seen here", S.C2.Rotation.AsByte == want, State(S.C2));
                }
                K.Check("mp6-facing three turns seen here", changes == 3, changes + " turns");
                foreach (int w in HostWaitForClientDone()) yield return w;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                Pawn p = S.C2;
                foreach (int w in EnterPlay(p)) yield return w;
                yield return K.WaitUntil(() => Entry(p)?.facing <= 3, "for the first facing", 600);
                yield return K.WaitTicks(60);
                // The lead over the command's latency: the walk starts a round trip after the key.
                bool lead0 = FpSettings.eyeLead;
                FpSettings.eyeLead = true;
                Turn(180f, 8f);
                yield return 10;
                float maxLead = 0f;
                yield return K.Key(Key.W, true);
                for (int i = 0; i < 40; i++)
                {
                    yield return 1;
                    maxLead = Mathf.Max(maxLead, new Vector2(FpCamera.EyeOffset.x, FpCamera.EyeOffset.z).magnitude);
                }
                yield return K.Key(Key.W, false);
                K.Check("mp6-facing lead shows over the latency", maxLead > 0.01f && maxLead <= FpCamera.LeadMax + 0.001f, maxLead.ToString("0.000"));
                FpSettings.eyeLead = lead0;
                yield return K.WaitUntil(() => !p.pather.Moving, "to stop", 300);
                yield return K.WaitTicks(30);
                for (int k = 0; k < 3; k++)
                {
                    byte want = (byte)((p.Rotation.AsByte + 1) & 3);
                    Turn(want * 90f, 8f);
                    double t0 = Now;
                    yield return K.WaitUntil(() => p.Rotation.AsByte == want, "for the turn to come back from the host", 600);
                    double took = Now - t0;
                    K.Info("mp6-facing turn " + (k + 1) + " took " + (took * 1000).ToString("0") + " ms (command round trip included)");
                    K.Check("mp6-facing turn " + (k + 1), p.Rotation.AsByte == want && took <= 0.6, "after " + (took * 1000).ToString("0") + " ms, " + State(p));
                    yield return K.WaitTicks(60);
                }
                foreach (int w in WaitLong(CompareTicks)) yield return w;
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
            }
            else
            {
                K.Check("mp6-facing role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }

        // ---------------------------------------------------------------- mp6-tags

        private static IEnumerator<int> MpTags()
        {
            if (K.IsHost)
            {
                foreach (int w in HostWaitForClient()) yield return w;
                foreach (int w in HostWaitForClientCommand()) yield return w;
                K.Direct.SetControl(S.C1, true, FpSettings.Flags);
                yield return K.WaitUntil(() => FpState.ControllerOf(S.C1) == FpIds.Local, "for the host's control of C1", 600);
                yield return K.WaitUntil(() => Players < 2 || ControlledByOther(S.C2), "for the client to play C2", 36000);
                foreach (int w in HostWaitForClientDone()) yield return w;
                K.Direct.SetControl(S.C1, false, 0);
                yield return K.WaitTicks(30);
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                foreach (int w in ClientReady()) yield return w;
                yield return K.WaitUntil(() => ControlledByOther(S.C1), "for the host's control of C1", 3600);
                int host = Entry(S.C1).controller;
                string name = FpIds.NameOf(host);
                // Flat (the client starts without the 3D view): the camera on C1.
                Find.CameraDriver.JumpToCurrentMapLoc(S.C1.Position);
                yield return K.WaitUntil(() => FpHud.LastFriendTags.Any(t => t.pawn == S.C1), "for C1's tag (flat)", 300);
                FpHud.FriendTagDrawn flat = FpHud.LastFriendTags.FirstOrDefault(t => t.pawn == S.C1);
                K.Check("mp6-tags flat tag names the host", flat.pawn == S.C1 && flat.name == name && name != "?", "\"" + flat.name + "\" at " + flat.at + ", host " + name);
                K.Shot("mp6_tags_flat");
                // In first person on C2, looking at C1: the tag over C1's head, near the crosshair.
                foreach (int w in EnterPlay(S.C2)) yield return w;
                foreach (int w in AimAt(At(S.C1, 1.0f))) yield return w;
                yield return 10;
                FpHud.FriendTagDrawn tag = FpHud.LastFriendTags.FirstOrDefault(t => t.pawn == S.C1);
                K.Check("mp6-tags 3D tag over C1", tag.pawn == S.C1 && Mathf.Abs(tag.at.x - UI.screenWidth / 2f) < UI.screenWidth / 6f && tag.at.y < UI.screenHeight / 2f + 10f,
                    tag.pawn == null ? "no tag" : "\"" + tag.name + "\" at " + tag.at + " (screen " + UI.screenWidth + " x " + UI.screenHeight + ")");
                K.Check("mp6-tags no tag on own colonist", !FpHud.LastFriendTags.Any(t => t.pawn == S.C2), string.Join(", ", FpHud.LastFriendTags.Select(t => t.pawn.LabelShort)));
                K.Shot("mp6_tags_3d");
                yield return 2;   // the capture is of this frame's picture: the tags stay on until it is drawn
                bool tags0 = FpSettings.friendTags;
                FpSettings.friendTags = false;
                yield return 5;
                K.Check("mp6-tags off", FpHud.LastFriendTags.Count == 0, FpHud.LastFriendTags.Count + " tags");
                FpSettings.friendTags = tags0;
                foreach (int w in WaitLong(CompareTicks)) yield return w;
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
            }
            else
            {
                K.Check("mp6-tags role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }

        // ---------------------------------------------------------------- mp6-both

        // The tailoring kit (as FpTestM3's mp3-craft kit), spawned by the host with its scene, before hosting.
        private static readonly IntVec3 BenchAt = new IntVec3(-4, 0, -6);
        private static readonly IntVec3 ClothAt = new IntVec3(-2, 0, -6);
        private const int ClothCount = 75;
        private static Building Bench;
        private static Thing Cloth;

        private static RecipeDef Tribalwear => DefDatabase<RecipeDef>.GetNamedSilentFail("Make_Apparel_TribalA");

        private static bool SpawnKit(Map map, out string error)
        {
            error = null;
            ThingDef benchDef = DefDatabase<ThingDef>.GetNamedSilentFail("HandTailoringBench");
            if (benchDef == null || Tribalwear == null || ThingDefOf.Cloth == null)
            {
                error = "HandTailoringBench, Make_Apparel_TribalA or Cloth is missing";
                return false;
            }
            Thing bench = ThingMaker.MakeThing(benchDef, ThingDefOf.Steel);
            bench.SetFactionDirect(Faction.OfPlayer);
            Bench = (Building)GenSpawn.Spawn(bench, S.Origin + BenchAt, map, Rot4.North);
            Thing cloth = ThingMaker.MakeThing(ThingDefOf.Cloth);
            cloth.stackCount = ClothCount;
            Cloth = GenSpawn.Spawn(cloth, S.Origin + ClothAt, map);
            K.Info("kit spawned: " + Bench + " at " + Bench.Position + ", " + Cloth + " x" + Cloth.stackCount);
            return true;
        }

        private static bool FindKit(Map map, out string error)
        {
            error = null;
            Bench = (S.Origin + BenchAt).GetThingList(map).OfType<Building>().FirstOrDefault(b => b.def.defName == "HandTailoringBench");
            Cloth = (S.Origin + ClothAt).GetThingList(map).FirstOrDefault(t => t.def == ThingDefOf.Cloth);
            if (Bench == null)
            {
                error = "no tailor bench at " + (S.Origin + BenchAt);
                return false;
            }
            return true;
        }

        private static WorkGiverDef GiverFor(Thing bench, RecipeDef recipe)
        {
            return bench == null || recipe == null ? null : DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(wg => wg.Worker is WorkGiver_DoBill
                && wg.fixedBillGiverDefs != null && wg.fixedBillGiverDefs.Contains(bench.def)
                && (recipe.requiredGiverWorkType == null || wg.workType == recipe.requiredGiverWorkType));
        }

        // Synced data only, so host and client pick the same colonist.
        private static bool CanCraft(Pawn p)
        {
            WorkGiverDef wg = GiverFor(Bench, Tribalwear);
            return p != null && wg != null && Tribalwear.PawnSatisfiesSkillRequirements(p) && !p.WorkTypeIsDisabled(wg.workType)
                && wg.Worker.MissingRequiredCapacity(p) == null;
        }

        private static Pawn Crafter => CanCraft(S.C2) ? S.C2 : CanCraft(S.C1) ? S.C1 : null;

        // Taps the check key with the needle in the middle of the arc (a Hit or a Great), or lets it run out.
        private static IEnumerable<int> AnswerRing(bool press, string label)
        {
            yield return K.WaitUntil(() => FpSkillCheck.State == FpRingState.Needle, "for the ring's needle (" + label + ")", 3600);
            if (!press)
            {
                yield return K.WaitUntil(() => FpSkillCheck.State != FpRingState.Needle, "for the needle to run out", 600);
                yield break;
            }
            float target = FpSkillCheck.ArcStartDeg + FpSkillCheck.ArcWidthDeg / 2f;
            float rate = 360f / FpTuning.Get.ringPeriod;
            while (FpSkillCheck.State == FpRingState.Needle)
            {
                float lead = Time.unscaledDeltaTime * rate - FpSettings.checkLatencyMs / 1000f * rate;
                if (FpSkillCheck.NeedleDeg + lead >= target)
                {
                    yield return K.Tap(Key.Space);
                    break;
                }
                yield return 1;
            }
            yield return K.WaitUntil(() => FpSkillCheck.State != FpRingState.Needle, "for the ring's result", 600);
            K.Info(label + ": outcome " + FpSkillCheck.LastOutcome);
        }

        private static IEnumerator<int> MpBoth()
        {
            const int Minutes5 = 5 * 3600;
            if (K.IsHost)
            {
                foreach (int w in HostWaitForClient()) yield return w;
                if (!FindKit(Find.CurrentMap, out string kitError))
                {
                    K.Check("mp6-both kit", false, kitError);
                }
                Pawn crafter = Crafter;
                Pawn mine = crafter == S.C1 ? S.C2 : S.C1;
                int mineId = mine.thingIDNumber;
                foreach (int w in HostWaitForClientCommand()) yield return w;
                K.Direct.SetControl(mine, true, FpSettings.Flags);
                yield return K.WaitUntil(() => FpState.ControllerOf(mine) == FpIds.Local, "for the host's control", 600);
                int end = K.Ticks + Minutes5;
                // The join point as soon as the client's colonist has a check pending (synced state, watched every frame:
                // the client lets that ring run out, about 1.7 s, so the save must come first).
                Pawn c = crafter;
                yield return K.WaitUntil(() => c != null && Entry(c) != null && Entry(c).pendingSeq != 0 || Players < 2,
                    "for the client's first check", 10800);
                bool joinPointDone = false;
                if (c != null && Entry(c) != null && Entry(c).pendingSeq != 0)
                {
                    joinPointDone = true;
                    K.Info("mp6-both: forcing a join point with the client's check pending: " + State(c));
                    var force = AccessTools.Method("Multiplayer.Client.Diag.FpSelfTestHost:ForceJoinPoint");
                    K.Check("mp6-both join point helper", force != null, force != null ? "forcing" : "FpSelfTestHost.ForceJoinPoint not found");
                    Game g0 = Current.Game;
                    force?.Invoke(null, null);
                    yield return K.WaitUntil(() => Current.Game != g0, "for the host's own reload", 7200);
                    foreach (int w in AfterReload()) yield return w;
                    FindKit(Find.CurrentMap, out _);
                    mine = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == mineId);
                    K.Check("mp6-both host's colonist still controlled after the join point", mine != null && FpState.ControllerOf(mine) == FpIds.Local,
                        mine == null ? "not found" : State(mine));
                }
                int bursts = 0;
                while (mine != null && K.Ticks < end && Players >= 2)
                {
                    // Walk a square, then (with a ranged weapon) draft and fire a burst at the pirate.
                    foreach (int w in HostSquare(mine, 90)) yield return w;
                    if (mine.equipment?.Primary != null && mine.equipment.Primary.def.IsRangedWeapon && S.Pirate != null && S.Pirate.Spawned && MP.InInterface)
                    {
                        if (!mine.Drafted)
                        {
                            mine.drafter.Drafted = true;   // synced; the host controls this colonist, so the guard lets it through
                            yield return K.WaitTicks(5);
                        }
                        FpCombat.FpFire(mine, new LocalTargetInfo(S.Pirate), true);   // synced call (IsoRim's [SyncMethod])
                        yield return K.WaitTicks(150);
                        FpCombat.FpStopFire(mine);
                        bursts++;
                        yield return K.WaitTicks(90);
                        K.Info("mp6-both burst " + bursts + ": " + State(mine));
                    }
                    yield return K.WaitTicks(60);
                }
                K.Check("mp6-both join point happened", joinPointDone, "no pending check seen on the client's colonist");
                if (mine != null)
                {
                    if (mine.Drafted && MP.InInterface)
                    {
                        mine.drafter.Drafted = false;
                    }
                    K.Direct.SetControl(mine, false, 0);
                }
                foreach (int w in HostWaitForClientDone()) yield return w;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                if (!FindKit(Find.CurrentMap, out string error) || Crafter == null)
                {
                    K.Check("mp6-both kit", false, error ?? "neither C2 nor C1 can make tribalwear");
                    yield return K.Done();
                    yield break;
                }
                Pawn p = Crafter;
                int pid = p.thingIDNumber;
                S.EnableWork(p, GiverFor(Bench, Tribalwear).workType);
                int start = K.Ticks;
                foreach (int w in EnterPlay(p)) yield return w;
                yield return K.WaitUntil(() => MP.InInterface, "for Multiplayer to take commands", 1800);
                FpBills.SendStartRecipe(p, Bench, Tribalwear, 1);
                yield return K.WaitUntil(() => p.CurJob?.def == JobDefOf.DoBill, "for the bill job", 1200);
                yield return K.WaitUntil(() => Entry(p) != null && Entry(p).pendingSeq != 0, "for the first check", 7200);
                int job0 = Entry(p).checkJob, seq0 = Entry(p).pendingSeq;
                K.Info("mp6-both first check: " + State(p));
                // The host forces a join point now: every machine reloads.
                Game g0 = Current.Game;
                yield return K.WaitUntil(() => Current.Game != g0, "for the join point's reload", 7200);
                foreach (int w in AfterReload()) yield return w;
                FindKit(Find.CurrentMap, out _);
                p = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == pid);
                yield return K.WaitUntil(() => FpPlay.Mode == FpMode.Playing && FpPlay.Confirmed, "to be playing again", 900);
                yield return K.WaitUntil(() => FpSkillCheck.State != FpRingState.Idle || Entry(p) == null || Entry(p).pendingSeq != seq0,
                    "for the ring to come back", 900);
                FpEntry e = Entry(p);
                K.Check("mp6-both same check after the join point", e != null && e.checkJob == job0 && e.pendingSeq == seq0 && FpSkillCheck.State != FpRingState.Idle,
                    "was job " + job0 + " seq " + seq0 + "; now " + State(p));
                if (FpSkillCheck.State != FpRingState.Idle)
                {
                    foreach (int w in AnswerRing(true, "mp6-both after the join point")) yield return w;
                }
                // The rest of the five minutes: answer each check (pressed, then run out, in turn); once the piece is done,
                // walk squares.
                int answered = 0;
                while (K.Ticks < start + Minutes5)
                {
                    if (p.CurJob?.def == JobDefOf.DoBill)
                    {
                        int until = K.Ticks + 1200;
                        yield return K.WaitUntil(() => Entry(p)?.pendingSeq != 0 || p.CurJob?.def != JobDefOf.DoBill || K.Ticks >= until, "for a check", 3600);
                        if (Entry(p)?.pendingSeq != 0 && FpSkillCheck.State != FpRingState.Result)
                        {
                            foreach (int w in AnswerRing(answered % 2 == 0, "mp6-both check " + answered)) yield return w;
                            answered++;
                        }
                    }
                    else
                    {
                        foreach (int w in ClientSquare(60)) yield return w;
                    }
                }
                K.Info("mp6-both answered " + answered + " more checks");
                foreach (int w in LeavePlay()) yield return w;
                yield return K.WaitTicks(60);
                yield return K.Done();
            }
            else
            {
                K.Check("mp6-both role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }

        // ---------------------------------------------------------------- mp6-soak

        // Multiplayer's "Desync tracing: slow (.NET)" setting, through reflection (IsoRim.dll can't reference Multiplayer.dll).
        private static bool SetSlowTracing()
        {
            try
            {
                Type mp = AccessTools.TypeByName("Multiplayer.Client.Multiplayer");
                object settings = AccessTools.Field(mp, "settings")?.GetValue(null);
                Type modeType = AccessTools.TypeByName("Multiplayer.Client.DesyncTracingMode");
                var field = settings == null ? null : AccessTools.Field(settings.GetType(), "desyncTracingMode");
                if (field == null || modeType == null)
                {
                    return false;
                }
                field.SetValue(settings, Enum.Parse(modeType, "Slow"));
                return field.GetValue(settings).ToString() == "Slow";
            }
            catch (Exception e)
            {
                K.Info("slow tracing: " + e.Message);
                return false;
            }
        }

        private static bool Desynced()
        {
            try
            {
                Type mp = AccessTools.TypeByName("Multiplayer.Client.Multiplayer");
                object session = AccessTools.Field(mp, "session")?.GetValue(null);
                return session != null && (bool)AccessTools.Field(session.GetType(), "desynced").GetValue(session);
            }
            catch (Exception e)
            {
                K.Info("desync flag: " + e.Message);
                return false;
            }
        }

        private static IEnumerator<int> MpSoak()
        {
            const int Soak = 25 * 3600;
            if (K.IsHost)
            {
                K.Check("mp6-soak slow tracing on (host)", SetSlowTracing(), "Multiplayer.settings.desyncTracingMode");
                foreach (int w in HostWaitForClient()) yield return w;
                foreach (int w in HostWaitForClientCommand()) yield return w;
                K.Direct.SetControl(S.C1, true, FpSettings.Flags);
                yield return K.WaitUntil(() => FpState.ControllerOf(S.C1) == FpIds.Local, "for the host's control of C1", 600);
                int end = K.Ticks + Soak;
                int round = 0;
                while (K.Ticks < end && Players >= 2 && !Desynced())
                {
                    foreach (int w in HostSquare(S.C1, 100 + 20 * (round % 3))) yield return w;
                    FpSync.SendFacing(S.C1, (byte)(round & 3));
                    yield return K.WaitTicks(120);
                    round++;
                }
                K.Check("mp6-soak no desync (host)", !Desynced(), round + " rounds, " + (K.Ticks - (end - Soak)) + " ticks");
                K.Direct.SetControl(S.C1, false, 0);
                foreach (int w in HostWaitForClientDone()) yield return w;
                yield return K.Done();
            }
            else if (K.IsClient)
            {
                K.Check("mp6-soak slow tracing on (client)", SetSlowTracing(), "Multiplayer.settings.desyncTracingMode");
                foreach (int w in EnterPlay(S.C2)) yield return w;
                int end = K.Ticks + Soak;
                int round = 0;
                while (K.Ticks < end && !Desynced() && FpPlay.Mode == FpMode.Playing)
                {
                    foreach (int w in ClientSquare(90 + 15 * (round % 4))) yield return w;
                    // Look round while standing (facing commands), and now and then draft and undraft.
                    for (int k = 0; k < 4; k++)
                    {
                        Turn(FpCamera.Yaw + 70f, 8f);
                        yield return 20;
                    }
                    if (round % 5 == 4)
                    {
                        yield return K.Tap(Key.R);
                        yield return K.WaitTicks(120);
                        yield return K.Tap(Key.R);
                    }
                    yield return K.WaitTicks(60);
                    round++;
                }
                K.Check("mp6-soak no desync (client)", !Desynced(), round + " rounds; " + Mode);
                K.Check("mp6-soak still playing", FpPlay.Mode == FpMode.Playing, Mode);
                foreach (int w in LeavePlay()) yield return w;
                yield return K.Done();
            }
            else
            {
                K.Check("mp6-soak role", false, "needs --mp (host and client)");
                yield return K.Done();
            }
        }
    }
}
