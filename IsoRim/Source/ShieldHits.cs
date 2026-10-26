using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Shots and shells stopped by a shield bubble (CompProjectileInterceptor) meet its 3D dome (ShieldDomes: a half
    // sphere of the shield's radius over its centre, on its floor). Only the drawing changes; where and when the game
    // stops a shot is the game's (or a gameplay addon's: ProjectZ judges bullets at their 3D flight height and shells
    // over the ground circle).
    //
    //   - Bombardment shells (and the orbital strike permits): the game stops a shell at the end of its fall when its
    //     cell is inside a shield (CheckBombardmentIntercept). A shell that will be stopped falls onto the dome over its
    //     cell instead of the ground (height sqrt(r^2 - d^2) above the dome's floor, reached at the same tick the game
    //     stops it), and the game's intercept effect (Props.interceptEffect: flash, dust) stands there on the dome.
    //   - Bullets, mortar and artillery shells: the game stops them where their flat flight crosses the circle; seen in
    //     3D that is in mid-air (a shell high over the rim) or just short of the dome. The shot is drawn on along its
    //     own drawn path (Projectiles.Path: the same arc, the same speed) until it meets the dome, and the game's
    //     intercept effect is held back until then and shown there, standing on the dome. If the drawn path never
    //     reaches the dome (a shell lobbed over it at a target behind it), it is drawn on to where it passes closest
    //     and the effect shows on the dome right under that point.
    // The game's own sound, the dome's glow and the shell's explosion stay where and when the game has them. Anything
    // the shield does not stop is drawn as before. With the 3D view off nothing here runs.
    public static class ShieldHits
    {
        // Harness only (probe shieldhits=0): as before (shells pass the dome, the effect on the ground).
        public static bool Enabled = true;

        private const int MaxTicks = 180;   // a shot is drawn on for at most this long

        private sealed class Ghost
        {
            public CompProjectileInterceptor comp;
            public Map map;
            public ThingDef def;
            public Material mat;
            public Quaternion rotation;
            public float altitude;
            public Projectiles.Path path;
            public float t0, tHit;
            public int startTick, hitTick;
            public Vector3 flash;        // true space (x, z on the ground rows, y cells above the ground)
            public Vector3 effectAt;     // where the game triggered the effect (its cell's middle)
            public bool entered;         // false: never met the dome, the effect is under the closest point
            public List<FleckCreationData> flecks = new List<FleckCreationData>();
        }

        private static readonly List<Ghost> ghosts = new List<Ghost>();

        private static readonly AccessTools.FieldRef<CompProjectileInterceptor, bool> debugNonHostile = AccessTools.FieldRefAccess<CompProjectileInterceptor, bool>("debugInterceptNonHostileProjectiles");

        // ---- the dome, in true space

        private static float FloorHeight(int z)
        {
            return Floors.On ? Floors.RowHeight(z) : 0f;
        }

        private static float GroundZ(float z)
        {
            return Floors.On ? z + Floors.RowOffset(Mathf.FloorToInt(z)).z : z;
        }

        // The dome's centre (on its floor) as drawn (ShieldDomes: over the parent's drawn position) and its radius.
        private static bool Dome(CompProjectileInterceptor comp, out Vector3 centre, out float radius)
        {
            centre = Vector3.zero;
            radius = 0f;
            Thing t = comp != null ? comp.parent : null;
            if (t == null || !t.Spawned)
            {
                return false;
            }
            Vector3 at = t.DrawPos;
            centre = new Vector3(at.x, FloorHeight(Mathf.FloorToInt(at.z)), GroundZ(at.z));
            radius = comp.Props.radius;
            return radius > 0.01f;
        }

        // Height (cells above the ground) of the dome's surface over a cell's middle.
        private static float SurfaceOver(Vector3 centre, float radius, IntVec3 c)
        {
            float dx = c.x + 0.5f - centre.x;
            float dz = GroundZ(c.z) + 0.5f - centre.z;
            return centre.y + Mathf.Sqrt(Mathf.Max(0f, radius * radius - dx * dx - dz * dz));
        }

        private static bool Shown(CompProjectileInterceptor comp)
        {
            return !Floors.On || Floors.Shown(Mathf.Max(0, Floors.FloorOfRow(comp.parent.Position.z)));
        }

        // ---- bombardment shells

        // Where a bombardment shell falling on c ends when a shield will stop it (SkyStrikes.BombardmentDrawAt): the
        // dome's surface over the cell, cells above the ground. The game's own test (CheckBombardmentIntercept, with the
        // first shield in the game's order), its distance on the ground (with floors: ProjectZ's ground distance).
        public static bool ShellStop(Bombardment b, IntVec3 c, out float height)
        {
            height = 0f;
            if (!Enabled || b == null || b.Map == null)
            {
                return false;
            }
            List<Thing> list = b.Map.listerThings.ThingsInGroup(ThingRequestGroup.ProjectileInterceptor);
            for (int i = 0; i < list.Count; i++)
            {
                CompProjectileInterceptor comp = list[i].TryGetComp<CompProjectileInterceptor>();
                if (comp == null || !comp.Active || !comp.Props.interceptAirProjectiles)
                {
                    continue;
                }
                if ((b.instigator == null || !b.instigator.HostileTo(comp.parent)) && !debugNonHostile(comp) && !comp.Props.interceptNonHostileProjectiles)
                {
                    continue;
                }
                IntVec3 s = comp.parent.Position;
                float r = comp.Props.radius;
                int dx = c.x - s.x;
                int dz = Mathf.RoundToInt(GroundZ(c.z) - GroundZ(s.z));
                if (dx * dx + dz * dz > r * r)
                {
                    continue;
                }
                Vector3 centre;
                float radius;
                if (!Dome(comp, out centre, out radius))
                {
                    return false;
                }
                height = SurfaceOver(centre, radius, c);
                return true;
            }
            return false;
        }

        // ---- the game's intercept effect: held back (shots) or stood up on the dome (bombardment shells)

        private static bool capturing;      // inside CheckIntercept: the effect's flecks are kept, not made
        private static CompProjectileInterceptor capturingFor;
        private static readonly List<FleckCreationData> captured = new List<FleckCreationData>();
        private static bool raising;        // inside CheckBombardmentIntercept: the effect's flecks stand this high
        private static float raiseHeight;
        private static CompProjectileInterceptor raisingFor;
        private static readonly Dictionary<EffecterDef, HashSet<FleckDef>> effectFlecks = new Dictionary<EffecterDef, HashSet<FleckDef>>();

        private static bool OfEffect(CompProjectileInterceptor comp, FleckDef fleck)
        {
            EffecterDef e = comp.Props.interceptEffect ?? EffecterDefOf.Interceptor_BlockedProjectile;
            HashSet<FleckDef> set;
            if (!effectFlecks.TryGetValue(e, out set))
            {
                set = new HashSet<FleckDef>();
                if (e.children != null)
                {
                    foreach (SubEffecterDef sub in e.children)
                    {
                        if (sub.fleckDef != null)
                        {
                            set.Add(sub.fleckDef);
                        }
                    }
                }
                effectFlecks[e] = set;
            }
            return fleck != null && set.Contains(fleck);
        }

        // FleckManager.CreateFleck, prefix. False = kept for later (not made now).
        public static bool CreateFleck(FleckCreationData data)
        {
            if (capturing && capturingFor != null && OfEffect(capturingFor, data.def))
            {
                captured.Add(data);
                return false;
            }
            if (raising && raisingFor != null && OfEffect(raisingFor, data.def))
            {
                Emitters.Raise(data.spawnPosition, raiseHeight, 0f, data.def);
            }
            return true;
        }

        // CompProjectileInterceptor.CheckBombardmentIntercept, prefix / finalizer.
        public static void BombardmentBegin(CompProjectileInterceptor comp, Bombardment.BombardmentProjectile shell)
        {
            Vector3 centre;
            float radius;
            if (!Enabled || !SkyStrikes.Enabled || !View3D.Ready || shell == null || !Dome(comp, out centre, out radius))
            {
                return;
            }
            raising = true;
            raisingFor = comp;
            // (the flecks are made on the cell's own rows: their floor's height is added there)
            raiseHeight = SurfaceOver(centre, radius, shell.targetCell) - FloorHeight(shell.targetCell.z);
        }

        public static void BombardmentEnd()
        {
            raising = false;
            raisingFor = null;
        }

        // ---- shots: CompProjectileInterceptor.CheckIntercept, prefix / postfix / finalizer

        public static void InterceptBegin(CompProjectileInterceptor comp)
        {
            if (Enabled && View3D.Ready)
            {
                capturing = true;
                capturingFor = comp;
                captured.Clear();
            }
        }

        public static void InterceptEnd()
        {
            capturing = false;
            capturingFor = null;
            captured.Clear();
        }

        // The game stopped p (its effect was triggered at newExactPos's cell, its flecks are in captured).
        public static void Intercepted(CompProjectileInterceptor comp, Projectile p, Vector3 newExactPos)
        {
            if (!capturing)
            {
                return;
            }
            List<FleckCreationData> kept = new List<FleckCreationData>(captured);
            captured.Clear();
            Map map = comp.parent != null ? comp.parent.Map : null;
            Vector3 effectAt = newExactPos.ToIntVec3().ToVector3Shifted();
            float t0;
            Projectiles.Path path = Projectiles.PathOf(p, out t0);
            Vector3 centre;
            float radius;
            if (path == null || map == null || !Dome(comp, out centre, out radius))
            {
                Replay(map, kept, effectAt, effectAt, 0f);   // as the game would have
                return;
            }
            Ghost g = new Ghost
            {
                comp = comp,
                map = map,
                def = p.def,
                mat = p.DrawMat,
                rotation = p.ExactRotation,
                altitude = p.def.Altitude,
                path = path,
                t0 = t0,
                startTick = Find.TickManager.TicksGame,
                effectAt = effectAt,
                flecks = kept
            };
            Meet(g, centre, radius);
            int ticks = Mathf.Clamp(Mathf.CeilToInt((g.tHit - g.t0) / Mathf.Max(1e-5f, path.perTick) - 1e-3f), 0, MaxTicks);
            g.hitTick = g.startTick + ticks;
            ghosts.Add(g);
            if (Probe.On)
            {
                Vector3 now = path.Point(t0);
                Log.Message("[IsoRim probe] shieldhit " + p.def.defName + " by " + comp.parent.def.defName + " r " + radius.ToString("0.0")
                    + " at t " + t0.ToString("0.000") + " (" + now.x.ToString("0.0") + ", h " + now.y.ToString("0.00") + ", " + now.z.ToString("0.0") + ", from dome " + ((now - centre).magnitude - radius).ToString("0.00")
                    + ") -> " + (g.entered ? "meets the dome" : "closest to the dome") + " at t " + g.tHit.ToString("0.000") + " in " + ticks + " ticks, flash ("
                    + g.flash.x.ToString("0.0") + ", h " + g.flash.y.ToString("0.00") + ", " + g.flash.z.ToString("0.0") + ") from dome " + ((g.flash - centre).magnitude - radius).ToString("0.00") + ", " + kept.Count + " flecks");
            }
        }

        // Where the drawn path from t0 on first enters the half sphere (from outside); if it never does before its end,
        // where it passes closest. The flash: that point, or the dome's surface nearest to it.
        private static void Meet(Ghost g, Vector3 centre, float radius)
        {
            Projectiles.Path path = g.path;
            float flat = new Vector2(path.destination.x - path.origin.x, path.destination.z - path.origin.z).magnitude;
            int steps = Mathf.Clamp(Mathf.CeilToInt((1f - g.t0) * Mathf.Max(flat, 1f) / 0.1f), 1, 2000);
            float best = float.MaxValue, bestT = g.t0;
            float prevT = g.t0;
            for (int i = 0; i <= steps; i++)
            {
                float t = Mathf.Lerp(g.t0, 1f, i / (float)steps);
                Vector3 q = path.Point(t);
                float gap = (q - centre).magnitude - radius;
                if (gap <= 0f && q.y >= centre.y - 0.05f)
                {
                    // Inside: halve back to the surface between the last outside point and this one.
                    float lo = prevT, hi = t;
                    if (i == 0)
                    {
                        lo = hi = t;
                    }
                    for (int k = 0; k < 20 && hi - lo > 1e-5f; k++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        if ((path.Point(mid) - centre).magnitude <= radius)
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid;
                        }
                    }
                    g.tHit = hi;
                    g.entered = true;
                    g.flash = i == 0 ? OnDome(path.Point(hi), centre, radius) : path.Point(hi);
                    return;
                }
                if (gap < best)
                {
                    best = gap;
                    bestT = t;
                }
                prevT = t;
            }
            g.tHit = bestT;
            g.entered = false;
            g.flash = OnDome(path.Point(bestT), centre, radius);
        }

        // The point of the dome's surface nearest to q (on the rim for a point below the dome's floor).
        private static Vector3 OnDome(Vector3 q, Vector3 centre, float radius)
        {
            Vector3 d = q - centre;
            if (d.y < 0f)
            {
                d.y = 0f;
            }
            if (d.sqrMagnitude < 1e-6f)
            {
                d = Vector3.up;
            }
            return centre + d.normalized * radius;
        }

        // The game's intercept flecks, made now at a true-space point (their spread kept), standing at its height.
        private static void Replay(Map map, List<FleckCreationData> flecks, Vector3 effectAt, Vector3 at, float height)
        {
            if (map == null)
            {
                return;
            }
            for (int i = 0; i < flecks.Count; i++)
            {
                FleckCreationData data = flecks[i];
                data.spawnPosition = new Vector3(at.x + data.spawnPosition.x - effectAt.x, 0f, at.z + data.spawnPosition.z - effectAt.z);
                if (height > 0f)
                {
                    Emitters.Raise(data.spawnPosition, height, 0f, data.def);
                }
                map.flecks.CreateFleck(data);
            }
        }

        // CompProjectileInterceptor.CompTick, postfix: the shots that reach this dome now flash there.
        public static void Tick(CompProjectileInterceptor comp)
        {
            if (ghosts.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            for (int i = ghosts.Count - 1; i >= 0; i--)
            {
                Ghost g = ghosts[i];
                bool gone = g.comp.parent == null || !g.comp.parent.Spawned || g.comp.parent.Map != g.map || now < g.startTick || now - g.startTick > MaxTicks + 60;
                if (gone)
                {
                    ghosts.RemoveAt(i);
                    continue;
                }
                if (g.comp != comp || now < g.hitTick)
                {
                    continue;
                }
                ghosts.RemoveAt(i);
                if (View3D.Ready && Shown(comp))
                {
                    Replay(g.map, g.flecks, g.effectAt, g.flash, g.flash.y);
                }
                else
                {
                    Replay(g.map, g.flecks, g.effectAt, g.effectAt, 0f);
                }
            }
        }

        // CompProjectileInterceptor.PostDraw, postfix: the shots still on their way to this dome.
        public static void Draw(CompProjectileInterceptor comp)
        {
            if (ghosts.Count == 0 || !View3D.Ready || comp == null || comp.parent == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            float sq = View3D.Squash;
            for (int i = 0; i < ghosts.Count; i++)
            {
                Ghost g = ghosts[i];
                if (g.comp != comp || now >= g.hitTick || now < g.startTick || g.mat == null)
                {
                    continue;
                }
                float t = Mathf.Min(g.t0 + (now - g.startTick) * g.path.perTick, g.tHit);
                Vector3 q = g.path.Point(t);
                Vector3 at = new Vector3(q.x, g.altitude + q.y / sq, q.z);
                Vector2 size = g.def.graphicData.drawSize;
                Matrix4x4 m;
                if (g.path.lobbed)
                {
                    m = SkyStrikes.Card(at, SkyStrikes.ScreenSpin(g.path.Along(t))) * Matrix4x4.Scale(new Vector3(size.x, 1f, size.y));
                }
                else
                {
                    m = Matrix4x4.TRS(at, g.rotation, new Vector3(size.x, 1f, size.y));
                }
                bool was = Floors.InDrawnSpace;
                Floors.InDrawnSpace = true;   // worked out in drawn space (a high shell is far above its rows)
                try
                {
                    Graphics.DrawMesh(MeshPool.plane10, m, g.mat, 0);
                }
                finally
                {
                    Floors.InDrawnSpace = was;
                }
            }
        }

        // Harness (probe shieldhitcheck=1): the shots on their way to a dome.
        public static void Report()
        {
            int now = Find.TickManager.TicksGame;
            Log.Message("[IsoRim probe] shieldhitcheck " + ghosts.Count + " on their way");
            foreach (Ghost g in ghosts)
            {
                float t = Mathf.Min(g.t0 + (now - g.startTick) * g.path.perTick, g.tHit);
                Vector3 q = g.path.Point(t);
                Log.Message("[IsoRim probe]   " + g.def.defName + " at (" + q.x.ToString("0.0") + ", h " + q.y.ToString("0.00") + ", " + q.z.ToString("0.0") + ") flash in " + (g.hitTick - now) + " ticks");
            }
        }
    }
}
