#version 450

// (Port) IsoRim 3D view: lamp and fire halos (Source/Halos.cs), fragment stage of the IsoRimHalo def (blend Additive:
// SrcAlpha One, so it only brightens). The falloff is worked out here, no texture: a soft wide glow that reaches 0 at
// the quad's rim, plus a small heart that goes part way to white. vColor = the glow colour times the halo's strength
// (IsoRimHalo.vert.glsl), vUv = -1..1 across the quad.

#ifdef INSTANCED
// Read by the vertex stage; declared here because the engine takes the material layout (and so this binding) from the
// fragment stage.
layout(std430, set = 2, binding = 2) readonly buffer _InstanceTransforms
{
    mat4 instanceTransforms[];
};
#endif

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

// Must match IsoRimHalo.vert.glsl exactly.
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _HaloRight;  // @default = vec4(1.0, 0.0, 0.0, 0.003)   xyz = the camera's right (true space), w = Squash
    vec4 _HaloUp;     // @default = vec4(0.0, 1.0, 0.0, 0.5)     xyz = the camera's up (true space), w = pull toward the camera (true cells)
    vec4 _HaloEye;    // @default = vec4(0.0, 0.0, 0.0, 0.0)     xyz = the eye (true space), w = 1 with a perspective camera
    vec4 _HaloFrame;  // @default = vec4(0.0, 1.0, 1.0, 0.0)     x = game time in seconds (flicker), y = the sky's luminance, z = the setting's strength
} uMaterial;

const float GlowShare = 0.6;    // the wide glow's share of the peak
const float HeartShare = 0.4;   // the heart's share
const float HeartTight = 30.0;  // how small the heart is (down to 1/e at a fifth of the radius)
const float HeartWhite = 0.5;   // how far the heart goes toward white

void main()
{
    float r2 = dot(vUv, vUv);
    float t = max(0.0, 1.0 - sqrt(r2));
    float glow = t * t;
    float heart = exp(-r2 * HeartTight) * t;
    vec3 c = vColor.rgb;
    vec3 hot = mix(c, vec3(max(c.r, max(c.g, c.b))), HeartWhite);
    outColor = vec4(c * (GlowShare * glow) + hot * (HeartShare * heart), 1.0);
}
