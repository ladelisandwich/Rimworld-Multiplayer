using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using Verse;
using Verse.Glow;

namespace IsoRim
{
    // Port (docs/isorim-lighting.md, options 2 and 3): the shown map's lights for IsoRim's own 3D light, read only.
    //
    // Collect (at most once a frame, inside Map.MapUpdate: from IsoGlow.BeginFrame and Halos.Draw, both called from the
    // DrawMapMesh prefix) reads the game's light list (GlowGrid.lights, private: lamps and fires, glowing terrain, other
    // mods' custom glowers) and keeps in All every light whose glow reaches into view (CameraDriver.CurrentViewRect, which
    // IsoRim widens for 3D), at most MaxAll: lamps and custom glowers before glowing terrain, then the nearest the screen
    // centre. For a glower it finds the thing (by thingIDNumber in the light's cell) and where its light comes from
    // (LampPlace), so the faces' lamp light and the halos agree.
    //
    // Option 2, lamps light 3D faces by direction (IsoRimSettings.lampFacing): BeginFrame puts the first MaxLamps of All in a
    // fixed order (kind, id), so the same lamps always make the same texture, and sends them to a small texture only when
    // that changed. The light pass (IsoRimSolid.frag.glsl, LampFacing) multiplies each colour of the glow by 1 + k * the
    // light-weighted cosine between the face and the lamps: brighter toward them, darker away, the same on average, so a
    // room keeps the brightness the game gives it and the picture still matches what the game counts as lit.
    // Option 3, halos (Halos.cs), draws from All, whatever option 2's setting is.
    //
    // _IsoLampTex (MaxLamps * 2 x 1, RGBA32, point-sampled; read with texelFetch): lamp i in texels 2i and 2i + 1:
    //   2i:     r = cell x & 255, g = cell z & 255, b = (x >> 8) | (z >> 8) << 4 (cells 0-4095), a = glow radius * RadiusUnit;
    //   2i + 1: r, g, b = the game's glow colour (scaled down to 255 at most), a = the light's height above the ground * HeightUnit.
    // _IsoLamps: x = lamps in the texture (0: none, the shader skips the term), y = k (IsoRimSettings.lampFacingStrength).
    // Count 0 with the setting or the lighting fix off, with stacked floors (their rows are shifted; not handled yet) or if
    // the grid cannot be read.
    // Multiplayer: visual only. Nothing here writes game state or draws a random number, and the settings are per player.
    [StaticConstructorOnStartup]
    public static class IsoLamps
    {
        public const int MaxLamps = 32;        // in the lamp texture: LampMax in IsoRimSolid.frag.glsl
        public const int MaxAll = 256;         // in All (Halos.MaxHalos)
        public const float RadiusUnit = 6f;    // radius byte: cells * 6, up to 42.5 (GlowGrid.MaxLightRadius is 40)
        public const float HeightUnit = 64f;   // height byte: true cells * 64, up to 3.98
        public const int KindGlower = 0, KindTerrain = 1, KindCustom = 2;

        // Where the light sits (true cells above the ground) when no thing says.
        private const float TerrainHeight = 0.05f;
        private const float CustomHeight = 0.5f;
        private const float DefaultHeight = 0.6f;

        public struct Lamp
        {
            public IntVec3 cell;    // where the game registered the light (GlowLight.position)
            public float radius;    // its glow radius (cells)
            public Color32 color;   // the game's glow colour, scaled down so that no channel is above 255
            public float bright;    // the glow colour's brightest channel / 255, not scaled down (a sun lamp's 370: 1.45)
            public int kind;        // KindGlower, KindTerrain or KindCustom
            public int id;          // thingIDNumber (glowers), cell index (terrain), the custom glower's id
            public Thing thing;     // the glower's thing; null for terrain and custom glowers, or if it is not in its cell
            public Vector3 at;      // where the light comes from, true space (x, height above the ground, z)
            internal int tier;      // 0 lamps and custom glowers, 1 glowing terrain (fills what is left)
            internal float dist2;   // squared distance from the screen centre's ground point
            internal long key;      // kind << 32 | id: the fixed order
        }

