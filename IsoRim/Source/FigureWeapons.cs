using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // What a 3D figure holds and wears over itself, posed by the game (2026-10-17, isorim-figure-weapon-anim.md).
    //
    // The game's own PawnRenderUtility.DrawEquipmentAndApparelExtras is asked to draw the figure's weapon (and worn extras such
    // as the shield belt's bubble) as it would for the pawn's sprite, with every mod that patches it, DrawCarriedWeapon,
    // DrawEquipmentAiming or the recoil. What it draws is caught on its way to Unity (DrawHook: Capturing) instead of drawn,
    // and each piece is put on the figure:
    //   - carried (drafted, standing guard, a job, duty or lord that shows the weapon): the game's picture of the pawn seen
    //     from the front (south) laid on the figure's chest, turned Tilt degrees so the muzzle comes forward, riding the
    //     figure's pose (bob, sway, seat, lean);
    //   - aimed (warm-up, burst, cool-down, melee): the game's top-down picture turned onto the aim as drawn (between floors
    //     too), stood up about the aim line (the picture's top up), pitched toward the target's height, hinged at the hands;
    //     recoil and any mod's offsets come out as drawn. The barrel's end as drawn is where shots start (Projectiles);
    //   - anything else (worn extras: the shield bubble) a card facing the camera round the figure's middle.
    // Nothing caught means nothing shows: the game would not draw a weapon now (sheathed, carrying something, eating).
    public static class FigureWeapons
    {
        private const float PersonHeight = 1.12f;   // a figure of size 1, feet to the top of the head (People.FigureHeight)
        private const float PictureHeight = 1.5f;   // the game's picture of a pawn (its 1.5-cell body mesh)
        private const float Picture = PersonHeight / PictureHeight;   // the game's picture units to figure units
        private const float HandY = 0.46f;          // the hands on a figure of size 1 (People.HandY)
        private const float WeaponGap = 0.05f;      // between the chest and the carried weapon, figure units
        private const float Tilt = 35f;             // the carried weapon's turn about the figure's up: muzzle forward
        private const float MaxPitch = 70f;

        private struct Caught
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material material;
            public Thing weapon;   // null: not a weapon (a worn extra)
            public bool carried;
        }

        private static readonly List<Caught> caught = new List<Caught>();
        public static bool Capturing;
        private static Thing drawingWeapon;
        private static bool carryPath;

        // For the harness (weaponcheck): what was drawn last for each pawn.
        private static readonly Dictionary<int, string> reports = new Dictionary<int, string>();
        private static readonly bool Reporting = Probe.On;

        // DrawHook, while capturing: one mesh the game drew.
        public static void Note(Mesh mesh, Matrix4x4 matrix, Material material)
        {
            if (ApparelModels.Swallowing)
            {
                ApparelModels.GameDrew = true;   // a bound shield's own worn picture: its model stands in (People.Draw)
                return;
            }
            caught.Add(new Caught { mesh = mesh, matrix = matrix, material = material, weapon = drawingWeapon, carried = carryPath });
        }

        // Patches.DrawEquipmentAiming_Prefix / _Finalizer: the game is drawing this weapon (null when done).
        public static void Weapon(Thing weapon)
        {
            if (Capturing)
            {
                drawingWeapon = weapon;
            }
        }

        // PawnRenderUtility.DrawCarriedWeapon: the game chose the carry pose.
        public static void DrawCarriedWeapon_Prefix()
        {
            if (Capturing)
            {
                carryPath = true;
            }
        }

        public static void DrawCarriedWeapon_Finalizer()
        {
            carryPath = false;
        }

        // A worn extra drawn straight to Unity (CompShield's bubble): caught while capturing, else drawn as before.
        public static void DrawOrCatch(Mesh mesh, Matrix4x4 matrix, Material material, int layer)
        {
            if (Capturing)
            {
                Note(mesh, matrix, material);
                return;
            }
            Graphics.DrawMesh(mesh, matrix, material, layer);
        }

        private static readonly MethodInfo drawMesh4 = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh),
            new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int) });

        public static IEnumerable<CodeInstruction> CatchDraws_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo to = AccessTools.Method(typeof(FigureWeapons), nameof(DrawOrCatch));
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand as MethodInfo == drawMesh4)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = to;
                }
                yield return ci;
            }
        }

        // Whether the game aims this pawn's weapon now (Patches.CardAim: its tests), and the aim as drawn: flat, degrees
        // clockwise from north, toward where the target shows (another floor too), or the verb's own aim (a spray gun).
        public static bool AimOf(Pawn pawn, bool neverAim, out float game, out float drawn)
        {
            float seen;
            drawn = 0f;
            if (!Patches.CardAim(pawn, neverAim, out game, out seen))
            {
                return false;
            }
            drawn = game;
            Verb verb = pawn.CurrentEffectiveVerb;
            if (verb != null && verb.AimAngleOverride.HasValue)
            {
                return true;
            }
            Stance_Busy busy = (Stance_Busy)pawn.stances.curStance;
            Vector3 to = busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted();
            Vector3 d = Floors.FlatDelta(pawn.DrawPos, to);
            if (d.x * d.x + d.z * d.z > 0.001f)
            {
                drawn = d.AngleFlat();
            }
            return true;
        }

        // The figure's weapon and worn extras. at: the figure's base (x, z in the game's rows, y its height over the floor,
        // cells), yaw its facing, local its pose in figure space (People: pose and size), size its size, front how far its
        // chest reaches forward and side how far its body reaches to the side (figure units, size 1). measure: nothing is
        // drawn, only where the aimed weapon is (Projectiles.FigureTip), for a figure not drawn now (People.MeasureWeapon).
        public static void Draw(Pawn pawn, bool neverAim, Vector3 drawLoc, Vector3 at, float yaw, Matrix4x4 local, float size, float front, float side, bool measure = false)
        {
            caught.Clear();
            ApparelModels.GameDrew = false;
            Capturing = true;
            try
            {
                PawnRenderUtility.DrawEquipmentAndApparelExtras(pawn, new Vector3(drawLoc.x, 0f, drawLoc.z), Rot4.South,
                    neverAim ? PawnRenderFlags.NeverAimWeapon : PawnRenderFlags.None);
            }
            finally
            {
                Capturing = false;
                drawingWeapon = null;
                carryPath = false;
            }
            string report = Reporting && !measure ? "" : null;
            if (caught.Count == 0)
            {
                if (report != null)
                {
                    reports[pawn.thingIDNumber] = "nothing drawn";
                }
                return;
            }
            float s = View3D.Squash;
            Matrix4x4 placed = Matrix4x4.Translate(new Vector3(at.x, at.y / s, at.z)) * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up));
            Matrix4x4 figure = Matrix4x4.Translate(at) * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up)) * local;   // true cells
            Vector3 origin = new Vector3(drawLoc.x, 0f, drawLoc.z);
            bool weapons = IsoRimMod.Settings.heldWeapons3D;
            for (int i = 0; i < caught.Count; i++)
            {
                Caught c = caught[i];
                // The game's drawing about the pawn's middle, flat (its drawing layers' heights dropped).
                Matrix4x4 m = Matrix4x4.Translate(-origin) * c.matrix;
                m.m13 = 0f;
                // The game draws its plane with no height (scale 0 along its normal): a unit normal, for the slab's thickness.
                Vector3 normal = Vector3.Cross(m.GetColumn(2), m.GetColumn(0)).normalized;
                m.SetColumn(1, new Vector4(normal.x, normal.y, normal.z, 0f));
                bool flipped = c.mesh == MeshPool.plane10Flip;
                if (measure && (c.weapon == null || c.carried))
                {
                    continue;
                }
                if (c.weapon == null)
                {
                    DrawExtra(pawn, c, drawLoc, at, size);
                    if (report != null)
                    {
                        report += "extra " + (c.material != null ? c.material.name : "?") + "; ";
                    }
                }
                else if (!weapons)
                {
                    continue;
                }
                else if (c.carried)
                {
                    // The front picture on the chest: picture x to the figure's left (as seen from its front), picture up
                    // (north) to up, at the body's middle, turned about the body's side so the muzzle comes forward.
                    Matrix4x4 onChest = Matrix4x4.Translate(new Vector3(0f, PersonHeight * 0.5f, front + WeaponGap))
                        * Matrix4x4.Translate(new Vector3(side * 0.8f, 0f, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(Tilt, Vector3.up))
                        * Matrix4x4.Translate(new Vector3(-side * 0.8f, 0f, 0f))
                        * Matrix4x4.Scale(new Vector3(Picture, Picture, Picture)) * PictureToFront * m * SlabToPlane(flipped);
                    SpriteSolids.DrawSlab(c.material, Floors.DrawnMatrix(placed * local * onChest));
                    if (report != null)
                    {
                        report += "carried " + c.weapon.def.defName + (flipped ? " (flipped)" : "") + " angle " + Angle(m).ToString("0") + "; ";
                    }
                }
                else
                {
                    float game, aim;
                    if (!AimOf(pawn, neverAim, out game, out aim))
                    {
                        game = aim = c.matrix.rotation.eulerAngles.y + 90f;   // a mod aiming on its own: along the picture
                    }
                    Vector3 dir = Quaternion.AngleAxis(aim, Vector3.up) * Vector3.forward;
                    // Onto the aim as drawn (another floor: the target shows elsewhere than in the game's rows).
                    Matrix4x4 turned = Matrix4x4.Rotate(Quaternion.AngleAxis(Mathf.DeltaAngle(game, aim), Vector3.up)) * m;
                    // Stood up about the aim line: the side of it nearest the picture's top goes up.
                    Vector3 right = Vector3.Cross(Vector3.up, dir);
                    Vector3 top = ((Vector3)turned.GetColumn(2)).normalized;
                    Vector3 side2 = Vector3.Dot(top, right) >= 0f ? right : -right;
                    Quaternion standUp = Quaternion.FromToRotation(side2, Vector3.up);
                    Vector3 hands = figure.MultiplyPoint3x4(new Vector3(0f, HandY, 0f));
                    float pitch = PitchOf(pawn, hands.y);
                    Quaternion raise = Quaternion.FromToRotation(dir, dir * Mathf.Cos(pitch * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(pitch * Mathf.Deg2Rad));
                    Matrix4x4 world = Matrix4x4.Translate(hands) * Matrix4x4.Rotate(raise * standUp)
                        * Matrix4x4.Scale(new Vector3(Picture * size, Picture * size, Picture * size)) * turned * SlabToPlane(flipped);
                    if (!measure)
                    {
                        SpriteSolids.DrawSlab(c.material, Floors.DrawnMatrix(Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f)) * world));
                    }
                    Projectiles.FigureTip(pawn, c.weapon, world);
                    if (report != null)
                    {
                        report += "aimed " + c.weapon.def.defName + (flipped ? " (flipped)" : "") + " game " + game.ToString("0") + " drawn " + aim.ToString("0")
                            + " pitch " + pitch.ToString("0.0") + " picture angle " + Angle(m).ToString("0") + " reach " + new Vector2(m.m03, m.m23).magnitude.ToString("0.000")
                            + " hands " + hands.ToString("F2") + " figure yaw " + yaw.ToString("0") + "; ";
                    }
                }
            }
            if (report != null)
            {
                Vector3 shift;
                float turn;
                if (BodyAnims.Active && BodyAnims.Read(pawn, drawLoc, out shift, out turn))
                {
                    report += "body anim shift " + shift.ToString("F3") + " turn " + turn.ToString("0.0") + "; ";
                }
                reports[pawn.thingIDNumber] = report;
            }
        }

        // The harness: what was drawn for this pawn last.
        public static string Report(Pawn pawn)
        {
            string r;
            return reports.TryGetValue(pawn.thingIDNumber, out r) ? r : "not drawn as a figure yet";
        }

        private static float Angle(Matrix4x4 m)
        {
            return Mathf.Atan2(m.m02, m.m22) * Mathf.Rad2Deg;   // where the picture's top points, clockwise from north
        }

        // The game's flat picture (x, z; y its normal) onto the figure's front (figure space: x its right, y up, z forward):
        // picture x to the figure's left, picture z (up the picture) up, the picture's normal forward.
        private static readonly Matrix4x4 PictureToFront = new Matrix4x4(
            new Vector4(-1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));

        // SpriteSolids' slab (sprite across x, up y, thickness z) onto the game's plane mesh (x, z; the flipped mesh shows the
        // sprite mirrored in x). Both keep the handedness (the slab is the same on both faces).
        private static readonly Matrix4x4 SlabPlane = new Matrix4x4(
            new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, -1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));
        private static readonly Matrix4x4 SlabPlaneFlip = new Matrix4x4(
            new Vector4(-1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));

        private static Matrix4x4 SlabToPlane(bool flipped)
        {
            return flipped ? SlabPlaneFlip : SlabPlane;
        }

        // How far the aim is raised (degrees, down negative): from the hands to the target's middle, between floors as drawn.
        private static float PitchOf(Pawn pawn, float handsY)
        {
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            if (busy == null || !busy.focusTarg.IsValid)
            {
                return 0f;
            }
            Vector3 from = pawn.DrawPos;
            Vector3 to = busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted();
            Vector3 a = from, b = to;
            Floors.Placed(ref a);   // (a hidden floor too: a figure measured there, People.MeasureWeapon)
            Floors.Placed(ref b);
            float rise = ((b.y - to.y) - (a.y - from.y)) * View3D.Squash;   // the floors' height between them, cells
            Vector3 d = b - a;
            d.y = 0f;
            float flat = Mathf.Max(d.magnitude, 0.5f);
            float targetY = rise + TargetHeight(busy.focusTarg);
            float p = Mathf.Atan2(targetY - handsY, flat) * Mathf.Rad2Deg;
            return Mathf.Clamp(p, -MaxPitch, MaxPitch);
        }

        // How high the target's middle is over its floor (Projectiles.AimHeight's measure).
        private static float TargetHeight(LocalTargetInfo t)
        {
            if (!t.HasThing)
            {
                return 0.1f;
            }
            if (t.Thing is Pawn p)
            {
                return p.Dead || p.GetPosture() != PawnPosture.Standing ? 0.12f : Interaction.PickHeight(p) * 0.45f;
            }
            return Mathf.Clamp(Interaction.PickHeight(t.Thing) * 0.5f, 0.05f, 1f);
        }

        // A worn extra (the shield bubble): a card facing the camera round the figure's middle, keeping its offset from the
        // pawn (an impact's jolt), its turn and size as the game draws it.
        private static void DrawExtra(Pawn pawn, Caught c, Vector3 drawLoc, Vector3 at, float size)
        {
            Vector3 p = c.matrix.GetColumn(3);
            Vector3 pawnPos = pawn.Drawer.DrawPos;
            Matrix4x4 local = Matrix4x4.Translate(new Vector3(-p.x, -p.y, -p.z)) * c.matrix;
            float s = View3D.Squash;
            Vector3 centre = new Vector3(drawLoc.x + (p.x - pawnPos.x), at.y + PersonHeight * 0.5f * size, drawLoc.z + (p.z - pawnPos.z));
            Matrix4x4 m = Matrix4x4.Translate(new Vector3(centre.x, centre.y / s, centre.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(View3D.Pitch - 90f, View3D.Yaw, 0f))
                * Matrix4x4.Scale(new Vector3(size, 1f, size))
                * local;
            m = Floors.DrawnMatrix(m);
            if (!ShipCapture.Note(c.mesh, m, c.material, null))
            {
                Graphics.DrawMesh(c.mesh, m, c.material, 0);
            }
        }
    }
}
