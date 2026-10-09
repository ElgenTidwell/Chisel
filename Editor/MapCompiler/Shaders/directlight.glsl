#version 430

layout(local_size_x = 8, local_size_y = 8) in;


layout(rgba32f, binding = 0) uniform readonly image2D gPosition;
layout(rgba16f, binding = 1) uniform readonly image2D gNormal;
layout(rgba16f, binding = 2) uniform readonly image2D gBasis1;
layout(rgba16f, binding = 3) uniform readonly image2D gBasis2;
layout(rgba16f, binding = 4) uniform readonly image2D gBasis3;

layout(std430, binding = 3) readonly buffer LightsBuffer { GpuLight lights[]; };
layout(std430, binding = 4) readonly buffer TexelOwnerBuffer { ivec2 texelOwner[]; };

uniform int lightCount;
uniform int rowStart;

float DistanceAttenuation(float dist, float range)
{
    return pow(max((range - dist) / range, 0.0), 3.0);
}

float SpotFalloff(float angle, float maxAngle, float innerAngle)
{
    float epsilon = innerAngle - maxAngle;
    return clamp((angle - maxAngle) / epsilon, 0, 1);
}

const int ShadowSamples = 16;
const float PointLightRadiusFraction = 0.03;
const float DirectionalAngularRadius = 0.02;

float ComputeSoftVisibility(vec3 origin, vec3 centerDir, float dist, float lightRadius, bool isDirectional, int excludeBrush, int excludeEntityGroup)
{
    vec3 t, b;
    BuildOrthonormalBasis(centerDir, t, b);

    float visible = 0.0;

    for (int s = 0; s < ShadowSamples; s++)
    {
        float u = mod(Halton(s + 1, 2), 1.0);
        float v = mod(Halton(s + 1, 3), 1.0);
        float r = sqrt(u) * lightRadius;
        float theta = 2.0 * 3.14159265 * v;
        vec3 offset = (cos(theta) * t + sin(theta) * b) * r;

        vec3 sampleDir;
        float sampleDist;

        if (isDirectional)
        {
            sampleDir = normalize(centerDir + offset);
            sampleDist = dist;
        }
        else
        {
            vec3 toSample = centerDir * dist + offset;
            sampleDist = length(toSample);
            sampleDir = toSample / max(sampleDist, 1e-5);
        }

        BvhHit shadow = TraceRayBvh(origin, sampleDir, sampleDist, excludeBrush, excludeEntityGroup);

        bool blocked = isDirectional
            ? !(shadow.hit && shadow.isSkybox)
            : shadow.hit;

        if (!blocked)
        {
            visible += 1.0;
        }
    }

    return visible / float(ShadowSamples);
}

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
        LM_B1(texelIdx) = vec4(0.0);
        LM_B2(texelIdx) = vec4(0.0);
        LM_B3(texelIdx) = vec4(0.0);
        return;
    }

    vec3 worldPos = posValid.rgb;
    vec3 normal = imageLoad(gNormal, texel).rgb;
    vec3 basis1 = imageLoad(gBasis1, texel).rgb;
    vec3 basis2 = imageLoad(gBasis2, texel).rgb;
    vec3 basis3 = imageLoad(gBasis3, texel).rgb;

    int excludeBrush = texelOwner[texelIdx].x;
    int excludeEntityGroup = texelOwner[texelIdx].y;

    vec3 accum1 = vec3(0.0);
    vec3 accum2 = vec3(0.0);
    vec3 accum3 = vec3(0.0);

    for (int i = 0; i < lightCount; i++)
    {
        GpuLight light = lights[i];

        vec3 toLight;
        float dist;

        if (light.type == 1)
        {
            toLight = light.rotation;
            dist = 512.0;
        }
        else
        {
            vec3 delta = light.position - worldPos;
            dist = length(delta);
            if (dist > light.range || dist < 0.0001)
            {
                continue;
            }
            toLight = delta / dist;
        }

        float facing = dot(normal, toLight);
        const float facingCutoff = 0.0;
        const float facingSoftness = 0.02;
        float facingAtten = clamp((facing - facingCutoff) / facingSoftness, 0.0, 1.0);
        if (facingAtten <= 0.0)
        {
            continue;
        }

        float spotFalloff = 1.0;
        if (light.type == 2)
        {
            float angle = acos(clamp(dot(toLight, light.rotation), -1.0, 1.0));
            float maxAngle = radians(light.angle);
            float innerAngle = radians(light.innerAngle);
            spotFalloff = SpotFalloff(angle, maxAngle, innerAngle);
            if (spotFalloff <= 0.0)
            {
                continue;
            }
        }

        bool isDirectional = light.type == 1;
        float lightRadius = isDirectional ? DirectionalAngularRadius : light.range * PointLightRadiusFraction;

        float visibility = ComputeSoftVisibility(worldPos, toLight, dist, lightRadius, isDirectional, excludeBrush, excludeEntityGroup);
        if (visibility <= 0.0)
        {
            continue;
        }

        vec3 contrib = vec3(0.0);

        if (light.type == 0)
        {
            float attn = DistanceAttenuation(dist, light.range);
            float d1 = clamp(dot(normal, toLight), 0.0, 1.0);
            contrib = light.color * (light.intensity * d1 * attn * visibility);
        }
        else if (light.type == 1)
        {
            float d1 = clamp(dot(normal, toLight), 0.0, 1.0);
            contrib = light.color * (light.intensity * visibility * d1);
        }
        else
        {
            float attn = DistanceAttenuation(dist, light.range);
            float d1 = dot(normal, toLight);
            contrib = light.color * (light.intensity * (abs(d1) * 0.4 + 0.6) * attn * spotFalloff * visibility);
        }

        float w1 = max(dot(basis1, toLight), 0.0);
        float w2 = max(dot(basis2, toLight), 0.0);
        float w3 = max(dot(basis3, toLight), 0.0);

        accum1 += contrib * w1;
        accum2 += contrib * w2;
        accum3 += contrib * w3;
    }
    LM_B1(texelIdx) = vec4(accum1, 1.0);
    LM_B2(texelIdx) = vec4(accum2, 1.0);
    LM_B3(texelIdx) = vec4(accum3, 1.0);
}