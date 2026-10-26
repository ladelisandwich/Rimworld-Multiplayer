using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // PROTOTYPE harness keys for the cliff look (CliffLook.cs):
    //   cliff=0|1|2        the old rock look, the smooth one, the smooth one with face noise; the map is redrawn
    //   cliffperf=reps     every block's wall layer (and the floors' terrain) rebuilt reps times, timed and logged
    // A probe file whose first line is "#cliffscript" is a script instead of passes: one step per line, run in order,
    // each a frame or more after the one before (not tied to the harness's clock jumps). Steps:
    //   load=<save>        the harness is switched off and the save loaded; the next step waits for the map
    //   wait=N             N frames
    //   floor=k            floor k viewed (stacked floors; the floors above hidden)
    //   look=x,z,size      the camera on ground cell (x, z) at that size; look=tunnel,size on the tunnel's room
    //   shot=name          a screenshot renders/<harness tag>/<name>.png once the blocks in view are drawn
    //   findtunnel=storey,len  a straight tunnel of len cells on that storey into rock with rock above, ending in a
    //                      5x5 room, entered from a ledge of that storey (logged; dig= digs it)
    //   dig                the found tunnel and room mined out (as by a miner) and unfogged
    //   quit               done.txt written, the game closed
    //   anything else      as a pass line (turn=, pitch=, set=, cutptr=, cliff=, cliffperf= ...)
    public static partial class Probe
    {
        private static List<string> script;
        private static int scriptAt;
        private static int scriptWait;
        private static bool scriptLoading;
        private static Game scriptOldGame;
        private static string pendingShotName;
        private static int pendingShotFrames;
        private static IntVec3 tunnelStart = IntVec3.Invalid;
        private static IntVec3 tunnelDir;
        private static int tunnelLen;
        private static int tunnelStorey;

        private static bool IsCliffScript()
        {
            if (script != null)
            {
                return true;
            }
            string[] lines = File.ReadAllLines(FilePath);
            if (lines.Length == 0 || lines[0].Trim() != "#cliffscript")
            {
                return false;
            }
            script = new List<string>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length > 0 && !line.StartsWith("#"))
                {
                    script.Add(line);
                }
            }
            Log.Message("[IsoRim probe] cliff script, " + script.Count + " steps");
            return true;
        }

        // Every frame from Begin while a cliff script runs; true = the normal passes are skipped.
        private static bool CliffScriptTick()
        {
            if (!IsCliffScript())
            {
                return false;
            }
            if (scriptLoading)
            {
                if (Current.Game == scriptOldGame || LongEventHandler.ShouldWaitForEvent || Find.CurrentMap == null
                    || Find.CurrentMap.listerThings.AllThings.Count == 0)
                {
                    return true;
                }
                scriptLoading = false;
                scriptWait = 120;
                Log.Message("[IsoRim probe] cliff script: loaded " + Find.CurrentMap);
                return true;
            }
            if (Find.CurrentMap == null || LongEventHandler.ShouldWaitForEvent)
            {
                scriptWait = Math.Max(scriptWait, 120);   // not before the map is up, and a moment after
                return true;
            }
            if (PanStripTick())
            {
                return true;
            }
            if (pendingShotName != null)
            {
                if (--pendingShotFrames == 0)
                {
                    string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
                    Directory.CreateDirectory(dir);
                    ScreenCapture.CaptureScreenshot(Path.Combine(dir, pendingShotName + ".png"));
                    Log.Message("[IsoRim probe] shot " + pendingShotName + ": cliff mode " + WallBoxes.CliffMode + ", yaw " + View3D.Yaw.ToString("0") + ", pitch " + View3D.Pitch.ToString("0.0") + RegenNote());
                    pendingShotName = null;
                    scriptWait = 6;
                }
                return true;
            }
            if (scriptWait > 0)
            {
                scriptWait--;
                return true;
            }
            if (scriptAt >= script.Count)
            {
                return true;
            }
            string step = script[scriptAt++];
            try
            {
                CliffStep(step);
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim probe] cliff step " + step + " failed: " + e);
            }
            return true;
        }

        private static void CliffStep(string step)
        {
            int eq = step.IndexOf('=');
            string key = eq < 0 ? step : step.Substring(0, eq);
            string[] a = eq < 0 ? new string[0] : step.Substring(eq + 1).Split(',');
            Map map = Find.CurrentMap;
            switch (key)
            {
                case "load":
                {
                    System.Reflection.FieldInfo cap = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("Capture17.Main"), "Active");
                    if (cap != null)
                    {
                        cap.SetValue(null, false);
                    }
                    View3D.Active = true;
                    scriptOldGame = Current.Game;
                    scriptLoading = true;
                    Log.Message("[IsoRim probe] cliff script: loading " + a[0]);
                    GameDataSaveLoader.LoadGame(a[0]);
                    return;
                }
                case "nocap":
                {
                    System.Reflection.FieldInfo cap = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("Capture17.Main"), "Active");
                    if (cap != null)
                    {
                        cap.SetValue(null, false);
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    scriptWait = 30;
                    return;
                }
                case "digrect":
                {
                    int storey = a.Length > 4 ? I(a[4]) : 0;
                    int mined = 0;
                    foreach (IntVec3 g in CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3])))
                    {
                        IntVec3 c = storey == 0 ? g : Floors.Stacked(map, g, storey);
                        Mineable m = c.IsValid && c.InBounds(map) ? c.GetEdifice(map) as Mineable : null;
                        if (m != null)
                        {
                            Rand.PushState(Gen.HashCombineInt(c.x, c.z * 7919 + 17));   // fixed seed per cell: the same yield every run
                            try { m.DestroyMined(null); } finally { Rand.PopState(); }
                            mined++;
                        }
                        if (c.IsValid && c.InBounds(map))
                        {
                            map.fogGrid.Unfog(c);
                            foreach (IntVec3 n in GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)))
                            {
                                if (n.InBounds(map))
                                {
                                    map.fogGrid.Unfog(n);
                                }
                            }
                        }
                    }
                    Log.Message("[IsoRim probe] digrect " + string.Join(",", a) + ": " + mined + " mined");
                    scriptWait = 30;
                    return;
                }
                case "wait":
                    scriptWait = I(a[0]);
                    return;
                case "floor":
                {
                    int was = Api.Floors != null && map != null ? Api.Floors.ViewFloor(map, I(a[0])) : -1;
                    Log.Message("[IsoRim probe] floor " + a[0] + " viewed (was " + was + ")");
                    scriptWait = 10;
                    return;
                }
                case "look":
                {
                    Vector3 at = a[0] == "tunnel" && tunnelStart.IsValid
                        ? (tunnelStart + tunnelDir * (tunnelLen + 2)).ToVector3Shifted()
                        : a[0] == "diag" && diagAt.IsValid ? diagAt.ToVector3Shifted()
                        : a[0] == "seam" && seamAt.IsValid ? seamAt.ToVector3Shifted() + (a.Length >= 4 ? new Vector3(F(a[1]), 0f, F(a[2])) : Vector3.zero)
                        : new Vector3(F(a[0]) + 0.5f, 0f, F(a[1]) + 0.5f);
                    float size = F(a[a.Length - 1]);
                    if (a[0] == "tunnel" && a.Length >= 3)
                    {
                        at += new Vector3(F(a[1]), 0f, 0f);   // look=tunnel,dx,size: moved along x
                    }
                    Find.Selector.ClearSelection();
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(at.x, 0f, at.z), size);
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                    {
                        p.Drawer?.tweener?.ResetTweenedPosToRoot();
                    }
                    scriptWait = 4;
                    return;
                }
                case "shot":
                    foreach (Window w in new List<Window>(Find.WindowStack.Windows))
                    {
                        Find.WindowStack.TryRemove(w, doCloseSound: false);
                    }
                    pendingShotName = a[0];
                    pendingShotFrames = 25;
                    return;
                case "findtunnel":
                    FindTunnel(map, I(a[0]), I(a[1]));
                    return;
                case "dig":
                    DigTunnel(map);
                    scriptWait = 30;
                    return;
                case "panstrip":
                    StartPanStrip(F(a[0]), F(a[1]), I(a[2]), a[3]);
                    return;
                case "findore":
                    FindOre(map, I(a[0]), a.Length > 1 ? I(a[1]) : 0);
                    return;
                case "findcap":
                    FindCap(map, I(a[0]), a.Length > 1 ? I(a[1]) : 0);
                    return;
                case "unlockfog":
                    // as ProjectZ 9a9f72e: tall mountains no longer lock the fog lid; set=fogLid,N then decides
                    Api.UnlockFogLid();
                    map.mapDrawer.RegenerateEverythingNow();
                    Log.Message("[IsoRim probe] fog lid unlocked, lid " + View3D.FogLid);
                    return;
                case "holescan":
                    HoleScan(map, I(a[0]), a.Length > 1 ? I(a[1]) : 0);
                    return;
                case "findtop":
                    FindTop(map, I(a[0]), a.Length > 1 ? I(a[1]) : 0);
                    return;
                case "digrel":
                    // digrel=dx0,dz0,dx1,dz1,storey: digrect relative to the cell findseam/findtop found
                    CliffStep("digrect=" + (seamAt.x + I(a[0])) + "," + (seamAt.z + I(a[1])) + "," + (seamAt.x + I(a[2])) + "," + (seamAt.z + I(a[3])) + "," + a[4]);
                    return;
                case "findseam":
                    FindSeam(map, I(a[0]), a.Length > 1 ? I(a[1]) : 0);
                    return;
                case "finddiag":
                    FindDiagonal(map, I(a[0]), I(a[1]), a.Length > 2 ? I(a[2]) : 0);
                    return;
                case "quit":
                {
                    string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "done.txt"), DateTime.Now.ToString("o") + " cliff script done");
                    Log.Message("[IsoRim probe] cliff script done, quitting");
                    Root.Shutdown();
                    return;
                }
                default:
                    Apply(step);
                    scriptWait = 20;
                    return;
            }
        }

        // cliff=, cliffperf= (from Scene's default branch).
        private static bool CliffKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "cliff":
                    WallBoxes.CliffMode = Mathf.Clamp(I(a[0]), 0, 2);
                    map.mapDrawer.RegenerateEverythingNow();
                    Log.Message("[IsoRim probe] cliff mode " + WallBoxes.CliffMode);
                    return true;
                case "cliffperf":
                    CliffPerf(map, Math.Max(1, I(a[0])));
                    return true;
            }
            return RockLookKey(key, a, map);
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<MapDrawer, Section[,]> cliffSections = HarmonyLib.AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        // Rebuilds every block's wall layer, and the terrain of the floors above the ground, reps times; logs the totals.
        private static void CliffPerf(Map map, int reps)
        {
            Section[,] sections = cliffSections(map.mapDrawer);
            if (sections == null)
            {
                return;
            }
            List<SectionLayer> walls = new List<SectionLayer>();
            List<SectionLayer> terrain = new List<SectionLayer>();
            foreach (Section s in sections)
            {
                SectionLayer w = s.GetLayer(typeof(SectionLayer_IsoRimWalls));
                if (w != null)
                {
                    walls.Add(w);
                }
                SectionLayer t = s.GetLayer(typeof(SectionLayer_Terrain));
                float h;
                int z;
                if (t != null && Floors.ColumnFloor(map, s.botLeft, out h, out z) >= 1)
                {
                    terrain.Add(t);
                }
            }
            int rock = 0;
            foreach (Thing th in map.listerThings.AllThings)
            {
                if (th.def.building != null && th.def.building.isNaturalRock)
                {
                    rock++;
                }
            }
            // a warm-up pass, then the timed ones
            foreach (SectionLayer l in walls)
            {
                l.Regenerate();
            }
            double wallMs = 0, terrMs = 0, worst = 0;
            for (int r = 0; r < reps; r++)
            {
                foreach (SectionLayer l in walls)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    l.Regenerate();
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    wallMs += ms;
                    worst = Math.Max(worst, ms);
                }
                foreach (SectionLayer l in terrain)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    l.Regenerate();
                    terrMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                }
            }
            Log.Message("[IsoRim probe] CLIFFPERF mode " + WallBoxes.CliffMode + ": map " + map.Size + ", natural rock " + rock + ", wall layers " + walls.Count
                + ": all " + (wallMs / reps).ToString("0.0") + " ms per rebuild of the map, " + (wallMs / reps / Math.Max(1, walls.Count)).ToString("0.000")
                + " ms per block, slowest block " + worst.ToString("0.00") + " ms; upper-floor terrain layers " + terrain.Count + ": "
                + (terrMs / reps).ToString("0.0") + " ms per rebuild (" + reps + " reps)");
        }

        // A tunnel on storey s: entrance cell open with a floor (a ledge), then len cells of rock with rock on the storey
        // above, three cells of such rock on each side, and a 5x5 room of it at the end (one cell of margin).
        private static void FindTunnel(Map map, int storey, int len)
        {
            tunnelStart = IntVec3.Invalid;
            int rows = Floors.GroundRowsOf(map);
            if (rows <= 0)
            {
                Log.Warning("[IsoRim probe] findtunnel: no floors");
                return;
            }
            Func<IntVec3, int, bool> rockAt = (g, k) =>
            {
                if (g.x < 1 || g.z < 1 || g.x >= map.Size.x - 1 || g.z >= rows - 1)
                {
                    return false;
                }
                IntVec3 c = k == 0 ? g : Floors.Stacked(map, g, k);
                if (!c.IsValid || !c.InBounds(map))
                {
                    return false;
                }
                Building b = c.GetEdifice(map);
                return b != null && b.def.building != null && b.def.building.isNaturalRock && !b.def.building.isResourceRock;
            };
            Func<IntVec3, bool> deep = g => rockAt(g, storey) && rockAt(g, storey + 1);
            IntVec3 centre = new IntVec3(map.Size.x / 2, 0, rows / 2);
            float best = float.MaxValue;
            for (int x = 6; x < map.Size.x - 6; x++)
            {
                for (int z = 6; z < rows - 6; z++)
                {
                    IntVec3 e = new IntVec3(x, 0, z);
                    IntVec3 eu = Floors.Stacked(map, e, storey);
                    if (!eu.IsValid || !eu.InBounds(map) || eu.GetEdifice(map) != null || !Floors.HasFloor(map, eu))
                    {
                        continue;
                    }
                    foreach (IntVec3 d in GenAdj.CardinalDirections)
                    {
                        IntVec3 side = new IntVec3(d.z, 0, -d.x);
                        bool ok = true;
                        for (int i = 1; i <= len + 5 && ok; i++)
                        {
                            int w = i <= len ? 3 : 3;
                            for (int j = -w; j <= w && ok; j++)
                            {
                                ok = deep(e + d * i + side * j);
                            }
                        }
                        if (!ok)
                        {
                            continue;
                        }
                        float dist = (e - centre).LengthHorizontalSquared;
                        if (dist < best)
                        {
                            best = dist;
                            tunnelStart = e;
                            tunnelDir = d;
                        }
                    }
                }
            }
            tunnelLen = len;
            tunnelStorey = storey;
            Log.Message("[IsoRim probe] findtunnel storey " + storey + " len " + len + ": " + (tunnelStart.IsValid ? "entrance " + tunnelStart + " dir " + tunnelDir
                + ", room centre " + (tunnelStart + tunnelDir * (len + 2)) : "none"));
        }

        private static IntVec3 diagAt = IntVec3.Invalid;

        // finddiag=storey,len[,skip]: the middle of a staircase outline (a diagonal run of rock) at least len steps long on
        // that storey, facing south-ish (toward the camera); the skip-th one found (by distance from the map's centre).
        private static void FindDiagonal(Map map, int storey, int len, int skip)
        {
            diagAt = IntVec3.Invalid;
            int rows = Floors.GroundRowsOf(map);
            int zMax = rows > 0 ? rows : map.Size.z;
            Func<int, int, bool> rock = (x, z) =>
            {
                if (x < 1 || z < 1 || x >= map.Size.x - 1 || z >= zMax - 1)
                {
                    return false;
                }
                IntVec3 g = new IntVec3(x, 0, z);
                IntVec3 c = storey == 0 ? g : Floors.Stacked(map, g, storey);
                Building b = c.IsValid && c.InBounds(map) ? c.GetEdifice(map) : null;
                return b != null && b.def.building != null && b.def.building.isNaturalRock;
            };
            List<KeyValuePair<float, IntVec3>> found = new List<KeyValuePair<float, IntVec3>>();
            IntVec3 centre = new IntVec3(map.Size.x / 2, 0, zMax / 2);
            for (int x = 8; x < map.Size.x - 8; x++)
            {
                for (int z = 8; z < zMax - 8; z++)
                {
                    for (int sx = -1; sx <= 1; sx += 2)
                    {
                        // steps going (sx, +1): each rock cell open to the south and to the sx side, rock behind
                        int n = 0;
                        while (n < len + 2)
                        {
                            int cx = x + sx * n, cz = z + n;
                            if (!rock(cx, cz) || rock(cx, cz - 1) || rock(cx + sx, cz) || !rock(cx - sx, cz) || !rock(cx, cz + 1))
                            {
                                break;
                            }
                            n++;
                        }
                        if (n >= len)
                        {
                            found.Add(new KeyValuePair<float, IntVec3>((new IntVec3(x, 0, z) - centre).LengthHorizontalSquared, new IntVec3(x + sx * n / 2, 0, z + n / 2)));
                        }
                    }
                }
            }
            found.Sort((p, q) => p.Key.CompareTo(q.Key));
            if (found.Count > skip)
            {
                diagAt = found[skip].Value;
            }
            Log.Message("[IsoRim probe] finddiag storey " + storey + " len " + len + ": " + found.Count + " found, using " + diagAt);
        }

        private static void DigTunnel(Map map)
        {
            if (!tunnelStart.IsValid)
            {
                Log.Warning("[IsoRim probe] dig: no tunnel");
                return;
            }
            IntVec3 side = new IntVec3(tunnelDir.z, 0, -tunnelDir.x);
            List<IntVec3> cells = new List<IntVec3>();
            for (int i = 2; i <= tunnelLen; i++)   // the first cell stays: a closed room (the cut-away looks for proper rooms)
            {
                cells.Add(tunnelStart + tunnelDir * i);
            }
            for (int i = tunnelLen + 1; i <= tunnelLen + 4; i++)
            {
                for (int j = -2; j <= 2; j++)
                {
                    cells.Add(tunnelStart + tunnelDir * i + side * j);
                }
            }
            int mined = 0;
            foreach (IntVec3 g in cells)
            {
                IntVec3 c = Floors.Stacked(map, g, tunnelStorey);
                Mineable m = c.GetEdifice(map) as Mineable;
                if (m != null)
                {
                    Rand.PushState(Gen.HashCombineInt(c.x, c.z * 7919 + 17));   // fixed seed per cell: the same yield every run
                    try { m.DestroyMined(null); } finally { Rand.PopState(); }
                    mined++;
                }
            }
            foreach (IntVec3 g in cells)
            {
                IntVec3 c = Floors.Stacked(map, g, tunnelStorey);
                foreach (IntVec3 n in GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)))
                {
                    if (n.InBounds(map))
                    {
                        map.fogGrid.Unfog(n);
                    }
                }
                map.fogGrid.Unfog(c);
            }
            Log.Message("[IsoRim probe] dig: " + mined + " rock cells mined on storey " + tunnelStorey + " (" + cells.Count + " cells), roofed "
                + Floors.Stacked(map, tunnelStart + tunnelDir * (tunnelLen + 2), tunnelStorey).Roofed(map));
        }
    }
}
