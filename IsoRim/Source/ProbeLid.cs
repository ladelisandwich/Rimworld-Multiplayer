using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness key for the per-storey fog lid (cliff script):
    //   lidpts=n,tag   logs up to n screen points of each kind in the current view, for pixel checks of the shot taken
    //                  next (lid on against lid off, before against after):
    //                    top   an open (unfogged) floor surface nothing shown stands on (the ground, or an upper floor);
    //                    fog   a fogged cell nothing shown covers: where the lid lies (at its storey's top + 0.012);
    //                    face  the middle of an open rock block's south face (open air or open ground south of it).
    //                  Only points the eye sees: the line to the eye passes through no shown block, floor or lid.
    //                  Lines "[IsoRim lidpts] tag kind px py x z storey" (px, py: image pixels from the top left).
    public static partial class Probe
    {
        private static void LidPoints(Map map, int n, string tag)
        {
            int rows = Floors.GroundRowsOf(map);
            int top = 0;
            for (int k = 1; k <= 16; k++)
            {
                float h0;
                int z0;
                Floors.FloorPlane(k, out h0, out z0);
                if (z0 <= 0 || !Floors.Shown(k))
                {
                    break;
                }
                top = k;
            }
            CellRect view = View3D.GroundRect().ClipInsideRect(new CellRect(0, 0, map.Size.x, rows > 0 ? rows : map.Size.z));
            int[] count = new int[3];
            string[] kinds = { "top", "fog", "face" };
            int tried = 0;
            // a fixed spread over the view (no randomness): every 3rd cell in both directions, storeys top-down
            for (int k = top; k >= 0; k--)
            {
                float hk;
                int zk;
                Floors.FloorPlane(k, out hk, out zk);
                for (int gz = view.minZ + 1; gz < view.maxZ; gz += 3)
                {
                    for (int gx = view.minX + 1; gx < view.maxX; gx += 3)
                    {
                        IntVec3 c = new IntVec3(gx, 0, gz + zk);
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        bool covered = k < top && Floors.HasFloor(map, Floors.Stacked(map, c, 1));
                        if (covered && rows > 0)
                        {
                            continue;
                        }
                        bool fogged = map.fogGrid.IsFogged(c);
                        float block = IsoCellMap.TopOf(map, c);
                        Building b = c.GetEdifice(map);
                        int kind = -1;
                        Vector3 p = Vector3.zero;
                        if (fogged)
                        {
                            kind = 1;
                            p = new Vector3(gx + 0.5f, hk + block + 0.012f, gz + 0.5f);
                        }
                        else if (b == null && Floors.HasFloor(map, c))
                        {
                            kind = 0;
                            p = new Vector3(gx + 0.5f, hk, gz + 0.5f);
                        }
                        else if (b != null && b.def.building != null && b.def.building.isNaturalRock && block > 0.2f)
                        {
                            IntVec3 s = c + IntVec3.South;
                            if (s.InBounds(map) && s.GetEdifice(map) == null && !map.fogGrid.IsFogged(s) && Floors.ColumnFloor(map, s, out float hs, out int zs) == Floors.ColumnFloor(map, c, out float hc, out int zc))
                            {
                                kind = 2;
                                p = new Vector3(gx + 0.5f, hk + block * 0.5f, gz - 0.03f);
                            }
                        }
                        if (kind < 0 || count[kind] >= n)
                        {
                            continue;
                        }
                        tried++;
                        if (!LidSeen(map, p, top))
                        {
                            continue;
                        }
                        Vector2 px = View3D.WorldToScreen(p);
                        if (px.x < 4 || px.y < 4 || px.x > Screen.width - 4 || px.y > Screen.height - 4)
                        {
                            continue;
                        }
                        count[kind]++;
                        Log.Message("[IsoRim lidpts] " + tag + " " + kinds[kind] + " " + Mathf.RoundToInt(px.x) + " " + Mathf.RoundToInt(Screen.height - px.y) + " " + gx + " " + gz + " " + k);
                    }
                }
            }
            Log.Message("[IsoRim lidpts] " + tag + " done: top " + count[0] + ", fog " + count[1] + ", face " + count[2] + " (" + tried + " tried), screen " + Screen.width + "x" + Screen.height + ", storeys shown 0.." + top);
        }

        // The line from p (map x, true height, ground row z) to the eye passes through nothing solid that is drawn: a
        // block, a floor (its slab) or a fog lid of a shown storey. Marched in 0.1-cell steps, the first 0.25 cells left
        // out (the surface p lies on).
        private static bool LidSeen(Map map, Vector3 p, int top)
        {
            Vector3 eye = View3D.Eye;
            // (the eye in true heights, as WorldToScreen and ScreenToHeight use it)
            Vector3 e = eye;
            Vector3 d = e - p;
            float len = d.magnitude;
            if (len < 0.01f)
            {
                return true;
            }
            d /= len;
            float hTop, zTop;
            int zt;
            Floors.FloorPlane(top, out hTop, out zt);
            float maxH = hTop + 2.5f;
            int rows = Floors.GroundRowsOf(map);
            for (float t = 0.25f; t < len; t += 0.1f)
            {
                Vector3 q = p + d * t;
                if (q.y > maxH)
                {
                    return true;
                }
                int x = Mathf.FloorToInt(q.x), gz = Mathf.FloorToInt(q.z);
                if (x < 0 || gz < 0 || x >= map.Size.x || gz >= (rows > 0 ? rows : map.Size.z))
                {
                    continue;
                }
                for (int k = 0; k <= top; k++)
                {
                    float hk;
                    int zk;
                    Floors.FloorPlane(k, out hk, out zk);
                    if (q.y < hk - 0.06f)
                    {
                        break;
                    }
                    IntVec3 c = new IntVec3(x, 0, gz + zk);
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    float block = IsoCellMap.TopOf(map, c);
                    bool floor = k == 0 || Floors.HasFloor(map, c);
                    float solidTop = hk + block + (map.fogGrid.IsFogged(c) ? 0.03f : 0f);
                    if (floor && q.y >= hk - 0.06f && q.y <= Mathf.Max(solidTop, hk + 0.005f))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
