using UnityEngine;
using Verse;

namespace IsoRim.Fp
{
    // First-person play's per-player options (fp-play-spec 13.1). Saved in IsoRim's settings file (Port 14) and shown on
    // IsoRim's View tab under the first-person camera options (Port 15). Never synced, and never read by simulation code:
    // the two that change what the simulation does for this player (autoNeeds, skillChecks) reach it only as the flags
    // of the synced FpSetControl command (Flags below).
    // IsoRim's own invertY, fpFov and fpEyeHeight are reused, not copied.
    public static class FpSettings
    {
        // M1
        public static bool play = true;                 // F5 on an own colonist takes control (off: IsoRim's ride-along view, as before)
        public static float sensitivity = 0.07f;        // degrees of look per raw mouse count
        public static bool hints = true;                // the action hint under the crosshair
        public static bool cheatSheet = true;           // the controls line once per session
        public static bool interactAnyDistance = false; // the left button works beyond arm's reach
        public static int moveInterruptMs = 300;        // how long a movement key is held before it interrupts a job
        public static bool resume = true;               // take control again after a Multiplayer reload (join points, resyncs)
        public static float hudScale = 1f;

        // M2-M6: saved from the start so a later milestone needs no settings change; shown once their feature lands.
        public static bool autoNeeds = true;            // M2: autopilot for urgent hunger, exhaustion, medical rest
        public static bool skillChecks = true;          // M3
        public static int checkLatencyMs = 16;          // M3: subtracted from a press's time
        public static bool checkSounds = true;          // M3
        public static bool colourBlind = false;         // M3: dashed arc, white great part
        public static bool showHitChance = true;        // M5
        public static bool eyeLead = true;              // M6
        public static float headBob = 0f;               // M6, 0..1
        public static bool friendTags = true;           // M6

        // The FpFlags this player's control asks for (FpSetControl's flags argument).
        public static byte Flags
        {
            get { return (byte)((autoNeeds ? FpFlags.AutoNeeds : 0) | (skillChecks ? FpFlags.Checks : 0)); }
        }

        // Called from IsoRimSettings.ExposeData (Port 14), so loading and saving follow IsoRim's own file.
        public static void Expose()
        {
            Scribe_Values.Look(ref play, "fpPlay", true);
            Scribe_Values.Look(ref sensitivity, "fpSensitivity", 0.07f);
            Scribe_Values.Look(ref hints, "fpHints", true);
            Scribe_Values.Look(ref cheatSheet, "fpCheatSheet", true);
            Scribe_Values.Look(ref interactAnyDistance, "fpInteractAnyDistance", false);
            Scribe_Values.Look(ref moveInterruptMs, "fpMoveInterruptMs", 300);
            Scribe_Values.Look(ref resume, "fpResume", true);
            Scribe_Values.Look(ref hudScale, "fpHudScale", 1f);
            Scribe_Values.Look(ref autoNeeds, "fpAutoNeeds", true);
            Scribe_Values.Look(ref skillChecks, "fpSkillChecks", true);
            Scribe_Values.Look(ref checkLatencyMs, "fpCheckLatencyMs", 16);
            Scribe_Values.Look(ref checkSounds, "fpCheckSounds", true);
            Scribe_Values.Look(ref colourBlind, "fpColourBlind", false);
            Scribe_Values.Look(ref showHitChance, "fpShowHitChance", true);
            Scribe_Values.Look(ref eyeLead, "fpEyeLead", true);
            Scribe_Values.Look(ref headBob, "fpHeadBob", 0f);
            Scribe_Values.Look(ref friendTags, "fpFriendTags", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Clamp();
            }
        }

        // A hand-edited or old file can't push a value out of its slider's range.
        private static void Clamp()
        {
            sensitivity = Mathf.Clamp(sensitivity, 0.01f, 0.3f);
            moveInterruptMs = Mathf.Clamp(moveInterruptMs, 0, 1000);
            hudScale = Mathf.Clamp(hudScale, 0.75f, 1.5f);
            checkLatencyMs = Mathf.Clamp(checkLatencyMs, 0, 100);
            headBob = Mathf.Clamp01(headBob);
        }

