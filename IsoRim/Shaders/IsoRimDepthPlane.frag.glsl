#version 450

// IsoRim depth plane: nothing is written but depth (colour writes are off in its ShaderDef).

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} u;

void main()
{
    outColor = u._Color;
}
