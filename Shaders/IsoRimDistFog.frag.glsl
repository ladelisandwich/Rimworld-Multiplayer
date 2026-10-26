#version 450

// IsoRim distance fog: the fog colour at the layer's alpha (see IsoRimDistFog.vert.glsl).

layout(location = 0) in float vAlpha;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _FogColor;    // @default = vec4(0.75, 0.82, 0.9, 1.0)
    vec4 _FogDepth;    // @default = vec4(1.0, 500.0, 100.0, 200.0)
    vec4 _FogShape;    // @default = vec4(1.5, 24.0, 1.0, 0.0)
} u;

void main()
{
    outColor = vec4(u._FogColor.rgb, vAlpha);
}
