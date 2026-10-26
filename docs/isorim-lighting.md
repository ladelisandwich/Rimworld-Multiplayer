# Lighting in RimWorld 1.7 (Waylith) and how to improve IsoRim's 3D light

Research for RimWorld 1.7.4990 (Oct 26 2026), done by reading the repo and the game's decompiled DLLs and shader files (ilspycmd), then checked by a second pass against the code. Nothing was built or run for it. `<game>` stands for the RimWorld folder. Decompiled file:line references (for example `MapRenderFeature.cs:308`) are to an `ilspycmd -p` dump of the named DLL and move with game updates.

**Short version:**
- The new engine has no lighting of its own. All light in RimWorld 1.7 is game shader maths on a flat 8-bit picture.
- IsoRim's 3D light is a sun shade on each face plus a copy of the game's per-cell light. Lamps don't care which way a face points, indoor faces still get sun shading, and only the ground gets shadows.
- The best value is to do more maths inside IsoRim's existing shader, using data IsoRim already has (the glow texture, the cell-height texture) plus a small list of lamps. Real shadow maps, SSAO and HDR are not practical on this engine.

---

## 1. How light works in RimWorld 1.7 on Waylith

### The engine (Waylith) does almost nothing for light

- **No light components, no shadows, no ambient, no tonemapping, no bloom.**
  - `UnityEngine.Light` in UnityStubs is just a set of data properties, and nothing reads them.
  - `RenderSettings` doesn't exist in UnityStubs.
  - `Graphics.DrawMesh` ignores castShadows and receiveShadows.
  - Waylith does have shadow-map hooks (`Waylith.Rendering.ShadowMapGlobal`, `MeshDrawFrame.ShadowView`), but nothing in Waylith, UnityStubs, RimWorld.dll or Assembly-CSharp sets them.
