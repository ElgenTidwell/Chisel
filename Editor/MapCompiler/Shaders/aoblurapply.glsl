#version 430
layout(local_size_x = 8, local_size_y = 8) in;

layout(rgba32f, binding = 0) uniform readonly image2D gPosition;
layout(rgba16f, binding = 1) uniform readonly image2D gNormal;

layout(std430, binding = 21) buffer LayerB1Buffer { vec4 lmB1[]; };
layout(std430, binding = 22) buffer LayerB2Buffer { vec4 lmB2[]; };
layout(std430, binding = 23) buffer LayerB3Buffer { vec4 lmB3[]; };
layout(std430, binding = 14) readonly buffer AOResultBuffer { float aoResult[]; };

uniform int rowStart;
uniform int blurRadius;
uniform float aoStrength;

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

    vec3 normal = imageLoad(gNormal, texel).rgb;

    float sum = 0.0;
    float weight = 0.0;

    for (int dy = -blurRadius; dy <= blurRadius; dy++)
    {
        for (int dx = -blurRadius; dx <= blurRadius; dx++)
        {
            ivec2 sampleTexel = texel + ivec2(dx, dy);
            if (sampleTexel.x < 0 || sampleTexel.x >= size.x || sampleTexel.y < 0 || sampleTexel.y >= size.y)
            {
                continue;
            }

            vec4 samplePosValid = imageLoad(gPosition, sampleTexel);
            if (samplePosValid.a < 0.5)
            {
                continue;
            }

            vec3 sampleNormal = imageLoad(gNormal, sampleTexel).rgb;
            if (dot(normal, sampleNormal) < 0.9)
            {
                continue;
            }

            int sampleIdx = sampleTexel.y * size.x + sampleTexel.x;
            sum += aoResult[sampleIdx];
            weight += 1.0;
        }
    }
    // int sampleIdx = texel.y * size.x + texel.x;

    float blurredAO = weight > 0.0 ? sum / weight : 0.0;
    float factor = 1.0 - blurredAO * aoStrength;
    // float factor = 1.0 - aoResult[sampleIdx] * aoStrength;

    int texelIdx = texel.y * size.x + texel.x;
    lmB1[texelIdx] *= factor;
    lmB2[texelIdx] *= factor;
    lmB3[texelIdx] *= factor;
}