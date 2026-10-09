#version 430
layout(local_size_x = 64) in;

layout(std430, binding = 3) readonly buffer PatchesBuffer { GpuPatch patches[]; };
layout(std430, binding = 4) readonly buffer TexelHomePatchBuffer { int texelHomePatch[]; };
layout(std430, binding = 5) readonly buffer PatchGatherInBuffer { float gatherIn[]; };
layout(std430, binding = 6) buffer PatchGatherOutBuffer { float gatherOut[]; };

uniform int patchCount;
uniform int patchOffset;
uniform int lightmapResolution;
uniform int sampleSeed;
uniform int sampleCount;
uniform float missValue;

void main()
{
    uint idx = uint(patchOffset) + gl_GlobalInvocationID.x;
    if (idx >= uint(patchCount))
    {
        return;
    }

    GpuPatch p = patches[idx];
    vec3 origin = p.center + p.normal * 0.01;

    vec3 t, b;
    BuildOrthonormalBasis(p.normal, t, b);

    float jU = HashToUnitFloat(HashPatchSeed(idx, uint(sampleSeed) * 2u));
    float jV = HashToUnitFloat(HashPatchSeed(idx, uint(sampleSeed) * 2u + 1u));

    float accum = 0.0;

    for (int i = 0; i < sampleCount; i++)
    {
        float u = mod(Halton(i + 1, 2) + jU, 1.0);
        float v = mod(Halton(i + 1, 3) + jV, 1.0);
        float phi = 2.0 * 3.14159265 * u;
        float cosT = sqrt(v);
        float sinT = sqrt(1.0 - v);

        vec3 dir = sinT * cos(phi) * t + sinT * sin(phi) * b + cosT * p.normal;

        BvhHit hit = TraceRayBvh(origin, dir, 256.0, -1, -1);

        if (!hit.hit || hit.isSkybox)
        {
            accum += missValue;
        }
        else
        {
            vec2 hitUV = GetTriHitUV(hit.triIdx, hit.u, hit.v);
            ivec2 hitTexel = ivec2(clamp(hitUV * float(lightmapResolution), vec2(0.0), vec2(float(lightmapResolution - 1))));
            int hitIdx = hitTexel.y * lightmapResolution + hitTexel.x;
            int hitPatch = texelHomePatch[hitIdx];

            accum += hitPatch >= 0 ? gatherIn[hitPatch] : gatherIn[idx];
        }
    }

    gatherOut[idx] = accum / float(sampleCount);
}