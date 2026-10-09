#version 430
layout(local_size_x = 64) in;

layout(std430, binding = 3) readonly buffer PatchesBuffer { GpuPatch patches[]; };
layout(std430, binding = 5) buffer SkyResultBuffer { float skyResult[]; };

uniform int patchCount;
uniform int patchOffset;

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

    float jU = HashToUnitFloat(HashPatchSeed(idx, 0u));
    float jV = HashToUnitFloat(HashPatchSeed(idx, 1u));

    const int NumSamples = 64;
    float visible = 0.0;

    for (int i = 0; i < NumSamples; i++)
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
            visible += 1.0 / float(NumSamples);
        }
    }

    skyResult[idx] = visible;
}