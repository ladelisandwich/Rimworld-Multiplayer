using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Strikes from the sky in 3D: the orbital power beam, bombardments (and the empire's orbital strike and salvo
    // permits, which are bombardments), lightning strikes (storms, flashstorms), and the ground glow of a power beam.
    //
    // Vanilla draws these as flat quads running "up the screen" from the impact, which in a top-down view reads as up
    // into the sky. In 3D that strip lay on the ground and ran north across the map (with floors, through other floors'
    // rows). One mechanism stands them up: the game's own flat drawing (its +z = up the screen) is put through
    // View3D.VerticalMatrix over the impact point, so it rises straight up from where it hits, turned toward the camera
    // about the vertical only (it does not tip over as the camera pitches). Vanilla's textures, colours, widths, tilt
    // (the beam's -12..12 degrees stays a tilt on screen), flicker, fades and timing are kept.
    //
    // Where it hits (Impact): the top of the impact cell's column on a map with floors (IFloorLayout): the highest floor
    // built in an unbroken stack above the cell, the first thing a strike from the sky meets (as ProjectZ lands shells
    // falling from the sky). Recomputed every frame, so a beam burning through a floor ends on the next one down.
    //
    //   - Power beam and the bombardment's red targeting line (CompOrbitalBeam.PostDraw): a column from SkyTop down to the
    //     impact; its bottom comes down out of the sky over vanilla's first 10 ticks; the end glow stands on the impact.
    //   - The power beam's 90-cell ground glow (Mote_PowerBeam, Mote_Bombardment): flat on the ground or roof it hits, not
    //     stood up as an overhead icon (Patches.ThingDraw_Prefix asks FlatGlow).
    //   - Bombardment shells (Bombardment.DrawAt): fall straight down over the target cell from vanilla's 60 cells of slide
    //     (times Skyfallers.HeightPerSlide) to the impact, nose first, over vanilla's 60 ticks; onto a shield's dome when
    //     the shield will stop it (ShieldHits).
    //   - Lightning (WeatherEvent_LightningStrike / _Delayed .WeatherEventDraw): vanilla's bolt mesh (200 cells) standing
    //     from the strike point up into the sky, faded as vanilla.
    //   - Mortar and artillery shells: Projectiles.DrawLobbed (a drawn ballistic arc; uses Impact for the landing).
    // With the 3D view off, the game draws as usual.
    [StaticConstructorOnStartup]
    public static class SkyStrikes
    {
        // Harness only (probe strike3d=0): the game's flat drawing (before/after shots).
        public static bool Enabled = true;

        // How high the beam and the bolt reach (true cells): vanilla's lightning height. Fog and depth hide the far end.
        public const float SkyTop = 200f;

        // Mortar and artillery shells (flyOverhead without a real arc of their own): the drawn arc's top, as a share of the
        // flat flight length, and its cap (cells).
        public const float ShellArc = 0.25f;
        public const float ShellArcMax = 60f;

        // ---- where it hits

        public struct Impact
        {
            public float height;   // cells above the ground: the top of the column (0 without floors)
            public int shift;      // rows from the cell's own floor down to the ground band (0 on the ground)
            public int floor;      // the floor of the top (0 = the ground)
            public int ownFloor;   // the cell's own floor
            public bool shown;     // the top's floor is drawn now
        }

        public static Impact ImpactAt(Map map, IntVec3 c)
        {
            Impact i = new Impact { shown = true };
            if (!Floors.On || map == null || !c.IsValid)
            {
                return i;
            }
            float h;
            int z;
            int k = Floors.ColumnFloor(map, c, out h, out z);
            if (k < 0)
            {
                return i;
            }
            IntVec3 ground = new IntVec3(c.x, 0, c.z - z);
            i.height = h;
            i.shift = z;
            i.floor = k;
            i.ownFloor = k;
            for (int j = k + 1; j < 32; j++)
            {
                IntVec3 up = Floors.Stacked(map, ground, j);
                if (!up.IsValid || !up.InBounds(map) || !Floors.HasFloor(map, up))
                {
                    break;
                }
                float hj;
                int zj;
                Floors.ColumnFloor(map, up, out hj, out zj);
                i.height = hj;
                i.floor = j;
            }
            i.shown = Floors.Shown(i.floor);
            return i;
        }

        // Height a shell falling from the sky onto this cell lands at: the column's top when a floor is built over the
        // cell, else own (the height the flight would end at anyway).
        public static float LandingHeight(Map map, IntVec3 c, float own)
        {
            Impact i = ImpactAt(map, c);
            return i.floor > i.ownFloor ? i.height + 0.1f : own;
        }

        // The angle (degrees clockwise from up the card) a sprite pointing along a true-space direction shows at on a card
        // facing the camera (Skyfallers' streak).
        public static float ScreenSpin(Vector3 dir)
        {
            float right = Vector3.Dot(dir, View3D.Right);
            float up = Vector3.Dot(dir, View3D.Up);
            return right * right + up * up > 1e-8f ? Mathf.Atan2(right, up) * Mathf.Rad2Deg : 0f;
        }

        // A sprite card facing the camera at a drawn-space point, turned by spin on the card.
        public static Matrix4x4 Card(Vector3 at, float spin)
        {
            return Matrix4x4.Translate(at)
                * Matrix4x4.Scale(new Vector3(1f, 1f / View3D.Squash, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(View3D.Pitch - 90f, View3D.Yaw, 0f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(spin, Vector3.up));
        }

        private static void Draw(Mesh mesh, Matrix4x4 m, Material mat, MaterialPropertyBlock block)
        {
            bool was = Floors.InDrawnSpace;
            Floors.InDrawnSpace = true;   // worked out in drawn space here (a tall column reaches far past its rows)
            try
            {
                Graphics.DrawMesh(mesh, m, mat, 0, null, 0, block);
            }
            finally
            {
                Floors.InDrawnSpace = was;
            }
        }

        // ---- the beam (CompOrbitalBeam.PostDraw, prefix)

        private static readonly AccessTools.FieldRef<CompOrbitalBeam, int> beamStart = AccessTools.FieldRefAccess<CompOrbitalBeam, int>("startTick");
        private static readonly AccessTools.FieldRef<CompOrbitalBeam, int> beamTotal = AccessTools.FieldRefAccess<CompOrbitalBeam, int>("totalDuration");
        private static readonly AccessTools.FieldRef<CompOrbitalBeam, int> beamFade = AccessTools.FieldRefAccess<CompOrbitalBeam, int>("fadeOutDuration");
        private static readonly AccessTools.FieldRef<CompOrbitalBeam, float> beamAngle = AccessTools.FieldRefAccess<CompOrbitalBeam, float>("angle");
        private static readonly Material beamMat = (Material)AccessTools.Field(typeof(CompOrbitalBeam), "BeamMat").GetValue(null);
        private static readonly Material beamEndMat = (Material)AccessTools.Field(typeof(CompOrbitalBeam), "BeamEndMat").GetValue(null);
        private static readonly MaterialPropertyBlock beamBlock = new MaterialPropertyBlock();

        public static bool BeamPostDraw(CompOrbitalBeam comp)
        {
            ThingWithComps parent = comp != null ? comp.parent : null;
            if (!Enabled || !View3D.Ready || parent == null || !parent.Spawned)
            {
                return true;
            }
            int passed = Find.TickManager.TicksGame - beamStart(comp);
            int left = beamTotal(comp) - passed;
            if (left <= 0)
            {
                return false;
            }
            CompProperties_OrbitalBeam props = comp.Props;
            Impact imp = ImpactAt(parent.Map, parent.Position);
            float sq = View3D.Squash;
            Vector3 drawPos = parent.DrawPos;
            Vector3 pivot = new Vector3(drawPos.x, AltitudeLayer.MetaOverlays.AltitudeFor() + imp.height / sq, drawPos.z - imp.shift);

            // Vanilla's numbers: the end glow is width/2 tall, the whole beam slides in over 10 ticks from the far end,
            // flickers by 2.5 % and fades over the last fadeOutDuration ticks.
            float endH = props.width * 0.5f;
            float angle = beamAngle(comp);
            float reach = Mathf.Max(SkyTop - imp.height, 10f) / Mathf.Max(0.5f, Mathf.Cos(angle * Mathf.Deg2Rad));
            float slide = (1f - Mathf.Min(passed / 10f, 1f)) * reach;
            float alpha = 0.975f + Mathf.Sin(passed * 0.3f) * 0.025f;
            int fade = beamFade(comp);
            if (left < fade)
            {
                alpha *= (float)left / fade;
            }
            Color color = props.color;
            color.a *= alpha;
            beamBlock.SetColor(ShaderPropertyIDs.Color, color);

            Matrix4x4 stand = View3D.VerticalMatrix(pivot) * Matrix4x4.Translate(pivot) * Matrix4x4.Rotate(Quaternion.Euler(0f, angle, 0f));
            Matrix4x4 beam = stand * Matrix4x4.Translate(new Vector3(0f, 0f, slide + endH + reach * 0.5f)) * Matrix4x4.Scale(new Vector3(props.width, 1f, reach));
            Matrix4x4 end = stand * Matrix4x4.Translate(new Vector3(0f, 0f, slide + endH * 0.5f)) * Matrix4x4.Scale(new Vector3(props.width, 1f, endH));
            Draw(MeshPool.plane10, beam, beamMat, beamBlock);
            Draw(MeshPool.plane10, end, beamEndMat, beamBlock);
            return false;
        }

        // ---- the ground glow (Patches.ThingDraw_Prefix): lies flat on what the strike hits, this high above the mote's
        // own floor (cells).
        public static bool FlatGlow(Mote mote, out float lift)
        {
            lift = 0f;
            if (!Enabled || mote == null || (mote.def != ThingDefOf.Mote_PowerBeam && mote.def != ThingDefOf.Mote_Bombardment))
            {
                return false;
            }
            float own = Floors.On ? Floors.RowHeight(mote.Position.z) : 0f;
            lift = ImpactAt(mote.Map, mote.Position).height - own + 0.05f;
            return true;
        }

        // ---- bombardment shells (Bombardment.DrawAt, prefix)

        private static readonly AccessTools.FieldRef<Bombardment, List<Bombardment.BombardmentProjectile>> shellsRef = AccessTools.FieldRefAccess<Bombardment, List<Bombardment.BombardmentProjectile>>("projectiles");
        private static readonly AccessTools.FieldRef<Bombardment.BombardmentProjectile, int> shellLife = AccessTools.FieldRefAccess<Bombardment.BombardmentProjectile, int>("lifeTime");
        private static readonly AccessTools.FieldRef<Bombardment.BombardmentProjectile, int> shellMax = AccessTools.FieldRefAccess<Bombardment.BombardmentProjectile, int>("maxLifeTime");
        private static readonly Material shellMat = (Material)AccessTools.Field(typeof(Bombardment), "ProjectileMaterial").GetValue(null);
        private static readonly Action<ThingWithComps> postDraw = AccessTools.MethodDelegate<Action<ThingWithComps>>(AccessTools.Method(typeof(ThingWithComps), "Comps_PostDraw"));

        public static bool BombardmentDrawAt(Bombardment b)
        {
            if (!Enabled || !View3D.Ready || b == null || !b.Spawned)
            {
                return true;
            }
            postDraw(b);   // the red targeting line (BeamPostDraw)
            List<Bombardment.BombardmentProjectile> shells = shellsRef(b);
            if (shells == null)
            {
                return false;
            }
            float sq = View3D.Squash;
            for (int n = 0; n < shells.Count; n++)
            {
                Bombardment.BombardmentProjectile s = shells[n];
                if (s == null || shellLife(s) <= 0)
                {
                    continue;
                }
                Impact imp = ImpactAt(b.Map, s.targetCell);
                if (!imp.shown)
                {
                    continue;   // lands on a floor hidden now (as skyfallers)
                }
                float t = 1f - (float)shellLife(s) / Mathf.Max(1, shellMax(s));
                // A shell a shield will stop ends on its dome (ShieldHits), at the tick the game stops it.
                float stop;
                float end = ShieldHits.ShellStop(b, s.targetCell, out stop) ? stop : imp.height;
                float h = end + Mathf.Lerp(60f, 0f, t) * Skyfallers.HeightPerSlide;
                Vector3 nose = new Vector3(s.targetCell.x + 0.5f, AltitudeLayer.MoteOverhead.AltitudeFor() + h / sq, s.targetCell.z + 0.5f - imp.shift);
                // Vanilla's Bullet_Big at 2.5 cells, turned 180 degrees (nose down), its nose at the height.
                Matrix4x4 m = Card(nose, 0f) * Matrix4x4.Translate(new Vector3(0f, 0f, 1.25f))
                    * Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f)) * Matrix4x4.Scale(new Vector3(2.5f, 1f, 2.5f));
                Draw(MeshPool.plane10, m, shellMat, null);
            }
            return false;
        }

        // ---- lightning (WeatherEventDraw, prefixes)

        private static readonly AccessTools.FieldRef<WeatherEvent, Map> eventMap = AccessTools.FieldRefAccess<WeatherEvent, Map>("map");
        private static readonly AccessTools.FieldRef<WeatherEvent_LightningStrike, IntVec3> strikeLoc = AccessTools.FieldRefAccess<WeatherEvent_LightningStrike, IntVec3>("strikeLoc");
        private static readonly AccessTools.FieldRef<WeatherEvent_LightningStrike, Mesh> strikeMesh = AccessTools.FieldRefAccess<WeatherEvent_LightningStrike, Mesh>("boltMesh");
        private static readonly AccessTools.FieldRef<WeatherEvent_LightningStrikeDelayed, IntVec3> delayedLoc = AccessTools.FieldRefAccess<WeatherEvent_LightningStrikeDelayed, IntVec3>("strikeLoc");
        private static readonly AccessTools.FieldRef<WeatherEvent_LightningStrikeDelayed, Mesh> delayedMesh = AccessTools.FieldRefAccess<WeatherEvent_LightningStrikeDelayed, Mesh>("boltMesh");
        private static readonly Func<WeatherEvent_LightningFlash, float> brightness = AccessTools.MethodDelegate<Func<WeatherEvent_LightningFlash, float>>(AccessTools.PropertyGetter(typeof(WeatherEvent_LightningFlash), "LightningBrightness"));
        private static readonly Material boltMat = (Material)AccessTools.Field(typeof(WeatherEvent_LightningStrike), "LightningMat").GetValue(null);

        public static bool LightningDraw(WeatherEvent_LightningFlash ev)
        {
            if (!Enabled || !View3D.Ready || ev == null)
            {
                return true;
            }
            IntVec3 loc;
            Mesh mesh;
            if (ev is WeatherEvent_LightningStrike ls)
            {
                loc = strikeLoc(ls);
                mesh = strikeMesh(ls);
            }
            else if (ev is WeatherEvent_LightningStrikeDelayed ld)
            {
                loc = delayedLoc(ld);
                mesh = delayedMesh(ld);
            }
            else
            {
                return true;
            }
            Map map = eventMap(ev);
            if (!loc.IsValid || mesh == null || map == null)
            {
                return true;   // (a storm's random strike keeps no cell: vanilla draws it nowhere either)
            }
            float b = brightness(ev);
            if (b < 0f)
            {
                return false;   // a delayed strike that has not struck yet
            }
            Impact imp = ImpactAt(map, loc);
            Vector3 at = loc.ToVector3ShiftedWithAltitude(AltitudeLayer.Weather);
            Vector3 pivot = new Vector3(at.x, at.y + imp.height / View3D.Squash, at.z - imp.shift);
            Draw(mesh, View3D.VerticalMatrix(pivot) * Matrix4x4.Translate(pivot), FadedMaterialPool.FadedVersionOf(boltMat, b), null);
            return false;
        }

        // ---- harness: one line per strike, bolt and lobbed shell on the map.
        public static void Report(Map map)
        {
            foreach (Thing t in map.listerThings.AllThings)
            {
                if (t is OrbitalStrike os)
                {
                    Impact i = ImpactAt(map, os.Position);
                    Log.Message("[IsoRim probe] strike " + os.def.defName + " at " + os.Position + " ticks left " + (os.duration - (Find.TickManager.TicksGame - Traverse.Create(os).Field("startTick").GetValue<int>()))
                        + " impact height " + i.height.ToString("0.00") + " floor " + i.floor + " (own " + i.ownFloor + ", shift " + i.shift + ")");
                    if (os is Bombardment bo && shellsRef(bo) is List<Bombardment.BombardmentProjectile> sl)
                    {
                        foreach (object o in sl)
                        {
                            if (o is Bombardment.BombardmentProjectile bp)
                            {
                                Impact si = ImpactAt(map, bp.targetCell);
                                float tt = 1f - (float)shellLife(bp) / Mathf.Max(1, shellMax(bp));
                                float stop;
                                bool stopped = ShieldHits.ShellStop(bo, bp.targetCell, out stop);
                                float end = stopped ? stop : si.height;
                                Log.Message("[IsoRim probe]   shell to " + bp.targetCell + " life " + shellLife(bp) + "/" + shellMax(bp) + " height " + (end + Mathf.Lerp(60f, 0f, tt) * Skyfallers.HeightPerSlide).ToString("0.0")
                                    + (stopped ? " (stops on a shield dome at " + stop.ToString("0.0") + ")" : ""));
                            }
                        }
                    }
                }
                else if (t is Projectile p && p.def.projectile.flyOverhead)
                {
                    Log.Message("[IsoRim probe] lobbed " + p.def.defName + " at " + p.Position + " to " + p.usedTarget.Cell + " landing height " + LandingHeight(map, p.usedTarget.Cell, 0f).ToString("0.00"));
                }
            }
            foreach (WeatherEvent ev in map.weatherManager.eventHandler.LiveEventsListForReading)
            {
                if (ev is WeatherEvent_LightningStrike ls)
                {
                    IntVec3 c = strikeLoc(ls);
                    Log.Message("[IsoRim probe] bolt at " + c + " brightness " + brightness(ls).ToString("0.00") + " impact height " + (c.IsValid ? ImpactAt(map, c).height : 0f).ToString("0.00"));
                }
            }
        }
    }
}
