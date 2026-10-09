struct BvhTriangleSurface
{
    vec2 uv0;
    vec2 uv1;
    vec2 uv2;
    uint albedo;
    uint padding;
};

layout(std430, binding = 2) readonly buffer BvhSurfacesBuffer { BvhTriangleSurface surfaces[]; };

vec2 GetTriHitUV(uint triIdx, float u, float v)
{
    BvhTriangleSurface s = surfaces[triIdx];
    return (1.0 - u - v) * s.uv0 + u * s.uv1 + v * s.uv2;
}