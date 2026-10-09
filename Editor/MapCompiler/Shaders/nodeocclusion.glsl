#version 430
layout(local_size_x = 8) in;

layout(std430, binding = 3) readonly buffer LightsBuffer { GpuLight lights[]; };
layout(std430, binding = 6) readonly buffer ChildPositionsBuffer { float childPosFlat[]; };
layout(std430, binding = 7) buffer LightNodeBlockedBuffer { int blocked[]; };

uniform int childCount;
uniform int childOffset;
uniform int lightCount;

vec3 GetChildPos(uint idx)
{
    uint i = idx * 3u;
    return vec3(childPosFlat[i], childPosFlat[i + 1u], childPosFlat[i + 2u]);
}

void main()
{
    uint idx = uint(childOffset) + gl_GlobalInvocationID.x;
    if (idx >= uint(childCount))
    {
        return;
    }

    vec3 worldPos = GetChildPos(idx);

    for (int i = 0; i < lightCount; i++)
    {
        GpuLight light = lights[i];
        bool isBlocked = true;

        if (light.type == 1)
        {
            BvhHit hit = TraceRayBvh(worldPos, light.rotation, 512.0, -1, -1);
            isBlocked = hit.hit && !hit.isSkybox;
        }
        else
        {
            vec3 delta = light.position - worldPos;
            float dist = length(delta);

            if (dist <= light.range && dist >= 0.0001)
            {
                vec3 dir = delta / dist;
                bool withinCone = true;

                if (light.type == 2)
                {
                    float pdot = dot(dir, light.rotation);
                    float angle = acos(clamp(pdot, -1.0, 1.0));
                    withinCone = angle <= radians(light.angle);
                }

                if (withinCone)
                {
                    BvhHit hit = TraceRayBvh(worldPos, dir, dist - 0.05, -1, -1);
                    isBlocked = hit.hit;
                }
            }
        }

        blocked[idx * uint(lightCount) + uint(i)] = isBlocked ? 1 : 0;
    }
}