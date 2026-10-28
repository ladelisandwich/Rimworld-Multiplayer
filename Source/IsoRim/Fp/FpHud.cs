using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim.Fp
{
    // First-person play's HUD (fp-play-spec 14): a UIRoot_Play.UIRootOnGUI postfix that draws the crosshair, the action
    // hint, the status line, toasts, the controls line and "click to resume" on Repaint, scaled by FpSettings.hudScale,
    // and gives the skill check (E) and the reticle (G) their turn. Windows draw over it (GUI.Window contents run after
    // the OnGUI pass). Local only; reads game state, never writes it.
    public static class FpHud
    {
        internal static readonly Color WarnColor = new Color(1f, 0.82f, 0.45f);
        private static readonly Color ActionColor = new Color32(0x7C, 0xD6, 0x7C, 0xFF);
        private static readonly Color TooFarColor = new Color32(0x9A, 0x9A, 0x9A, 0xFF);
        private static readonly Color InvalidColor = new Color32(0xE0, 0x5A, 0x5A, 0xFF);
        private static readonly Color OutlineColor = new Color(0f, 0f, 0f, 0.6f);
        private static readonly Color PanelColor = new Color(0f, 0f, 0f, 0.4f);
        private static readonly Color TrackColor = new Color(1f, 1f, 1f, 0.25f);
        private static readonly Color AccentColor = new Color(0.55f, 0.78f, 0.95f);   // IsoRimMod's accent

        private const float StatusFromBottom = 112f;   // spec 14: the status line's top, UI pixels from the bottom
        private const double CheatSheetSeconds = 8.0;
        private const double ReportRefresh = 0.25;      // the job report is rebuilt at most 4 times a second
        private const int MaxToasts = 4;
        private const float InspectPaneClear = 450f;    // the inspect pane (432 wide) stays clear of the centred lines

        private sealed class ToastEntry
        {
            public string text;
            public Color color;
            public double start;
            public double until;
        }

        private static readonly List<ToastEntry> toasts = new List<ToastEntry>();
        private static double cheatSheetUntil = -1;
        private static Pawn statusPawn;
        private static double statusAt = -1;
        private static string statusLine;

        private static double Now
        {
            get { return Time.realtimeSinceStartupAsDouble; }
        }

        // A short line above the status line (main thread). The same text again restarts instead of stacking. Drawn
        // whether or not play is on, so a "couldn't take control" toast shows after play went back to the ride-along.
        public static void Toast(string text, Color color, float seconds = 1.5f)
        {
            if (text.NullOrEmpty())
            {
                return;
            }
            double now = Now;
            toasts.RemoveAll(t => t.text == text);
            toasts.Add(new ToastEntry { text = text, color = color, start = now, until = now + Mathf.Max(0.3f, seconds) });
            while (toasts.Count > MaxToasts)
            {
                toasts.RemoveAt(0);
            }
        }

        // From FpPlay on the session's first entry.
        internal static void ShowCheatSheet()
        {
            cheatSheetUntil = Now + CheatSheetSeconds;
        }

        internal static void Install(Harmony h)
        {
            FpHooks.Patch(h, "first-person HUD (UIRoot_Play.UIRootOnGUI)",
                AccessTools.DeclaredMethod(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)),
                postfix: new HarmonyMethod(typeof(FpHud), nameof(UIRootOnGUI_Postfix)));
        }

        public static void UIRootOnGUI_Postfix()
        {
            GameFont font = Text.Font;
            TextAnchor anchor = Text.Anchor;
            Color color = GUI.color;
            try
            {
                OnGUI();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: drawing the HUD failed: " + e, 93652401);
            }
            finally
            {
                Text.Font = font;
                Text.Anchor = anchor;
                GUI.color = color;
            }
        }

        private static void OnGUI()
        {
            Event e = Event.current;
            if (e == null || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            bool playing = FpPlay.Mode == FpMode.Playing;
            if (playing)
            {
                try
                {
                    FpSkillCheck.OnGUI();   // every event type (E): its ring is an immediate window, its keys are polled
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[IsoRim] First person: drawing the skill check failed: " + ex, 93652402);
                }
            }
            if (e.type != EventType.Repaint || Find.UIRoot != null && Find.UIRoot.screenshotMode.Active)
            {
                return;
            }
            float s = Mathf.Clamp(FpSettings.hudScale, 0.75f, 1.5f);
            DrawToasts(s);
            Pawn p = FpPlay.Pawn;
            if (!playing || p == null || !RimWorld.Planet.WorldRendererUtility.DrawingMap)
            {
                return;   // play stays on over the world map (spec 6.3), but its crosshair, hint and status belong to the map
            }
            FpHits.Hit("HUD drawn");
            Vector2 c = new Vector2(UI.screenWidth / 2f, UI.screenHeight / 2f);
            if (FpSkillCheck.State == FpRingState.Idle)
            {
                DrawCrosshair(c, s, CrosshairColor());
            }
            if (p.Drafted)
            {
                try
                {
                    FpCombat.ReticleOnGUI(c);
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[IsoRim] First person: drawing the reticle failed: " + ex, 93652403);
                }
            }
            if (FpSettings.hints && FpSkillCheck.State == FpRingState.Idle)
            {
                DrawHint(c, s);   // not under a skill-check ring: its arc runs where the hint line would be
            }
            DrawStatus(p, s);
            DrawCheatSheet(s);
            if (FpInput.ResumeClickNeeded)
            {
                DrawClickToResume(c, s);
            }
        }

        // ---------------------------------------------------------------- crosshair and hint

        private static Color CrosshairColor()
        {
            switch (FpActions.HintKind)
            {
                case FpHintKind.Action:
                    return ActionColor;
                case FpHintKind.TooFar:
                    return TooFarColor;
                case FpHintKind.Invalid:
                    return InvalidColor;
                default:
                    return Color.white;
            }
        }

        // A 2x2 dot and four 6 px ticks 5 px out from the centre, 2 px thick, each with a 1 px dark outline.
        private static void DrawCrosshair(Vector2 c, float s, Color color)
        {
            float t = Mathf.Max(1f, Mathf.Round(2f * s));
            float len = Mathf.Round(6f * s);
            float gap = Mathf.Round(5f * s);
            float cx = Mathf.Round(c.x);
            float cy = Mathf.Round(c.y);
            float h = t / 2f;
            Rect[] parts =
            {
                new Rect(cx - h, cy - h, t, t),
                new Rect(cx - h, cy - gap - len, t, len),
                new Rect(cx - h, cy + gap, t, len),
                new Rect(cx - gap - len, cy - h, len, t),
                new Rect(cx + gap, cy - h, len, t),
            };
            for (int i = 0; i < parts.Length; i++)
            {
                Widgets.DrawBoxSolid(parts[i].ExpandedBy(1f), OutlineColor);
            }
            for (int i = 0; i < parts.Length; i++)
            {
                Widgets.DrawBoxSolid(parts[i], color);
            }
        }

        // 18 px under the crosshair: "[LMB] <action>   [RMB] more", or "Too far — [RMB] more".
        private static void DrawHint(Vector2 c, float s)
        {
            string label = FpActions.Hint;
            string text;
            Color color;
            switch (FpActions.HintKind)
            {
                case FpHintKind.Action:
                    if (label.NullOrEmpty())
                    {
                        return;
                    }
                    text = "R3D_Fp_Hud_Action".Translate(label);
                    color = Color.white;
                    break;
                case FpHintKind.TooFar:
                    text = "R3D_Fp_Hud_TooFar".Translate();
                    color = TooFarColor;
                    break;
                case FpHintKind.Invalid:
                    if (label.NullOrEmpty())
                    {
                        return;
                    }
                    text = "R3D_Fp_Hud_Invalid".Translate(label);
                    color = InvalidColor;
                    break;
                default:
                    return;
            }
            Text.Font = FontFor(s);
            float lh = Text.LineHeight;
            float w = Mathf.Min(UI.screenWidth - 40f, 900f);
            Rect r = new Rect(c.x - w / 2f, c.y + 18f * s, w, lh);
            ShadowLabel(r, Fit(text, w), color, TextAnchor.UpperCenter);
        }

        // ---------------------------------------------------------------- status line

        private static void DrawStatus(Pawn p, float s)
        {
            string line = StatusLine(p);
            Text.Font = FontFor(s);
            float lh = Text.LineHeight;
            float maxW = Mathf.Clamp(UI.screenWidth - 2f * InspectPaneClear, 300f, 900f);
            float w = Mathf.Min(Text.CalcSize(line).x + 4f, maxW);
            float y = StatusTop(s);
            Rect r = new Rect(UI.screenWidth / 2f - w / 2f, y, w, lh);
            Widgets.DrawBoxSolid(r.ExpandedBy(8f * s, 2f * s), PanelColor);
            ShadowLabel(r, Fit(line, w), Color.white, TextAnchor.MiddleCenter);
            float fill = FpInput.InterruptFill;
            if (fill >= 0f)
            {
                DrawFillRing(new Vector2(r.x - 8f * s - 10f * s, r.center.y), 7f * s, fill);
            }
        }

        // The status line's top: spec 14's 112 px from the bottom, raised above the game's gizmo grid while one is drawn
        // (the selected colonist's commands, right of the inspect pane, under the screen's centre at 1280 x 720): at 112 px
        // the line, the toasts' base and the skill-check bar under it (FpSkillCheck.BarY) sat on the grid's first row, whose
        // top is 115 px from the bottom. GizmoGridDrawer.HeightDrawnRecently is this frame's grid, drawn before this postfix.
        internal static float StatusTop(float s)
        {
            float top = UI.screenHeight - StatusFromBottom * s;
            float grid = GizmoGridDrawer.HeightDrawnRecently;
            if (grid > 0f)
            {
                // The line and the bar under it as FpSkillCheck.BarY places it, its ticks, and a gap above the grid.
                float below = Mathf.Max(20f * s, Text.LineHeightOf(FontFor(s)) + 10f * s) + 12f * s;
                top = Mathf.Min(top, UI.screenHeight - grid - below);
            }
            return top;
        }

        // "<Name> — <job report>", then one of taking control / autopilot / drafted, then need warnings. Rebuilt at most 4
        // times a second (the job report and translations aren't free).
        private static string StatusLine(Pawn p)
        {
            double now = Now;
            if (p == statusPawn && statusLine != null && now - statusAt < ReportRefresh)
            {
                return statusLine;
            }
            statusPawn = p;
            statusAt = now;
            string report = null;
            try
            {
                report = p.jobs?.curDriver?.GetReport();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the job report for the HUD failed: " + e, 93652404);
            }
            string line = p.LabelShortCap;
            if (!report.NullOrEmpty())
            {
                line += " — " + report.CapitalizeFirst();
            }
            string reason;
            if (!FpPlay.Confirmed)
            {
                line += Part("R3D_Fp_Hud_TakingControl".Translate(), WarnColor);
            }
            else if ((reason = FpState.AutopilotReason(p)) != null)
            {
                line += Part("R3D_Fp_Hud_Autopilot".Translate(reason, KeyLabel(R3DKeyDefOf.R3D_FirstPerson)), WarnColor);
            }
            else if (p.Drafted)
            {
                line += Part("R3D_Fp_Hud_Drafted".Translate(), AccentColor);
            }
            if (p.needs?.food != null && p.needs.food.CurCategory >= HungerCategory.Hungry)
            {
                line += Part("R3D_Fp_Hud_Hungry".Translate(), WarnColor);
            }
            if (p.needs?.rest != null && p.needs.rest.CurCategory >= RestCategory.Tired)
            {
                line += Part("R3D_Fp_Hud_Tired".Translate(), WarnColor);
            }
            if (p.health?.hediffSet != null && p.health.hediffSet.BleedRateTotal > 0.01f)
            {
                line += Part("R3D_Fp_Hud_Bleeding".Translate(), InvalidColor);
            }
            statusLine = line;
            return line;
        }

        private static string Part(TaggedString text, Color color)
        {
            return "  ·  " + text.ToString().Colorize(color);
        }

        // The wait before a held movement key interrupts a job: a small ring filling clockwise from 12 o'clock.
        private static void DrawFillRing(Vector2 c, float radius, float fill)
        {
            const int n = 20;
            GUI.color = Color.white;   // Widgets.DrawLine tints on top of GUI.color under UnityStubs (CLAUDE.md)
            for (int i = 0; i < n; i++)
            {
                float a0 = i * 360f / n;
                float a1 = (i + 1) * 360f / n;
                Color col = (i + 0.5f) / n <= fill ? WarnColor : TrackColor;
                Widgets.DrawLine(c + Dir(a0) * radius, c + Dir(a1) * radius, col, 1f);
            }
        }

        // Screen direction for an angle clockwise from 12 o'clock (UI y grows down).
        private static Vector2 Dir(float deg)
        {
            float r = deg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(r), -Mathf.Cos(r));
        }

        // ---------------------------------------------------------------- toasts, controls, click to resume

        private static void DrawToasts(float s)
        {
            if (toasts.Count == 0)
            {
                return;
            }
            double now = Now;
            toasts.RemoveAll(t => now >= t.until);
            Text.Font = FontFor(s);
            float lh = Text.LineHeight;
            float y = StatusTop(s) - 6f * s;
            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                ToastEntry t = toasts[i];
                float a = Mathf.Clamp01((float)(t.until - now) / 0.3f) * Mathf.Clamp01((float)(now - t.start) / 0.1f);
                y -= lh + 4f * s;
                Color col = t.color;
                col.a *= a;
                ShadowLabel(new Rect(0f, y, UI.screenWidth, lh), t.text, col, TextAnchor.MiddleCenter);
            }
        }

        // Top left, for 8 s on the session's first entry (FpSettings.cheatSheet).
        private static void DrawCheatSheet(float s)
        {
            double now = Now;
            if (now >= cheatSheetUntil)
            {
                return;
            }
            float a = Mathf.Clamp01((float)(cheatSheetUntil - now));
            string text = "R3D_Fp_Hud_CheatSheet".Translate(MoveKeysLabel(), KeyLabel(KeyBindingDefOf.Command_ColonistDraft),
                KeyLabel(FpKeyDefOf.R3D_FpSkillCheck), KeyLabel(FpKeyDefOf.R3D_FpFreeCursor), KeyLabel(R3DKeyDefOf.R3D_FirstPerson));
            Text.Font = FontFor(s);
            float w = Mathf.Min(Text.CalcSize(text).x + 4f, UI.screenWidth - 40f);
            float h = Text.CalcHeight(text, w);
            Rect r = new Rect(16f * s, 16f * s, w, h);
            Color panel = PanelColor;
            panel.a *= a;
            Widgets.DrawBoxSolid(r.ExpandedBy(8f * s, 4f * s), panel);
            ShadowLabel(r, text, new Color(1f, 1f, 1f, a), TextAnchor.UpperLeft);
        }

        private static void DrawClickToResume(Vector2 c, float s)
        {
            Text.Font = s >= 1.1f ? GameFont.Medium : GameFont.Small;
            string text = "R3D_Fp_ClickToResume".Translate();
            Vector2 size = Text.CalcSize(text);
            Rect r = new Rect(c.x - size.x / 2f, c.y - 60f * s - size.y, size.x, size.y);
            Widgets.DrawBoxSolid(r.ExpandedBy(12f * s, 6f * s), PanelColor);
            ShadowLabel(r, text, Color.white, TextAnchor.MiddleCenter);
        }

        // ---------------------------------------------------------------- helpers

        // The game's fonts come in three sizes: the HUD scale picks one rather than scaling the GUI matrix.
        private static GameFont FontFor(float s)
        {
            if (s < 0.9f)
            {
                return GameFont.Tiny;
            }
            return s >= 1.3f ? GameFont.Medium : GameFont.Small;
        }

        private static void ShadowLabel(Rect r, string text, Color color, TextAnchor anchor)
        {
            Text.Anchor = anchor;
            GUI.color = new Color(0f, 0f, 0f, 0.8f * color.a);
            Widgets.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text.StripTags());
            GUI.color = color;
            Widgets.Label(r, text);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // The text, or its plain version cut to the width (cutting rich text could split a colour tag).
        private static string Fit(string text, float width)
        {
            if (Text.CalcSize(text).x <= width)
            {
                return text;
            }
            return text.StripTags().Truncate(width);
        }

        // A key binding's main key as the keyboard options show it ("?" if unbound or not loaded).
        internal static string KeyLabel(KeyBindingDef k)
        {
            return k != null ? k.MainKeyLabel : "?";
        }

        // "WASD" (or whatever the four map movement keys are bound to).
        internal static string MoveKeysLabel()
        {
            return KeyLabel(KeyBindingDefOf.MapDolly_Up) + KeyLabel(KeyBindingDefOf.MapDolly_Left)
                + KeyLabel(KeyBindingDefOf.MapDolly_Down) + KeyLabel(KeyBindingDefOf.MapDolly_Right);
        }
    }
}
