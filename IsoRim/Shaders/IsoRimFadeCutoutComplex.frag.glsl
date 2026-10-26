#version 450

// Faded floors (Source/FadeFloors.cs, prototype): the game's CutoutComplex.frag.glsl (1.7) with the FadeFloors drop added
// (_IsoFade, FadeDrop). Re-diff on game updates.

#include "MaskGate.glsl"

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _ColorTwo;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _IsoFade;   // @default = 0.0   faded floors (FadeFloors): share of pixels kept, 0 = drawn solid
    float _IsoFadeFrom; // @default = 0.0   faded floors: where in the dither pattern (0-1) the kept share starts
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex; // @default = black

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
    vec2 uv = vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;
    vec4 f = texture(_MainTex, uv);
    vec4 colPower = texture(_MaskTex, uv);
    colPower.rg *= MaskGate(colPower);

    vec4 colMultiplier = colPower.r * (uMaterial._Color * vColor) + (1.0 - colPower.r);
    f *= colMultiplier;

    vec4 colTwoMultiplier = colPower.g * uMaterial._ColorTwo + (1.0 - colPower.g);
    f *= colTwoMultiplier;

    if (f.a < 0.5)
        discard;
    outColor = f;
}
