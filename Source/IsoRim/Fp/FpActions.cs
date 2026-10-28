using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim.Fp
{
    // Track C (fp-play-spec 8.3, 8.4): the left button's main action on what the crosshair points at, the right button's
    // full float menu pinned to that point, and the hint under the crosshair.
    //
    // Interface only: never writes game state itself. Every order goes out through the game's own float-menu options
    // (their actions end in calls Multiplayer syncs: TryTakeOrderedJob, the float-menu delegates) or through another
    // track's sender (bills, work orders, firing), so in a session each one runs on every client as a command.
    public enum FpHintKind : byte { None, Action, TooFar, Invalid }

    public static class FpActions
    {
        private const float HintInterval = 0.25f;   // seconds: the hint builds the float menu at most 4 times a second

        // The HUD's colours (spec 14), for the toasts.
        private static readonly Color TooFarColour = new Color(0.604f, 0.604f, 0.604f);   // #9A9A9A
        private static readonly Color RefusedColour = new Color(0.878f, 0.353f, 0.353f);  // #E05A5A

        // The right button's menu while it is open, and the point and thing it was opened on (pinned: the pointer is
        // that point until the menu closes, so the option chosen gets the cell it was offered for).
        private static FloatMenuMap menu;
        private static Pawn menuPawn;
        private static bool menuOpen;
        private static Vector3 menuPoint;
        private static Thing menuThing;

        // The hint, and what it was worked out for.
        private static string hint;
        private static FpHintKind hintKind;
        private static bool hintKnown;
        private static Pawn hintPawn;
        private static Thing hintThing;
        private static IntVec3 hintCell;
        private static bool hintReach;
        private static bool hintDrafted;
        private static bool hintAnyDistance;
        private static float hintTime = -1000f;

        // A first-person float menu is open (FpInput.PointerOverride keeps the pointer pinned to MenuPoint meanwhile).
        // Updated once a frame, so it stays true through the OnGUI pass in which an option closes the menu.
        public static bool MenuOpen
        {
            get { return menuOpen; }
        }

        public static Vector3 MenuPoint
        {
            get { return menuPoint; }
        }

        // The thing the open menu was opened on (null for a ground cell or with no menu): first under the pinned pointer.
        internal static Thing MenuThing
        {
            get { return menuOpen ? menuThing : null; }
        }

        // What the left button would do on the crosshair's target (a float-menu option's label, a bench's picker, a work
        // order, a shot) with HintKind Action; the game's reason it can't (a refused option's label) or null with Invalid;
        // null with TooFar or None. The HUD adds the button names.
        public static string Hint
        {
            get { return hint; }
        }

        public static FpHintKind HintKind
        {
            get { return hintKind; }
        }

        // From FpInput.OnRootUpdate, every frame in every program state.
        internal static void Update()
        {
            try
            {
                UpdateMenu();
                UpdateHint();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: updating the action hint failed: " + e, 93653101);
                ClearHint();
            }
        }

        // Left button down while captured (spec 8.3): the best option the right-button menu would offer, chosen at once.
        internal static void PrimaryDown()
        {
            if (MpBridge.Simulating)
            {
                return;
            }
            try
            {
                Pawn p = FpPlay.Pawn;
                if (FpPlay.Mode != FpMode.Playing || p == null)
                {
                    return;
                }
                FpHit hit = FpPick.Current;
                if (!hit.Valid)
                {
                    return;
                }
                if (p.Drafted && FpCombat.TryPrimaryDown(p, hit))
                {
                    return;
                }
                if (!hit.InReach && !FpSettings.interactAnyDistance)
                {
                    FpHud.Toast("R3D_Fp_TooFar".Translate(), TooFarColour);
                    return;
                }
                if (FpBills.TryOpenPicker(p, hit) || FpOrders.TryOrderWork(p, hit))
                {
                    ForgetHint();
                    return;
                }
                if (!MenuAllowed(p))
                {
                    return;
                }
                List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { p }, hit.Point, out FloatMenuContext _);
                FloatMenuOption best = Best(opts);
                if (best == null)
                {
                    FloatMenuOption refused = FirstRefused(opts);
                    FpHud.Toast(refused != null ? refused.Label : (string)"R3D_Fp_Nothing".Translate(), RefusedColour);
                    return;
                }
                FpHits.Hit("LMB option chosen");
                string label = best.Label;
                // floatMenu null: no menu to revalidate against (FloatMenuOption.Chosen is null-safe there)
                best.Chosen(true, null);
                FpHud.Toast(label, Color.white);
                ForgetHint();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: the left-button action failed: " + e, 93653102);
            }
        }

        internal static void PrimaryUp()
        {
            if (MpBridge.Simulating)
            {
                return;
            }
            try
            {
                FpCombat.PrimaryUp();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: releasing the left button failed: " + e, 93653103);
            }
        }

        // Right button down while captured (spec 8.4): every option for the crosshair's target, at any distance, in the
        // game's own map float menu, centred on the screen and pinned to the target's point. The cursor watchdog
        // (FpInput) frees the cursor while it is open and captures it again after.
        internal static void Secondary()
        {
            if (MpBridge.Simulating)
            {
                return;
            }
            try
            {
                Pawn p = FpPlay.Pawn;
                if (FpPlay.Mode != FpMode.Playing || p == null || Find.WindowStack == null)
                {
                    return;
                }
                FpHit hit = FpPick.Current;
                if (!hit.Valid)
                {
                    FpHud.Toast("R3D_Fp_NoOrders".Translate(), RefusedColour);
                    return;
                }
                if (!MenuAllowed(p))
                {
                    return;
                }
                List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { p }, hit.Point, out FloatMenuContext _);
                if (opts.Count == 0)
                {
                    FpHud.Toast("R3D_Fp_NoOrders".Translate(), RefusedColour);
                    return;
                }
                CloseMenu();
                // Pinned before the menu exists: its options are revalidated against the pointer from its first frame on.
                menuPoint = hit.Point;
                menuThing = hit.Thing;
                menuPawn = p;
                menuOpen = true;
                menu = new FloatMenuMap(opts, hit.Thing != null ? hit.Thing.LabelCap : "", hit.Point)
                {
                    givesColonistOrders = true,
                    vanishIfMouseDistant = false,   // the freed cursor appears wherever the system puts it
                };
                Find.WindowStack.Add(menu);
                // The game places a float menu at the pointer, which is hidden and anywhere while captured: under the
                // crosshair instead, kept on screen.
                Rect r = menu.windowRect;
                r.center = new Vector2(Verse.UI.screenWidth / 2f, Verse.UI.screenHeight / 2f + 40f);
                r.x = Mathf.Clamp(r.x, 0f, Mathf.Max(0f, Verse.UI.screenWidth - r.width));
                r.y = Mathf.Clamp(r.y, 0f, Mathf.Max(0f, Verse.UI.screenHeight - r.height));
                menu.windowRect = r;
                FpHits.Hit("RMB menu opened");
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] First person: opening the right-button menu failed: " + e, 93653104);
            }
        }

        // ---- the menu

        private static void UpdateMenu()
        {
            if (menu == null)
            {
                menuOpen = false;
                menuThing = null;
                menuPawn = null;
                return;
            }
            WindowStack stack = Find.WindowStack;
            // A menu for a colonist no longer played (left, released by the simulation, another game) goes: its options
            // would order a pawn this player no longer steers, at a pointer no longer pinned.
            if (stack != null && (FpPlay.Mode != FpMode.Playing || FpPlay.Pawn != menuPawn) && stack.IsOpen(menu))
            {
                stack.TryRemove(menu, false);
            }
            menuOpen = stack != null && stack.IsOpen(menu);
            if (!menuOpen)
            {
                menu = null;
                menuThing = null;
                menuPawn = null;
            }
        }

        private static void CloseMenu()
        {
            if (menu != null && Find.WindowStack != null && Find.WindowStack.IsOpen(menu))
            {
                Find.WindowStack.TryRemove(menu, false);
            }
            menu = null;
            menuOpen = false;
            menuThing = null;
            menuPawn = null;
        }

        // ---- the hint (spec 8.3)

        private static void UpdateHint()
        {
            Pawn p = FpPlay.Pawn;
            if (FpPlay.Mode != FpMode.Playing || p == null || !p.Spawned)
            {
                ClearHint();
                return;
            }
            FpHit hit = FpPick.Current;
            if (!hit.Valid)
            {
                // Nothing under the crosshair: no menu to build, so no need to wait for the next slot.
                ClearHint();
                return;
            }
            // With the cursor free (the free-cursor key, a window) the game's pointer is the visible cursor again, and
            // IsoRim's picker would put what lies under it first in the float menu's things. The crosshair doesn't move
            // meanwhile, so the last hint stays right.
            if (!FpInput.PointerOverride)
            {
                return;
            }
            bool drafted = p.Drafted;
            bool anyDistance = FpSettings.interactAnyDistance;
            if (hintKnown && hintPawn == p && hintThing == hit.Thing && hintCell == hit.Cell && hintReach == hit.InReach
                && hintDrafted == drafted && hintAnyDistance == anyDistance)
            {
                return;
            }
            // A changed target keeps the last hint until the next slot (each one may build the whole float menu).
            float now = Time.realtimeSinceStartup;
            if (now - hintTime < HintInterval && now >= hintTime)
            {
                return;
            }
            hintTime = now;
            hintKnown = true;
            hintPawn = p;
            hintThing = hit.Thing;
            hintCell = hit.Cell;
            hintReach = hit.InReach;
            hintDrafted = drafted;
            hintAnyDistance = anyDistance;
            WorkOutHint(p, hit, anyDistance);
        }

        // The first of: the combat hint (drafted, or a hostile pawn), the bench's recipe picker, a work order, the best
        // float-menu option. Kind: TooFar out of reach (whatever the target offers), Invalid with nothing to do.
        private static void WorkOutHint(Pawn p, FpHit hit, bool anyDistance)
        {
            string combat = FpCombat.Hint(p, hit);
            if (combat != null)
            {
                SetHint(combat, FpHintKind.Action);
                return;
            }
            if (!hit.InReach && !anyDistance)
            {
                SetHint(null, FpHintKind.TooFar);
                return;
            }
            string s = FpBills.PickerHint(p, hit) ?? FpOrders.OrderHint(p, hit);
            if (s != null)
            {
                SetHint(s, FpHintKind.Action);
                return;
            }
            // Checked first: GetOptions would post a "can't" message on its own (spec 1, corrections).
            AcceptanceReport allowed = FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(p);
            if (!allowed.Accepted)
            {
                SetHint(allowed.Reason.NullOrEmpty() ? null : allowed.Reason, FpHintKind.Invalid);
                return;
            }
            List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { p }, hit.Point, out FloatMenuContext _);
            FloatMenuOption best = Best(opts);
            if (best != null)
            {
                SetHint(best.Label, FpHintKind.Action);
                return;
            }
            // Nothing the left button can take: the game's own reason, if it offers one ("Cannot wear: burning").
            SetHint(FirstRefused(opts)?.Label, FpHintKind.Invalid);
        }

        private static void SetHint(string text, FpHintKind kind)
        {
            hint = text;
            hintKind = kind;
        }

        private static void ClearHint()
        {
            hint = null;
            hintKind = FpHintKind.None;
            hintKnown = false;
            hintPawn = null;
            hintThing = null;
        }

        // After an order the target's options change (the job taken, the bill added): work the hint out again at the next
        // slot even if the crosshair stays put.
        private static void ForgetHint()
        {
            hintKnown = false;
        }

        // ---- shared

        // The game's own gate for a colonist's float menu (downed, deathresting, a lord that forbids it), checked before
        // GetOptions, which would post the reason as a game message; it shows as a first-person message instead.
        private static bool MenuAllowed(Pawn p)
        {
            AcceptanceReport ok = FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(p);
            if (!ok.Accepted && !ok.Reason.NullOrEmpty())
            {
                FpHud.Toast(ok.Reason, RefusedColour);
            }
            return ok.Accepted;
        }

        // The option the right-button menu would list first among those the left button may take: enabled, with an
        // action, not a plain "go here" (walking is the movement keys' job). Same order as FloatMenu's own sort (priority,
        // then orderInPriority, both descending, stable).
        private static FloatMenuOption Best(List<FloatMenuOption> opts)
        {
            FloatMenuOption best = null;
            if (opts == null)
            {
                return null;
            }
            for (int i = 0; i < opts.Count; i++)
            {
                FloatMenuOption o = opts[i];
                if (o == null || o.Disabled || o.action == null || o.isGoto)
                {
                    continue;
                }
                if (best == null || o.Priority > best.Priority || o.Priority == best.Priority && o.orderInPriority > best.orderInPriority)
                {
                    best = o;
                }
            }
            return best;
        }

        // The first refused option (no action; its label carries the game's reason), walking aside.
        private static FloatMenuOption FirstRefused(List<FloatMenuOption> opts)
        {
            if (opts == null)
            {
                return null;
            }
            for (int i = 0; i < opts.Count; i++)
            {
                FloatMenuOption o = opts[i];
                if (o != null && o.Disabled && !o.isGoto)
                {
                    return o;
                }
            }
            return null;
        }
    }
}
