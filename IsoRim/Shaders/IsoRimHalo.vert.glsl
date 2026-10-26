#version 450

// (Port) IsoRim 3D view: lamp and fire halos (Source/Halos.cs, docs/isorim-lighting.md option 3), vertex stage of the
// IsoRimHalo def. One instanced draw of a quad with corners x, y = -1..1 (Halos.QuadMesh) for the lit lamps on screen:
// each copy is stood square to the camera at its light, pulled a little toward the camera so the lamp's own body does
// not hide it, sized by its glow radius and faded by the night. Instanced only (Halos.Draw); a plain draw of this
// program puts every corner outside the view.
//
// A copy's "matrix" is a data record written by Halos.Pack, not a transform:
//   [0] rgb = the glow colour (brightest channel 1), a = the halo's strength (colour brightness and kind in)
//   [1] x = radius (true cells), y = flicker amount (0 steady, 1 a fire), z = flicker phase, w = 1 under a roof
//   [2] unused (0)
//   [3] xyz = the light in true space (x, height above the map's ground, z), w = 1. Column 3 is where a transform keeps
//       its translation (IsoRim.vert.glsl reads model[3] as the origin), so the engine's sort centre lies near the lights.

layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

#ifdef INSTANCED
layout(std430, set = 2, binding = 2) readonly buffer _InstanceTransforms
{
    mat4 instanceTransforms[];
};
#endif

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

// Must match IsoRimHalo.frag.glsl exactly (the layout is read from the fragment stage).
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _HaloRight;  // @default = vec4(1.0, 0.0, 0.0, 0.003)   xyz = the camera's right (true space), w = Squash
    vec4 _HaloUp;     // @default = vec4(0.0, 1.0, 0.0, 0.5)     xyz = the camera's up (true space), w = pull toward the camera (true cells)
    vec4 _HaloEye;    // @default = vec4(0.0, 0.0, 0.0, 0.0)     xyz = the eye (true space), w = 1 with a perspective camera
    vec4 _HaloFrame;  // @default = vec4(0.0, 1.0, 1.0, 0.0)     x = game time in seconds (flicker), y = the sky's luminance, z = the setting's strength
} uMaterial;

const float NightFrom = 0.55;   // sky luminance at or below which a halo is at full strength (clear night 0.58, caves 0.37)
const float NightTo = 0.90;     // and from which it is gone (clear day 1.0; rain or fog by day 0.8 leave about a fifth)
const float RoofDark = 0.2;     // a roof over the light counts as this share less sky (the game's overlay: 0.39)

void main()
{
#ifdef INSTANCED
    mat4 d = instanceTransforms[gl_InstanceIndex];
    vec4 colour = d[0];
    vec4 shape = d[1];
    vec3 light = d[3].xyz;

    float sky = uMaterial._HaloFrame.y * (1.0 - RoofDark * shape.w);
    float night = 1.0 - smoothstep(NightFrom, NightTo, sky);
    float t = uMaterial._HaloFrame.x;
    float flicker = 1.0 + shape.y * (0.12 * sin(t * 7.0 + shape.z) + 0.06 * sin(t * 13.1 + shape.z * 1.7));
    float k = colour.a * night * flicker * uMaterial._HaloFrame.z;

    // Toward the camera: one direction for the orthographic camera, toward the eye with perspective.
    vec3 right = uMaterial._HaloRight.xyz;
    vec3 up = uMaterial._HaloUp.xyz;
    vec3 toward = cross(up, right);
    if (uMaterial._HaloEye.w > 0.5)
    {
        vec3 v = uMaterial._HaloEye.xyz - light;
        float lv = length(v);
        if (lv > 1e-4)
            toward = v / lv;
    }
    vec3 c = light + toward * uMaterial._HaloUp.w + (inPosition.x * right + inPosition.y * up) * shape.x;
    float squash = max(uMaterial._HaloRight.w, 1e-5);
    vec4 world = vec4(c.x, c.y / squash, c.z, 1.0);   // drawn space: heights divided by Squash, as every IsoRim mesh
    gl_Position = k > 0.004 ? uVertex.ViewProj * world : vec4(2.0, 2.0, 0.5, 1.0);   // nothing to add: outside the view, all corners alike
    vColor = vec4(colour.rgb * k, 1.0);
    vUv = inPosition.xy;
#else
    gl_Position = vec4(2.0, 2.0, 0.5, 1.0);
    vColor = vec4(0.0);
    vUv = vec2(0.0);
#endif
}
