using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim.Fp
{
    // Track C (fp-play-spec 8.1, 8.2): what the crosshair points at while this player plays a colonist, and the game's
    // pointer replaced by the crosshair while the cursor is captured (or a first-person float menu is open).
    //
    // Interface only: main thread, reads the map and this player's camera, writes no game state, no Rand. What it finds
    // reaches the simulation only through the orders the player gives with it: float-menu options (whose click point
    // Multiplayer syncs as the mouse cell, MapMouseCell) and first-person commands. That is why UI.MouseCell must be the
    // crosshair's cell (or the pinned menu's) whenever an order can be given from it.
    //
    // IsoRim's own picker (Interaction.PickUnderPointer) can't serve a first-person ray: it probes heights from the top
    // down and falls back to the far end of the ray (170 cells) for any probe above the eye, and it picks the pawn whose
    // head the eye is in (fp_isorim_fp.md, section 3). This one walks the ray cell by cell from the eye instead.
    public struct FpHit
    {
        public bool Valid;
        public Thing Thing;
        public IntVec3 Cell;
        public Vector3 Point;      // map space (x, 0, z), inside the thing's rect for things
        public float Dist;
        public bool InReach;
    }

    public static class FpPick
    {
        private const float PointMargin = 0.02f;   // a hit point stays this far inside its cell (as MpView), never on a border
        private const float MinThingTop = 0.15f;   // flat things (blueprints, conduits, rugs) still catch a ray reaching the floor
        private const float MaxPickTop = 6f;       // cells over the floor: nothing pickable stands higher, a rising ray past it ends
        private const int PawnSearchSteps = 32;    // golden-section steps for a pawn's closest approach (~1e-6 of the ray)
        private const int PawnEntrySteps = 24;     // bisection steps for where the ray first touches a pawn

        // Where UI.MouseCell points while the crosshair shows nothing (the sky, fog, past the reach of the walk): off the
        // map, as the game's own pointer is past a map edge, so nothing that checks InBounds acts on it.
        private static readonly Vector3 OffMap = new Vector3(-1000f, 0f, -1000f);

        // Current, worked out once for each camera: per frame, and again if the camera moved within the frame (the Update
        // pass can run before CameraDriver.Update places this frame's eye; OnGUI always sees the placed one).
        private static int cacheFrame = -1;
        private static Pawn cachePawn;
        private static Map cacheMap;
        private static Vector3 cacheEye;
        private static Vector3 cacheFwd;
        private static FpHit cache;

        private struct PawnHit
        {
            public Pawn pawn;
            public float t;   // where the ray first comes within the pawn's pick radius
        }

        private static readonly List<PawnHit> pawnHits = new List<PawnHit>();
        private static readonly Comparison<PawnHit> NearestFirst = (a, b) =>
        {
            int c = a.t.CompareTo(b.t);
            return c != 0 ? c : a.pawn.thingIDNumber.CompareTo(b.pawn.thingIDNumber);
        };

        // What the crosshair points at now, from the played colonist's eye. Not Valid outside play, before IsoRim has
        // placed the first-person camera, or when the ray meets nothing.
        public static FpHit Current
        {
            get
            {
                if (FpPlay.Mode != FpMode.Playing)
                {
                    return default(FpHit);
                }
                Pawn pawn = FpPlay.Pawn;
                Map map = Find.CurrentMap;
                if (pawn == null || map == null || !pawn.Spawned || pawn.Map != map || FirstPerson.Pawn != pawn || !View3D.Ready)
                {
                    return default(FpHit);
                }
                Vector3 eye = View3D.EyePosition;
                Vector3 fwd = View3D.Forward;
                int frame = Time.frameCount;
                if (frame == cacheFrame && pawn == cachePawn && map == cacheMap && eye == cacheEye && fwd == cacheFwd)
                {
                    return cache;
                }
                cacheFrame = frame;
                cachePawn = pawn;
                cacheMap = map;
                cacheEye = eye;
                cacheFwd = fwd;
                try
                {
                    cache = Ray(map, eye, fwd, FpTuning.Get.pickMaxCells, pawn);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] First person: finding what the crosshair points at failed: " + e, 93653001);
                    cache = default(FpHit);
                }
                return cache;
            }
        }

        // The first thing (or ground cell) a ray meets. eye and dir are true space (View3D: height in cells, z on the ground
        // rows); the walk stays on the floor `ignore` stands on (stacked floors, Floors) and goes up to maxCells over the
        // ground. Order (spec 8.1): within one cell a pawn wins, then an item, a plant, a building, anything else; a
        // full-height edifice (wall, rock, closed door) ends the walk, as the hit if the ray meets it, else with nothing;
        // so does the map edge, a fogged cell, and a ray that rises past everything. Never falls back to a far point.
        public static FpHit Ray(Map map, Vector3 eye, Vector3 dir, float maxCells, Pawn ignore, float pawnRadiusScale = 1f)
        {
            if (map == null || maxCells <= 0f || dir.sqrMagnitude < 1e-10f)
            {
                return default(FpHit);
            }
            Vector3 d = dir.normalized;

            // The floor worked on: true point (x, y, z) is map cell (floor x, floor z + zShift) at height y - floorH.
            bool floors = Floors.On && map == Find.CurrentMap;
            int k = 0;
            float floorH = 0f;
            int zShift = 0;
            if (floors)
            {
                if (ignore != null && ignore.Spawned && ignore.Map == map)
                {
                    k = Mathf.Max(0, Floors.FloorOfRow(ignore.Position.z));
                }
                Floors.FloorPlane(k, out floorH, out zShift);
            }

            float flat = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            float tEnd = maxCells / Mathf.Max(flat, 0.05f);   // maxCells over the ground (the pitch is clamped short of vertical)
            CollectPawns(map, eye, d, tEnd, ignore, floors, k, pawnRadiusScale);
            int pawnNext = 0;

            // A 2D walk (Amanatides-Woo) over the cells the ray crosses, in true x, z.
            int cx = Mathf.FloorToInt(eye.x);
            int cz = Mathf.FloorToInt(eye.z);
            int stepX = d.x > 0f ? 1 : d.x < 0f ? -1 : 0;
            int stepZ = d.z > 0f ? 1 : d.z < 0f ? -1 : 0;
            float tDeltaX = stepX != 0 ? 1f / Mathf.Abs(d.x) : float.MaxValue;
            float tDeltaZ = stepZ != 0 ? 1f / Mathf.Abs(d.z) : float.MaxValue;
            float tNextX = stepX > 0 ? (cx + 1 - eye.x) * tDeltaX : stepX < 0 ? (eye.x - cx) * tDeltaX : float.MaxValue;
            float tNextZ = stepZ > 0 ? (cz + 1 - eye.z) * tDeltaZ : stepZ < 0 ? (eye.z - cz) * tDeltaZ : float.MaxValue;
            float tIn = 0f;
            int maxSteps = Mathf.CeilToInt(maxCells) * 2 + 4;
            for (int step = 0; step < maxSteps; step++)
            {
                float tOut = Mathf.Min(tNextX, Mathf.Min(tNextZ, tEnd));
                IntVec3 c = new IntVec3(cx, 0, cz + zShift);
                // Past the map edge or out of this floor's rows (a straight ray never comes back), or into fog: nothing.
                if (!c.InBounds(map) || floors && Floors.FloorOfRow(c.z) != k || c.Fogged(map))
                {
                    return default(FpHit);
                }
                float yIn = eye.y + d.y * tIn - floorH;   // heights over this floor
                if (d.y >= 0f && yIn > MaxPickTop)
                {
                    return default(FpHit);
                }
                float yOut = eye.y + d.y * tOut - floorH;
                // The walking surface: a bridge deck or its ramp (ground rows only, as MpView.TruePoint), else the floor.
                float ground = zShift == 0 && Bridges.Active ? Bridges.HeightAt(map, cx + 0.5f, cz + 0.5f) : 0f;
                bool landed = d.y < 0f && yOut <= ground;
                if (landed)
                {
                    tOut = Mathf.Clamp(tIn + (ground - yIn) / d.y, tIn, tOut);
                    yOut = ground;
                }

                // A wall, rock or closed door: nothing behind it counts, and no pawn stands in it.
                Building edifice = c.GetEdifice(map);
                bool blocker = edifice != null && edifice.def.Fillage == FillCategory.Full && !(edifice is Building_Door door && door.Open);

                // Pawns first in a cell. Their drawn bodies (moving between cells, wider than one) are tested on their own
                // (CollectPawns); a pawn counts in the cell where the ray first touches it. Not in a blocker's cell: a
                // large pawn's pick radius (up to 0.72) reaches into the wall from the cell behind it, and would be picked
                // through the wall.
                while (pawnNext < pawnHits.Count && pawnHits[pawnNext].t < tIn)
                {
                    pawnNext++;
                }
                if (!blocker && pawnNext < pawnHits.Count && pawnHits[pawnNext].t <= tOut)
                {
                    PawnHit ph = pawnHits[pawnNext];
                    Vector3 dp = ph.pawn.DrawPos;
                    Vector3 at = new Vector3(dp.x, 0f, dp.z);
                    return Hit(ph.pawn, at.ToIntVec3(), at, ph.t, ignore);
                }

                // Then whatever stands in the cell where the ray passes at its height.
                float lo = Mathf.Min(yIn, yOut);
                float hi = Mathf.Max(yIn, yOut);
                Thing best = null;
                int bestRank = int.MaxValue;
                float bestBottom = 0f;
                float bestTop = 0f;
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (!Pickable(t))
                    {
                        continue;
                    }
                    // Grass, bushes and other wild undergrowth don't hide what is behind them: at a low first-person
                    // angle the ray crosses the grass of a cell or two before it reaches an item lying there. They are
                    // only the hit in the cell where the ray reaches the ground.
                    if (!landed && Undergrowth(t))
                    {
                        continue;
                    }
                    float b = Floors.ThingOffset(t).y - floorH;   // a bridge deck's lift (and the layout's own)
                    float bottom = b + Interaction.PickBottom(t);
                    float top = b + Mathf.Max(Interaction.PickHeight(t), MinThingTop);
                    if (blocker && t == edifice)
                    {
                        // A blocker stands at least wall height, as IsoRim draws it: a door is drawn every frame by the
                        // game (RealtimeOnly), so its form is flat and its pick height 0, and a ray at eye height would pass
                        // over a closed door and end on it with nothing.
                        top = Mathf.Max(top, b + View3D.WallHeight);
                    }
                    if (hi < bottom || lo > top)
                    {
                        continue;
                    }
                    int rank = Rank(t);
                    if (rank < bestRank)
                    {
                        best = t;
                        bestRank = rank;
                        bestBottom = bottom;
                        bestTop = top;
                    }
                }
                if (best != null)
                {
                    // The point where the ray meets it: onto its top going down, into its bottom going up, else where the
                    // ray enters the cell.
                    float tp = tIn;
                    if (d.y < 0f && yIn > bestTop)
                    {
                        tp = tIn + (bestTop - yIn) / d.y;
                    }
                    else if (d.y > 0f && yIn < bestBottom)
                    {
                        tp = tIn + (bestBottom - yIn) / d.y;
                    }
                    tp = Mathf.Clamp(tp, tIn, tOut);
                    return Hit(best, c, InCell(eye.x + d.x * tp, eye.z + d.z * tp + zShift, c), tp, ignore);
                }

                // A wall, rock or closed door the ray passes over (or under): nothing behind it counts.
                if (blocker)
                {
                    return default(FpHit);
                }
                if (landed)
                {
                    return Hit(null, c, InCell(eye.x + d.x * tOut, eye.z + d.z * tOut + zShift, c), tOut, ignore);
                }
                if (tOut >= tEnd)
                {
                    return default(FpHit);
                }
                if (tNextX < tNextZ)
                {
                    cx += stepX;
                    tIn = tNextX;
                    tNextX += tDeltaX;
                }
                else
                {
                    cz += stepZ;
                    tIn = tNextZ;
                    tNextZ += tDeltaZ;
                }
            }
            return default(FpHit);
        }

        // Whether pawn p can act on a target from where it stands (spec 8.1): within reachCells over the ground of the
        // target's cells (a pawn: one cell round where it is drawn), or within interactionCellReach of its interaction
        // cell (a workbench used from its interaction spot).
        public static bool InReach(Pawn p, Thing t, IntVec3 cell)
        {
            if (p == null || !p.Spawned)
            {
                return false;
            }
            FpTuningDef T = FpTuning.Get;
            float x0, x1, z0, z1;
            if (t is Pawn tp && tp.Spawned)
            {
                if (tp.Map != p.Map)
                {
                    return false;
                }
                Vector3 td = tp.DrawPos;
                x0 = td.x - 0.5f;
                x1 = td.x + 0.5f;
                z0 = td.z - 0.5f;
                z1 = td.z + 0.5f;
            }
            else if (t != null && t.Spawned)
            {
                if (t.Map != p.Map)
                {
                    return false;
                }
                CellRect r = t.OccupiedRect();
                x0 = r.minX;
                x1 = r.maxX + 1f;
                z0 = r.minZ;
                z1 = r.maxZ + 1f;
            }
            else if (cell.IsValid)
            {
                x0 = cell.x;
                x1 = cell.x + 1f;
                z0 = cell.z;
                z1 = cell.z + 1f;
            }
            else
            {
                return false;
            }
            Vector3 at = p.DrawPos;
            float ex = Mathf.Max(x0 - at.x, Mathf.Max(0f, at.x - x1));
            float ez = Mathf.Max(z0 - at.z, Mathf.Max(0f, at.z - z1));
            if (ex * ex + ez * ez <= T.reachCells * T.reachCells)
            {
                return true;
            }
            return t != null && !(t is Pawn) && t.Spawned && t.def.hasInteractionCell
                && p.Position.DistanceTo(t.InteractionCell) <= T.interactionCellReach;
        }

        // Port 3 (UIToMapPosition_Prefix): UI.MouseCell, UI.MouseMapPosition and everything built on them (float menus,
        // Multiplayer's MapMouseCell sync context) see the crosshair's point while the pointer is overridden, and the
        // pinned menu point while a first-person float menu is open. Only the pointer's own position is replaced; any other
        // screen point (view rect corners, labels) keeps IsoRim's projection. Never inside a tick or a command (simulation
        // code never reads the crosshair; Multiplayer's MouseCellPatch gives commands the synced cell).
        internal static bool PointerMapPos(Vector2 ui, ref Vector3 result)
        {
            if (MpBridge.Simulating || !FpInput.PointerOverride || ui != Verse.UI.MousePositionOnUI)
            {
                return false;
            }
            FpHits.Hit("Pointer is crosshair");
            if (FpActions.MenuOpen)
            {
                result = FpActions.MenuPoint;
                return true;
            }
            FpHit h = Current;
            result = h.Valid ? h.Point : OffMap;
            return true;
        }

        // Port 4 (ThingsUnderMouse_Postfix): the crosshair's thing (or the pinned menu's) first in the game's list of things
        // under the pointer, as the float-menu providers and the selector read it. True whenever the pointer is overridden,
        // so IsoRim's own picker, which reads the hidden cursor's position, is skipped. Interface only, as above.
        internal static bool ThingsUnderCrosshair(ref List<Thing> result, TargetingParameters parms)
        {
            if (MpBridge.Simulating || !FpInput.PointerOverride)
            {
                return false;
            }
            FpHits.Hit("Things under crosshair");
            // The game lists every pawn drawn within 0.8 cells of the point, so the played colonist itself whenever the
            // crosshair rests on something at its feet; the float menu then offers its self options (drop the weapon...)
            // as if it had been clicked, and the left button took "Drop" over the item aimed at. The crosshair never
            // points at the played colonist (spec 8.1).
            Pawn self = FpPlay.Pawn;
            if (self != null)
            {
                result.Remove(self);
            }
            Thing t = FpActions.MenuOpen ? FpActions.MenuThing : Current.Thing;
            if (t != null && t.Spawned && t.Map == Find.CurrentMap && (parms == null || parms.CanTarget(new TargetInfo(t))))
            {
                result.Remove(t);
                result.Insert(0, t);
            }
            return true;
        }

        // Port 16 (MpView.PointerMapPoint): the point the other players see as this player's cursor while the pointer is
        // overridden: the crosshair's, the pinned menu point while the menu is open, the colonist's own spot when the
        // crosshair shows nothing (rather than a corner of the map).
        internal static bool PointerMapPoint(out Vector3 p)
        {
            p = Vector3.zero;
            if (FpPlay.Mode != FpMode.Playing || !FpInput.PointerOverride)
            {
                return false;
            }
            if (FpActions.MenuOpen)
            {
                p = FpActions.MenuPoint;
                return true;
            }
            FpHit h = Current;
            if (h.Valid)
            {
                p = h.Point;
                return true;
            }
            Pawn pawn = FpPlay.Pawn;
            if (pawn != null && pawn.Spawned)
            {
                Vector3 dp = pawn.DrawPos;
                p = new Vector3(dp.x, 0f, dp.z);
                return true;
            }
            return false;
        }

        // ---- the walk's parts

        private static FpHit Hit(Thing t, IntVec3 cell, Vector3 point, float dist, Pawn actor)
        {
            return new FpHit
            {
                Valid = true,
                Thing = t,
                Cell = cell,
                Point = point,
                Dist = dist,
                InReach = InReach(actor, t, cell),
            };
        }

        // A map point (x, 0, z) kept inside cell c, so its cell (UI.MouseCell) is c.
        private static Vector3 InCell(float x, float z, IntVec3 c)
        {
            return new Vector3(Mathf.Clamp(x, c.x + PointMargin, c.x + 1f - PointMargin), 0f,
                Mathf.Clamp(z, c.z + PointMargin, c.z + 1f - PointMargin));
        }

        // Things the crosshair can stop on (pawns are tested on their own). Filth, motes and projectiles in flight are
        // never targets; another faction's blueprints are hidden in a session (Factions/Blueprints.cs, MpBridge).
        private static bool Pickable(Thing t)
        {
            if (t == null || t is Pawn || t.def == null || t is Filth || t is Mote)
            {
                return false;
            }
            ThingCategory cat = t.def.category;
            if (cat == ThingCategory.Projectile || cat == ThingCategory.Filth || cat == ThingCategory.Mote || cat == ThingCategory.Gas)
            {
                return false;
            }
            return !MpBridge.HidesBlueprint(t);
        }

        // Wild plants that are neither trees nor harvestable (grass, tall grass, bushes, dandelions).
        private static bool Undergrowth(Thing t)
        {
            return t is Plant && t.def.plant != null && !t.def.plant.IsTree && !t.def.plant.Harvestable;
        }

        // The order within one cell (spec 8.1): item, plant, building, anything else (pawns come before all of them).
        private static int Rank(Thing t)
        {
            switch (t.def.category)
            {
                case ThingCategory.Item:
                    return 0;
                case ThingCategory.Plant:
                    return 1;
                case ThingCategory.Building:
                    return 2;
                default:
                    return 3;
            }
        }

        // Every pawn the ray passes within its pick radius of (spec 8.1: tuning.pawnPickRadius x clamp(sqrt(body size),
        // 0.6, 1.6) x the caller's scale, round the vertical segment where the pawn is drawn, from its pick bottom to its
        // pick height), with where the ray first touches it, nearest first. Pawns on other floors, in fog, and pawns
        // invisible to the actor's faction (as GenUI.TargetsAt) are left out.
        private static void CollectPawns(Map map, Vector3 eye, Vector3 d, float tEnd, Pawn ignore, bool floors, int k, float scale)
        {
            pawnHits.Clear();
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            float baseRadius = FpTuning.Get.pawnPickRadius * scale;
            float flat2 = d.x * d.x + d.z * d.z;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p == ignore || p.Destroyed || !p.Spawned)
                {
                    continue;
                }
                float r = baseRadius * Mathf.Clamp(Mathf.Sqrt(p.BodySize), 0.6f, 1.6f);
                Vector3 off = Floors.ThingOffset(p);
                // Cheap test first: the ray's ground track against where the pawn is, with room for a seat's offset.
                Vector3 dp = p.DrawPos;
                if (TrackDistSq(eye, d, flat2, tEnd, dp.x + off.x, dp.z + off.z) > (r + 0.6f) * (r + 0.6f))
                {
                    continue;
                }
                if (floors && Floors.FloorOfRow(p.Position.z) != k || p.Position.Fogged(map))
                {
                    continue;
                }
                if (p.IsPsychologicallyInvisible() && (ignore == null || p.Faction != ignore.Faction))
                {
                    continue;
                }
                Vector3 spot = Poses.DrawnSpot(p, out float _);   // sitting: on the seat, as IsoRim draws them
                Segment s = new Segment
                {
                    x = spot.x + off.x,
                    z = spot.z + off.z,
                    bottom = off.y + Interaction.PickBottom(p),
                    top = off.y + Interaction.PickHeight(p),
                };
                float rr = r * r;
                float tBest = Closest(eye, d, s, tEnd);
                if (s.DistSq(eye, d, tBest) > rr)
                {
                    continue;
                }
                // The distance only falls up to tBest (it is convex along the ray), so bisect for the first touch.
                float t = 0f;
                if (s.DistSq(eye, d, 0f) > rr)
                {
                    float a = 0f;
                    float b = tBest;
                    for (int n = 0; n < PawnEntrySteps; n++)
                    {
                        float m = 0.5f * (a + b);
                        if (s.DistSq(eye, d, m) <= rr)
                        {
                            b = m;
                        }
                        else
                        {
                            a = m;
                        }
                    }
                    t = b;
                }
                pawnHits.Add(new PawnHit { pawn = p, t = t });
            }
            if (pawnHits.Count > 1)
            {
                pawnHits.Sort(NearestFirst);
            }
        }

        // Squared ground distance from (x, z) to the ray's ground track between t = 0 and tEnd.
        private static float TrackDistSq(Vector3 eye, Vector3 d, float flat2, float tEnd, float x, float z)
        {
            float t = flat2 > 1e-8f ? Mathf.Clamp(((x - eye.x) * d.x + (z - eye.z) * d.z) / flat2, 0f, tEnd) : 0f;
            float dx = eye.x + d.x * t - x;
            float dz = eye.z + d.z * t - z;
            return dx * dx + dz * dz;
        }

        // The point of the ray (0..tEnd) nearest a pawn's segment: golden-section search, the distance being convex in t.
        private static float Closest(Vector3 eye, Vector3 d, Segment s, float tEnd)
        {
            const float G = 0.618034f;
            float a = 0f;
            float b = tEnd;
            float m1 = b - G * (b - a);
            float m2 = a + G * (b - a);
            float f1 = s.DistSq(eye, d, m1);
            float f2 = s.DistSq(eye, d, m2);
            for (int n = 0; n < PawnSearchSteps; n++)
            {
                if (f1 <= f2)
                {
                    b = m2;
                    m2 = m1;
                    f2 = f1;
                    m1 = b - G * (b - a);
                    f1 = s.DistSq(eye, d, m1);
                }
                else
                {
                    a = m1;
                    m1 = m2;
                    f1 = f2;
                    m2 = a + G * (b - a);
                    f2 = s.DistSq(eye, d, m2);
                }
            }
            return 0.5f * (a + b);
        }

        // A pawn as the picker sees it: a vertical segment (true space) at its drawn spot.
        private struct Segment
        {
            public float x;
            public float z;
            public float bottom;
            public float top;

            // Squared distance from the ray's point at t to the segment.
            public float DistSq(Vector3 eye, Vector3 d, float t)
            {
                float dx = eye.x + d.x * t - x;
                float dz = eye.z + d.z * t - z;
                float y = eye.y + d.y * t;
                float dy = y < bottom ? bottom - y : y > top ? y - top : 0f;
                return dx * dx + dz * dz + dy * dy;
            }
        }
    }
}
