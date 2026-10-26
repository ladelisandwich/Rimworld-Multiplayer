using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Port) Where a lamp's light comes from in the 3D view (docs/isorim-lighting.md, options 2 and 3): true space, x and z
    // in map cells, y the height above the map's ground in true cells. Read only.
    //   - A thing drawn as a model: the middle of the model's glowing faces (ModelMaterial.glow: bulbs, shades, screens,
    //     flames, embers), placed as the bake places the model (ModelBaker.PointOnMap: its turn, fit, offset, a wall fitting
    //     on its wall), plus what it stands on and a bridge deck; without glowing faces, a third of the way up its live flame
    //     (ModelDef.flameAt / flameSize), else 85 % of its height.
    //   - A burning fire: low, by its size. A pawn: at chest height.
    //   - Anything else: just under the top of its 3D shape (Forms.HeightOf), at least LowLight.
    // Kept per thing until it moves, turns or the 3D settings change (View3D.Stamp), so a caller may ask every frame.
    public static class LampPlace
    {
        public const float PawnLight = 0.7f;
        public const float LowLight = 0.25f;

        private struct Placed
        {
            public Vector3 at;
            public IntVec3 cell;
            public Rot4 rot;
            public int stamp;
            public int map;
        }

        private static readonly Dictionary<int, Placed> placed = new Dictionary<int, Placed>();   // by thingIDNumber
        private static readonly Dictionary<int, Vector3> glowMiddles = new Dictionary<int, Vector3>();   // by Model.id; y < -0.5: none

        public static Vector3 Of(Thing t)
        {
            Vector3 at = t.DrawPos;
            Fire fire = t as Fire;
            if (fire != null)
            {
                at.y = Mathf.Clamp(0.15f + 0.35f * fire.fireSize, 0.15f, 0.6f);
                return at;
            }
            if (t is Pawn)
            {
                at.y = PawnLight + Bridges.LiftOf(t);
                return at;
            }
            int mapId = t.Map != null ? t.Map.uniqueID : -1;
            Placed p;
            if (placed.TryGetValue(t.thingIDNumber, out p) && p.map == mapId && p.cell == t.Position && p.rot == t.Rotation && p.stamp == View3D.Stamp)
            {
                return p.at;
            }
            if (placed.Count > 4096)
            {
                placed.Clear();
            }
            at = Work(t, at);
            placed[t.thingIDNumber] = new Placed { at = at, cell = t.Position, rot = t.Rotation, stamp = View3D.Stamp, map = mapId };
            return at;
        }

        private static Vector3 Work(Thing t, Vector3 at)
        {
            ThingForm form = Forms.For(t.def);
            ModelEntry entry = form.model;
            if (form.form == Form3D.Model && entry != null && entry.HasModel && !entry.def.layFlat)
            {
                try
                {
                    Model m = entry.Pick(t);
                    if (m != null)
                    {
                        Vector3 local = GlowMiddle(m);
                        if (local.y < -0.5f)
                        {
                            ModelDef def = entry.def;
                            local = def.flameAt.y >= 0f && def.flameSize.x > 0f
                                ? def.flameAt + new Vector3(0f, def.flameSize.x * 0.35f, 0f)
                                : new Vector3(0f, m.maxY * 0.85f, 0f);
                        }
                        Vector3 fit;
                        float yaw;
                        Vector3 p = ModelBaker.PointOnMap(t, entry, m, local, out fit, out yaw);
                        p.y += Forms.BaseUnder(t, false) + Bridges.LiftOf(t);
                        return p;
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Placing the light of " + t.def.defName + " in 3D failed (its halo and lamp light sit at its top instead): " + e.Message, 93611802);
                }
            }
            at.y = Mathf.Max(LowLight, Forms.HeightOf(t) * 0.9f) + Bridges.LiftOf(t);
            return at;
        }

        // The middle of a model's glowing faces (model space), or y = -1 when it has none.
        private static Vector3 GlowMiddle(Model m)
        {
            Vector3 v;
            if (glowMiddles.TryGetValue(m.id, out v))
            {
                return v;
            }
            v = new Vector3(0f, -1f, 0f);
            if (m.positions != null && m.material != null && m.materials != null)
            {
                Vector3 sum = Vector3.zero;
                int n = 0;
                int count = Mathf.Min(m.positions.Length, m.material.Length);
                for (int i = 0; i < count; i++)
                {
                    int k = m.material[i];
                    if (k >= 0 && k < m.materials.Length && m.materials[k] != null && m.materials[k].glow)
                    {
                        sum += m.positions[i];
                        n++;
                    }
                }
                if (n > 0)
                {
                    v = sum / n;
                }
            }
            glowMiddles[m.id] = v;
            return v;
        }
    }
}
