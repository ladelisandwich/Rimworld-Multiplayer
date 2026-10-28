using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim.Fp
{
    // The only door from the IsoRim subtree into first-person play: each "// Port: first-person play" line in IsoRim's own
    // files calls one member here (fp-play-spec 4.1), and every member only forwards to the module that owns the work.
    // Keeping the subtree edits to one-line calls keeps `git subtree pull` simple.
    //
    // The forwarders called from IsoRim's Harmony hooks (pointer, picking, brackets) catch their module's exceptions
    // and log them once: an exception escaping one of those would break the game method IsoRim patched (UI.MouseCell,
    // GenUI.ThingsUnderMouse, the selection brackets), not just first-person play.
    public static class FpHooks
    {
        private static readonly List<string> missing = new List<string>();
        private static readonly HashSet<string> warned = new HashSet<string>();

        // Hooks that could not be installed ("what (reason)"), for the dev action and the self-test.
        public static int MissingCount
        {
            get { return missing.Count; }
        }

        public static IEnumerable<string> Missing
        {
            get { return missing; }
        }

        // Port 1 (Patches.ApplyAll, with IsoRim's own Harmony instance): every first-person hook, in a fixed order. Each
        // module installs its own patches through Patch below; one module failing leaves the others in.
        public static void InstallAll(Harmony h)
        {
            Install("input", () => FpInput.Install(h));
            Install("HUD", () => FpHud.Install(h));
            Install("control job", () => FpControlJob.Install(h));
            Install("skill checks", () => FpWork.Install(h));
            Install("work levers", () => FpLevers.Install(h));
            Install("self-test", () => FpTestKit.Install(h));
            if (missing.Count > 0)
            {
                // Without dev mode too, like IsoRim's own "Hooks missing" line: a game update that drops one shows in any log.
                Log.Warning("[IsoRim] First-person hooks missing (" + missing.Count + "): " + string.Join("; ", missing));
            }
        }

        private static void Install(string module, Action install)
        {
            try
            {
                install();
            }
            catch (Exception e)
            {
                missing.Add(module + " (" + e.GetType().Name + ")");
                Log.Error("[IsoRim] First person: installing the " + module + " hooks failed: " + e);
            }
        }

        // Port 2 (Patches.RootUpdate_Postfix, every frame in every program state).
        public static void OnRootUpdate()
        {
            FpHits.Hit("Port OnRootUpdate");
            try
            {
                FpInput.OnRootUpdate();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the frame update failed: " + e, 93652101);
            }
        }

        // Port 3 (Patches.UIToMapPosition_Prefix): while the cursor is captured, the game's pointer is the crosshair.
        public static bool PointerMapPos(Vector2 ui, ref Vector3 result)
        {
            try
            {
                return FpPick.PointerMapPos(ui, ref result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the crosshair's map position failed: " + e, 93652102);
                return false;
            }
        }

        // Port 4 (Patches.ThingsUnderMouse_Postfix): the thing under the crosshair first. True when it handled the list.
        public static bool ThingsUnderCrosshair(ref List<Thing> result, TargetingParameters parms)
        {
            try
            {
                return FpPick.ThingsUnderCrosshair(ref result, parms);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the things under the crosshair failed: " + e, 93652103);
                return false;
            }
        }

        // Port 5 (Patches.DrawSelectionBracketFor_Prefix): no bracket drawn round the played pawn's own feet.
        public static bool HidesBracket(object thing)
        {
            try
            {
                return FpPlay.HidesBracket(thing);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the bracket check failed: " + e, 93652104);
                return false;
            }
        }

        // Port 6 (View3D.HandleKeys, before HandleLook): true while playing, when the mouse look is first-person play's
        // and IsoRim's button look, Q/E and tilt keys are skipped.
        public static bool LookFrame()
        {
            FpHits.Hit("Port LookFrame");
            try
            {
                return FpInput.LookFrame();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the look update failed: " + e, 93652105);
                return false;
            }
        }

        // Port 7 (View3D.RightLookOn): the right button is the float menu while playing, never IsoRim's right-drag look.
        public static bool PlayActive
        {
            get { return FpPlay.Mode == FpMode.Playing; }
        }

        // Port 8 (FirstPerson.Toggle): true when first-person play handled the key and IsoRim's toggle must not run.
        public static bool Toggle()
        {
            FpHits.Hit("Port Toggle");
            try
            {
                return FpPlay.TryToggle();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the first-person key failed: " + e, 93652106);
                return false;
            }
        }

        // Port 9 (FirstPerson.Leave, after the camera let go): every IsoRim way out of first person.
        public static void CameraLeft(Pawn was)
        {
            FpHits.Hit("Port CameraLeft");
            try
            {
                FpPlay.CameraLeft(was);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: leaving play with the camera failed: " + e, 93652107);
            }
        }

        // Port 10 (FirstPerson.Reset): a new or loaded game, or the program state not Playing. May run off the main
        // thread (the Game constructor), so the module only stores plain values there.
        public static void CameraReset(Pawn was)
        {
            FpHits.Hit("Port CameraReset");
            try
            {
                FpPlay.CameraReset(was);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: resetting play with the camera failed: " + e, 93652108);
            }
        }

        // Ports 11-13 (FirstPerson.Yaw, Pitch, Eye): while playing, the camera turns with first-person play's own yaw and
        // pitch, not IsoRim's eased walking heading.
        public static bool Steering
        {
            get { return FpPlay.Mode == FpMode.Playing && FirstPerson.On; }
        }

        public static float Yaw
        {
            get { return FpCamera.Yaw; }
        }

        public static float Pitch
        {
            get { return FpCamera.Pitch; }
        }

        public static Vector3 EyeOffset
        {
            get { return FpCamera.EyeOffset; }
        }

        // Port 14 (IsoRimSettings.ExposeData): first-person play's settings live in IsoRim's settings file.
        public static void ExposeSettings()
        {
            FpSettings.Expose();
        }

        // Port 15 (IsoRimMod's View tab, after the eye height slider).
        public static void DrawSettings(Listing_Standard l)
        {
            FpSettings.DrawSection(l);
        }

        // Port 16 (MpView.PointerMapPoint, port-owned): while playing with the cursor captured, the point sent to the other
        // players as this player's cursor is the crosshair's.
        public static bool PointerMapPoint(out Vector3 p)
        {
            try
            {
                return FpPick.PointerMapPoint(out p);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the crosshair's point for the other players failed: " + e, 93652109);
                p = Vector3.zero;
                return false;
            }
        }

        // Applies one first-person patch. The target is always patched through its declaring type: Harmony 3 keys its patch
        // records by MethodBase, and a method looked up through a subclass gets a second record and a second detour on
        // the same code, of which only the last runs (CLAUDE.md, Facts). A missing target or a failed patch logs one
        // warning (without dev mode too) and returns false, so only that feature is off.
        internal static bool Patch(Harmony h, string what, MethodBase target,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null)
        {
            if (target == null)
            {
                MarkMissing(what, "not found");
                return false;
            }
            try
            {
                MethodBase declared = Declared(target);
                h.Patch(declared, prefix, postfix, null, finalizer);
                ModLog.Note("[IsoRim] First-person hook: " + what + " on " + declared.DeclaringType?.Name + "." + declared.Name);
                return true;
            }
            catch (Exception e)
            {
                MarkMissing(what, e.Message);
                return false;
            }
        }

        // The same method as seen from its declaring type (a no-op when it already is).
        private static MethodBase Declared(MethodBase m)
        {
            Type t = m.DeclaringType;
            if (t == null || m.ReflectedType == t)
            {
                return m;
            }
            return MethodBase.GetMethodFromHandle(m.MethodHandle, t.TypeHandle) ?? m;
        }

        private static void MarkMissing(string what, string reason)
        {
            missing.Add(what + " (" + reason + ")");
            if (warned.Add(what))
            {
                Log.Warning("[IsoRim] First-person hook missing: " + what + " (" + reason + ")");
            }
        }

        [DebugAction("IsoRim", "FP: patch hit counts", allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        public static void LogHitCounts()
        {
            string m = missing.Count == 0 ? "none" : string.Join("; ", missing);
            Log.Message("[IsoRim] First-person patch hit counts (missing hooks: " + m + "):\n" + FpHits.Dump());
        }

        [DebugAction("IsoRim", "FP: reset hit counts", allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        public static void ResetHitCounts()
        {
            FpHits.Reset();
            Log.Message("[IsoRim] First-person patch hit counts reset.");
        }
    }

    // How often each first-person hook ran, for "FP: patch hit counts" (dev mode) and the self-test: a hook that never
    // fires (inlined target, dropped by a game update) shows as 0 or missing. Locked: the Game constructor (Port 10) can
    // run on a loading thread. Never read by simulation code.
    public static class FpHits
    {
        private static readonly Dictionary<string, int> counts = new Dictionary<string, int>();

        public static void Hit(string name)
        {
            lock (counts)
            {
                counts.TryGetValue(name, out int n);
                counts[name] = n + 1;
            }
        }

        public static int Count(string name)
        {
            lock (counts)
            {
                counts.TryGetValue(name, out int n);
                return n;
            }
        }

        public static string Dump()
        {
            lock (counts)
            {
                if (counts.Count == 0)
                {
                    return "  (no hits)";
                }
                StringBuilder b = new StringBuilder();
                foreach (KeyValuePair<string, int> kv in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    b.Append("  ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
                }
                return b.ToString().TrimEnd('\n');
            }
        }

        public static void Reset()
        {
            lock (counts)
            {
                counts.Clear();
            }
        }
    }
}
