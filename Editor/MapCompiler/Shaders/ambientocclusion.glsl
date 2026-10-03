#version 430
layout(local_size_x = 8, local_size_y = 8) in;

layout(rgba32f, binding = 0) uniform readonly image2D gPosition;
layout(rgba16f, binding = 1) uniform readonly image2D gNormal;

layout(std430, binding = 8) readonly buffer TexelSourceBrushBuffer { int texelSourceBrush[]; };
layout(std430, binding = 9) readonly buffer TexelEntityGroupBuffer { int texelEntityGroup[]; };
layout(std430, binding = 14) buffer AOResultBuffer { float aoResult[]; };

uniform int rowStart;
uniform int sampleCount;
uniform float aoRadius;

void main()
{
    ivec2 texel = ivec2(int(gl_GlobalInvocationID.x), int(gl_GlobalInvocationID.y) + rowStart);
    ivec2 size = imageSize(gPosition);
    if (texel.x >= size.x || texel.y >= size.y)
    {
        return;
    }

    int texelIdx = texel.y * size.x + texel.x;

    vec4 posValid = imageLoad(gPosition, texel);
    if (posValid.a < 0.5)
    {
        aoResult[texelIdx] = 0.0;
        return;
    }

    vec3 worldPos = posValid.rgb;
    vec3 normal = imageLoad(gNormal, texel).rgb;
    vec3 origin = worldPos + normal * 0.01;

    int excludeBrush = texelSourceBrush[texelIdx];
    int excludeEntityGroup = texelEntityGroup[texelIdx];

    vec3 t, b;
    BuildOrthonormalBasis(normal, t, b);

    // uint tileSeed = (gl_WorkGroupID.y * 92837111u) ^ (gl_WorkGroupID.x * 689287499u);
    // float jU = HashToUnitFloat(HashPatchSeed(tileSeed, 6u));
    // float jV = HashToUnitFloat(HashPatchSeed(tileSeed, 7u));

    float occlusion = 0.0;

    for (int i = 0; i < sampleCount; i++)
    {
        float u = mod(Halton(i + 1, 2), 1.0);
        float v = mod(Halton(i + 1, 3), 1.0);
        float phi = 2.0 * 3.14159265 * u;
        float cosT = sqrt(v);
        float sinT = sqrt(1.0 - v);

        vec3 dir = sinT * cos(phi) * t + sinT * sin(phi) * b + cosT * normal;

        BvhHit hit = TraceRayBvh(origin, dir, aoRadius, excludeBrush, excludeEntityGroup);
        if (hit.hit && !hit.isSkybox)
        {
            occlusion += (aoRadius - hit.distance) / aoRadius;
        }
    }

    aoResult[texelIdx] = (clamp(occlusion / float(sampleCount), 0.0, 1.0));
}