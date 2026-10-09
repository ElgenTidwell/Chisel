#version 430
layout(local_size_x = 8, local_size_y = 8) in;

layout(r32i, binding = 0) uniform readonly iimage2D srcSourceBrush;
layout(r32i, binding = 1) uniform readonly iimage2D srcEntityGroup;

layout(std430, binding = 4) writeonly buffer TexelOwnerBuffer { ivec2 texelOwner[]; };

void main()
{
    ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
    ivec2 size = imageSize(srcSourceBrush);
    if (texel.x >= size.x || texel.y >= size.y) return;

    int idx = texel.y * size.x + texel.x;
    texelOwner[idx] = ivec2(imageLoad(srcSourceBrush, texel).r, imageLoad(srcEntityGroup, texel).r);
}