#version 430
layout(local_size_x = 8) in;

layout(std430, binding = 18) readonly buffer TexelHomePatchBuffer { int texelHomePatch[]; };
layout(std430, binding = 31) readonly buffer PatchFinalValuesBuffer { vec4 patchValues[]; };
layout(std430, binding = 15) readonly buffer ChildPositionsBuffer { float childPosFlat[]; };
layout(std430, binding = 16) buffer LightNodeSHOutBuffer { vec4 shOut[]; };

uniform int childCount;
uniform int childOffset;
uniform int sampleCount;
uniform int lightmapResolution;
uniform vec3 ambientColor;
uniform float ambientIntensity;

const float PI = 3.14159265358979323846;

vec3 GetChildPos(uint idx)
{
    uint i = idx * 3u;
    return vec3(childPosFlat[i], childPosFlat[i + 1u], childPosFlat[i + 2u]);
}

vec3 FibonacciSphereDir(int i, int n)
{
    float goldenAngle = PI * (3.0 - sqrt(5.0));
    float y = 1.0 - (2.0 * float(i) + 1.0) / float(n);
    float radius = sqrt(max(0.0, 1.0 - y * y));
    float theta = goldenAngle * float(i);
    return vec3(cos(theta) * radius, y, sin(theta) * radius);
}

void EvaluateSHBasis(vec3 d, out float basis[9])
{
    basis[0] = 0.282095;
    basis[1] = 0.488603 * d.y;
    basis[2] = 0.488603 * d.z;
    basis[3] = 0.488603 * d.x;
    basis[4] = 1.092548 * d.x * d.y;
    basis[5] = 1.092548 * d.y * d.z;
    basis[6] = 0.315392 * (3.0 * d.z * d.z - 1.0);
    basis[7] = 1.092548 * d.x * d.z;
    basis[8] = 0.546274 * (d.x * d.x - d.y * d.y);
}

void main()
{
    uint idx = uint(childOffset) + gl_GlobalInvocationID.x;
    if (idx >= uint(childCount))
    {
        return;
    }

    vec3 worldPos = GetChildPos(idx);

    vec3 coeffs[9];
    for (int c = 0; c < 9; c++)
    {
        coeffs[c] = vec3(0.0);
    }

    for (int i = 0; i < sampleCount; i++)
    {
        vec3 dir = FibonacciSphereDir(i, sampleCount);

        vec3 radiance;
        BvhHit hit = TraceRayBvh(worldPos, dir, 512.0, -1, -1);

        if (!hit.hit || hit.isSkybox)
        {
            radiance = ambientColor * ambientIntensity;
        }
        else
        {
            vec2 hitUV = GetTriHitUV(hit.triIdx, hit.u, hit.v);
            ivec2 hitTexel = ivec2(clamp(hitUV * float(lightmapResolution), vec2(0.0), vec2(float(lightmapResolution - 1))));
            int hitIdx = hitTexel.y * lightmapResolution + hitTexel.x;
            int hitPatch = texelHomePatch[hitIdx];
            radiance = hitPatch >= 0 ? patchValues[hitPatch].rgb : vec3(0.0);
        }

        float basis[9];
        EvaluateSHBasis(dir, basis);

        for (int c = 0; c < 9; c++)
        {
            coeffs[c] += radiance * basis[c];
        }
    }

    float norm = 4.0 * PI / float(sampleCount);
    uint outBase = idx * 9u;

    for (int c = 0; c < 9; c++)
    {
        shOut[outBase + uint(c)] = vec4(coeffs[c] * norm, 0.0);
    }
}