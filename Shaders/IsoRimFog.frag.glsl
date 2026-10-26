#version 450

// IsoRim: the game's Data\Core\Shaders\Map\FogOfWar.frag.glsl (1.7.4976) unchanged apart from the
// global members and the cell texture that IsoRimFog.vert.glsl reads (the material layout is read
// from this stage). Compare with the game's file after a game update.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv; // object-space XZ

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _IsoView;    // global, no default: w = Squash
    vec4 _IsoCellMap; // global, no default: origin x, origin z, width, height of the cell texture
    vec4 _IsoLevel;   // global, no default: x = the map's ground height in cells
    vec4 _IsoLid;     // @default = vec4(0.0, 0.0, 0.0, 0.0)   vertex stage only: x = 1 where fog under a shown floor gets no lid
    float _IsoFade;   // @default = 0.0   faded floors (FadeFloors): share of pixels kept, 0 = drawn solid
    float _IsoFadeFrom; // @default = 0.0   faded floors: where in the dither pattern (0-1) the kept share starts
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DetailTex;
layout(set = 2, binding = 3) uniform sampler2D _IsoCellTex; // @vertex @default = black

// ---- Faded floors (Source/FadeFloors.cs, prototype): a fragment of a floor above the viewed one is dropped where the
// 4 x 4 ordered dither pattern, fixed on the screen, is outside the floor's kept range: _IsoFade wide (0 = drawn solid), from
// _IsoFadeFrom, wrapping round (floors with overlapping ranges cover each other only there).
const float FadeBayer[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

bool FadeDrop()
{
    float keep = uMaterial._IsoFade;
    if (keep <= 0.0)
        return false;
    ivec2 q = ivec2(floor(gl_FragCoord.xy)) & 3;
    return fract((FadeBayer[q.y * 4 + q.x] + 0.5) / 16.0 - uMaterial._IsoFadeFrom) >= keep;
}

void main()
{
    if (FadeDrop())
        discard;
    vec2 uv = vUv / 64.0;
    uv.y = -uv.y;
    vec4 mainCol = texture(_MainTex, uv);
    vec4 detailCol = texture(_DetailTex, uv * 8.0);
    outColor = mainCol * detailCol * vColor * uMaterial._Color;
}
