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
        private static Thing hintWeapon;     // the primary the hint was worked out with (a swap changes what a click does)
        private static bool hintValid;
        private static bool hintCombat;      // the hint came from FpCombat: worked out again at every slot (where the shooter
                                             // stands and the target's cover change the refusal and the hit chance)
        private static bool hintNoClick;     // ... and names a key, not a click (FpCombat.LastHintNoClick when it was set)
        private static float hintTime = -1000f;

        // Wheel cycling (M6, spec 7.1): the left button's float-menu options in the order Best ranks them (at most
        // CycleMax), as the last hint pass found them, and which one the left button takes now (0: the best).
        private const int CycleMax = 3;
        private static readonly List<string> cycleLabels = new List<string>();
        private static int cycleIndex;

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

        // The hint names a key, not what the left button does (FpCombat's undrafted "R: raise your weapon"): the HUD leaves
        // out its "[LMB]" and keeps the crosshair white.
        internal static bool HintNoClick
        {
            get { return hintCombat && hintNoClick && hintKind == FpHintKind.Action; }
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
                // Combat first, before the hit's own test: an invalid hit (the ray rose over a standing pawn's shoulder)
                // still fires at a hostile within the aim's wider radius (review finding, M5).
                if (p.Drafted && FpCombat.TryPrimaryDown(p, hit))
                {
                    return;
                }
                if (!hit.Valid)
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
                FloatMenuOption best = Cycled(opts) ?? Best(opts);
                if (best == null)
                {
                    FloatMenuOption refused = FirstRefused(opts);
                    FpHud.Toast(refused != null ? refused.Label : (string)"R3D_Fp_Nothing".Translate(), RefusedColour);
                    return;
                }
                FpHits.Hit("LMB option chosen");
                string label = best.Label;
                LastChosen = label;
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
            if (!hit.Valid && !FpCombat.NearHostile(p))
            {
                // Nothing under the crosshair and no hostile beside it: no menu to build, so no need to wait for the next
                // slot. (A hostile within the aim's wider radius still gets the combat hint: review finding, M5.)
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
            Thing weapon = p.equipment?.Primary;
            bool same = hintKnown && hintPawn == p && hintThing == hit.Thing && hintCell == hit.Cell && hintReach == hit.InReach
                && hintDrafted == drafted && hintAnyDistance == anyDistance && hintValid == hit.Valid && hintWeapon == weapon;
            if (same && !hintCombat)
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
            if (same)
            {
                // The same target with a combat hint: only the combat part can have changed (the shooter walked, the target
                // moved into cover; review finding, M5: the hint kept "Out of range" while the reticle showed 45%). It
                // builds no float menu. Once it has nothing to say, the target is worked out afresh below.
                string again = FpCombat.Hint(p, hit);
                if (again != null)
                {
                    SetCombatHint(again);
                    return;
                }
            }
            if (!same)
            {
                cycleIndex = 0;   // a new target starts at its best option
            }
            hintKnown = true;
            hintPawn = p;
            hintThing = hit.Thing;
            hintCell = hit.Cell;
            hintReach = hit.InReach;
            hintDrafted = drafted;
            hintAnyDistance = anyDistance;
            hintWeapon = weapon;
            hintValid = hit.Valid;
            WorkOutHint(p, hit, anyDistance);
        }

        // The first of: the combat hint (drafted, or a hostile pawn), the bench's recipe picker, a work order, why a work
        // order can't be given, the best float-menu option. Kind: TooFar out of reach (whatever the target offers), Invalid
        // with nothing to do.
        private static void WorkOutHint(Pawn p, FpHit hit, bool anyDistance)
        {
            hintCombat = false;
            hintNoClick = false;
            cycleLabels.Clear();
            string combat = FpCombat.Hint(p, hit);
            if (combat != null)
            {
                SetCombatHint(combat);
                return;
            }
            if (!hit.Valid)
            {
                SetHint(null, FpHintKind.None);   // only the combat hint works off the crosshair's ray
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
            // A rock or plant the colonist can't be ordered onto (outside the allowed area, forbidden, incapable): the
            // reason, in red, as the click would toast it (track D's request; the float menu usually offers nothing there).
            string refusal = FpOrders.RefusalHint(p, hit);
            if (refusal != null)
            {
                SetHint(refusal, FpHintKind.Invalid);
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
            List<FloatMenuOption> ranked = Ranked(opts);
            if (ranked.Count > 0)
            {
                for (int i = 0; i < ranked.Count && i < CycleMax; i++)
                {
                    cycleLabels.Add(ranked[i].Label);
                }
                if (cycleIndex >= cycleLabels.Count)
                {
                    cycleIndex = 0;
                }
                SetHint(CycleText(), FpHintKind.Action);
                return;
            }
            // Nothing the left button can take: the game's own reason, if it offers one ("Cannot wear: burning").
            SetHint(FirstRefused(opts)?.Label, FpHintKind.Invalid);
        }

        // A combat hint: Invalid (red crosshair, spec 14) when it can't be acted on (a friendly, out of range, no line of
        // fire), else Action. FpCombat.LastHintNoClick (the undrafted "raise your weapon") is read by the HUD.
        private static void SetCombatHint(string text)
        {
            hintCombat = true;
            hintNoClick = FpCombat.LastHintNoClick;
            cycleLabels.Clear();
            SetHint(text, FpCombat.LastHintBlocked ? FpHintKind.Invalid : FpHintKind.Action);
        }

        // The wheel (M6, spec 7.1; FpInput on a scroll while captured): the left button's float-menu option moves along the
        // first CycleMax of the ranked options, the hint following at once. Nothing when the hint isn't a float-menu
        // option or there is only one.
        internal static void CycleOption(int step)
        {
            if (step == 0 || cycleLabels.Count < 2 || hintCombat || hintKind != FpHintKind.Action)
            {
                return;
            }
            int n = cycleLabels.Count;
            cycleIndex = ((cycleIndex + step) % n + n) % n;
            SetHint(CycleText(), FpHintKind.Action);
            FpHits.Hit("LMB option cycled");
        }

        // The option the wheel picked, when it is still among the first CycleMax of the ranked options (matched by label);
        // null for the best one or when the menu changed, so the click takes Best.
        private static FloatMenuOption Cycled(List<FloatMenuOption> opts)
        {
            if (cycleIndex <= 0 || cycleIndex >= cycleLabels.Count || hintCombat)
            {
                return null;
            }
            string want = cycleLabels[cycleIndex];
            List<FloatMenuOption> ranked = Ranked(opts);
            for (int i = 0; i < ranked.Count && i < CycleMax; i++)
            {
                if (ranked[i].Label == want)
                {
                    return ranked[i];
                }
            }
            return null;
        }

        // The cycled option's label, with its place ("2/3") while there is more than one to pick from.
        private static string CycleText()
        {
            string label = cycleLabels[cycleIndex];
            return cycleLabels.Count < 2 ? label : label + "  (" + (cycleIndex + 1) + "/" + cycleLabels.Count + ")";
        }

        // The left button's option index (the self-test reads it).
        internal static int CycleIndex
        {
            get { return cycleIndex; }
        }

        internal static int CycleCount
        {
            get { return cycleLabels.Count; }
        }

        // The label of the float-menu option the left button last chose (the self-test reads it).
        internal static string LastChosen { get; private set; }

        // The option the left button takes now, without the "(2/3)" (the self-test reads it).
        internal static string CycledLabel
        {
            get { return cycleIndex < cycleLabels.Count ? cycleLabels[cycleIndex] : null; }
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
            hintWeapon = null;
            hintCombat = false;
            hintNoClick = false;
            cycleLabels.Clear();
            cycleIndex = 0;
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
            List<FloatMenuOption> ranked = Ranked(opts);
            return ranked.Count > 0 ? ranked[0] : null;
        }

        // Every option the left button may take, best first (as Best ranks them; ties keep the menu's order).
        private static List<FloatMenuOption> Ranked(List<FloatMenuOption> opts)
        {
            List<FloatMenuOption> ranked = new List<FloatMenuOption>();
            if (opts == null)
            {
                return ranked;
            }
            for (int i = 0; i < opts.Count; i++)
            {
                FloatMenuOption o = opts[i];
                if (o == null || o.Disabled || o.action == null || o.isGoto)
                {
                    continue;
                }
                int at = ranked.Count;
                while (at > 0 && (o.Priority > ranked[at - 1].Priority || o.Priority == ranked[at - 1].Priority && o.orderInPriority > ranked[at - 1].orderInPriority))
                {
                    at--;
                }
                ranked.Insert(at, o);
            }
            return ranked;
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
