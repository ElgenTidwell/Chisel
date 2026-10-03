#version 430
layout(local_size_x = 64) in;

layout(std430, binding = 10) readonly buffer PatchesBuffer { GpuPatch patches[]; };
layout(std430, binding = 18) readonly buffer TexelHomePatchBuffer { int texelHomePatch[]; };
layout(std430, binding = 29) readonly buffer PatchShotInBuffer { vec4 shotIn[]; };
layout(std430, binding = 30) buffer PatchShotOutBuffer { vec4 shotOut[]; };
layout(std430, binding = 11) buffer PatchAccumBuffer { vec4 accumTotal[]; };

uniform int patchCount;
uniform int patchOffset;
uniform int lightmapResolution;
uniform int sampleSeed;
uniform int sampleCount;
uniform float bounceRadius;

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

    vec3 incoming = vec3(0.0);

    for (int i = 0; i < sampleCount; i++)
    {
        float u = mod(Halton(i + 1, 2) + jU, 1.0);
        float v = mod(Halton(i + 1, 3) + jV, 1.0);
        float phi = 2.0 * 3.14159265 * u;
        float cosT = sqrt(v);
        float sinT = sqrt(1.0 - v);

        vec3 dir = sinT * cos(phi) * t + sinT * sin(phi) * b + cosT * p.normal;

        BvhHit hit = TraceRayBvh(origin, dir, bounceRadius, -1, -1);
        if (!hit.hit || hit.isSkybox)
        {
            continue;
        }

        vec2 hitUV = GetTriHitUV(hit.triIdx, hit.u, hit.v);
        ivec2 hitTexel = ivec2(clamp(hitUV * float(lightmapResolution), vec2(0.0), vec2(float(lightmapResolution - 1))));
        int hitIdx = hitTexel.y * lightmapResolution + hitTexel.x;
        int hitPatch = texelHomePatch[hitIdx];
        if (hitPatch < 0)
        {
            continue;
        }

        incoming += shotIn[hitPatch].rgb;
    }

    incoming /= float(sampleCount);
    vec3 reflected = incoming * (p.albedo*0.7+0.3);

    shotOut[idx] = vec4(reflected, 0.0);
    accumTotal[idx] += vec4(reflected, 0.0);
}