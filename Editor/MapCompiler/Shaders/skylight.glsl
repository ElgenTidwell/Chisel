#version 430
layout(local_size_x = 8, local_size_y = 8) in;

layout(rgba32f, binding = 0) uniform readonly image2D gPosition;
layout(rgba16f, binding = 1) uniform readonly image2D gNormal;

layout(std430, binding = 4) readonly buffer TexelOwnerBuffer { ivec2 texelOwner[]; };

layout(std430, binding = 5) buffer SkyVisibilityBuffer { float skyVisibility[]; };


uniform int rowStart;
uniform int sampleCount;
uniform int skyLightScale;

void main()
{
    ivec2 gridCoord = ivec2(int(gl_GlobalInvocationID.x), int(gl_GlobalInvocationID.y) + rowStart);
    ivec2 size = imageSize(gPosition);
    ivec2 texel = gridCoord * skyLightScale;

    if (texel.x >= size.x || texel.y >= size.y)
    {
        return;
    }

    int texelIdx = texel.y * size.x + texel.x;

    vec4 posValid = imageLoad(gPosition, texel);
    if (posValid.a < 0.5)
    {
        skyVisibility[texelIdx] = -1.0;
        return;
    }

    vec3 worldPos = posValid.rgb;
    vec3 normal = imageLoad(gNormal, texel).rgb;
    vec3 origin = worldPos + normal * 0.01;

    int excludeBrush = texelOwner[texelIdx].x;
    int excludeEntityGroup = texelOwner[texelIdx].y;

    vec3 t, b;
    BuildOrthonormalBasis(normal, t, b);

    uint seed = uint(texelIdx);
    float jU = HashToUnitFloat(HashPatchSeed(seed, 0u));
    float jV = HashToUnitFloat(HashPatchSeed(seed, 1u));

    float visible = 0.0;

    for (int i = 0; i < sampleCount; i++)
    {
        float u = mod(Halton(i + 1, 2) + jU, 1.0);
        float v = mod(Halton(i + 1, 3) + jV, 1.0);
        float phi = 2.0 * 3.14159265 * u;
        float cosT = sqrt(v);
        float sinT = sqrt(1.0 - v);

        vec3 dir = sinT * cos(phi) * t + sinT * sin(phi) * b + cosT * normal;

        BvhHit hit = TraceRayBvh(origin, dir, 64.0, excludeBrush, excludeEntityGroup);
        if (!hit.hit || hit.isSkybox)
        {
            visible += 1.0 / float(sampleCount);
        }
    }

    skyVisibility[texelIdx] = visible;
}