        // Every option back to its default (IsoRim's "Reset all" only knows its own fields).
        public static void ResetAll()
        {
            play = true;
            sensitivity = 0.07f;
            hints = true;
            cheatSheet = true;
            interactAnyDistance = false;
            moveInterruptMs = 300;
            resume = true;
            hudScale = 1f;
            autoNeeds = true;
            skillChecks = true;
            checkLatencyMs = 16;
            checkSounds = true;
            colourBlind = false;
            showHitChance = true;
            eyeLead = true;
            headBob = 0f;
            friendTags = true;
        }

        private static readonly Color Accent = new Color(0.55f, 0.78f, 0.95f);   // IsoRimMod's accent
        private static readonly Color Rule = new Color(1f, 1f, 1f, 0.18f);

        // Drawn inside IsoRim's camera section, right after the first-person eye height (Port 15): a small heading of its
        // own, the options indented under it, and a rule after them so the camera options that follow don't read as part
        // of it. Only the options of milestones that have landed are shown.
        public static void DrawSection(Listing_Standard l)
        {
            if (l == null)
            {
                return;
            }
            l.Gap(6f);
            Rect head = l.GetRect(24f);
            GUI.color = Accent;
            Widgets.Label(head, "R3D_Fp_Settings_Section".Translate());
            GUI.color = Color.white;
            TooltipHandler.TipRegion(head, "R3D_Fp_Settings_SectionTip".Translate());
            l.Indent(12f);
            l.CheckboxLabeled("R3D_Fp_Settings_Play".Translate(), ref play, "R3D_Fp_Settings_PlayTip".Translate());
            if (play)
            {
                sensitivity = Slider(l, "R3D_Fp_Settings_Sensitivity".Translate(), sensitivity, 0.01f, 0.3f,
                    "R3D_Fp_Settings_DegPerCount".Translate(sensitivity.ToString("0.00")), "R3D_Fp_Settings_SensitivityTip".Translate());
                l.CheckboxLabeled("R3D_Fp_Settings_Hints".Translate(), ref hints, "R3D_Fp_Settings_HintsTip".Translate());
                l.CheckboxLabeled("R3D_Fp_Settings_CheatSheet".Translate(), ref cheatSheet, "R3D_Fp_Settings_CheatSheetTip".Translate());
                l.CheckboxLabeled("R3D_Fp_Settings_AnyDistance".Translate(), ref interactAnyDistance, "R3D_Fp_Settings_AnyDistanceTip".Translate());
                moveInterruptMs = Mathf.RoundToInt(Slider(l, "R3D_Fp_Settings_MoveInterrupt".Translate(), moveInterruptMs, 0f, 1000f,
                    "R3D_Fp_Settings_Ms".Translate(moveInterruptMs.ToString()), "R3D_Fp_Settings_MoveInterruptTip".Translate()) / 10f) * 10;
                l.CheckboxLabeled("R3D_Fp_Settings_Resume".Translate(), ref resume, "R3D_Fp_Settings_ResumeTip".Translate());
                hudScale = Slider(l, "R3D_Fp_Settings_HudScale".Translate(), hudScale, 0.75f, 1.5f, hudScale.ToStringPercent(),
                    "R3D_Fp_Settings_HudScaleTip".Translate());
                if (Widgets.ButtonText(l.GetRect(26f).LeftPart(0.5f), "R3D_Fp_Settings_Reset".Translate()))
                {
                    ResetAll();
                }
            }
            l.Outdent(12f);
            Rect rule = l.GetRect(6f);
            GUI.color = Rule;
            Widgets.DrawLineHorizontal(rule.x, rule.y + 3f, rule.width);
            GUI.color = Color.white;
        }

        // IsoRimMod's slider row: the label on the left, the value on the right in the accent colour, the slider under it.
        private static float Slider(Listing_Standard l, string label, float value, float min, float max, string shown, string tip)
        {
            Rect r = l.GetRect(22f);
            Widgets.Label(r.LeftPart(0.72f), label);
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = Accent;
            Widgets.Label(r.RightPart(0.28f), shown);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            if (tip != null)
            {
                TooltipHandler.TipRegion(r, tip);
            }
            return l.Slider(value, min, max);
        }
    }
}
