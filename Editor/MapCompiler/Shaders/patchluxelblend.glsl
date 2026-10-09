#version 430
layout(local_size_x = 8, local_size_y = 8) in;

layout(rgba32f, binding = 0) uniform readonly image2D gPosition;
layout(rgba16f, binding = 1) uniform readonly image2D gNormal;
layout(rgba16f, binding = 2) uniform readonly image2D gBasis1;
layout(rgba16f, binding = 3) uniform readonly image2D gBasis2;
layout(rgba16f, binding = 4) uniform readonly image2D gBasis3;

layout(std430, binding = 3) readonly buffer PatchesBuffer { GpuPatch patches[]; };
layout(std430, binding = 4) readonly buffer TexelHomePatchBuffer { int texelHomePatch[]; };
layout(std430, binding = 5) readonly buffer PatchFinalValuesBuffer { vec4 patchValues[]; };
layout(std430, binding = 6) readonly buffer NeighborCountBuffer { int neighborCount[]; };
layout(std430, binding = 7) readonly buffer NeighborIndicesBuffer { int neighborIndices[]; };


uniform int rowStart;
uniform float blendRadius;
uniform int maxNeighbors;

void main()
{
    ivec2 texel = ivec2(int(gl_GlobalInvocationID.x), int(gl_GlobalInvocationID.y) + rowStart);
    ivec2 size = imageSize(gPosition);
    if (texel.x >= size.x || texel.y >= size.y)
    {
        return;
    }

    vec4 posValid = imageLoad(gPosition, texel);
    if (posValid.a < 0.5)
    {
        return;
    }

    int texelIdx = texel.y * size.x + texel.x;
    int homePatch = texelHomePatch[texelIdx];
    if (homePatch < 0)
    {
        return;
    }

    vec3 worldPos = posValid.rgb;
    vec3 normal = imageLoad(gNormal, texel).rgb;
    vec3 basis1 = imageLoad(gBasis1, texel).rgb;
    vec3 basis2 = imageLoad(gBasis2, texel).rgb;
    vec3 basis3 = imageLoad(gBasis3, texel).rgb;
    vec3 origin = worldPos + normal * 0.02;

    vec3 homeColor = patchValues[homePatch].rgb;

    vec3 accum1 = vec3(0);
    vec3 accum2 = vec3(0);
    vec3 accum3 = vec3(0);
    float sumW1 = 0.0, sumW2 = 0.0, sumW3 = 0.0;

    int count = min(neighborCount[homePatch], maxNeighbors);
    for (int k = 0; k < count; k++)
    {
        int nid = neighborIndices[homePatch * maxNeighbors + k];
        // float coarseVisibility = neighborVisibility[homePatch * maxNeighbors + k];
        GpuPatch np = patches[nid];

        vec3 diff = np.center - worldPos;
        float dist = length(diff);
        if (dist > blendRadius)
        {
            continue;
        }

        float ndot = dot(normal, np.normal);
        if (ndot <= 0.0)
        {
            continue;
        }

        // float visibility = coarseVisibility;
        float visibility = 0;

        // if (coarseVisibility < 1.0)
        // {
            vec3 dir = diff / max(dist, 1e-5);
            visibility = TraceRayBvhAny(origin, dir, dist - 0.05, -1, -1) ? 0.0 : 1.0;
        // }

        if (visibility <= 0.0)
        {
            continue;
        }

        float distClamped = max(dist, blendRadius * 0.1);
        float spatialW = 1.0 - distClamped / blendRadius;
        float normalW = pow(max(0.0, ndot), 6.0);
        float baseW = spatialW * normalW * visibility;

        vec3 dirToPatch = diff / distClamped;
        float dirFactor1 = max(0.0, 1.0 + dot(basis1, dirToPatch));
        float dirFactor2 = max(0.0, 1.0 + dot(basis2, dirToPatch));
        float dirFactor3 = max(0.0, 1.0 + dot(basis3, dirToPatch));

        vec3 npColor = patchValues[nid].rgb;

        accum1 += npColor * baseW * dirFactor1;
        accum2 += npColor * baseW * dirFactor2;
        accum3 += npColor * baseW * dirFactor3;

        sumW1 += baseW * dirFactor1;
        sumW2 += baseW * dirFactor2;
        sumW3 += baseW * dirFactor3;
    }

    accum1 = mix(homeColor, accum1, clamp(sumW1,0,1));
    accum2 = mix(homeColor, accum2, clamp(sumW2,0,1));
    accum3 = mix(homeColor, accum3, clamp(sumW3,0,1));

    vec3 blended1 = accum1 / max(sumW1, 1);
    vec3 blended2 = accum2 / max(sumW2, 1);
    vec3 blended3 = accum3 / max(sumW3, 1);

    LM_B1(texelIdx) += vec4(blended1, 0.0);
    LM_B2(texelIdx) += vec4(blended2, 0.0);
    LM_B3(texelIdx) += vec4(blended3, 0.0);
}