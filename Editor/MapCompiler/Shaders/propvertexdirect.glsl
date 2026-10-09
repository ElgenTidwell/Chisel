#version 430
layout(local_size_x = 8) in;

layout(std430, binding = 3) readonly buffer LightsBuffer { GpuLight lights[]; };
layout(std430, binding = 4) readonly buffer VertPositionsBuffer { float vertPosFlat[]; };
layout(std430, binding = 5) readonly buffer VertNormalsBuffer { float vertNormalFlat[]; };
layout(std430, binding = 6) buffer VertDirectOutBuffer { vec4 directOut[]; };

uniform int vertCount;
uniform int vertOffset;
uniform int lightCount;

vec3 GetVertPos(uint idx)
{
    uint i = idx * 3u;
    return vec3(vertPosFlat[i], vertPosFlat[i + 1u], vertPosFlat[i + 2u]);
}

vec3 GetVertNormal(uint idx)
{
    uint i = idx * 3u;
    return vec3(vertNormalFlat[i], vertNormalFlat[i + 1u], vertNormalFlat[i + 2u]);
}

float DistanceAttenuation(float dist, float range)
{
    return pow(max((range - dist) / range, 0.0), 3.0);
}

float SpotFalloff(float angle, float maxAngle, float innerAngle)
{
    float epsilon = innerAngle - maxAngle;
    return clamp((angle - maxAngle) / epsilon, 0, 1);
}

const int ShadowSamples = 8;
const float PointLightRadiusFraction = 0.03;
const float DirectionalAngularRadius = 0.02;

float ComputeSoftVisibility(vec3 origin, vec3 centerDir, float dist, float lightRadius, bool isDirectional)
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

        BvhHit shadow = TraceRayBvh(origin, sampleDir, sampleDist, -1, -1);

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
    uint idx = uint(vertOffset) + gl_GlobalInvocationID.x;
    if (idx >= uint(vertCount))
    {
        return;
    }

    vec3 worldPos = GetVertPos(idx);
    vec3 normal = GetVertNormal(idx);
    vec3 accum = vec3(0.0);

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

        float facing = clamp(dot(normal, toLight), 0.0, 1.0);
        if (facing <= 0.0)
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

        float visibility = ComputeSoftVisibility(worldPos, toLight, dist, lightRadius, isDirectional);
        if (visibility <= 0.0)
        {
            continue;
        }

        vec3 contrib;

        if (light.type == 0)
        {
            float attn = DistanceAttenuation(dist, light.range);
            contrib = light.color * (light.intensity * facing * attn * visibility);
        }
        else if (light.type == 1)
        {
            contrib = light.color * (light.intensity * facing * visibility);
        }
        else
        {
            float attn = DistanceAttenuation(dist, light.range);
            contrib = light.color * (light.intensity * facing * attn * spotFalloff * visibility);
        }

        accum += contrib;
    }

    directOut[idx] = vec4(accum, 1.0);
}