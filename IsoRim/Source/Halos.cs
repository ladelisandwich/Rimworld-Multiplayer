using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Port) Lamp and fire halos in the 3D view (docs/isorim-lighting.md, option 3): a soft glow round every lit lamp, torch,
    // campfire, fire and other glower thing, in its glow colour, at the point its light comes from (LampPlace), sized by its
    // glow radius. Visual only: reads the shown map's lights (IsoLamps, Lamps.cs), the sky colour, roofs and fog, and writes only
    // its own material; no Verse.Rand (fires flicker by a sine of game time and the thing's ID).
    //
    // One instanced draw a frame: DrawMeshInstanced of a four-corner quad on the IsoRimHalo def (blend Additive, SrcAlpha One;
    // queue 3151 as the game's glow motes, after the night and indoor light overlay at 3100, so the overlay does not darken
    // it; depth-tested, no depth write, no culling). Walls, models and pawns in front hide a halo; roofs, drawn later (3600),
    // cover it. Each copy's matrix is a data record (Pack), not a transform; IsoRimHalo.vert.glsl stands it square to the
    // camera, pulled Pull toward it so the lamp's own body does not hide it, and fades it by the night: full at a sky
    // luminance of 0.55 or less (clear night 0.58, caves 0.37), none from 0.9 (clear day 1.0; rain or fog by day leave about
    // a fifth), a roof over the light counting as a fifth less sky.
    // Fires are toned down: a burning fire and a ritual fire already throw the game's big FireGlow motes (Fire.cs,
    // CompRitualFireOverlay); campfires, torches and braziers have their flame. Glowing terrain and custom glowers get none.
    // Called from Patches.DrawMapMesh_Prefix (inside Map.MapUpdate, after the sky and the glow grid are updated). Not drawn
    // on maps with stacked floors (first cut) or during the gravship cutscene and capture.
    [StaticConstructorOnStartup]   // Port: a static Material field (the game warns in dev mode otherwise)
    public static class Halos
    {
        public const string Def = "IsoRimHalo";
        public const int MaxHalos = 256;

        // The look (tune in game; the falloff and the night curve are in the shaders).
        private const float RadiusPerGlow = 0.14f;   // halo radius (true cells) per cell of glow radius: a standing lamp (12) 1.7
        private const float MinRadius = 0.5f;
        private const float MaxRadius = 2.6f;
        private const float Peak = 0.55f;            // added at the middle of a white lamp's halo at night, setting 100 %
        private const float BurningShare = 0.3f;     // fires that throw FireGlow motes
        private const float FlameShare = 0.6f;       // fire overlays: campfires, torches, braziers, darklight torches
        private const float PlantShare = 0.5f;       // glowing plants
        private const float FireFlicker = 1f;
        private const float FlameFlicker = 0.8f;
        private const float Pull = 0.5f;             // true cells toward the camera: past the lamp's own body, short of a wall in front

        public static int Drawn;   // halos queued this frame (probes)

        private static Material mat;
        private static bool tried;
        private static Mesh quad;
        private static bool logged;
        private static readonly Matrix4x4[] data = new Matrix4x4[MaxHalos];
        private static readonly Dictionary<ThingDef, Vector2> kinds = new Dictionary<ThingDef, Vector2>();

        private static readonly int rightId = Shader.PropertyToID("_HaloRight");
        private static readonly int upId = Shader.PropertyToID("_HaloUp");
        private static readonly int eyeId = Shader.PropertyToID("_HaloEye");
        private static readonly int frameId = Shader.PropertyToID("_HaloFrame");

        public static void Draw(Map map)
        {
            Drawn = 0;
            IsoRimSettings st = IsoRimMod.Settings;
            if (st == null || !st.lampHalos || map == null || !View3D.Ready || Floors.Count > 0
                || WorldComponent_GravshipController.CutsceneInProgress || WorldComponent_GravshipController.GravshipRenderInProgess)
            {
                return;
            }
            Material m = HaloMaterial;
            Mesh q = QuadMesh;
            if (m == null || q == null)
            {
                return;
            }
            IsoLamps.Collect(map);   // at most once a frame, whoever asks first (IsoGlow.BeginFrame too)
            List<IsoLamps.Lamp> lamps = IsoLamps.Map == map ? IsoLamps.All : null;
            if (lamps == null || lamps.Count == 0)
            {
                return;
            }
            float s = View3D.Squash;
            int n = 0;
            for (int i = 0; i < lamps.Count && n < MaxHalos; i++)
            {
                if (Pack(map, lamps[i], s, ref data[n]))
                {
                    n++;
                }
            }
            if (n == 0)
            {
                return;
            }
            Color sky = IsoGlow.SkyFor(map);   // the game's overlay sky colour (white with sky lighting off: no halos)
            Vector3 r = View3D.Right, u = View3D.Up, eye = View3D.EyePosition;
            m.SetVector(rightId, new Vector4(r.x, r.y, r.z, s));
            m.SetVector(upId, new Vector4(u.x, u.y, u.z, Pull));
            m.SetVector(eyeId, new Vector4(eye.x, eye.y, eye.z, View3D.Perspective ? 1f : 0f));
            // Game seconds, wrapped every hour of game time at 1x so the float stays exact (the flicker's phase jumps then).
            float time = Find.TickManager != null ? (Find.TickManager.TicksGame % 216000) / 60f : Time.realtimeSinceStartup % 3600f;
            float lum = 0.299f * sky.r + 0.587f * sky.g + 0.114f * sky.b;
            m.SetVector(frameId, new Vector4(time, lum, Mathf.Clamp(st.haloStrength, 0f, 2f), 0f));
            Graphics.DrawMeshInstanced(q, 0, m, data, n, null, UnityEngine.Rendering.ShadowCastingMode.Off, false, 0);
            Drawn = n;
            if (!logged && Prefs.DevMode)
            {
                logged = true;
                Log.Message("[IsoRim] Lamp halos: " + n + " drawn in the first frame with any (sky luminance " + lum.ToString("0.00") + ")");
            }
        }

        // One light's record for IsoRimHalo.vert.glsl, or false for none: not a lit glower thing on this map (glowing terrain
        // and custom glowers), fogged, black, or off screen.
        //   [0] rgb = colour (brightest channel 1), a = strength; [1] x = radius (true cells), y = flicker, z = flicker phase,
        //   w = 1 under a roof; [2] unused; [3] = the light (true space), 1.
        private static bool Pack(Map map, IsoLamps.Lamp l, float squash, ref Matrix4x4 record)
        {
            Thing t = l.thing;
            if (t == null || !t.Spawned || t.Map != map || l.radius <= 0f)
            {
                return false;
            }
            IntVec3 c = t.Position;
            if (!c.InBounds(map) || map.fogGrid.IsFogged(c))
            {
                return false;
            }
            float top = l.bright;   // brightest channel / 255, not scaled down (a sun lamp's 1.45)
            float peak = Mathf.Max(l.color.r, Mathf.Max(l.color.g, l.color.b));
            if (top <= 0.01f || peak <= 0f)
            {
                return false;
            }
            Vector3 at = l.at;   // LampPlace.Of(t), worked out by IsoLamps.Collect
            float radius = Mathf.Clamp(l.radius * RadiusPerGlow, MinRadius, MaxRadius);
            float reach = radius + Pull;
            if (!Cull.BoxVisible(new Vector3(at.x - reach, (at.y - reach) / squash, at.z - reach), new Vector3(at.x + reach, (at.y + reach) / squash, at.z + reach)))
            {
                return false;
            }
            Vector2 kind = KindOf(t.def);
            record = new Matrix4x4(
                new Vector4(l.color.r / peak, l.color.g / peak, l.color.b / peak, Peak * Mathf.Clamp(top, 0.35f, 1.5f) * kind.x),
                new Vector4(radius, kind.y, (t.thingIDNumber % 1000) * 0.37f, map.roofGrid.Roofed(c) ? 1f : 0f),
                Vector4.zero,
                new Vector4(at.x, at.y, at.z, 1f));
            return true;
        }

        // x: the share of a lamp's strength, y: how much it flickers. By def: the class and the comps come from it.
        private static Vector2 KindOf(ThingDef def)
        {
            Vector2 k;
            if (kinds.TryGetValue(def, out k))
            {
                return k;
            }
            if (def.thingClass != null && typeof(Fire).IsAssignableFrom(def.thingClass))
            {
                k = new Vector2(BurningShare, FireFlicker);   // throws the game's FireGlow motes (Fire.cs)
            }
            else if (def.GetCompProperties<CompProperties_FireOverlayRitual>() != null)
            {
                k = new Vector2(BurningShare, FireFlicker);   // so does a ritual fire (CompRitualFireOverlay)
            }
            else if (def.GetCompProperties<CompProperties_FireOverlay>() != null)
            {
                k = new Vector2(FlameShare, FlameFlicker);    // campfires, torches, braziers, darklight torches (subclass)
            }
            else if (def.category == ThingCategory.Plant)
            {
                k = new Vector2(PlantShare, 0f);
            }
            else
            {
                k = new Vector2(1f, 0f);
            }
            kinds[def] = k;
            return k;
        }

        private static Material HaloMaterial
        {
            get
            {
                if (!tried)
                {
                    tried = true;
                    Shader sh = R3DMaterials.ShaderOf(Def);   // null, logged once, if the def is missing
                    if (sh != null)
                    {
                        mat = new Material(sh);
                        mat.name = "IsoRim_Halo";
                    }
                }
                return mat;
            }
        }

        // The quad every halo is drawn from: corners x, y = -1..1 (the vertex stage turns it to the camera).
        private static Mesh QuadMesh
        {
            get
            {
                if (quad == null)
                {
                    quad = new Mesh();
                    quad.name = "IsoRim_HaloQuad";
                    quad.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) };
                    quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                    quad.RecalculateBounds();
                    MeshGuard.Check(quad, "halo quad");
                }
                return quad;
            }
        }
    }
}
