#version 450

// IsoRim 3D view: vertex stage of the IsoRimFog def, the fog of unexplored areas drawn as a lid
// lying on the tops of the rock and walls under it. The game's MapWorldUV.vert.glsl (same inputs
// and outputs; the texture follows the flat x and z, so lifting does not move it) plus the lift:
// each corner of the game's fog mesh (corners, edge middles and centres of the cells) rises to the
// highest top among the cells it touches, read from the map's cell texture (IsoCellMap: one texel
// per cell, red and green = top height in 1/4096 cells), and a little above it. The mesh itself is never changed,
// so the lid follows mining, building and cut-away the same frame the cell texture does.
//
// Per storey (upper floors): each corner reads its own cell (the section mesh's own map rows, inPosition, not the
// ground rows the floor's matrix moves it onto), so a storey's lid lies on that storey's tops. With _IsoLid.x = 1
// (the storey above this one is shown) a fogged cell with a floor right above it (cell bit 8) is "covered": no lid
// there, the floor above (and its own lid, if fogged) is what shows. A corner touching a covered cell lies on the
// uncovered cells only; if it touches no uncovered fogged cell it is clear (alpha 0), so the half-cell fade dips
// into the covered column instead of lying level with the floor above. Corners touching no covered cell are lifted
// as before (maps without floors, the lid with nothing shown above: unchanged).

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor;    // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv; // object-space XZ; fragment divides by the per-shader tiling size

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

// Must match IsoRimFog.frag.glsl exactly (the layout is read from the fragment stage).
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;
    vec4 _IsoView;    // global: w = Squash
    vec4 _IsoCellMap; // global: origin x, origin z, width, height (cells) of the cell texture
    vec4 _IsoLevel;   // global: x = the map's ground height in cells (0 today)
    vec4 _IsoLid;     // per material: x = 1 where a fogged cell under a shown floor gets no lid
} uMaterial;

layout(set = 2, binding = 3) uniform sampler2D _IsoCellTex;

// How high the lid sits above the tops (cells), as the 1.5 lift.
const float Above = 0.012;

// The top of the cell at p (x), and its kind (y): 0 open, 1 fogged, 2 fogged under a shown floor (covered).
vec2 TopAt(vec2 p, ivec2 size)
{
    ivec2 c = ivec2(floor(p - uMaterial._IsoCellMap.xy));
    if (c.x < 0 || c.y < 0 || c.x >= size.x || c.y >= size.y)
    {
        return vec2(0.0);
    }
    // Rows are stored north first (the texture upload flips them).
    vec4 t = texelFetch(_IsoCellTex, ivec2(c.x, size.y - 1 - c.y), 0);
    float full = (round(t.r * 255.0) * 256.0 + round(t.g * 255.0)) / 4096.0;   // 16-bit height, 1/4096 cell steps
    int bits = int(round(t.a * 255.0));
    float kind = (bits & 4) == 0 ? 0.0 : (uMaterial._IsoLid.x > 0.5 && (bits & 8) != 0 ? 2.0 : 1.0);
    return vec2(full * (1.0 - (1.0 - uMaterial._IsoLevel.y) * t.b), kind);   // (shader cut-away) b = how far the block is cut down
}

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    ivec2 size = ivec2(uMaterial._IsoCellMap.zw);
    float squash = uMaterial._IsoView.w;
    vColor = inColor;
    if (squash > 0.0 && size.x > 0)
    {
        // The cells touching this corner: a little to each side of it (a corner touches four,
        // an edge middle two, a cell centre one), in the mesh's own rows.
        vec2 a = TopAt(inPosition.xz + vec2(-0.02, -0.02), size), b = TopAt(inPosition.xz + vec2(0.02, -0.02), size);
        vec2 c = TopAt(inPosition.xz + vec2(-0.02, 0.02), size), d = TopAt(inPosition.xz + vec2(0.02, 0.02), size);
        float top;
        if (max(max(a.y, b.y), max(c.y, d.y)) < 1.5)
        {
            top = max(max(a.x, b.x), max(c.x, d.x));
        }
        else
        {
            // a covered cell among them: lie on the others; clear unless one of them is fogged
            top = max(max(a.y < 1.5 ? a.x : 0.0, b.y < 1.5 ? b.x : 0.0), max(c.y < 1.5 ? c.x : 0.0, d.y < 1.5 ? d.x : 0.0));
            if (a.y != 1.0 && b.y != 1.0 && c.y != 1.0 && d.y != 1.0)
            {
                vColor.a = 0.0;
            }
        }
        world.y += (uMaterial._IsoLevel.x + top + Above) / squash;
    }
    gl_Position = uVertex.ViewProj * world;
    vUv = inPosition.xz;
}
