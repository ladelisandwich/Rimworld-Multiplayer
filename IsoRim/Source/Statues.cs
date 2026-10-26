using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Statues (Odyssey Statue, CompStatue). The game's CompStatue.DrawAt draws the StatueBase sprite, then the statue's
    // subject: a pawn made for the statue (fakePawn), drawn by PawnRenderer.RenderPawnAt facing south, coloured all over
    // by the statue colour (PawnRenderer.StatueColor: the paint, else the stuff). While the Statue's 3D model (the base
    // slab, baked by SectionLayer_IsoRimBodies) stands in, the flat base sprite is left out and the subject is drawn the
    // way IsoRim draws people: a 3D figure (People.Draw), standing still on top of the slab, facing south, in the statue
    // colour (DynamicModels.BuildMerged mono). With people drawn as sprites, the upright sprite is lifted onto the slab.
    // 3D off, or no model: the game's own drawing.
    public static class Statues
    {
        private static AccessTools.FieldRef<CompStatue, Pawn> fakePawn;
        private static bool tried;

        // The top of the base the subject stands on while it is being drawn; below 0 otherwise.
        internal static float ground = -1f;

        // The colour the subject being drawn is carved in, or null when no statue subject is being drawn.
        internal static Color? Colour(Pawn pawn)
        {
            if (ground < 0f || pawn == null || pawn.Drawer == null || pawn.Drawer.renderer == null)
            {
                return null;
            }
            return pawn.Drawer.renderer.StatueColor;
        }

        // A figure's colour carved in the statue colour: the statue colour, lighter or darker with the colour's brightness,
        // so skin, hair and clothes still read as parts (the game greys the sprite and multiplies it by the colour).
        internal static Color Carved(Color c, Color stone)
        {
            float k = Mathf.Lerp(0.88f, 1.10f, Mathf.Clamp01(c.r * 0.299f + c.g * 0.587f + c.b * 0.114f));
            return new Color(stone.r * k, stone.g * k, stone.b * k, 1f);
        }

        public static bool DrawAt_Prefix(CompStatue comp, Vector3 drawPos)
        {
            try
            {
                Thing parent = comp.parent;
                if (parent == null || !SectionLayer_IsoRimBodies.StandsIn(parent))
                {
                    return true;
                }
                if (!tried)
                {
                    tried = true;
                    fakePawn = AccessTools.FieldRefAccess<CompStatue, Pawn>("fakePawn");
                }
                Pawn subject = fakePawn != null ? fakePawn(comp) : null;
                if (subject == null)
                {
                    return true;   // not set up yet: the game sets it up (and draws its flat base this once)
                }
                // As CompStatue.DrawAt: a smaller head moves the figure back a little, drawn just under the base's altitude.
                float head = subject.ageTracker != null && subject.ageTracker.CurLifeStage != null ? subject.ageTracker.CurLifeStage.headSizeFactor ?? 1f : 1f;
                drawPos += Vector3.back * ((1f - head) * 0.5f);
                drawPos -= Altitudes.AltIncVect * 0.25f;
                ground = Forms.HeightOf(parent);
                try
                {
                    subject.Drawer.renderer.RenderPawnAt(drawPos, Rot4.South, true);
                }
                finally
                {
                    ground = -1f;
                }
                return false;
            }
            catch (Exception e)
            {
                ground = -1f;
                Log.ErrorOnce("[IsoRim] Drawing a statue on its 3D base failed: " + e, 93611290);
                return true;
            }
        }
    }
}
