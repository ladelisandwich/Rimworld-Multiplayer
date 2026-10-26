using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Port (docs/isorim-lighting.md, option 4): sun shadows on IsoRim's 3D faces, and one sun for faces and ground.
    //
    // Faces (IsoRimSettings.faceShadows): IsoRimSolid.frag.glsl (SunVisible) follows the ray from each sunlit fragment of a
    // lit model or wall toward IsoRim's sun (_IsoSun) across the map's cell texture (IsoCellMap: the tops of walls, rock
    // and doors, cell bit 16) and, while roofs are drawn, the roof texture, and takes the sun's share of the face's shade
    // away where something stands above the ray; not under a roof (the sun shade's cover read, OpenSky). BeginFrame hands
    // it _IsoShadow: x = strength (the option, by day; with the sun shade off also as dark as the game's ground shadows are
    // now against a clear day's, which the sun shade's contrast already follows when it is on; 0 = off),
    // y = farthest march along the ground (cells), z = the highest top anything reaches (true cells above the ground; the
    // march stops once the ray is above it), w = how far under a top the shadow is full (true cells: the soft edge).
    //
    // Ground (IsoRimSettings.oneSun): the game's ground shadow cast (GenCelestial.GetLightSourceInfo with LightType.Shadow:
    // the _CastVect global the SunShadow shaders slide by, and the area SectionLayer_SunShadows draws) is taken from
    // IsoRim's own light instead (CastFor): straight away from the light the faces are shaded with, as long as a wall of
    // View3D.WallHeight throws it. Off: the game's own cast mirrored north-south, as before (Patches.GetLightSourceInfo_Postfix).
    //
    // Visual only (multiplayer): reads the map's sun glow, IsoRim's options, light and cell data; writes shader globals and
    // the vector handed back to the game's own visual callers; no Rand, no game state. Main thread: BeginFrame from
    // MapDrawer.DrawMapMesh's prefix (inside Map.MapUpdate), CastFor from the callers of GetLightSourceInfo(Shadow)
    // (SkyManager.SkyManagerUpdate, SectionLayer_SunShadows.GetSunShadowsViewRect, the dev celestial dialog).
    public static class SunShadows
    {
        public const float MaxMarch = 12f;   // cells along the ground: a roof ridge 3.4 cells up throws 8.5 under the lowest sun (22 degrees)
        public const float Soft = 0.08f;     // true cells under a top where the shadow is full
        private const float MinUp = 0.05f;   // the light's lowest height used for the cast (IsoRim's light never goes below 22 degrees)
        private const int RoofLevels = 4;    // SectionLayer_IsoRimRoofs.MaxLevel (private): a built roof rises at most this many pitches
        private const float ClearDayDark = 0.26f;   // 1 - the mean of Clear's day shadow colour (0.718, 0.745, 0.757), Weathers.xml
        private static readonly int shadowId = Shader.PropertyToID("_IsoShadow");

        // The faces' shadows read the cell texture, so it is kept up to date and bound (IsoCellMap.BeginFrame).
        public static bool Wanted
        {
            get { return IsoRimMod.Settings != null && IsoRimMod.Settings.faceShadows; }
        }

        // Before the map's meshes are drawn, after the cell texture is up to date (Patches.DrawMapMesh_Prefix).
        public static void BeginFrame(Map map)
        {
            Shader.SetGlobalVector(shadowId, Globals(map));
        }

        private static Vector4 Globals(Map map)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (st == null || !View3D.Active || map == null || !st.faceShadows || st.faceShadowStrength <= 0f || (map.Biome != null && map.Biome.disableShadows))
            {
                return Vector4.zero;
            }
            IsoCellMap cells = IsoCellMap.Existing(map);
            if (cells == null || !cells.Uploaded)
            {
                return Vector4.zero;
            }
            // By day only: they fade out as IsoRim's light swings over to the moon (R3DMaterials.LightAt); with "Sun moves"
            // off the light is the fixed mid-morning sun at every hour.
            float day = st.sunMoves ? 1f - R3DMaterials.NightWeight(GenCelestial.CurCelestialSunGlow(map)) : 1f;
            // With the sun shade off: as dark as the game's own ground shadows are now, against a clear day's: fainter in
            // rain, fog and snow, and fading out toward dusk (MatBases.SunShadow.color: the weather's shadow colour times the
            // game's shadow strength, set by SkyManagerUpdate earlier in this Map.MapUpdate). With it on, the shader's
            // contrast (R3DMaterials.SetSunShade, from the same colour) already scales the sun's share, so it is not applied
            // twice.
            float ground = 1f;
            Material shadowMat = MatBases.SunShadow;
            if (shadowMat != null)
            {
                Color c = shadowMat.color;
                ground = Mathf.Clamp01((1f - (c.r + c.g + c.b) / 3f) / ClearDayDark);
            }
            float strength = Mathf.Clamp01(st.faceShadowStrength) * day * (st.sunShade ? 1f : ground);
            if (strength < 0.001f)
            {
                return Vector4.zero;
            }
            float ceiling = cells.MaxTop;
            if (RoofClip.On)
            {
                Vector4 roof = RoofClip.Globals;   // y wall height, z rise over the walls, w pitch per level (0 for flat roofs)
                ceiling = Mathf.Max(ceiling, roof.y + roof.z + roof.w * RoofLevels);
            }
            return new Vector4(strength, MaxMarch, ceiling + 0.05f, Soft);
        }

        // The ground shadow cast of one wall (staticSunShadowHeight 1, the game's scale) under the light the 3D faces are
        // shaded with (R3DMaterials.SunDir, true space, toward the light): the top of a wall of View3D.WallHeight true cells
        // falls WallHeight / tan(elevation) cells from its foot, straight away from the light; x east, y north (the map's z),
        // as GenCelestial.LightInfo.vector. False (the caller keeps the game's cast) with oneSun off or for a map not shown.
        public static bool CastFor(Map map, out Vector2 cast)
        {
            cast = Vector2.zero;
            IsoRimSettings st = IsoRimMod.Settings;
            if (st == null || !st.oneSun || map == null || map != Find.CurrentMap)
            {
                return false;
            }
            try
            {
                Vector3 light = R3DMaterials.SunDir;
                float scale = View3D.WallHeight / Mathf.Max(light.y, MinUp);
                cast = new Vector2(-light.x * scale, -light.z * scale);
                float most = GenCelestial.ShadowMaxLengthDay;
                if (cast.sqrMagnitude > most * most)
                {
                    cast = cast.normalized * most;
                }
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Casting the ground shadows from the 3D sun failed (the game's own are used): " + e, 93611209);
                cast = Vector2.zero;
                return false;
            }
        }
    }
}
