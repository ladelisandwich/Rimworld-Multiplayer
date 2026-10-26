using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only test of Api version 8 (figure hooks and model drawing), with a sample consumer that uses only the Api:
    // a red cube on each person's head, a blue cylinder on the chest, a yellow square of its own mesh on the back, a green
    // cube and a cylinder drawn as models, a corpse's figure on a table. Keys (each fixture is spawned
    // fresh, from fixed seeds; the previous one is removed first; the camera is put over it):
    //   api8=1|0             the sample consumer's figure hook on/off (Api.AddFigureHook / RemoveFigureHook)
    //   api8hide=1|0         the sample leaves out its head cube under headgear over the whole head (and with no head)
    //   api8fix=stand,x,z    seven drafted colonists in a row from x: facing N, E, S, W (plain), marine helmet, cowboy hat, burka
    //   api8fix=walk,x,z     a colonist at x,z walking 7 cells east (a real Goto job), one game tick per frame for 140 frames
    //   api8fix=lie,x,z      a downed colonist (anaesthetic) at x,z and a dead one at x+2,z
    //   api8model=x,z        Api.DrawModel each frame: a green stone cube at x,z (scale overload), a cylinder at x+1,z
    //                        (matrix overload) in the table's sprite colour; api8model=off stops
    //   api8figure=x,z       Api.DrawFigure each frame: the lie fixture's corpse on the table at x,z (no shadow), fitted
    //                        with Api.FigureBounds; api8figure=off stops
    //   api8check=label,N    after N frames: per fixture pawn, what the hook saw, with PASS/FAIL lines
    //   api8alloc=W,N        bytes allocated inside each People.Draw over N frames after W warm-up frames
    //                        (GC.GetAllocatedBytesForCurrentThread; also how many draws allocated nothing)
    //   api8neat=x,z         a dead colonist whose corpse is put on the cell (a Neat Storage shelf, drawn by that mod)
    //   api8sprite=1         logs the opaque extent of the game's body and head sprites (south), for FigureBoxes
    public static partial class Probe
    {
        private class Api8Rec
        {
            public string role;
            public Pawn pawn;
            public int calls, framesSeen, lastFrame = -1, headAttached, bodyAttached, moved;
            public int facingOk, facingChecked, moving, movingOk;
            public float maxDist, minHeadHeight = float.MaxValue, minChestDot = float.MaxValue, maxHeadUpY;
            public bool lying, headHidden, faceHidden, fullHead, hairShown, headgear, dead;
            public Vector3 lastPos;
            public bool hasLast;
        }

        private static bool api8Patched, api8On, api8Hide, api8Drawing;
        private static ModelHandle api8Cube, api8Cyl;
        private static Material api8Back;
        private static readonly List<Thing> api8Spawned = new List<Thing>();
        private static readonly Dictionary<int, Api8Rec> api8Recs = new Dictionary<int, Api8Rec>();
        private static int api8TickFrames, api8CheckFrames = -1, api8AllocFrames = -1, api8Frames;
        private static string api8CheckLabel;
        private static IntVec3 api8ModelCell = IntVec3.Invalid, api8FigureCell = IntVec3.Invalid;
        private static Corpse api8Corpse;
        private static int api8ModelCalls, api8FigureTrue, api8FigureFalse, api8HookCalls, api8ScopeCalls;
        private static long api8AllocBytes, api8AllocDraws, api8AllocZero, api8AllocMax;
        private static int api8AllocWarm, api8AllocAfter;
        private static readonly FigureHook api8Hook = Api8Sample;

        private static bool Api8Key(string key, string[] a, Map map)
        {
            if (!key.StartsWith("api8"))
            {
                return false;
            }
            Api8Patch();
            switch (key)
            {
                case "api8":
                    api8On = a[0] == "1";
                    if (api8On)
                    {
                        Api.AddFigureHook(api8Hook);
                    }
                    else
                    {
                        Api.RemoveFigureHook(api8Hook);
                    }
                    Log.Message("[IsoRim probe] api8 hook " + (api8On ? "on" : "off") + ", Api.Version " + Api.Version);
                    return true;
                case "api8hide":
                    api8Hide = a[0] == "1";
                    return true;
                case "api8fix":
                    Api8Fixture(a[0], new IntVec3(I(a[1]), 0, I(a[2])), map);
                    return true;
                case "api8model":
                    api8ModelCell = a[0] == "off" ? IntVec3.Invalid : new IntVec3(I(a[0]), 0, I(a[1]));
                    return true;
                case "api8figure":
                    api8FigureCell = a[0] == "off" ? IntVec3.Invalid : new IntVec3(I(a[0]), 0, I(a[1]));
                    return true;
                case "api8check":
                    api8CheckLabel = a[0];
                    api8CheckFrames = I(a[1]);
                    Api8Reset();
                    return true;
                case "api8alloc":
                    // api8alloc=warm,N: N frames measured after warm frames (the fixture's figures built first)
                    api8AllocWarm = I(a[0]);
                    api8AllocFrames = -1;
                    api8AllocAfter = I(a[1]);
                    return true;
                case "api8sprite":
                    Api8Sprites();
                    return true;
                case "api8neat":
                {
                    // a dead colonist (fixed seed) whose corpse is put on the cell (a Neat Storage shelf): drawn by that mod
                    IntVec3 nc = new IntVec3(I(a[0]), 0, I(a[1]));
                    Pawn np = Api8Pawn("neat corpse", nc + new IntVec3(0, 0, -2), Rot4.South, 8301, map);
                    np.drafter.Drafted = false;
                    np.Kill(null);
                    Corpse corpse = np.Corpse;
                    api8Recs.Remove(np.thingIDNumber);
                    if (corpse != null)
                    {
                        corpse.DeSpawn();
                        GenSpawn.Spawn(corpse, nc, map);
                    }
                    Log.Message("[IsoRim probe] api8neat corpse at " + (corpse != null ? corpse.Position.ToString() : "-") + " holder " + (corpse != null && corpse.ParentHolder != null ? corpse.ParentHolder.ToString() : "-")
                        + ", building " + (nc.GetFirstBuilding(map) != null ? nc.GetFirstBuilding(map).def.defName : "none"));
                    return true;
                }
            }
            return false;
        }

        private static void Api8Patch()
        {
            if (api8Patched)
            {
                return;
            }
            api8Patched = true;
            ModContentPack me = LoadedModManager.RunningModsListForReading.FirstOrDefault(m => m.assemblies.loadedAssemblies.Contains(typeof(Api).Assembly));
            api8Cube = Api.LoadModel(me, "Models/Dev/api8_cube.obj");
            api8Cyl = Api.LoadModel(me, "Models/Dev/api8_cylinder.obj");
            Log.Message("[IsoRim probe] api8 models: cube " + (api8Cube != null ? api8Cube.Min + ".." + api8Cube.Max : "MISSING") + ", cylinder " + (api8Cyl != null ? "ok" : "MISSING")
                + ", same handle again " + (Api.LoadModel(me, "Models/Dev/api8_cube.obj") == api8Cube) + ", by calling assembly " + (Api.LoadModel("Models/Dev/api8_cube.obj") == api8Cube));
            api8Back = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Cutout, new Color(1f, 0.85f, 0.1f));
            Harmony h = new Harmony("peren.isorim.probe.api8");
            h.Patch(AccessTools.Method(typeof(MapComponentUtility), nameof(MapComponentUtility.MapComponentUpdate)), postfix: new HarmonyMethod(typeof(Probe), nameof(Api8Update)));
            h.Patch(AccessTools.Method(typeof(People), nameof(People.Draw)), prefix: new HarmonyMethod(typeof(Probe), nameof(Api8DrawPrefix)), postfix: new HarmonyMethod(typeof(Probe), nameof(Api8DrawPostfix)));
            h.Patch(AccessTools.Method(typeof(DynamicModels), nameof(DynamicModels.Draw)), prefix: new HarmonyMethod(typeof(Probe), nameof(Api8CountPrefix)));
            h.Patch(AccessTools.Method(typeof(DynamicModels), nameof(DynamicModels.DrawLocal)), prefix: new HarmonyMethod(typeof(Probe), nameof(Api8CountPrefix)));
        }

        public static void Api8CountPrefix()
        {
            if (api8Drawing)
            {
                api8ModelCalls++;
            }
        }

        public static void Api8DrawPrefix(out long __state)
        {
            __state = api8AllocFrames > 0 ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        }

        public static void Api8DrawPostfix(long __state)
        {
            if (api8AllocFrames > 0)
            {
                long b = GC.GetAllocatedBytesForCurrentThread() - __state;
                api8AllocBytes += b;
                api8AllocDraws++;
                if (b == 0)
                {
                    api8AllocZero++;
                }
                api8AllocMax = Math.Max(api8AllocMax, b);
            }
        }

        private static void Api8Reset()
        {
            api8Frames = 0;
            api8ModelCalls = api8FigureTrue = api8FigureFalse = api8HookCalls = api8ScopeCalls = 0;
            foreach (Api8Rec r in api8Recs.Values)
            {
                Pawn p = r.pawn;
                string role = r.role;
                r.calls = r.framesSeen = r.headAttached = r.bodyAttached = r.moved = r.facingOk = r.facingChecked = r.moving = r.movingOk = 0;
                r.lastFrame = -1;
                r.maxDist = 0f;
                r.minHeadHeight = r.minChestDot = float.MaxValue;
                r.maxHeadUpY = 0f;
                r.hasLast = false;
                r.pawn = p;
                r.role = role;
            }
        }

        // ---- the sample consumer (Api only)

        private static void Api8Sample(FigureFrame f)
        {
            api8HookCalls++;
            bool hideHead = api8Hide && (f.FullHeadCovered || f.HeadHidden);
            Vector3 top = f.HeadCentre + new Vector3(0f, f.HeadSize.y * 0.5f + 0.07f, 0f);
            Matrix4x4 cubeLocal = Matrix4x4.TRS(top, Quaternion.identity, Vector3.one * 0.12f);
            Vector3 chest = f.BodyCentre + new Vector3(0f, f.BodySize.y * 0.2f, f.BodySize.z * 0.5f + 0.04f);
            Matrix4x4 cylLocal = Matrix4x4.TRS(chest, Quaternion.AngleAxis(90f, Vector3.right), new Vector3(0.12f, 0.06f, 0.12f));
            if (!hideHead)
            {
                f.AttachToHead(api8Cube, cubeLocal, new Color(0.9f, 0.15f, 0.1f));
            }
            if (f.BodyShown)
            {
                f.AttachToBody(api8Cyl, cylLocal, new Color(0.15f, 0.3f, 0.95f));
                // a mesh of the mod's own (a yellow square on the back), its own material
                Vector3 back = f.BodyCentre + new Vector3(0f, f.BodySize.y * 0.2f, -f.BodySize.z * 0.5f - 0.02f);
                f.AttachMeshToBody(MeshPool.plane10, api8Back, Matrix4x4.TRS(back, Quaternion.AngleAxis(-90f, Vector3.right), Vector3.one * 0.16f));
            }

            // ---- the test's bookkeeping (not part of the sample)
            Api8Rec r;
            if (f.Pawn == null || !api8Recs.TryGetValue(f.Pawn.thingIDNumber, out r))
            {
                return;
            }
            if (FigureScope.Active)
            {
                r.moved++;   // drawn by Api.DrawFigure: checked by api8figure's own lines
                return;
            }
            float s = Api.HeightSquash;
            Vector3 cubeW = f.Head.MultiplyPoint3x4(top);
            cubeW.y *= s;
            Vector3 chestW = f.Body.MultiplyPoint3x4(chest);
            chestW.y *= s;
            Vector3 bodyW = f.Body.MultiplyPoint3x4(new Vector3(0f, f.BodyCentre.y + f.BodySize.y * 0.2f, 0f));
            bodyW.y *= s;
            Vector3 up = f.Head.MultiplyVector(Vector3.up);
            up.y *= s;
            up.Normalize();
            Vector3 dp = f.Pawn.Spawned ? f.Pawn.DrawPos : (f.Pawn.Corpse != null ? f.Pawn.Corpse.DrawPos : cubeW);
            r.calls++;
            if (r.lastFrame != Time.frameCount)
            {
                r.framesSeen++;
                r.lastFrame = Time.frameCount;
            }
            if (!hideHead)
            {
                r.headAttached++;
            }
            if (f.BodyShown)
            {
                r.bodyAttached++;
            }
            r.lying = f.Lying;
            r.dead = f.Dead;
            r.headHidden = f.HeadHidden;
            r.faceHidden = f.FaceHidden;
            r.fullHead = f.FullHeadCovered;
            r.hairShown = f.HairShown;
            r.headgear = f.HeadgearShown;
            r.maxDist = Mathf.Max(r.maxDist, new Vector2(cubeW.x - dp.x, cubeW.z - dp.z).magnitude);
            r.minHeadHeight = Mathf.Min(r.minHeadHeight, cubeW.y);
            r.maxHeadUpY = Mathf.Max(r.maxHeadUpY, Mathf.Abs(up.y));
            Vector3 fwd = Quaternion.AngleAxis(f.Yaw, Vector3.up) * Vector3.forward;
            Vector3 chestDir = new Vector3(chestW.x - bodyW.x, 0f, chestW.z - bodyW.z).normalized;
            r.minChestDot = Mathf.Min(r.minChestDot, Vector3.Dot(chestDir, fwd));
            if (!f.Lying)
            {
                r.facingChecked++;
                if (f.Facing == f.Pawn.Rotation)
                {
                    r.facingOk++;
                }
            }
            if (r.hasLast)
            {
                Vector3 step = new Vector3(dp.x - r.lastPos.x, 0f, dp.z - r.lastPos.z);
                if (step.magnitude > 0.005f)
                {
                    r.moving++;
                    if (Vector3.Dot(step.normalized, fwd) > 0.7f)
                    {
                        r.movingOk++;
                    }
                }
            }
            r.lastPos = dp;
            r.hasLast = true;
        }

        // Part 2 of the sample: models and a figure drawn each frame, from MapComponentUpdate as another mod would.
        public static void Api8Update(Map map)
        {
            if (map != Find.CurrentMap)
            {
                return;
            }
            if (api8TickFrames > 0)
            {
                api8TickFrames--;
                Find.TickManager.DoSingleTick();
                foreach (Thing t in api8Spawned)
                {
                    Pawn p = t as Pawn;
                    if (p != null && p.Spawned)
                    {
                        p.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                }
            }
            api8Drawing = true;
            try
            {
                if (api8ModelCell.IsValid)
                {
                    Vector3 c = api8ModelCell.ToVector3Shifted();
                    Api.DrawModel(api8Cube, new Vector3(c.x, 0.2f, c.z), 30f, Vector3.one * 0.4f, new Color(0.2f, 0.8f, 0.25f), Api.StuffSwatch(ThingDefOf.BlocksGranite));
                    Thing table = api8FigureCell.IsValid ? api8FigureCell.GetFirstBuilding(map) : null;
                    Color tc = table != null ? Api.SpriteColour(table) : Color.white;
                    Api.DrawModel(api8Cyl, new Vector3(c.x + 1f, 0f, c.z), 0f, Matrix4x4.Translate(new Vector3(0f, 0.35f, 0f)) * Matrix4x4.Scale(new Vector3(0.35f, 0.7f, 0.35f)), tc, table != null ? Api.StuffSwatch(table) : null);
                }
                if (api8FigureCell.IsValid && api8Corpse != null && !api8Corpse.Destroyed)
                {
                    Pawn inner = api8Corpse.InnerPawn;
                    Thing table = api8FigureCell.GetFirstBuilding(map);
                    float top = table != null ? Forms.HeightOf(table) : 0.5f;   // the test's own table height (an addon knows its furniture)
                    Vector3 lo, hi;
                    float scale = 1f;
                    if (Api.FigureBounds(inner, out lo, out hi))
                    {
                        Vector3 size = hi - lo;
                        scale = Mathf.Min(1f, 1.6f / Mathf.Max(0.01f, size.z), 1.6f / Mathf.Max(0.01f, size.x));
                        if (api8Frames == 5 && api8CheckLabel != null)
                        {
                            Log.Message("[IsoRim probe] api8 figure bounds " + lo.ToString("0.00") + ".." + hi.ToString("0.00") + " scale " + scale.ToString("0.00"));
                        }
                    }
                    Vector3 at = table != null ? table.TrueCenter() : api8FigureCell.ToVector3Shifted();
                    Matrix4x4 local = Matrix4x4.Scale(Vector3.one * scale) * Matrix4x4.Translate(new Vector3(-(lo.x + hi.x) * 0.5f, -lo.y, -(lo.z + hi.z) * 0.5f));
                    if (Api.DrawFigure(inner, new Vector3(at.x, top, at.z), 90f, local, false))
                    {
                        api8FigureTrue++;
                    }
                    else
                    {
                        api8FigureFalse++;
                    }
                }
            }
            finally
            {
                api8Drawing = false;
            }

            if (api8AllocWarm > 0 && --api8AllocWarm == 0)
            {
                api8AllocFrames = api8AllocAfter;
                api8AllocBytes = api8AllocDraws = api8AllocZero = api8AllocMax = 0;
            }
            if (api8AllocFrames > 0 && --api8AllocFrames == 0)
            {
                Log.Message("[IsoRim probe] api8alloc hooks " + FigureHooks.Count + ": " + api8AllocBytes + " bytes in " + api8AllocDraws + " People.Draw calls ("
                    + (api8AllocDraws > 0 ? (api8AllocBytes / (double)api8AllocDraws).ToString("0.0") : "-") + " per draw), draws with 0 bytes " + api8AllocZero
                    + ", largest " + api8AllocMax + " (the look refresh every 90 frames per person: People.LookOf)");
            }
            if (api8CheckFrames >= 0)
            {
                api8Frames++;
                if (api8Frames >= api8CheckFrames)
                {
                    api8CheckFrames = -1;
                    Api8Report();
                }
            }
        }

        private static void Api8Report()
        {
            string L = "[IsoRim probe] api8check " + api8CheckLabel + ": ";
            bool view = Api.ViewReady;
            Log.Message(L + "frames " + api8Frames + ", view " + view + ", modelsOn " + Api.ModelsOn + ", hook calls " + api8HookCalls + ", model draws " + api8ModelCalls
                + ", DrawFigure true " + api8FigureTrue + " false " + api8FigureFalse);
            if (!view)
            {
                bool none = api8HookCalls == 0 && api8ModelCalls == 0 && api8FigureTrue == 0;
                Log.Message(L + "3D off: nothing drawn " + (none ? "PASS" : "FAIL"));
                return;
            }
            if (api8ModelCell.IsValid)
            {
                Log.Message(L + "models drawn " + (api8ModelCalls >= 2 * api8Frames - 2 ? "PASS" : "FAIL"));
            }
            if (api8FigureCell.IsValid)
            {
                Log.Message(L + "figure on table " + (api8FigureTrue >= api8Frames - 1 ? "PASS" : "FAIL"));
            }
            foreach (Api8Rec r in api8Recs.Values.OrderBy(x => x.role))
            {
                float perFrame = api8Frames > 0 ? r.framesSeen / (float)api8Frames : 0f;
                string head = "calls " + r.calls + " on " + r.framesSeen + "/" + api8Frames + " frames, head cubes " + r.headAttached + ", chest " + r.bodyAttached
                    + ", lying " + r.lying + ", dead " + r.dead + ", headHidden " + r.headHidden + ", faceHidden " + r.faceHidden + ", fullHead " + r.fullHead + ", hair " + r.hairShown + ", headgear " + r.headgear
                    + ", facing ok " + r.facingOk + "/" + r.facingChecked + ", cube-pawn max " + r.maxDist.ToString("0.00") + ", cube height min " + r.minHeadHeight.ToString("0.00")
                    + ", chest dot min " + r.minChestDot.ToString("0.00") + ", head up |y| max " + r.maxHeadUpY.ToString("0.00") + ", moving " + r.movingOk + "/" + r.moving
                    + (r.moved > 0 ? ", via DrawFigure " + r.moved : "");
                List<string> fails = new List<string>();
                if (r.framesSeen == 0 || perFrame < 0.9f)
                {
                    fails.Add("not seen every frame");
                }
                if (!r.lying)
                {
                    if (r.facingOk < r.facingChecked && r.role != "walk")
                    {
                        fails.Add("facing");
                    }
                    if (r.maxDist > 0.45f)
                    {
                        fails.Add("cube off the pawn");
                    }
                    if (r.headAttached > 0 && r.minHeadHeight < 1.0f)
                    {
                        fails.Add("cube not over the head");
                    }
                    if (r.minChestDot < 0.7f)
                    {
                        fails.Add("chest not in front");
                    }
                }
                else
                {
                    if (r.maxHeadUpY > 0.5f)
                    {
                        fails.Add("head not lying");
                    }
                    if (r.maxDist > 1.0f)
                    {
                        fails.Add("cube off the pawn");
                    }
                }
                if (r.role == "walk" && (r.moving < 20 || r.movingOk < r.moving * 0.9f))
                {
                    fails.Add("not facing the walk");
                }
                if (r.role.EndsWith("burka") && !r.headHidden)
                {
                    fails.Add("burka: head not hidden");
                }
                if (r.role.EndsWith("helmet") && (!r.fullHead || r.hairShown))
                {
                    fails.Add("helmet: not full head / hair shown");
                }
                bool covered = r.role.EndsWith("helmet") || r.role.EndsWith("burka");
                if (api8Hide && covered && r.headAttached > 0)
                {
                    fails.Add("head cube under full headgear");
                }
                if ((!api8Hide || !covered) && r.headAttached == 0)
                {
                    fails.Add("no head cube");
                }
                Log.Message(L + r.role + " " + r.pawn.LabelShort + ": " + head + " -> " + (fails.Count == 0 ? "PASS" : "FAIL (" + string.Join(", ", fails) + ")"));
            }
        }

        // ---- fixtures

        private static void Api8Clear()
        {
            foreach (Thing t in api8Spawned)
            {
                Pawn p = t as Pawn;
                Thing gone = p != null && p.Corpse != null ? (Thing)p.Corpse : t;
                if (gone != null && !gone.Destroyed)
                {
                    gone.Destroy(DestroyMode.Vanish);
                }
            }
            api8Spawned.Clear();
            api8Recs.Clear();
            api8Corpse = null;
            api8TickFrames = 0;
        }

        private static Pawn Api8Pawn(string role, IntVec3 c, Rot4 rot, int seed, Map map, params string[] wear)
        {
            Pawn p;
            Rand.PushState(seed);
            try
            {
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                    fixedGender: Gender.Male, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f, developmentalStages: DevelopmentalStage.Adult,
                    forceNoIdeo: true, forbidAnyTitle: true));
                p.apparel.DestroyAll();
                p.equipment.DestroyAllEquipment();
                p.story.bodyType = BodyTypeDefOf.Male;
                p.story.hairDef = DefDatabase<HairDef>.GetNamed("Mop");
                foreach (string d in new[] { "Apparel_BasicShirt", "Apparel_Pants" }.Concat(wear))
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamed(d);
                    p.apparel.Wear((Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null), false);
                }
            }
            finally
            {
                Rand.PopState();
            }
            GenSpawn.Spawn(p, c, map, rot);
            p.drafter.Drafted = true;
            p.Rotation = rot;
            api8Spawned.Add(p);
            api8Recs[p.thingIDNumber] = new Api8Rec { role = role, pawn = p };
            return p;
        }

        private static void Api8Fixture(string kind, IntVec3 c, Map map)
        {
            Api8Clear();
            // the camera over the fixture, so its pawns are drawn (and checked) every frame
            Find.CameraDriver.SetRootPosAndSize(c.ToVector3Shifted() + new Vector3(kind == "stand" ? 6f : 3f, 0f, 0f), 9f);
            switch (kind)
            {
                case "stand":
                {
                    string[] roles = { "1 north", "2 east", "3 south", "4 west", "5 helmet", "6 cowboy", "7 burka" };
                    Rot4[] rots = { Rot4.North, Rot4.East, Rot4.South, Rot4.West, Rot4.South, Rot4.South, Rot4.South };
                    string[] hats = { null, null, null, null, "Apparel_PowerArmorHelmet", "Apparel_CowboyHat", "Apparel_Burka" };
                    for (int i = 0; i < roles.Length; i++)
                    {
                        Api8Pawn(roles[i], c + new IntVec3(2 * i, 0, 0), rots[i], 8001 + i, map, hats[i] != null ? new[] { hats[i] } : new string[0]);
                    }
                    break;
                }
                case "walk":
                {
                    Pawn w = Api8Pawn("walk", c, Rot4.East, 8101, map);
                    bool taken = w.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, c + new IntVec3(7, 0, 0)), JobTag.DraftedOrder);
                    api8TickFrames = 140;
                    Log.Message("[IsoRim probe] api8 walk job " + taken);
                    break;
                }
                case "lie":
                {
                    Pawn down = Api8Pawn("lie down", c, Rot4.South, 8201, map);
                    down.drafter.Drafted = false;
                    Hediff h = HediffMaker.MakeHediff(HediffDefOf.Anesthetic, down);
                    h.Severity = 1f;
                    down.health.AddHediff(h);
                    Pawn dead = Api8Pawn("lie dead", c + new IntVec3(2, 0, 0), Rot4.South, 8202, map);
                    dead.drafter.Drafted = false;
                    dead.Kill(null);
                    api8Corpse = dead.Corpse;
                    Log.Message("[IsoRim probe] api8 lie: downed " + down.Downed + ", corpse " + (api8Corpse != null && api8Corpse.Spawned));
                    break;
                }
                default:
                    Log.Warning("[IsoRim probe] api8fix: unknown fixture " + kind);
                    break;
            }
        }

        // ---- measuring the game's sprites (FigureBoxes constants)

        private static void Api8Sprites()
        {
            foreach (BodyTypeDef b in new[] { BodyTypeDefOf.Male, BodyTypeDefOf.Female, BodyTypeDefOf.Thin, BodyTypeDefOf.Hulk, BodyTypeDefOf.Fat })
            {
                Graphic g = GraphicDatabase.Get<Graphic_Multi>(b.bodyNakedGraphicPath, ShaderDatabase.CutoutSkin, Vector2.one, Color.white);
                Log.Message("[IsoRim probe] api8sprite body " + b.defName + " headOffset " + b.headOffset + ": " + Api8Extent(g.MatSouth));
            }
            HeadTypeDef head = DefDatabase<HeadTypeDef>.GetNamedSilentFail("Male_AverageNormal");
            if (head != null)
            {
                Graphic g = GraphicDatabase.Get<Graphic_Multi>(head.graphicPath, ShaderDatabase.CutoutSkin, Vector2.one, Color.white);
                Log.Message("[IsoRim probe] api8sprite head " + head.defName + ": " + Api8Extent(g.MatSouth));
            }
            Model upper = People.Part("person_male_upper"), lower = People.Part("person_male_lower"), headM = People.Part("person_head");
            foreach (Model m in new[] { upper, lower, headM })
            {
                if (m != null)
                {
                    ModelHandle h = new ModelHandle(m);
                    Log.Message("[IsoRim probe] api8sprite model " + m.id + ": " + h.Min.ToString("0.000") + ".." + h.Max.ToString("0.000"));
                }
            }
        }

        // Opaque rows and columns of a sprite material's texture (128 samples a side), in pixels (row 0 = sample row 0) and in
        // cells of the 1.5-cell quad from its middle.
        private static string Api8Extent(Material mat)
        {
            Texture tex = mat != null ? mat.mainTexture : null;
            Color32[] px = tex != null ? SpritePixels.Sample(tex, Vector2.one, Vector2.zero, 128) : null;
            if (px == null)
            {
                return "unreadable";
            }
            int r0 = 128, r1 = -1, c0 = 128, c1 = -1;
            for (int r = 0; r < 128; r++)
            {
                for (int col = 0; col < 128; col++)
                {
                    if (px[r * 128 + col].a > 128)
                    {
                        r0 = Math.Min(r0, r);
                        r1 = Math.Max(r1, r);
                        c0 = Math.Min(c0, col);
                        c1 = Math.Max(c1, col);
                    }
                }
            }
            Func<int, float> cells = v => (v + 0.5f - 64f) / 128f * 1.5f;
            // the width of the opaque area at the row a quarter of the way up from its bottom row (the waist/hips)
            int widthRow = r0 + (r1 - r0) / 4;
            int w0 = 128, w1 = -1;
            for (int col = 0; col < 128; col++)
            {
                if (widthRow >= 0 && widthRow < 128 && px[widthRow * 128 + col].a > 128)
                {
                    w0 = Math.Min(w0, col);
                    w1 = Math.Max(w1, col);
                }
            }
            return tex.width + "x" + tex.height + " rows " + r0 + ".." + r1 + " (" + cells(r0).ToString("0.000") + ".." + cells(r1).ToString("0.000") + ")"
                + " cols " + c0 + ".." + c1 + " (" + cells(c0).ToString("0.000") + ".." + cells(c1).ToString("0.000") + "), width at row " + widthRow + ": " + w0 + ".." + w1;
        }
    }
}
