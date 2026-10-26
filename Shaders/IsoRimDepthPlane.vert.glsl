#version 450

// IsoRim depth plane: the map's ground as one flat quad that only writes depth (no colour), drawn
// before the distance fog. The game's terrain writes no depth, so without it the fog and the sky
// would take the bare ground for empty space.

layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0);
}
