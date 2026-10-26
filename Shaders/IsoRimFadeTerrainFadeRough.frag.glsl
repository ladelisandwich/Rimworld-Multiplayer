#version 450

// Faded floors (Source/FadeFloors.cs, prototype): the game's TerrainFadeRough.frag.glsl (1.7) with the FadeFloors drop added
// (_IsoFade, FadeDrop). Re-diff on game updates.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv; // object-space XZ

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _IsoFade;   // @default = 0.0   faded floors (FadeFloors): share of pixels kept, 0 = drawn solid
    float _IsoFadeFrom; // @default = 0.0   faded floors: where in the dither pattern (0-1) the kept share starts
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _AlphaAddTex; // noise; white default -> smooth edges

float roughFade(vec2 position, float rawAlpha)
{
    const float noiseImportance = 0.6;
    const float exPart = 1.0 - noiseImportance;

    float noise = texture(_AlphaAddTex, position * 2.0).r * 0.333
                + texture(_AlphaAddTex, position * 5.0).g * 0.333
                + texture(_AlphaAddTex, position * 10.0).b * 0.333;

    float alpha = mix(rawAlpha, noise, noiseImportance);

    // Expand the middle of the alpha to make a sharper line.
    alpha -= (1.0 - exPart) * 0.5;
    alpha /= exPart;

    const float clampSpeed = 1.5;
    alpha = clamp(alpha, max(rawAlpha * clampSpeed - clampSpeed + 1.0, 0.0), min(rawAlpha * clampSpeed, 1.0));
    return alpha;
}

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
    vec2 uv = vUv / 16.0;
    uv.y = -uv.y; 
    vec4 rawCol = texture(_MainTex, uv) * vColor;
    vec4 finalCol = rawCol;
    finalCol.a = roughFade(uv, rawCol.a);
    outColor = finalCol * uMaterial._Color;
}