        // The lights of Map whose glow reaches into view this frame (Collect).
        public static readonly List<Lamp> All = new List<Lamp>(MaxAll);
        private static readonly List<Lamp> terrainNear = new List<Lamp>(MaxLamps);   // Gather: the nearest glowing terrain

        // The map the last Collect was for (null before any).
        public static Map Map { get; private set; }

        private static int collectedFrame = -1;
        private static bool failed;

        private static readonly int texId = Shader.PropertyToID("_IsoLampTex");
        private static readonly int lampsId = Shader.PropertyToID("_IsoLamps");

        private static LiveTexture tex;
        private static readonly Lamp[] shown = new Lamp[MaxLamps];
        private static readonly Color32[] pixels = new Color32[MaxLamps * 2];
        private static readonly Color32[] sent = new Color32[MaxLamps * 2];
        private static int sentCount = -1;
        private static int uploads;

        private sealed class ByRank : IComparer<Lamp>
        {
            public int Compare(Lamp a, Lamp b)
            {
                if (a.tier != b.tier)
                {
                    return a.tier < b.tier ? -1 : 1;
                }
                if (a.dist2 != b.dist2)
                {
                    return a.dist2 < b.dist2 ? -1 : 1;
                }
                return a.key.CompareTo(b.key);
            }
        }

        private sealed class ByKey : IComparer<Lamp>
        {
            public int Compare(Lamp a, Lamp b)
            {
                return a.key.CompareTo(b.key);
            }
        }

        private static readonly ByRank byRank = new ByRank();
        private static readonly ByKey byKey = new ByKey();

        // Gathers the lights of map into All, at most once a frame (the first caller does the work). Main thread, inside
        // Map.MapUpdate (after GlowGridUpdate_First). Reads only.
        public static void Collect(Map map)
        {
            if (map == null || (collectedFrame == Time.frameCount && Map == map))
            {
                return;
            }
            collectedFrame = Time.frameCount;
            Map = map;
            All.Clear();
            if (failed || map.glowGrid == null || Find.CameraDriver == null)
            {
                return;
            }
            try
            {
                if (!Gather(map))
                {
                    failed = true;   // GlowGrid.lights cannot be read in this game version (warned once by Grid.Read)
                    All.Clear();
                }
            }
            catch (Exception e)
            {
                failed = true;
                All.Clear();
                Log.Warning("[IsoRim] Reading the map's lights failed, so lamps light no 3D faces by direction and have no halos until the game restarts: " + e);
            }
        }

        private static bool Gather(Map map)
        {
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);   // IsoRim's 3D view rect (CurrentViewRect_Postfix)
            Vector2 focus;
            if (View3D.Ready)
            {
                Vector3 f = View3D.ScreenToGround(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
                focus = new Vector2(f.x, f.z);
            }
            else
            {
                IntVec3 c = view.CenterCell;
                focus = new Vector2(c.x + 0.5f, c.z + 0.5f);
            }
            // Who uses the list this frame: the lamp texture (the first MaxLamps) and the halos (lit things only, up to MaxAll)
            IsoRimSettings st = IsoRimMod.Settings;
            bool facing = st.lightingFix && st.lampFacing;
            bool halos = st.lampHalos;
            terrainNear.Clear();
            if (!Grid.Read(map.glowGrid, view, focus, All, facing, terrainNear))
            {
                return false;
            }
            // Glowing terrain (one light per cell: thousands on a lava map) only fills what lamps and custom glowers leave of the
            // texture, nearest first; the halos never draw it (Grid.Read keeps only the MaxLamps nearest)
            if (facing && All.Count < MaxLamps && terrainNear.Count > 0)
            {
                terrainNear.Sort(byRank);
                for (int k = 0; k < terrainNear.Count && All.Count < MaxLamps; k++)
                {
                    All.Add(terrainNear[k]);
                }
            }
            // Order matters only where the list is cut: at MaxLamps for the texture, at MaxAll for the halos
            if (All.Count > (facing ? MaxLamps : MaxAll))
            {
                All.Sort(byRank);   // the texture takes the first MaxLamps, halos the first MaxAll
                if (All.Count > MaxAll)
                {
                    All.RemoveRange(MaxAll, All.Count - MaxAll);
                }
            }
            int placed = halos ? All.Count : Mathf.Min(All.Count, MaxLamps);   // only what will be used gets its light point
            for (int k = 0; k < placed; k++)
            {
                Lamp l = All[k];
                Place(map, ref l);
                All[k] = l;
            }
            return true;
        }

