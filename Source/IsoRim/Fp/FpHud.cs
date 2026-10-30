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
        private const float ControlsClear = 210f;       // and the game's time controls (GlobalControls, 200 wide) at the bottom right
        private const string Separator = "  ·  ";

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
        private static readonly List<string> statusParts = new List<string>();

        // ---------------------------------------------------------------- what was drawn last (the self-test reads these)

        internal struct StatusLineDrawn
        {
            public string text;   // plain, as laid out (before any cut)
            public Rect rect;
        }

        internal struct FriendTagDrawn
        {
            public Pawn pawn;
            public string name;
            public Vector2 at;     // UI pixels: the tag's foot, over the pawn's head
            public bool checking;  // a skill check is pending on the pawn
        }

        internal static readonly List<StatusLineDrawn> LastStatusLines = new List<StatusLineDrawn>();
        internal static bool LastStatusCut;             // a status part was wider than its line and was cut
        internal static int LastStatusFrame = -1;
        internal static string LastToast;               // the last toast's text (the self-test's failure details)
        internal static Rect LastCheatSheetRect;        // zero while it doesn't show
        internal static int LastCheatSheetFrame = -1;
        internal static Vector2 LastHudPointer;         // UI.MousePositionOnUI during the HUD's repaint while playing
        internal static int LastHudPointerFrame = -1;
        internal static readonly List<FriendTagDrawn> LastFriendTags = new List<FriendTagDrawn>();
        internal static int LastFriendTagsFrame = -1;
        internal static float LastFlashAlpha;

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
            LastToast = text;
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
            if (playing)
            {
                // The pointer the interface sees during this pass (the self-test checks the pin, FpInput.ImguiPass_Prefix).
                LastHudPointer = UI.MousePositionOnUI;
                LastHudPointerFrame = Time.frameCount;
            }
            float s = Mathf.Clamp(FpSettings.hudScale, 0.75f, 1.5f);
            bool map = RimWorld.Planet.WorldRendererUtility.DrawingMap;
            if (map)
            {
                try
                {
                    DrawFriendTags(s);   // under the rest of the HUD; every player in a session, playing or not
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[IsoRim] First person: drawing the other players' tags failed: " + ex, 93652405);
                }
            }
            Pawn p = FpPlay.Pawn;
            float toastBase = StatusTop(s);
            if (playing && p != null && map)
            {
                // Play stays on over the world map (spec 6.3), but its crosshair, hint and status belong to the map.
                FpHits.Hit("HUD drawn");
                Vector2 c = new Vector2(UI.screenWidth / 2f, UI.screenHeight / 2f);
                DrawDamageFlash(p, s);
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
                toastBase = DrawStatus(p, s);
                try
                {
                    DrawNeedsStrip(p, s);
                }
                catch (Exception ex)
                {
                    LastNeedsRect = default(Rect);
                    Log.ErrorOnce("[IsoRim] First person: drawing the needs strip failed: " + ex, 93652406);
                }
                DrawCheatSheet(s);
                if (FpInput.ResumeClickNeeded)
                {
                    DrawClickToResume(c, s);
                }
            }
            else
            {
                flashPawn = null;
                LastStatusLines.Clear();
                LastNeedsRect = default(Rect);
            }
            DrawToasts(s, toastBase, playing && p != null && map);
        }

        // ---------------------------------------------------------------- crosshair and hint

        private static Color CrosshairColor()
        {
            switch (FpActions.HintKind)
            {
                case FpHintKind.Action:
                    return FpActions.HintNoClick ? Color.white : ActionColor;   // a key to press, not a click to make
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
                    // "R: raise your weapon" undrafted on a hostile: the left button doesn't draft, so no "[LMB]".
                    text = (FpActions.HintNoClick ? "R3D_Fp_Hud_NoClick" : "R3D_Fp_Hud_Action").Translate(label);
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

        // The status in one or more lines (M6: at 1280 x 720 the autopilot status was cut, the centred line having only the
        // 380 px between the inspect pane and the screen's middle). Its parts ("name — report", the control state, each need
        // warning) go on one centred line when they fit there; otherwise they wrap at the parts into at most two centred
        // lines; otherwise into the band between the inspect pane and the time controls (wider, centred in that band). Only
        // a single part wider than its line is cut. The last line sits at StatusTop (the skill-check bar goes under it, as
        // before); earlier lines stack above it. Returns the top of the first line (the toasts go above it).
        // The inspect pane is 72 px per visible tab, at least 432 (InspectPaneUtility.PaneWidthFor): a colonist with the
        // DLCs' tabs gets a wider one, so its real right edge is used when it is open.
        private static float DrawStatus(Pawn p, float s)
        {
            List<string> parts = StatusParts(p);
            Text.Font = FontFor(s);
            float lh = Text.LineHeight;
            float gap = 2f * s;
            float sw = UI.screenWidth;
            float paneClear = InspectPaneRight();
            // The centred space clear of both panes; under 300 px (a narrow screen or a wide inspect pane) the centred line
            // would sit on the inspect pane, so the band is used straight away.
            float free = 2f * Mathf.Min(sw / 2f - paneClear, sw / 2f - ControlsClear);
            float centred = Mathf.Clamp(free, 300f, 900f);
            float bandX0 = Mathf.Min(paneClear, Mathf.Max(0f, sw - 320f));
            // Never into the time controls (review finding, M6: at 1366 x 768 with 125% UI scale and a wide inspect pane a
            // band at least 300 wide ran over the date): a narrower band wraps into more lines instead.
            float bandX1 = sw - ControlsClear;
            float bandW = Mathf.Min(bandX1 - bandX0, 900f);
            float midX = sw / 2f;
            float maxW = centred;
            List<string> lines = Wrap(parts, centred);
            if (free < 300f || lines.Count > 2 || Widest(lines) > centred)
            {
                lines = Wrap(parts, bandW);
                maxW = bandW;
                midX = (bandX0 + bandX1) / 2f;
            }
            float bottom = StatusTop(s);
            float top = bottom - (lines.Count - 1) * (lh + gap);
            LastStatusLines.Clear();
            LastStatusCut = false;
            Rect panel = default(Rect);
            Rect[] rects = new Rect[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                float w = Mathf.Min(Text.CalcSize(lines[i]).x + 4f, maxW);
                rects[i] = new Rect(midX - w / 2f, top + i * (lh + gap), w, lh);
                panel = i == 0 ? rects[i] : Union(panel, rects[i]);
            }
            Widgets.DrawBoxSolid(panel.ExpandedBy(8f * s, 2f * s), PanelColor);
            for (int i = 0; i < lines.Count; i++)
            {
                string shown = Fit(lines[i], rects[i].width);
                if (!ReferenceEquals(shown, lines[i]))
                {
                    LastStatusCut = true;
                }
                ShadowLabel(rects[i], shown, Color.white, TextAnchor.MiddleCenter);
                LastStatusLines.Add(new StatusLineDrawn { text = lines[i].StripTags(), rect = rects[i] });
            }
            LastStatusFrame = Time.frameCount;
            float fill = FpInput.InterruptFill;
            if (fill >= 0f && rects.Length > 0)
            {
                Rect last = rects[rects.Length - 1];
                DrawFillRing(new Vector2(last.x - 8f * s - 10f * s, last.center.y), 7f * s, fill);
            }
            return top;
        }

        // Greedy: parts joined with the separator while the line stays within width; a part alone on its line may be wider
        // (Fit cuts it when drawn).
        private static List<string> Wrap(List<string> parts, float width)
        {
            List<string> lines = new List<string>();
            string line = null;
            foreach (string part in parts)
            {
                if (line == null)
                {
                    line = part;
                    continue;
                }
                string joined = line + Separator + part;
                if (Text.CalcSize(joined).x + 4f <= width)
                {
                    line = joined;
                }
                else
                {
                    lines.Add(line);
                    line = part;
                }
            }
            if (line != null)
            {
                lines.Add(line);
            }
            return lines;
        }

        private static float Widest(List<string> lines)
        {
            float w = 0f;
            foreach (string l in lines)
            {
                w = Mathf.Max(w, Text.CalcSize(l).x + 4f);
            }
            return w;
        }

        // Where the centred lines must start to stay clear of the inspect pane: its open window's right edge plus a gap, and
        // never left of the narrowest pane's (the pane may open or close under a free cursor; the line shouldn't jump).
        internal static float InspectPaneRight()
        {
            float right = 0f;
            Window w = Find.WindowStack?.WindowOfType<MainTabWindow_Inspect>();
            if (w != null)
            {
                right = w.windowRect.xMax + (InspectPaneClear - 432f);
            }
            return Mathf.Max(InspectPaneClear, right);
        }

        private static Rect Union(Rect a, Rect b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
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

        // "<Name> — <job report>", then one of taking control / autopilot / drafted, then need warnings, each a part of its
        // own (DrawStatus wraps between parts). Rebuilt at most 4 times a second (the job report and translations aren't
        // free).
        private static List<string> StatusParts(Pawn p)
        {
            double now = Now;
            if (p == statusPawn && statusAt >= 0 && now - statusAt < ReportRefresh)
            {
                return statusParts;
            }
            statusPawn = p;
            statusAt = now;
            statusParts.Clear();
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
            statusParts.Add(line);
            string reason;
            if (!FpPlay.Confirmed)
            {
                statusParts.Add(Part("R3D_Fp_Hud_TakingControl".Translate(), WarnColor));
            }
            else if ((reason = FpState.AutopilotReason(p)) != null)
            {
                statusParts.Add(Part("R3D_Fp_Hud_Autopilot".Translate(reason, KeyLabel(R3DKeyDefOf.R3D_FirstPerson)), WarnColor));
            }
            else if (p.Drafted)
            {
                statusParts.Add(Part("R3D_Fp_Hud_Drafted".Translate(), AccentColor));
            }
            if (p.needs?.food != null && p.needs.food.CurCategory >= HungerCategory.Hungry)
            {
                statusParts.Add(Part("R3D_Fp_Hud_Hungry".Translate(), WarnColor));
            }
            if (p.needs?.rest != null && p.needs.rest.CurCategory >= RestCategory.Tired)
            {
                statusParts.Add(Part("R3D_Fp_Hud_Tired".Translate(), WarnColor));
            }
            if (p.health?.hediffSet != null && p.health.hediffSet.BleedRateTotal > 0.01f)
            {
                statusParts.Add(Part("R3D_Fp_Hud_Bleeding".Translate(), InvalidColor));
            }
            return statusParts;
        }

        private static string Part(TaggedString text, Color color)
        {
            return text.ToString().Colorize(color);
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

        // Above the status (its first line's top when it shows, else StatusTop). While the status shows (playing on the map),
        // each toast also stays clear of the inspect pane and the time controls, as the status does (a centred toast ran
        // under the pane's right edge, and windows draw over the HUD); otherwise centred on the screen as before.
        private static void DrawToasts(float s, float statusTop, bool clearPanes)
        {
            if (toasts.Count == 0)
            {
                return;
            }
            double now = Now;
            toasts.RemoveAll(t => now >= t.until);
            Text.Font = FontFor(s);
            float lh = Text.LineHeight;
            float y = statusTop - 6f * s;
            float sw = UI.screenWidth;
            float x0 = clearPanes ? Mathf.Min(InspectPaneRight(), Mathf.Max(0f, sw - 320f)) : 0f;
            float x1 = clearPanes ? sw - ControlsClear : sw;   // as the status band: never into the time controls
            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                ToastEntry t = toasts[i];
                float a = Mathf.Clamp01((float)(t.until - now) / 0.3f) * Mathf.Clamp01((float)(now - t.start) / 0.1f);
                y -= lh + 4f * s;
                Color col = t.color;
                col.a *= a;
                float w = Mathf.Min(Text.CalcSize(t.text).x + 4f, x1 - x0);
                float x = Mathf.Clamp(sw / 2f - w / 2f, x0, x1 - w);   // centred on the screen when that is clear
                ShadowLabel(new Rect(x, y, w, lh), Fit(t.text, w), col, TextAnchor.MiddleCenter);
            }
        }

        // For 8 s on the session's first entry (FpSettings.cheatSheet). M6: no longer at the top left, where the game's
        // message stack starts (IsoRim's "First person with ..." message sat on it) and the line ran under the colonist bar:
        // centred under the colonist bar, and moved down a message row at a time while it would overlap a live message
        // (the game draws its messages earlier in the same pass, so the sheet would cover them).
        private static void DrawCheatSheet(float s)
        {
            double now = Now;
            if (now >= cheatSheetUntil)
            {
                LastCheatSheetRect = default(Rect);
                return;
            }
            float a = Mathf.Clamp01((float)(cheatSheetUntil - now));
            string text = "R3D_Fp_Hud_CheatSheet".Translate(MoveKeysLabel(), KeyLabel(KeyBindingDefOf.Command_ColonistDraft),
                KeyLabel(FpKeyDefOf.R3D_FpSkillCheck), KeyLabel(FpKeyDefOf.R3D_FpFreeCursor), KeyLabel(R3DKeyDefOf.R3D_FirstPerson));
            Text.Font = FontFor(s);
            float w = Mathf.Min(Text.CalcSize(text).x + 4f, UI.screenWidth - 40f);
            float h = Text.CalcHeight(text, w);
            Rect r = new Rect(UI.screenWidth / 2f - w / 2f, Mathf.Max(16f * s, ColonistBarBottom() + 10f * s), w, h);
            Rect box = r.ExpandedBy(8f * s, 4f * s);
            for (int i = 0; i < 20 && Messages.CollidesWithAnyMessage(box, out _); i++)
            {
                r.y += 26f;   // one row of the message stack (Messages.MessagesDoGUI)
                box.y += 26f;
            }
            Color panel = PanelColor;
            panel.a *= a;
            Widgets.DrawBoxSolid(box, panel);
            ShadowLabel(r, text, new Color(1f, 1f, 1f, a), TextAnchor.UpperCenter);
            LastCheatSheetRect = box;
            LastCheatSheetFrame = Time.frameCount;
        }

        // The colonist bar's lowest portrait and the name label under it (0 when it shows nobody).
        private static float ColonistBarBottom()
        {
            ColonistBar bar = Find.ColonistBar;
            List<Vector2> locs = bar?.DrawLocs;
            if (locs == null || locs.Count == 0)
            {
                return 0f;
            }
            float bottom = 0f;
            for (int i = 0; i < locs.Count; i++)
            {
                bottom = Mathf.Max(bottom, locs[i].y);
            }
            return bottom + bar.Size.y + 20f;
        }

        // ---------------------------------------------------------------- other players (M6)

        private static readonly Color TagColor = new Color(0.55f, 0.78f, 0.95f);    // IsoRimMod's accent
        private static readonly Color GreatColor = new Color32(0xFF, 0xD2, 0x4A, 0xFF);   // as the skill check's results
        private static readonly Color MissColor = new Color32(0xE0, 0x50, 0x50, 0xFF);
        private const double OutcomePopSeconds = 1.5;
        private const float TagFadeFrom = 35f;      // cells: tags fade out beyond this depth in the perspective view...
        private const float TagFadeOver = 15f;      // ...over this many

        // The last outcome seen per pawn (UI only, never a decision): a change starts the pop over its tag.
        private sealed class SeenOutcome
        {
            public int tick;
            public byte outcome;
            public double at = -999;
        }

        private static readonly Dictionary<int, SeenOutcome> seenOutcomes = new Dictionary<int, SeenOutcome>();

        // Spec 14, M6: "<player>" over each pawn another player controls on the map shown, with a small ring while a skill
        // check of theirs is pending and the result over it for 1.5 s after one lands (all synced state, so every player
        // sees the same). In the 3D view the tag stands over the head as this player's view draws it (MpView's storey
        // placement); flat, over the pawn's top edge. Not for the pawn this player is riding along in. Only in a session
        // (FpSettings.friendTags).
        private static void DrawFriendTags(float s)
        {
            LastFriendTags.Clear();
            LastFriendTagsFrame = Time.frameCount;
            if (!MpBridge.InSession || FpRegistry.ControlledCount == 0)
            {
                seenOutcomes.Clear();
                return;
            }
            if (!FpSettings.friendTags)
            {
                return;
            }
            Map map = Find.CurrentMap;
            FpMapComp comp = FpMapComp.For(map);
            if (comp == null || comp.entries.Count == 0)
            {
                return;
            }
            int local = FpIds.Local;
            foreach (FpEntry e in comp.entries)
            {
                Pawn p = e.pawn;
                if (p == null || !p.Spawned || p.Map != map || e.controller < 0 || e.controller == local
                    || FirstPerson.On && FirstPerson.Pawn == p)
                {
                    continue;
                }
                if (!TagPosition(map, p, out Vector2 at, out float alpha))
                {
                    continue;
                }
                string name = NameOf(e.controller);
                DrawTag(at, name, alpha, s, e);
                LastFriendTags.Add(new FriendTagDrawn { pawn = p, name = name, at = at, checking = e.pendingSeq != 0 });
            }
        }

        // Player names for the tags, looked up in Multiplayer's player list at most once a second per player.
        private static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        private static double namesAt = -1;

        private static string NameOf(int id)
        {
            double now = Now;
            if (now - namesAt > 1.0)
            {
                names.Clear();
                namesAt = now;
            }
            if (!names.TryGetValue(id, out string name))
            {
                name = FpIds.NameOf(id);
                names[id] = name;
            }
            return name;
        }

        // The tag's foot in UI pixels and its opacity; false when the pawn is off screen or behind the camera.
        private static bool TagPosition(Map map, Pawn p, out Vector2 at, out float alpha)
        {
            alpha = 1f;
            if (MpView.On)
            {
                Vector3 t = MpView.TruePoint(map, p.DrawPos, false);
                t.y += Interaction.PickHeight(p) + 0.3f;
                if (View3D.Perspective)
                {
                    float depth = View3D.DepthOf(t);
                    if (depth <= 0.2f || depth > View3D.CamDistance + View3D.ViewDistance)
                    {
                        at = default(Vector2);
                        return false;
                    }
                    alpha = Mathf.Clamp01(1f - (depth - TagFadeFrom) / TagFadeOver);
                }
                Vector2 px = View3D.WorldToScreen(t);
                float scale = Prefs.UIScale;
                at = new Vector2(px.x / scale, UI.screenHeight - px.y / scale);
            }
            else
            {
                at = GenMapUI.LabelDrawPosFor(p, 0.75f);   // the game's own label placement, over the pawn instead of under it
            }
            return alpha > 0.05f && at.x > -50f && at.x < UI.screenWidth + 50f && at.y > -20f && at.y < UI.screenHeight + 20f;
        }

        private static void DrawTag(Vector2 at, string name, float alpha, float s, FpEntry e)
        {
            Text.Font = s >= 1.3f ? GameFont.Small : GameFont.Tiny;
            Vector2 size = Text.CalcSize(name);
            float pad = 4f * s;
            float chevron = 4f * s;
            Rect label = new Rect(Mathf.Round(at.x - size.x / 2f), Mathf.Round(at.y - chevron - size.y - 2f * s), size.x, size.y);
            Color panel = PanelColor;
            panel.a *= alpha;
            Widgets.DrawBoxSolid(label.ExpandedBy(pad, 1f * s), panel);
            // A small chevron pointing down at the head.
            GUI.color = Color.white;   // Widgets.DrawLine tints on top of GUI.color under UnityStubs (CLAUDE.md)
            Color tag = TagColor;
            tag.a *= alpha;
            float tipY = at.y - 1f * s;
            Widgets.DrawLine(new Vector2(at.x - chevron, tipY - chevron), new Vector2(at.x, tipY), tag, Mathf.Max(1f, 2f * s));
            Widgets.DrawLine(new Vector2(at.x + chevron, tipY - chevron), new Vector2(at.x, tipY), tag, Mathf.Max(1f, 2f * s));
            ShadowLabel(label, name, tag, TextAnchor.MiddleCenter);
            if (e.pendingSeq != 0)
            {
                DrawPendingRing(new Vector2(label.x - pad - 7f * s, label.center.y), 5f * s, alpha);
            }
            DrawOutcomePop(e, new Vector2(at.x, label.y - 2f * s), alpha, s);
        }

        // A pending check on another player's pawn: a faint ring with a spark going round (the skill check's look, small).
        private static void DrawPendingRing(Vector2 c, float radius, float alpha)
        {
            const int n = 12;
            GUI.color = Color.white;
            Color track = new Color(1f, 1f, 1f, 0.35f * alpha);
            for (int i = 0; i < n; i++)
            {
                Widgets.DrawLine(c + Dir(i * 360f / n) * radius, c + Dir((i + 1) * 360f / n) * radius, track, 1f);
            }
            float t = (float)(Now % FpTuning.Get.ringPeriod / FpTuning.Get.ringPeriod);
            Vector2 spark = c + Dir(t * 360f) * radius;
            Widgets.DrawBoxSolid(new Rect(spark.x - 1.5f, spark.y - 1.5f, 3f, 3f), new Color(1f, 1f, 1f, 0.9f * alpha));
        }

        // GREAT / HIT / MISS over the tag for 1.5 s after a result lands (a change of the entry's lastOutcomeTick).
        private static void DrawOutcomePop(FpEntry e, Vector2 foot, float alpha, float s)
        {
            int id = e.pawn.thingIDNumber;
            if (!seenOutcomes.TryGetValue(id, out SeenOutcome seen))
            {
                seenOutcomes[id] = new SeenOutcome { tick = e.lastOutcomeTick, outcome = e.lastOutcome };   // first sight: no pop
                return;
            }
            if (seen.tick != e.lastOutcomeTick)
            {
                seen.tick = e.lastOutcomeTick;
                seen.outcome = e.lastOutcome;
                seen.at = e.lastOutcome <= FpOutcome.Great ? Now : -999;
            }
            double age = Now - seen.at;
            if (age < 0 || age >= OutcomePopSeconds)
            {
                return;
            }
            string text;
            Color col;
            switch (seen.outcome)
            {
                case FpOutcome.Great:
                    text = "R3D_Fp_Check_Great".Translate();
                    col = GreatColor;
                    break;
                case FpOutcome.Hit:
                    text = "R3D_Fp_Check_Hit".Translate();
                    col = Color.white;
                    break;
                default:
                    text = "R3D_Fp_Check_Miss".Translate();
                    col = MissColor;
                    break;
            }
            col.a *= alpha * Mathf.Clamp01((float)(OutcomePopSeconds - age) / 0.4f);
            Text.Font = s >= 1.3f ? GameFont.Small : GameFont.Tiny;
            Vector2 size = Text.CalcSize(text);
            float rise = (float)age * 10f * s;
            ShadowLabel(new Rect(foot.x - size.x / 2f, foot.y - size.y - rise, size.x, size.y), text, col, TextAnchor.MiddleCenter);
        }

        // ---------------------------------------------------------------- needs strip (M6)

        internal static Rect LastNeedsRect;     // zero while it doesn't show (the self-test reads it)

        private struct NeedRow
        {
            public string label;
            public float level;
            public Color color;
        }

        private static readonly List<NeedRow> needRows = new List<NeedRow>();

        // Spec 14, M6: food, rest, mood and health as four small bars at the bottom left. The played colonist stays selected,
        // so the inspect pane fills the bottom left: the strip sits just above the pane's tab row (where it would be with the
        // pane closed too, so it doesn't jump when a free cursor closes it). An open tab of the pane is a window and covers
        // it. A need the colonist doesn't have is left out. The bar turns amber, then red, at the game's own thresholds.
        private static void DrawNeedsStrip(Pawn p, float s)
        {
            LastNeedsRect = default(Rect);
            needRows.Clear();
            Need_Food food = p.needs?.food;
            if (food != null)
            {
                needRows.Add(new NeedRow
                {
                    label = food.def.LabelCap,
                    level = food.CurLevelPercentage,
                    color = food.CurCategory >= HungerCategory.UrgentlyHungry ? InvalidColor : food.CurCategory >= HungerCategory.Hungry ? WarnColor : ActionColor,
                });
            }
            Need_Rest rest = p.needs?.rest;
            if (rest != null)
            {
                needRows.Add(new NeedRow
                {
                    label = rest.def.LabelCap,
                    level = rest.CurLevelPercentage,
                    color = rest.CurCategory >= RestCategory.Exhausted ? InvalidColor : rest.CurCategory >= RestCategory.Tired ? WarnColor : ActionColor,
                });
            }
            Need_Mood mood = p.needs?.mood;
            if (mood != null)
            {
                float level = mood.CurLevelPercentage;
                Verse.AI.MentalBreaker breaker = p.mindState?.mentalBreaker;
                needRows.Add(new NeedRow
                {
                    label = mood.def.LabelCap,
                    level = level,
                    color = breaker != null && level < breaker.BreakThresholdMajor ? InvalidColor
                        : breaker != null && level < breaker.BreakThresholdMinor ? WarnColor : ActionColor,
                });
            }
            if (p.health?.summaryHealth != null)
            {
                float hp = p.health.summaryHealth.SummaryHealthPercent;
                needRows.Add(new NeedRow
                {
                    label = "R3D_Fp_Hud_Health".Translate(),
                    level = hp,
                    color = hp < 0.35f ? InvalidColor : hp < 0.7f || p.health.hediffSet.BleedRateTotal > 0.01f ? WarnColor : ActionColor,
                });
            }
            if (needRows.Count == 0)
            {
                return;
            }
            Text.Font = GameFont.Tiny;
            float rowH = Mathf.Max(Text.LineHeight, 14f * s);
            float gap = 2f * s;
            float labelW = 0f;
            foreach (NeedRow r in needRows)
            {
                labelW = Mathf.Max(labelW, Text.CalcSize(r.label).x + 6f * s);
            }
            float barW = 90f * s;
            float h = needRows.Count * rowH + (needRows.Count - 1) * gap;
            float tabsTop = UI.screenHeight - 35f - InspectPaneUtility.PaneHeight - InspectPaneUtility.TabHeight;
            Window pane = Find.WindowStack?.WindowOfType<MainTabWindow_Inspect>();
            if (pane != null)
            {
                tabsTop = Mathf.Min(tabsTop, pane.windowRect.y - InspectPaneUtility.TabHeight);
            }
            Rect box = new Rect(14f * s, tabsTop - 10f * s - h, labelW + barW, h);
            Rect panel = box.ExpandedBy(6f * s, 4f * s);
            Widgets.DrawBoxSolid(panel, PanelColor);
            for (int i = 0; i < needRows.Count; i++)
            {
                NeedRow r = needRows[i];
                float y = box.y + i * (rowH + gap);
                ShadowLabel(new Rect(box.x, y, labelW, rowH), r.label, Color.white, TextAnchor.MiddleLeft);
                Rect bar = new Rect(box.x + labelW, y + rowH / 2f - 3f * s, barW, 6f * s);
                Widgets.DrawBoxSolid(bar, TrackColor);
                Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(r.level), bar.height), r.color);
            }
            LastNeedsRect = panel;
        }

        // ---------------------------------------------------------------- damage flash (M6)

        private static Pawn flashPawn;
        private static float flashHealth;
        private static double flashStart = -999;
        private static float flashStrength;
        private const double FlashSeconds = 0.6;

        // Spec 14, M6: a red glow round the screen's edges when the played colonist's summary health drops, stronger for a
        // bigger hit, fading over FlashSeconds.
        private static void DrawDamageFlash(Pawn p, float s)
        {
            LastFlashAlpha = 0f;
            float hp = p.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
            double now = Now;
            if (p != flashPawn)
            {
                flashPawn = p;
                flashHealth = hp;
                flashStart = -999;
            }
            else if (hp < flashHealth - 0.0005f)
            {
                flashStrength = Mathf.Clamp(0.35f + (flashHealth - hp) * 4f, 0.35f, 0.8f);
                flashStart = now;
            }
            flashHealth = hp;
            float age = (float)(now - flashStart);
            if (age < 0f || age >= FlashSeconds)
            {
                return;
            }
            float a = flashStrength * (1f - age / (float)FlashSeconds);
            LastFlashAlpha = a;
            const int bands = 6;
            float t = Mathf.Max(2f, 6f * s);
            float sw = UI.screenWidth, sh = UI.screenHeight;
            for (int i = 0; i < bands; i++)
            {
                Color c = new Color(0.85f, 0.05f, 0.05f, a * (1f - (float)i / bands) * 0.5f);
                float o = i * t;
                Widgets.DrawBoxSolid(new Rect(o, o, sw - 2f * o, t), c);
                Widgets.DrawBoxSolid(new Rect(o, sh - o - t, sw - 2f * o, t), c);
                Widgets.DrawBoxSolid(new Rect(o, o + t, t, sh - 2f * o - 2f * t), c);
                Widgets.DrawBoxSolid(new Rect(sw - o - t, o + t, t, sh - 2f * o - 2f * t), c);
            }
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
