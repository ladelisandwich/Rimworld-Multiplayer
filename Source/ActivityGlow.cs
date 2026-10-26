using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Glowing marks that follow a thing's activity (the obelisks' runes): the entry's activityGlowModel, drawn every frame
    // over the baked body with its glowing faces unlit (ObjLoader.Glowing), as the game's Graphic_ActivityMask lights its
    // mask in colorTwo with alpha = CompActivity.ActivityLevel. With a = the activity level (0..1):
    //   below MinLevel the part is not drawn (vanilla's mask is invisible at 0); a thing with no CompActivity (the
    //     labyrinth obelisk, whose runes vanilla always shows) is drawn at FixedLevel;
    //   colour = lerp(0.6 x the part's own colour, colorTwo (else glowColor), a^1.2) x (1 + 0.6 a), so it warms from a dim
    //     ember to full red and brightens;
    //   pulse: x (1 + 0.18 a sin(2 pi ticks / period)), the period shortening from 240 to 60 ticks as a rises (a slow
    //     breath when calm, a racing throb near activation);
    //   flicker above 0.8: in 5-tick slots, a slot drops to 45 % with a chance rising to 15 % at full activity;
    //   flecks at 0.9 and up: every 90..30 ticks the game's HeatGlow fleck in colorTwo at a random point of the part,
    //     stood up at that point's height (Emitters.Raise), drifting up.
    // All by game ticks (still while paused); one small mesh per thing a frame. Colours are quantised by DynamicModels'
    // cache (32 steps a channel), so a thing uses at most a few dozen cached meshes.
    public static class ActivityGlow
    {
        public const float MinLevel = 0.1f;

        private static List<ThingDef> defs;
        private static readonly Dictionary<int, int> lastFleck = new Dictionary<int, int>();

        private static void Init()
        {
            defs = new List<ThingDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                ModelEntry entry;
                if (ModelLibrary.TryGet(def, out entry) && entry.activityGlow != null && entry.HasModel)
                {
                    defs.Add(def);
                }
            }
        }

        private static bool StandsIn(Thing t)
        {
            if (t == null || !IsoRimMod.Settings.models3D || !View3D.Ready)
            {
                return false;
            }
            ThingForm form = Forms.For(t.def);
            if (form.form != Form3D.Model || form.model == null || form.model.activityGlow == null)
            {
                return false;
            }
            return t.def.drawerType != DrawerType.RealtimeOnly || SectionLayer_IsoRimBodies.StandsIn(t);
        }

        // Things with no CompActivity (the labyrinth's obelisk) glow steadily at this level: vanilla always shows their runes.
        public const float FixedLevel = 0.6f;

        public static float Level(Thing t)
        {
            CompActivity a = (t as ThingWithComps)?.GetComp<CompActivity>();
            return a != null ? Mathf.Clamp01(a.ActivityLevel) : FixedLevel;
        }

        // The colour the part is drawn in at this activity, before the flicker.
        public static Color ColourAt(ModelEntry entry, ThingDef def, float a, int ticks, int id)
        {
            Color target = def.graphicData != null && def.graphicData.colorTwo != Color.white ? def.graphicData.colorTwo : entry.def.glowColor;
            Color c = Color.Lerp(entry.activityGlowBase * 0.6f, target, Mathf.Pow(a, 1.2f)) * (1f + 0.6f * a);
            float period = Mathf.Lerp(240f, 60f, a);
            c *= 1f + 0.18f * a * Mathf.Sin(Mathf.PI * 2f * ticks / period + id * 0.7f);
            c.a = 1f;
            return c;
        }

        public static void DrawAll()
        {
            Map map = Find.CurrentMap;
            if (!IsoRimMod.Settings.models3D || map == null || Find.TickManager == null)
            {
                return;
            }
            try
            {
                if (defs == null)
                {
                    Init();
                }
                if (defs.Count == 0)
                {
                    return;
                }
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(3);
                for (int d = 0; d < defs.Count; d++)
                {
                    List<Thing> things = map.listerThings.ThingsOfDef(defs[d]);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        if (view.Contains(t.Position) && !t.Fogged() && StandsIn(t))
                        {
                            Draw(t, Forms.For(t.def).model, map);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing activity glow failed: " + e, 93611273);
            }
        }

        private static void Draw(Thing t, ModelEntry entry, Map map)
        {
            float a = Level(t);
            if (a < MinLevel)
            {
                return;
            }
            ModelDef def = entry.def;
            int ticks = Find.TickManager.TicksGame;
            int id = t.thingIDNumber;
            bool steady = (t as ThingWithComps)?.GetComp<CompActivity>() == null;   // FixedLevel: no pulse
            Color c = steady ? ColourAt(entry, t.def, a, 0, 0) : ColourAt(entry, t.def, a, ticks, id);
            if (a > 0.8f && Hashing.Unit(id, ticks / 5, 31) < 0.15f * (a - 0.8f) / 0.2f)
            {
                c *= 0.45f;
                c.a = 1f;
            }
            float yaw = entry.Yaw(t.Rotation);
            if (entry.Swapped(t.def))
            {
                yaw += 90f;
            }
            Vector3 fit = entry.Fit(t.def) * def.scale;
            Vector3 at = t.TrueCenter();
            Vector3 offset = Quaternion.AngleAxis(yaw, Vector3.up) * def.offset;
            DynamicModels.Draw(entry.activityGlow, new Vector3(at.x + offset.x, offset.y, at.z + offset.z), yaw, fit, c, null);
            if (a >= 0.9f && !Find.TickManager.Paused)
            {
                Fleck(t, entry, a, ticks, yaw, fit, at + offset, map);
            }
        }

        private static void Fleck(Thing t, ModelEntry entry, float a, int ticks, float yaw, Vector3 fit, Vector3 at, Map map)
        {
            int every = Mathf.RoundToInt(Mathf.Lerp(90f, 30f, (a - 0.9f) / 0.1f));
            int last;
            if (lastFleck.TryGetValue(t.thingIDNumber, out last) && ticks - last < every)
            {
                return;
            }
            lastFleck[t.thingIDNumber] = ticks;
            Vector3[] pos = entry.activityGlow.positions;
            if (pos == null || pos.Length == 0)
            {
                return;
            }
            Vector3 p = Vector3.Scale(pos[Mathf.FloorToInt(Hashing.Unit(t.thingIDNumber, ticks, 37) * pos.Length) % pos.Length], fit);
            Vector3 w = Quaternion.AngleAxis(yaw, Vector3.up) * p;
            Vector3 spawn = new Vector3(at.x + w.x, AltitudeLayer.MoteOverhead.AltitudeFor(), at.z + w.z);
            Color target = t.def.graphicData != null ? t.def.graphicData.colorTwo : entry.def.glowColor;
            FleckCreationData data = FleckMaker.GetDataStatic(spawn, map, FleckDefOf.HeatGlow, 0.9f + 0.6f * a);
            data.instanceColor = target;
            Emitters.Raise(data.spawnPosition, p.y, 0.25f, FleckDefOf.HeatGlow);
            map.flecks.CreateFleck(data);
        }
    }
}
