using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim.Fp
{
    // The Esc panel while playing (fp-play-spec 7.5): Resume, Leave first person, Mod settings, Game menu, and the controls
    // below. Opened by Esc while the cursor is captured (FpInput); being a window, it frees the cursor while open. Esc
    // again, a click outside or Resume closes it and the cursor is captured again. Local only: Leave goes through
    // FpPlay.Leave (FpSetControl off), nothing else here touches the game.
    public class Dialog_FpMenu : Window
    {
        // The Leave button as last drawn, in UI pixels from the screen's top left (the self-test clicks it).
        public static Rect LeaveButtonRect;

        private const float RowHeight = 26f;
        private const float RowGap = 4f;
        private static readonly Color Accent = new Color(0.55f, 0.78f, 0.95f);   // IsoRimMod's accent

        public override Vector2 InitialSize
        {
            get { return new Vector2(300f, 260f); }
        }

        public Dialog_FpMenu()
        {
            forcePause = false;
            absorbInputAroundWindow = true;
            closeOnCancel = true;
            closeOnClickedOutside = true;
            preventCameraMotion = false;
            doCloseX = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (FpPlay.Mode != FpMode.Playing)
            {
                Close(false);   // play ended under the menu (F5, the colonist downed, a reload)
                return;
            }
            Pawn p = FpPlay.Pawn;
            float y = 0f;

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Accent;
            Widgets.Label(new Rect(0f, y, inRect.width, RowHeight), "R3D_Fp_Menu_Title".Translate(p != null ? p.LabelShortCap : "?"));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            y += RowHeight + RowGap;

            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, RowHeight), "R3D_Fp_Menu_Resume".Translate()))
            {
                Close();
            }
            y += RowHeight + RowGap;

            Rect leave = new Rect(0f, y, inRect.width, RowHeight);
            LeaveButtonRect = new Rect(windowRect.x + Margin + leave.x, windowRect.y + Margin + leave.y, leave.width, leave.height);
            if (Widgets.ButtonText(leave, "R3D_Fp_Menu_Leave".Translate()))
            {
                FpPlay.Leave("R3D_Fp_Left".Translate());
                Close();
            }
            y += RowHeight + RowGap;

            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, RowHeight), "R3D_Fp_Menu_ModSettings".Translate()))
            {
                Mod mod = LoadedModManager.GetMod<IsoRimMod>();
                Close(false);
                if (mod != null)
                {
                    Find.WindowStack.Add(new Dialog_ModSettings(mod));
                }
            }
            y += RowHeight + RowGap;

            if (Widgets.ButtonText(new Rect(0f, y, inRect.width, RowHeight), "R3D_Fp_Menu_GameMenu".Translate()))
            {
                Close(false);
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Menu);
            }
            y += RowHeight + RowGap + 4f;

            DrawControls(new Rect(0f, y, inRect.width, inRect.height - y));
        }

        public override void PostClose()
        {
            base.PostClose();
            LeaveButtonRect = default(Rect);
        }

        // Two columns of "key  action", in the game's tiny font.
        private static void DrawControls(Rect r)
        {
            string[] keys =
            {
                FpHud.MoveKeysLabel(), "R3D_Fp_Menu_KeyMouse".Translate(), "R3D_Fp_Menu_KeyLmb".Translate(), "R3D_Fp_Menu_KeyRmb".Translate(),
                FpHud.KeyLabel(KeyBindingDefOf.Command_ColonistDraft), FpHud.KeyLabel(FpKeyDefOf.R3D_FpSkillCheck),
                FpHud.KeyLabel(FpKeyDefOf.R3D_FpFreeCursor), FpHud.KeyLabel(R3DKeyDefOf.R3D_FirstPerson),
            };
            string[] actions =
            {
                "R3D_Fp_Menu_Move".Translate(), "R3D_Fp_Menu_Look".Translate(), "R3D_Fp_Menu_Use".Translate(), "R3D_Fp_Menu_Orders".Translate(),
                "R3D_Fp_Menu_Draft".Translate(), "R3D_Fp_Menu_Check".Translate(), "R3D_Fp_Menu_Cursor".Translate(), "R3D_Fp_Menu_LeaveKey".Translate(),
            };
            Text.Font = GameFont.Tiny;
            int rows = (keys.Length + 1) / 2;
            float rowH = Mathf.Min(Text.LineHeight, r.height / rows);
            float colW = r.width / 2f;
            float keyW = 52f;
            for (int i = 0; i < keys.Length; i++)
            {
                float x = r.x + (i / rows) * colW;
                float yy = r.y + (i % rows) * rowH;
                GUI.color = Accent;
                Widgets.Label(new Rect(x, yy, keyW, rowH), keys[i]);
                GUI.color = Color.white;
                Widgets.Label(new Rect(x + keyW, yy, colW - keyW, rowH), actions[i]);
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }
    }
}
