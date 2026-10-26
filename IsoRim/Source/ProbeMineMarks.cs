using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for the tint of rock marked for mining (MineMarks):
    //   minecolour=1                 logs the colours of the game's Designations/Mine and MineVein textures (in-engine sample)
    //   minemark=x0,z0,x1,z1[,vein]  the game's mine designator (or the vein designator) used on every cell of the rectangle it
    //                                accepts, as the player's drag (DesignateMultiCell)
    //   mineunmark=x0,z0,x1,z1       the game's cancel designator used on the rectangle (as the player's drag)
    //   minetint=s|off               the tint's strength (MineMarks.StrengthOverride; off: the default)
    //   minecheck=x,z                logs whether the block at (x,z) is marked and the colour it is drawn in
    //   mineoutline=0|1              the faint top-edge line of marked blocks off / on (MineMarks.OutlineOn)
    //   mineline=f                   how far the line goes toward white (MineMarks.LineLight)
    //   minemarktop=0|1              the game's mark on the block's top off (back on the ground, raised as an icon) / on
    public static partial class Probe
    {
        private static bool MineMarkKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "minecolour":
                    LogMarkColour(DesignationDefOf.Mine);
                    LogMarkColour(DesignationDefOf.MineVein);
                    return true;
                case "minemark":
                case "mineunmark":
                {
                    Designator d = key == "mineunmark" ? new Designator_Cancel() : a.Length > 4 && a[4] == "vein" ? (Designator)new Designator_MineVein() : new Designator_Mine();
                    List<IntVec3> cells = new List<IntVec3>();
                    foreach (IntVec3 c in CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3])))
                    {
                        if (c.InBounds(map) && d.CanDesignateCell(c).Accepted)
                        {
                            cells.Add(c);
                        }
                    }
                    int before = map.designationManager.AllDesignations.Count;
                    d.DesignateMultiCell(cells);
                    Log.Message("[IsoRim probe] " + key + " " + string.Join(",", a) + ": " + cells.Count + " cells accepted, designations " + before + " -> " + map.designationManager.AllDesignations.Count);
                    return true;
                }
                case "minetint":
                    MineMarks.StrengthOverride = a[0] == "off" ? -1f : F(a[0]);
                    MapSections.QueueAll(map, typeof(SectionLayer_IsoRimWalls), true);
                    Log.Message("[IsoRim probe] minetint " + MineMarks.Strength);
                    return true;
                case "minetintcol":
                    MineMarks.ColourOverride = a[0] == "off" ? (Color?)null : new Color(F(a[0]), F(a[1]), F(a[2]), 1f);
                    MapSections.QueueAll(map, typeof(SectionLayer_IsoRimWalls), true);
                    Log.Message("[IsoRim probe] minetintcol " + MineMarks.Colour);
                    return true;
                case "minedo":
                {
                    // the nearest colonist that can mine given the game's mine job on the (already marked) block, then the game
                    // runs that many ticks (real job, real ticks; the same pass, as the harness respawns pawns between passes)
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    Thing rock = c.GetEdifice(map);
                    Pawn p = null;
                    foreach (Pawn x in map.mapPawns.FreeColonistsSpawned)
                    {
                        if (!x.Downed && !x.WorkTypeIsDisabled(WorkTypeDefOf.Mining) && (p == null || x.Position.DistanceToSquared(c) < p.Position.DistanceToSquared(c)))
                        {
                            p = x;
                        }
                    }
                    if (rock == null || p == null)
                    {
                        Log.Warning("[IsoRim probe] minedo: no rock or no miner at " + c);
                        return true;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    int hp = rock.HitPoints;
                    bool ok = p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Mine, rock), JobTag.Misc);
                    int n = I(a[2]);
                    int ran = 0;
                    for (; ran < n && rock.Spawned; ran++)
                    {
                        Find.TickManager.DoSingleTick();
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    Log.Message("[IsoRim probe] minedo " + c + ": " + p.LabelShort + " job " + ok + ", hp " + hp + " -> " + (rock.Spawned ? rock.HitPoints.ToString() : "mined")
                        + " after " + ran + " ticks; designation left " + (map.designationManager.DesignationAt(c, DesignationDefOf.Mine) != null));
                    return true;
                }
                case "mineoutline":
                    MineMarks.OutlineOn = a[0] != "0";
                    MapSections.QueueAll(map, typeof(SectionLayer_IsoRimWalls), true);
                    Log.Message("[IsoRim probe] mineoutline " + MineMarks.OutlineOn);
                    return true;
                case "mineline":
                    MineMarks.LineLight = F(a[0]);
                    MapSections.QueueAll(map, typeof(SectionLayer_IsoRimWalls), true);
                    Log.Message("[IsoRim probe] mineline " + MineMarks.LineLight);
                    return true;
                case "minemarktop":
                    MineMarks.MarkOnTop = a[0] != "0";
                    Log.Message("[IsoRim probe] minemarktop " + MineMarks.MarkOnTop);
                    return true;
                case "minecheck":
                {
                    Building b = new IntVec3(I(a[0]), 0, I(a[1])).GetEdifice(map);
                    Log.Message("[IsoRim probe] minecheck " + a[0] + "," + a[1] + ": " + (b == null ? "no block" : b.def.defName + " marked=" + MineMarks.Marked(b) + " own=" + b.DrawColor + " drawn=" + MineMarks.Tint(b, b.DrawColor)));
                    return true;
                }
            }
            return false;
        }

        private static void LogMarkColour(DesignationDef def)
        {
            Material mat = def != null ? def.iconMat : null;
            Texture tex = mat != null ? mat.mainTexture : null;
            if (tex == null)
            {
                Log.Message("[IsoRim probe] minecolour " + (def != null ? def.defName : "null") + ": no texture");
                return;
            }
            Color32[] px = SpritePixels.GpuSample(tex, 64);
            try
            {
                // the texture as read, for a look (renders\minecolour\<def>.png in the save data folder)
                Texture2D img = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                img.SetPixels32(px);
                img.Apply();
                string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", "minecolour");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, def.defName + ".png"), img.EncodeToPNG());
                Object.Destroy(img);
            }
            catch (System.Exception e)
            {
                Log.Warning("[IsoRim probe] minecolour png: " + e.Message);
            }
            float r = 0f, g = 0f, b = 0f, w = 0f;
            float r2 = 0f, g2 = 0f, b2 = 0f;
            int n2 = 0;
            Dictionary<int, int> buckets = new Dictionary<int, int>();
            for (int i = 0; i < px.Length; i++)
            {
                float al = px[i].a / 255f;
                r += px[i].r * al;
                g += px[i].g * al;
                b += px[i].b * al;
                w += al;
                if (px[i].a >= 128)
                {
                    r2 += px[i].r;
                    g2 += px[i].g;
                    b2 += px[i].b;
                    n2++;
                    int key = (px[i].r / 32) * 100 + (px[i].g / 32) * 10 + px[i].b / 32;
                    int count;
                    buckets.TryGetValue(key, out count);
                    buckets[key] = count + 1;
                }
            }
            List<KeyValuePair<int, int>> top = new List<KeyValuePair<int, int>>(buckets);
            top.Sort((x, y) => y.Value.CompareTo(x.Value));
            string tops = "";
            for (int i = 0; i < top.Count && i < 6; i++)
            {
                tops += " rgb/32=" + top[i].Key.ToString("000") + "x" + top[i].Value;
            }
            Log.Message("[IsoRim probe] minecolour " + def.defName + " tex " + tex.name + " " + tex.width + "x" + tex.height + " matColour " + mat.color
                + " alpha-weighted mean " + (w > 0f ? new Color(r / w / 255f, g / w / 255f, b / w / 255f).ToString() : "-")
                + " coverage " + (w / px.Length).ToString("0.00")
                + " opaque mean " + (n2 > 0 ? new Color(r2 / n2 / 255f, g2 / n2 / 255f, b2 / n2 / 255f).ToString() : "-") + " n " + n2 + tops);
        }
    }
}
