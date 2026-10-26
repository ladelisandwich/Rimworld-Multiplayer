#version 450

// IsoRim distance fog (perspective camera only), with no depth texture: the mesh is a stack of
// full-screen quads, one per layer, each placed at a distance in front of the camera and drawn with
// the depth test on and the fog colour at a small alpha. A layer only shows on pixels whose scene
// is further away than it, so a pixel gathers the layers in front of it: fog by real distance.
// Layer i (uv0.x = (i + 1) / layers) sits at mix(start, end, t); its alpha makes the total after
// it equal to the fog curve, so the last layer (t = 1, at end) is solid.

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 3) in vec4 inUv0;      // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(location = 0) out float vAlpha;

// Must match IsoRimDistFog.frag.glsl exactly (the layout is read from the fragment stage).
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _FogColor;    // @default = vec4(0.75, 0.82, 0.9, 1.0)
    vec4 _FogDepth;    // @default = vec4(1.0, 500.0, 100.0, 200.0)   near, far, start, end (view distance)
    vec4 _FogShape;    // @default = vec4(1.5, 24.0, 1.0, 0.0)          x = curve power, y = layers, z = strength
} u;

float Curve(float t)
{
    return pow(clamp(t, 0.0, 1.0), u._FogShape.x) * clamp(u._FogShape.z, 0.0, 1.0);
}

void main()
{
    float t = inUv0.x;
    float layers = max(u._FogShape.y, 1.0);
    float tPrev = t - 1.0 / layers;
    float d = mix(u._FogDepth.z, u._FogDepth.w, t);
    float n = u._FogDepth.x;
    float f = u._FogDepth.y;
    // The engine's perspective (reverse-Z): depth = n (f - d) / (d (f - n)).
    float z = clamp(n * (f - d) / (d * (f - n)), 0.0, 1.0);
    gl_Position = vec4(inPosition.xy, z, 1.0);
    float now = Curve(t);
    float before = tPrev > 0.0 ? Curve(tPrev) : 0.0;
    vAlpha = now >= 0.9999 ? 1.0 : clamp(1.0 - (1.0 - now) / max(1.0 - before, 1e-4), 0.0, 1.0);
}