        // The light's thing and where its light comes from.
        private static void Place(Map map, ref Lamp l)
        {
            float h;
            if (l.kind == KindTerrain)
            {
                h = TerrainHeight;
            }
            else if (l.kind == KindCustom)
            {
                h = CustomHeight;
            }
            else
            {
                l.thing = ThingAt(map, l.cell, l.id);
                if (l.thing != null)
                {
                    l.at = LampPlace.Of(l.thing);
                    return;
                }
                h = DefaultHeight;
            }
            l.at = new Vector3(l.cell.x + 0.5f, h, l.cell.z + 0.5f);
        }

        private static Thing ThingAt(Map map, IntVec3 c, int id)
        {
            if (!c.InBounds(map))
            {
                return null;
            }
            List<Thing> things = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t != null && t.thingIDNumber == id)
                {
                    return t;
                }
            }
            return null;
        }

        // ---- option 2: the lamp texture

        // Before the shown map is drawn (IsoGlow.BeginFrame, inside Map.MapUpdate, lighting fix on). floors: the map has
        // stacked floors (Floors.GlowFloors().x > 0).
        public static void BeginFrame(Map map, bool floors)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (st == null || !st.lampFacing || floors || map == null)
            {
                Off();
                return;
            }
            Collect(map);
            int n = Map == map ? Mathf.Min(All.Count, MaxLamps) : 0;
            if (n == 0)
            {
                Off();
                return;
            }
            Pack(n);
            Send(n);
            if (tex != null)
            {
                Shader.SetGlobalTexture(texId, tex.Use());
            }
            Shader.SetGlobalVector(lampsId, new Vector4(n, Mathf.Clamp01(st.lampFacingStrength), 0f, 0f));
        }

        // No lamp directions this frame: the shader leaves the glow as it is.
        public static void Off()
        {
            Shader.SetGlobalVector(lampsId, Vector4.zero);
        }

        // The first n lights of All, in the fixed order, into pixels.
        private static void Pack(int n)
        {
            for (int k = 0; k < n; k++)
            {
                shown[k] = All[k];
            }
            Array.Sort(shown, 0, n, byKey);
            for (int k = 0; k < n; k++)
            {
                Lamp c = shown[k];
                int x = Mathf.Clamp(c.cell.x, 0, 4095);
                int z = Mathf.Clamp(c.cell.z, 0, 4095);
                pixels[2 * k] = new Color32((byte)(x & 255), (byte)(z & 255), (byte)((x >> 8) | ((z >> 8) << 4)),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(c.radius * RadiusUnit), 0, 255));
                pixels[2 * k + 1] = new Color32(c.color.r, c.color.g, c.color.b, (byte)Mathf.Clamp(Mathf.RoundToInt(c.at.y * HeightUnit), 0, 255));
            }
            for (int i = 2 * n; i < pixels.Length; i++)
            {
                pixels[i] = default(Color32);
            }
        }

        // Sends the packed lamps if they differ from what the texture holds (at most once a frame).
        private static void Send(int n)
        {
            bool same = tex != null && n == sentCount;
            for (int i = 0; same && i < 2 * n; i++)
            {
                Color32 a = pixels[i], b = sent[i];
                same = a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
            }
            if (same)
            {
                return;
            }
            Array.Copy(pixels, sent, pixels.Length);
            sentCount = n;
            Stopwatch watch = uploads < 3 && ModLog.Diag ? Stopwatch.StartNew() : null;
            if (tex == null)
            {
                tex = new LiveTexture(MaxLamps * 2, 1, "IsoRim_Lamps", FilterMode.Point);
            }
            tex.Upload(pixels);
            uploads++;
            if (watch != null)
            {
                ModLog.Note("[IsoRim] Lamp texture (" + n + " lamps) uploaded in " + watch.Elapsed.TotalMilliseconds.ToString("0.000") + " ms");
            }
        }

        // ---- the game's light list. Only this class names the glow grid's private field and its engine types: if a game
        // update moves them, Read fails when it is compiled, inside Collect's try (as LiveTexture.Next).
        private static class Grid
        {
            private static AccessTools.FieldRef<GlowGrid, NativeList<GlowLight>> lightsOf;
            private static bool tried;

            // Adds every light of the grid whose glow reaches into view. False if GlowGrid.lights cannot be read.
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static bool Read(GlowGrid grid, CellRect view, Vector2 focus, List<Lamp> into, bool terrain, List<Lamp> terrainInto)
            {
                if (!tried)
                {
                    tried = true;
                    FieldInfo f = AccessTools.Field(typeof(GlowGrid), "lights");
                    if (f != null && f.FieldType == typeof(NativeList<GlowLight>))
                    {
                        lightsOf = AccessTools.FieldRefAccess<GlowGrid, NativeList<GlowLight>>(f);
                    }
                    else
                    {
                        Log.Warning("[IsoRim] GlowGrid.lights is " + (f == null ? "missing" : "a " + f.FieldType.FullName)
                            + " in this game version, so lamps light no 3D faces by direction and have no halos.");
                    }
                }
                if (lightsOf == null || grid == null)
                {
                    return false;
                }
                ref NativeList<GlowLight> lights = ref lightsOf(grid);
                if (!lights.IsCreated)
                {
                    return true;
                }
                int n = lights.Length;
                int worst = -1;            // terrainInto: the farthest kept, replaced by a nearer one once it is full
                float worstDist2 = 0f;
                for (int i = 0; i < n; i++)
                {
                    GlowLight l = lights[i];
                    float radius = l.glowRadius;
                    if (!(radius > 0f))
                    {
                        continue;
                    }
                    IntVec3 c = l.position;
                    int reach = Mathf.CeilToInt(radius);
                    if (c.x + reach < view.minX || c.x - reach > view.maxX || c.z + reach < view.minZ || c.z - reach > view.maxZ)
                    {
                        continue;
                    }
                    ColorInt gc = l.glowColor;
                    int r = Math.Max(0, gc.r), g = Math.Max(0, gc.g), b = Math.Max(0, gc.b);
                    int m = Math.Max(r, Math.Max(g, b));
                    if (m <= 0)
                    {
                        continue;
                    }
                    float bright = m / 255f;
                    if (m > 255)
                    {
                        r = r * 255 / m;
                        g = g * 255 / m;
                        b = b * 255 / m;
                    }
                    int kind = l.isTerrain ? KindTerrain : (l.isCustom ? KindCustom : KindGlower);
                    float dx = c.x + 0.5f - focus.x, dz = c.z + 0.5f - focus.y;
                    float d2 = dx * dx + dz * dz;
                    if (kind == KindTerrain)
                    {
                        // Only the MaxLamps nearest, and only for the lamp texture: one compare for most cells
                        if (!terrain || terrainInto.Count >= MaxLamps && d2 >= worstDist2)
                        {
                            continue;
                        }
                    }
                    Lamp lamp = new Lamp
                    {
                        cell = c,
                        radius = radius,
                        color = new Color32((byte)r, (byte)g, (byte)b, 255),
                        bright = bright,
                        kind = kind,
                        id = l.id,
                        tier = kind == KindTerrain ? 1 : 0,
                        dist2 = d2,
                        key = ((long)kind << 32) | (uint)l.id,
                    };
                    if (kind != KindTerrain)
                    {
                        into.Add(lamp);
                        continue;
                    }
                    if (terrainInto.Count < MaxLamps)
                    {
                        terrainInto.Add(lamp);
                    }
                    else
                    {
                        terrainInto[worst] = lamp;
                    }
                    if (terrainInto.Count == MaxLamps)
                    {
                        worst = 0;
                        for (int k = 1; k < MaxLamps; k++)
                        {
                            if (terrainInto[k].dist2 > terrainInto[worst].dist2)
                            {
                                worst = k;
                            }
                        }
                        worstDist2 = terrainInto[worst].dist2;
                    }
                }
                return true;
            }
        }
    }
}