- **One forward pass into an 8-bit picture.**
  - Map colour is `B8G8R8A8_UNorm` (`EngineApplicationOptions` defaults; `Program.Main` doesn't override them).
  - Depth/stencil is D32S8, with no MSAA.
  - Values are in gamma space (`QualitySettings.activeColorSpace` is hard-coded to Gamma), and depth is reverse-Z.
  - The map camera has `allowHDR=false`. Only the world map gets an HDR target (`WorldRenderFeature`, R16G16B16A16SFloat).
- **The only post-process is a saturation mix** (`<game>/Rendering/Shaders/PostFX/PostFxComposite.frag.glsl:12-17`). It is driven by the sky's saturation through `Bootstrap` → `CompositePass.Saturation`.
- **Other engine facts that matter for lighting work:**
  - `MapRenderFeature` runs a water-depth pre-pass every frame into an R16 float target that map shaders can read as `_WaterOutputTex` (`MapRenderFeature.cs:53, 59-84, 279`). It belongs to water, but it shows how a custom pre-pass feature would look.
  - UnityStubs' `SystemInfo.usesReversedZBuffer` returns false although Waylith is reverse-Z (`MeshPipelineCache.cs:177`), so C# depth maths written the Unity way comes out wrong.
  - The swapchain format can fall back from B8G8R8A8_UNorm on some systems (`WindowRenderContext.cs:108-138`).
- **What the engine gives shaders each frame:**
  - ViewProj and WorldToCamera.
  - `SunDir`, which is a copy of the game's `_CastVect` global (`MapRenderFeature.CaptureFrame`, decomp `MapRenderFeature.cs:308-312`). That value is a 2D ground-shadow cast vector, not a real 3D sun.
- **How shaders are built:**
  - Shaders are GLSL 450, compiled at runtime by Slang.
  - Material uniforms are read from comment annotations in the **fragment** file (`ShaderMetadataParser`).
  - Each running mod's `Shaders` folder is searched before the game's (`ShaderSourceLoader.TryResolveShaderPathUncached`).
  - Draws sort by renderQueue. Above queue 2500 they are also sorted back to front (`DrawSortKey`, `TransparentDepthOrder`).

### The game: a light grid plus flat overlays

- **GlowGrid (simulation state).**
  - Each light (`CompGlower`, glowing terrain, custom lights) floods out cell by cell. Straight steps cost 100 and diagonal steps 141. Buildings with `blockLight` stop it, and the radius is at most 40 (`Verse.Glow.ComputeGlowGridsJob`, `GlowGrid.cs:201`).
  - Brightness falls off as `glowColor × lerp(1 − d/r, 1/d², 0.4)` (`ComputeGlowGridsJob.SetGlowFromDist`).
  - The results are summed per cell into `accumulatedGlow` (Color32; alpha 1 means "overlit").
  - The simulation reads it through:
    - `GroundGlowAt` / `PsychGlowAt`: work speed, plant growth and mood.
    - `VisualGlowAt`: darklight checks look at its hue.
  - **It must never be written by a lighting mod.**
  - Doors block light whether open or shut (`DoorBase` `blockLight`, `<game>/Data/Core/Defs/ThingDefs_Buildings/Buildings_Structure.xml:34`). Vanilla has no windows.
- **The lighting overlay.**
  - `Verse.SectionLayer_LightingOverlay.GenerateLightingOverlay` builds a mesh with a vertex at every cell corner (plus cell centres).
    - RGB is the average `VisualGlowAt` of the up to 4 neighbouring cells that don't block light.
    - Alpha is "sky cover", raised to at least 100/255 under a roof.
  - The `LightOverlay` shader (queue 3100, blend DstColor×SrcColor) multiplies the whole scene by `sky × (1 − cover) + glow`. Overbright is soft-clamped to about 1.5× (`<game>/Data/Core/Shaders/Map/LightOverlay.frag.glsl:14-26`, def `Shaders.Map.xml:162-173`).
  - So a roofed room shows 61% of the sky colour on screen while the simulation counts 0 sky glow there. What players see and what the simulation uses already differ.
- **The sky.** `Verse.SkyManager.SkyManagerUpdate` runs each frame inside `Map.MapUpdate`. It sets:
  - the overlay's sky colour;
  - saturation;
  - the sun-shadow material colours, `Lerp(white, weather shadow colour, CurShadowStrength)` (`SkyManager.cs:70-80`);
  - `_CastVect` (`SkyManager.cs:138`).
  - Clear weather has strong shadows (day shadow colour 0.718), while rain and fog have weak ones (0.92) (`Weathers.xml:17` vs `:117`).
- **Sun shadows.**
  - `SectionLayer_SunShadows` draws flat footprint quads whose outer edge `SunShadow.vert.glsl` slides along `SunDir × height` (queue 3170, multiply, stencil so each pixel darkens once).
  - Thing shadows (`SunShadowFade`, 3175) work the same way.
  - Edge shadows darken around walls at 2950.
  - Glows (motes, fire glow) are additive at 3151 and 3201, after the overlay, so the overlay doesn't darken them.

**Multiplayer rules:**
- **Safe to read:** `VisualGlowAt`, `litGlowers`, `SkyManager.CurSky`, the material colours and the globals.
- **Never write:** GlowGrid, glowers, `curSky` or `curSkyGlowInt`, weather.
- **Never call:** `WeatherWorker.CurSkyTarget`. At latitude 75 or more it writes saved `WeatherManager` fields (`WeatherWorker.cs:96-155`).
- **No `Verse.Rand`** in light code.
- **Write only render state:** materials and shader globals.
- `GlowGridUpdate_First` runs inside ticks in MP (`Source/Client/AsyncTime/AsyncTimeComp.cs:169-176`), so a `map.events.GlowChanged` listener may only set a flag.

---

## 2. How IsoRim lights its 3D view today

IsoRim's 3D light has three layers, all visual only.

1. **Sun or moon shade on each face** (`IsoRim/Shaders/IsoRimSolid.frag.glsl:383-398`).
   - The face normal comes from screen derivatives and is snapped to 26 directions (`QUANTIZE 1`, `:23`, `:253-264`).
   - It is shaded against one global `_IsoSun` (`R3DMaterials.SetLight`, `IsoRim/Source/Models.cs:3554-3559`):
     - Models: 0.5–1.0 (`ModelShade`, `:267-271`).
     - Walls: 0.52–0.80 (`WallShade`, `:274-284`).
   - IsoRim's sun runs from azimuth 95° to 265° and elevation 22° → 62° → 22° (`SunDirAt`, `Models.cs:3464`). It slerps to the game's moon at night (`LightAt`, `:3538`).
2. **Night, lamp and indoor light.**
   - `IsoGlowMap` copies the overlay mesh's corner colours into a (W+1)×(H+1) bilinear texture `_IsoGlowTex` (`IsoRim/Source/Glow.cs:28-163`, postfix `Generated` `:410-418`).
   - A second "light pass" (`IsoRimLitMark*`, queue 3099, stencil bit 32) redraws every lit mesh and multiplies it by the game's overlay formula, sampled at the face's own spot (`IsoRimSolid.frag.glsl:287-296`, `:350-381`).
   - The game's overlay, pulled toward the camera, lights everything else.
3. **Shadows on the ground only:**
   - the game's footprint shadows, mirrored north–south by a `GenCelestial.GetLightSourceInfo` postfix (`IsoRim/Source/Patches.cs:591-592`, `:737-744`);
   - tree shadows (`Baking.cs:2992-3133`);
   - fixed "soft shadow" decals (`Baking.cs:3136-3400`).

### What looks flat or wrong, and why

| What you see | Why | Where |
|---|---|---|
| A lamp lights every face of a pawn or wall equally, front and back, top and bottom | The light pass uses only the cell's colour. There is no direction or height to the lamp. | `IsoRimSolid.frag.glsl:352-374` |
| Inside a roofed room, faces still have a "sunny" side and a 0.5 "shady" side | The sun shade never checks for a roof (indoor faces do get the game's 61% sky reduction through the light pass; what's missing is a roof-aware directional term) | `IsoRimSolid.frag.glsl:383-398` |
| Same strong contrast in rain, fog and at night as on a clear noon | The shade curve is fixed. `_IsoSun.w` is always 0, so the game's weather shadow strength is ignored. | `Models.cs:3558`; `SkyManager.cs:70-80` |
| No warm-sun / cool-shade contrast at dawn and dusk | Faces do turn warm at dusk, but only because the game's warm dusk sky colour tints everything (through `_IsoSky` and the overlay). There is no separate sun colour on the lit side against a cooler shaded side. IsoRim's sun colour only tints the sky disc. | `IsoRim/Source/Sky.cs:221,229`; `Weathers.xml:22` |
| Face shading and ground shadows disagree | IsoRim's sun and the game's cast vector differ by up to ~28° of azimuth and a lot of elevation. The postfix only mirrors north–south. | `Patches.cs:737-744` |
| No shadows on walls, pawns or models; pawns get a round blob | Only ground footprints exist. There is no depth texture or shadow map. | `Baking.cs:3409-3465` |
| Faceted, banded shading on round models | Normals snapped to 26 bins; meshes carry no normals (`vNormal` is reused for roof clipping) | `IsoRimSolid.frag.glsl:23,28` |
| No glow or halo around lamps or fire; the picture can't get "brighter than white" | 8-bit target. Overbright stops at about 1.5× through the overlay trick, and the grade clamps to 0..1. | `IsoRim/Shaders/IsoRimGrade.frag.glsl:71-77` |
| Upright game sprites take the light of the ground behind them on screen | The pulled overlay (`OverlayPull` 30) | `IsoRim/Source/View3D.cs:33,851-866` |
| In MP with async time, the 3D sun may follow world time rather than the map's (likely; not seen in game) | `View3D.LightNow` (`View3D.cs:1154-1161`) runs from the `CameraDriver.Update` postfix (`Patches.cs:1019-1025`), outside `Map.MapUpdate`. Only `MapUpdate` gets the map's own clock (`Source/Client/AsyncTime/SetMapTime.cs:42-57`). | |

**Cost today:** the light pass already draws every lit mesh twice (`Glow.cs:271-307`, `Patches.cs:1825`, `Instancing.cs:120-128`).

**Stale docs:** the `SunMovesTip` string, the "see SunStep" comment at `View3D.cs:256`, and `DEVELOPMENT_original.txt`'s LightStep section.

---

## 3. What a mod can and can't do on this engine

| Capability | Status | Notes |
|---|---|---|
| Custom GLSL shaders via ShaderDef + mod `Shaders` folder | Yes | Fragment file defines the material layout. Max 16 samplers, no uniform arrays in the block. `-WatchShaders` hot reload. Magenta on compile error. |
| Global uniforms and textures | Yes | IsoRim already uses `_IsoSun`, `_IsoGlowTex`, `_IsoCellTex` and others |
| A list of lights in a shader | Yes, best as a small data texture | IsoRim's `LiveTexture` (proven, one batched upload) can carry a few dozen lights. `Shader.SetGlobalVectorArray` also reaches shaders as a std430 `buffer` (`Material.ComposeStorageArrays`), but the first call switches the whole game onto a slower path: `GlobalShaderState.HasArrays` becomes true for the process, every material with a storage buffer (all vanilla instanced shaders) then recomposes its arrays whenever globals change, which is every frame (`_Time`), under a write lock, and each IsoRim material uploads its own copy when the bytes change. Other gotchas: the first call fixes the array length, an empty array throws, and an unset buffer slot is bound to some other buffer. Untested and unmeasured. |
| Upload data textures without a stall | Yes | Whole-image, batched uploads via IsoRim's `LiveTexture` (`IsoRim/Source/LiveTexture.cs`). Float `SetPixels` textures read as 0 (`IsoRim/Source/Cells.cs:19-20`). |
| Full-screen passes | Yes, but costly | `sceneGrab` in a ShaderDef. Each grab is a full-resolution 8-bit colour copy and splits the render pass, but only while that pass is drawn. |
| Read scene depth in a shader | No | Grabs copy colour only (`SceneGrabTargets.RecordGrab`). Terrain writes no depth (`IsoRim/Source/Sky.cs:175-180`). |
| Shadow maps | Possible, but too costly or too fragile | Supported but expensive: a capture-only layer (`LayerPassRouter.RegisterLayerMask`, as vanilla routes the gravship mask, `Bootstrap.cs:351-356`) plus a camera with a float `targetTexture`, or `Camera.Render`. Both wait on the GPU on the main thread every frame (`CameraTextureBaker.RecordAndSubmit`, `WaitForFence(-1)`) and draw instanced meshes non-instanced. Unsupported: a custom `IRenderFeature` (`EngineApplication.Instance.FrameRenderer.InsertFeature`), which is undocumented Vulkan-level internals. |
| Stencil shadow volumes | Possible (untested) | ShaderDefs expose the stencil state this needs (`stencilDepthFailOp`, IncrementWrap/DecrementWrap, cull Front/Back, colour writes off: `ShaderProgramDefinition.cs:21-39`). The engine applies one stencil state to both faces, so it takes two draws. Exact shadows from walls and models without reading depth, but heavy fill when zoomed out and a tight stencil-bit budget (the game's SunShadow uses bits 4 and 8, IsoRim's light pass 32); ground depth exists only in perspective. |
| `Graphics.Blit`, offscreen cameras | Work, but stall the main thread | Never use them every frame |
| HDR map target or tonemapping | No | The map target is 8-bit UNorm. Changing it is engine surgery. |
| MSAA | No | IsoRim uses FXAA |
| Unity `ComputeShader`, `CommandBuffer` | No (stubs) | |
| Waylith's own compute (`ComputeKernelCache`, `ComputeDispatchQueue.Enqueue`) | Probably | Public and asynchronous, and it runs before the map pass. No mod has used it yet. |
| Per-pass GPU timing | No | Time a pass by drawing it N extra times (IsoRim probe keys `tiltgpu`, `gradegpu`, `IsoRim/Source/ProbeScene.cs:42-48`) |

---

## 4. Improvement options, ranked by value for effort

Every option below only reads game state (GlowGrid, sky, glowers) and writes only IsoRim's own materials, textures and shader globals. None uses `Verse.Rand`. Any flicker comes from a stateless hash of time. Frame-time figures are estimates; nothing has been measured.

| # | Option | Size | Frame cost | Status |
|---|---|---|---|---|
| 1 | Sun shade that respects roofs, weather and time of day | Small | ~1 texture read per 3D pixel | Feasible now |
| 2 | Lamp light with facing and height on 3D faces | Small (first cut) to medium | A loop over ≤32 lights in the light pass | Feasible now (light list in a small data texture) |
| 3 | Lamp and fire halos (cheap bloom) | Small | 1 instanced draw | Feasible now |
| 4 | Sun shadows on 3D faces from IsoRim's cell heights, one sun for faces and ground | Medium | ~8–12 texture reads per sunlit 3D pixel | Feasible now (stencil shadow volumes are the exact but costlier alternative) |
| 5 | Ambient occlusion from cell heights, or baked into vertices | Small–medium | 2–4 reads per pixel, or 0 if baked | Feasible now |
| 6 | Smoother, leak-free coloured light (per-cell light index) | Medium | ≤4 lights per pixel; CPU work only when lights change | Feasible now |
| 7 | Light through open doors and windows | Medium | Small | Feasible; low value in vanilla |
| 8 | Screen-space bloom | Medium | 2+ full-resolution passes and grabs | Feasible but costly and 8-bit |
| 9 | Real shadow maps | Large | A stall, or an extra depth pass | Blocked in practice |
| 10 | SSAO | Large | — | Blocked (no depth) |
| 11 | HDR and tonemapping | Large | — | Blocked (engine surgery) |

### 1. Sun shade that respects roofs, weather and time of day (day/night sky ambience)
- **Player sees:**
  - Rooms under a roof lose the fake sunny and shady sides.
  - Overcast, rain and fog give soft, low-contrast faces.
  - Lit sides warmer than shaded sides at dawn and dusk, and faces pointing down slightly darker.
- **How:**
  - In `IsoRimSolid.frag.glsl` main pass (`:383-398`), sample `_IsoGlowTex` alpha at the face's spot. It is the game's sky cover: at least 0.39 under a roof, about 0 outside. Fade the directional shade toward a mild fixed shade as cover rises.
  - In C#, read `MatBases.SunShadow.color` after `SkyManagerUpdate`. It captures both inputs: the game's shadow strength (`CurShadowStrength`, which depends only on the time of day) and the weather's shadow colour (clear 0.718, rain and fog 0.92, `Weathers.xml:17, 65, 117`). The game also publishes `_CastVect.w` (shadow strength), `_WaterCastVectSun.w`/`_WaterCastVectMoon.w` (sun and moon light intensity) and `_DayPercent` every frame (`SkyManager.cs:74-78, 138`). Pass what's needed in `_IsoSun.w`, which is unused today (`Models.cs:3558`), and scale the 0.5 floor with it. (Adding new globals to the uniform block instead means updating `IsoRim.vert.glsl` and `IsoRimTex.vert.glsl` too.)
  - For dawn and dusk, add a warm sun colour on the lit side only and keep the shaded side neutral or slightly cool. The game's dusk sky colour (0.858, 0.650, 0.423, `Weathers.xml:22`) already warms every face through `_IsoSky`, so a warm term on both sides would warm everything twice; the sky is cool only at night (`Weathers.xml:28`).
  - None of this changes the uniform block, so `IsoRim.vert.glsl` and `IsoRimTex.vert.glsl` need no change.
  - Also move `SetLight(LightNow())` into `DrawMapMesh_Prefix` (`Patches.cs:714`). That prefix runs inside `Map.MapUpdate` (`Map.cs:1176`), so the sun follows the map's clock under async time. Check what else in `View3D.Tick` reads `LightDir` first.
- **Touches:** `IsoRimSolid.frag.glsl` (`ModelShade`/`WallShade` area), `R3DMaterials.SetLight`, `IsoGlow.BeginFrame` (`Glow.cs:359`).
- **Cost:** one extra bilinear fetch and about 10 ALU ops per 3D pixel.
- **Risk:** the overall look shifts, so keep the brightness at noon outdoors as it is today.

### 2. Lamp light with facing and height on 3D faces
- **Player sees:**
  - The side of a pawn, wall or table facing a lamp is brighter and the back is darker.
  - Wall tops near a floor lamp differ from their feet.
  - A campfire lights faces from below and the side.
- **How:**
  - Read the map's lights, read only, inside `Map.MapUpdate` (safe: `GlowGridUpdate_First` finishes its jobs synchronously, `GlowGrid.cs:418-445`). Take them from `GlowGrid.lights`, not only `litGlowers`, so glowing terrain and custom glowers count too (`RegisterTerrain`/`RegisterCustomGlower`, `TerrainGrid.cs:251ff`; Odyssey has glowing terrain). Both fields are private: use `AccessTools.FieldRefAccess` delegates, since IsoRim has no Publicizer. The `CompGlower` `GlowRadius`/`GlowColor` getters are pure.
  - Keep up to 32 near the view, in a stable order (for example by `thingIDNumber`), and upload them only when the set or the view's light set changes, as a small `LiveTexture` (two texels per light).
  - In the light-pass branch (`IsoRimSolid.frag.glsl:370-374`), weight each lamp by the game's falloff and multiply the glow texture's RGB by a facing factor normalised to average 1 over directions (above 1 toward lamps, below 1 away). The glow texture stays the brightness baseline, so a room the game calls dark still looks dark, and the simulation's lit/dark state still matches the screen.
- **Touches:**
  - A new collector (for example `IsoRim/Source/Lamps.cs`) called from `IsoGlow.BeginFrame`.
  - The light pass in `IsoRimSolid.frag.glsl`, plus the buffer declared in that fragment file.
  - A setting.
  - Plant cards (`IsoRimCardMark.frag.glsl`) can stay as they are.
- **Cost:** a loop over at most 32 lights only in the light pass. Estimated well under 1 ms at 1080p on a desktop GPU; must be measured.
- **Risks:**
  - Without an occlusion test, a lamp behind a wall can tilt the facing of faces next door. Brightness stays right; option 6 removes this.
  - Stacked floors (`_IsoFloorGlow`) need a floor filter. Skip lights when floors are present in the first cut.

### 3. Lamp and fire halos (cheap bloom)
- **Player sees:** a soft coloured glow around lit lamps, torches and fires at night, hidden behind walls.
- **How:**
  - One instanced draw of camera-facing quads with a radial texture at each lit glower, additive at queue 3151 (IsoRim's `IsoRimAdd` blend, `IsoRim/Source/Beams.cs:13-16`).
  - Size them from the glow radius, colour them from `GlowColor`, and scale intensity by darkness (1 − sky luminance) so they fade by day.
  - Depth-tested so walls and roofs hide them.
  - Fire already has vanilla glow motes, so tone fire halos down.
  - Optional: give "glow" materials (flames, screens) a truly unlit mode so the light pass doesn't multiply them. Today every solid material goes through the cell's light (`IsoGlow.Use(solid, OwnCell)` in the generic setup around `Models.cs:3787-3798`). This matters mostly for glowing parts of things that aren't glowers.
- **Touches:** shares the light list with option 2; adds a new small shader def.
- **Cost:** 1 draw call and a little fill.
- **Status:** feasible now.

### 4. Sun shadows on 3D faces (height field), one sun for faces and ground
- **Player sees:**
  - Walls and rock cast shadows onto other walls, pawns and furniture.
  - Ground shadows point the same way as the face shading.
- **How:**
  - `_IsoCellTex` already holds each cell's top height (r,g: 16-bit, 1/4096 cell) and is bound in `IsoRimSolid` (`IsoRim/Source/Cells.cs:11-16`; read at `IsoRimSolid.frag.glsl:166-170`). The top does not include IsoRim's cut-away: apply the b channel (how far the block is cut down) as `FogLid` does (`IsoRimSolid.frag.glsl:170`).
  - In the main pass, for sunlit outdoor faces only, step from cell to cell toward `_IsoSun` for up to about 10 cells. Compare the ray height with each cell's top and use the result to switch off the direct term. Also read `_IsoRoofTex` (binding 5), or sun shines through roofed buildings' interiors. Stacked floors shift rows and need the same handling as elsewhere.
  - Drive the game's ground shadows from the same sun: change the existing `GetLightSourceInfo` postfix (its only callers are visual: `SkyManager.cs:69-73`, `SectionLayer_SunShadows.cs:164`, the dev celestial dialog), or overwrite the `_CastVect` global after `SkyManagerUpdate`. Overwriting `_CastVect` drops the game's lightning-flash shadow override (`SkyManager.cs:62-66`), so keep that case.
- **Limits:**
  - Pawns, trees and furniture don't cast through the height field; keep blobs and tree shadows for those.
  - Decide whether walls lowered by the cut-away still cast shadows.
- **Cost:** about 8–12 texel fetches per sunlit outdoor 3D pixel, by day only.
- **Risk:** the shadow length in squashed space needs care. The `_CastVect` override is untested.

### 5. Ambient occlusion
- **Player sees:** darker room corners, wall feet, and gaps between furniture and walls on the 3D faces.
- **How:**
  - **Runtime:** in the main pass, read the 2–4 neighbouring cell tops from `_IsoCellTex` and darken fragments near the foot of a taller neighbour.
  - **Baked:** extend the existing vertex darkening (`BottomShade`/`LineShade`, `Baking.cs:690-694, 873-884`) to look at neighbours when blocks rebuild. This costs nothing per frame.
  - The ground already has the `AroundBlock` bands (`Baking.cs:3315-3330`).
- **Real SSAO** is blocked (option 10).
- **Status:** feasible now.

### 6. Smoother, leak-free coloured light (finer than one value per cell)
- **Player sees:** smooth lamp pools with sub-cell gradients on faces, no light bleeding through walls, and coloured lamps mixing per pixel.
- **How:**
  - On the CPU, build a per-cell index texture (RGBA8: the 4 strongest lights that actually reach each cell) whenever lights change.
    - Mark it dirty when the glower set changes, or from `GlowChanged` (flag only).
    - "Reaches" comes from IsoRim's own wall test on the cell data, or from the game's per-light flood areas (private `GlowGrid` pool).
  - Upload it with `LiveTexture`.
  - The shader shades only those ≤4 lights. This bounds the per-pixel cost and replaces option 2's brute-force loop.
  - To extend it to the ground, give `IsoRimLightOverlay` the same term. Its fragments are pulled toward the camera, so move each one back along the view direction to find its ground spot.
  - Normalise so each cell's average stays at the game's glow value.
- **Alternatives:** a wall-aware 2–4× upsample of the glow texture on change, or Waylith compute (untested).

### 7. Light through open doors and windows
- **Player sees:** a sun patch on the floor through an open door by day, and a faint spill of a lit room's light out of an open door at night.
- **How:**
  - A floor quad projected from the door opening along the sun, drawn after the overlay, by day only.
  - Allow the option 6 index to pass one or two cells through doors where `Building_Door.Open` is true, at reduced weight.
- **Caveat:** in the simulation, doors block light open or shut, and vanilla has no windows. Keep the spill faint so the screen doesn't call a dark cell lit. Window mods are untested.

### 8. Screen-space bloom
- **How:** a threshold-and-blur pass from scene grabs just before the grade (4900).
- **Cost:** at least two full-resolution passes plus grabs, about as much as tilt-shift's two 15-tap passes (time it with the `tiltgpu` probe).
- **Limits:** 8-bit, so it can't tell a lamp from a white wall unless lamps are marked somehow (untested idea: the alpha channel).
- **Status:** try only if halos (option 3) aren't enough.

### 9–11. Shadow maps, SSAO, HDR
- **Shadow maps:**
  - `Camera.Render` stalls the main thread, sees only this frame's queued draws, and draws IsoRim's instanced meshes as non-instanced.
  - A capture-only layer plus a camera with a float target works through supported APIs but stalls the main thread every frame.
  - A custom render feature (depth-only `MeshBatchRenderer.RecordPass` into a D32 RenderTexture, with manual PCF because there are no comparison samplers) is large and breaks on game updates. 1.7.4990 already moved `TexturePixelData` (`LiveTexture.cs:99-101`).
  - Stencil shadow volumes (section 3) give exact shadows without depth reads; worth a prototype only if the height-field march (option 4) looks too coarse.
- **SSAO:** needs depth, which material shaders can't read.
- **HDR:** would mean replacing `SceneColorTargets`, the grab targets and `CompositePass` input in RimWorld.dll.
- Not recommended.

---

## 5. Recommended first step: option 2, first cut (lamp facing on 3D faces)

This gives the biggest visible change at night and stays small. Its light list (a small data texture) is also what options 3 and 6 build on.

**What to build**
1. **C#, new `IsoRim/Source/Lamps.cs`,** called at the end of `IsoGlow.BeginFrame` (`Glow.cs:359`, inside `Map.MapUpdate`):
   - Read `GlowGrid.lights` (and `litGlowers` if simpler for a first cut) through one-time `AccessTools.FieldRefAccess` delegates. Read only.
   - Keep lights whose radius reaches `CameraDriver.CurrentViewRect`, up to 32, in a stable order (by `thingIDNumber`, cell index for terrain).
   - Write them into a 64×1 RGBA8 `LiveTexture` (two texels per light: position x, z as 16-bit like the cell heights in `Cells.cs`, radius and height; colour), plus the count in a global float. Upload only when the list changes. Write count 0 when the new setting is off or stacked floors are present.
2. **GLSL, `IsoRimSolid.frag.glsl`.** Bind the texture as a new global sampler (like `_IsoCellTex`; the shader has room up to 16 samplers) and scale the glow in the light pass:
   ```glsl
   float LampFacing(vec3 p, vec3 n)   // 1 = unchanged on average
   {
       int count = int(_IsoLampCount);
       float sw = 0.0, sf = 0.0;
       for (int i = 0; i < min(count, 32); i++) {
           vec4 a = LampPosRadius(i);          // decoded from texel 2i: x, height, z, radius (cells)
           vec3 d = a.xyz - p;
           float r = max(1.0, length(d));
           if (r > a.w) continue;
           float w = mix(1.0 - r / a.w, 1.0 / (r * r), 0.4) * dot(LampColor(i), vec3(0.333));
           sw += w;
           sf += w * (1.0 + 0.5 * dot(n, d / length(d)));   // 0.5 facing away .. 1.5 facing the lamp; averages 1 over directions
       }
       return sw > 1e-4 ? sf / sw : 1.0;
   }
   ```
   Then after `g = textureLod(_IsoGlowTex, uv, 0.0);` (`:372`), add `if (lightPass) g.rgb *= LampFacing(p, faceNormal);`. `p` and `faceNormal` are already true-space values (`:316-322`). Tune the strength (0.5) in game; a term of the form 1 + k·cos keeps the average at 1, so rooms keep their brightness (a clamped max(0, cos) term would darken them).
3. **A setting** "Lamps light faces by direction" (default off until checked) with an English key, so you can compare on and off.

**How to check it in game**
1. **Build and install.** Build in the container (`dotnet build Source/Multiplayer.sln -c Release`), then install from the host (`distrobox-host-exec env SKIP_BUILD=1 ./scripts/install_local.sh`). Launch with `DOTNET_TieredCompilation=0`. Adding `-WatchShaders` lets you tune the GLSL live; this is untested under Proton.
2. **Look at it.** In dev mode, put a standing lamp in a closed, roofed room with a pawn and a table, at night. Switch the 3D view on and turn the camera around the pawn (Q/E).
   - Expected: the side facing the lamp is brighter and the far side darker, walls brighter toward the lamp, and the room's overall brightness about the same with the setting on or off.
   - If nothing changes when toggling, the light texture or its count isn't arriving. Report that.
   - A magenta mesh means the shader failed to compile; the reason is in Player.log.
3. **Frame time.** In a base with many lamps at night, zoomed out, note "Frame Time … Avg" from Multiplayer's in-game debug text (`Source/Client/UI/IngameDebug.cs:51`) with the setting on and off. Report both numbers. If the difference is over about 0.5 ms, lower the cap or move to option 6.
4. **Multiplayer.** Run host and client on the same build, one with 3D and the setting on, the other with them off. Play about 20 minutes through a night. Report:
   - any desync;
   - any `[IsoRim]` errors in either Player.log (the Proton log path is in CLAUDE.md).

   No desync is expected because nothing is written to game state.
