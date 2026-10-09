#version 430
layout(local_size_x = 64) in;

layout(std430, binding = 3) readonly buffer PatchesBuffer { GpuPatch patches[]; };
layout(std430, binding = 4) readonly buffer BucketOffsetsBuffer { int bucketOffsets[]; };
layout(std430, binding = 5) readonly buffer BucketIndicesBuffer { int bucketIndices[]; };
layout(std430, binding = 6) writeonly buffer NeighborCountBuffer { int neighborCount[]; };
layout(std430, binding = 7) writeonly buffer NeighborIndicesBuffer { int neighborIndices[]; };
layout(std430, binding = 2) readonly buffer CellOffsetsBuffer { int cellOffsetsFlat[]; };

uniform int patchCount;
uniform int patchOffset;
uniform vec3 gridOrigin;
uniform float cellSize;
uniform ivec3 gridDims;
uniform float blendRadius;
uniform int cellOffsetCount;
uniform int maxNeighbors;

int CellIndex(ivec3 cell)
{
    return (cell.z * gridDims.y + cell.y) * gridDims.x + cell.x;
}

bool CellValid(ivec3 cell)
{
    return !(any(lessThan(cell, ivec3(0))) || any(greaterThanEqual(cell, gridDims)));
}

float TestVisibility(GpuPatch p, GpuPatch np)
{
    vec3 points[5];
    points[0] = p.center;
    points[1] = p.c0;
    points[2] = p.c1;
    points[3] = p.c2;
    points[4] = p.c3;

    int clearCount = 0;

    for (int i = 0; i < 5; i++)
    {
        vec3 origin = points[i] + p.normal * 0.02;
        vec3 diff = np.center - origin;
        float dist = length(diff);
        if (dist < 1e-5)
        {
            clearCount++;
            continue;
        }
        vec3 dir = diff / dist;

        if (!TraceRayBvhAny(origin, dir, dist - 0.05, -1, -1))
        {
            clearCount++;
        }
    }

    return float(clearCount) / 5.0;
}

void main()
{
    uint idx = uint(patchOffset) + gl_GlobalInvocationID.x;
    if (idx >= uint(patchCount))
    {
        return;
    }

    GpuPatch p = patches[idx];

    float extent = PatchExtent(p);
    float searchRadius = blendRadius + extent;
    float radiusSq = searchRadius * searchRadius;

    ivec3 baseCell = ivec3(floor((p.center - gridOrigin) / cellSize));

    int found = 0;

    for (int oi = 0; oi < cellOffsetCount && found < maxNeighbors; oi++)
    {
        ivec3 cellOffset = ivec3(cellOffsetsFlat[oi * 3], cellOffsetsFlat[oi * 3 + 1], cellOffsetsFlat[oi * 3 + 2]);
        ivec3 cell = baseCell + cellOffset;
        if (!CellValid(cell))
        {
            continue;
        }

        int cellIdx = CellIndex(cell);
        int start = bucketOffsets[cellIdx];
        int end = bucketOffsets[cellIdx + 1];

        for (int k = start; k < end && found < maxNeighbors; k++)
        {
            int nid = bucketIndices[k];
            // if (nid == int(idx))
            // {
            //     continue;
            // }

            GpuPatch np = patches[nid];
            vec3 diff = np.center - p.center;
            float distSq = dot(diff, diff);
            if (distSq > radiusSq)
            {
                continue;
            }

            float ndot = dot(p.normal, np.normal);
            if (ndot <= 0.0)
            {
                continue;
            }

            // float visibility = TestVisibility(p, np);
            // if (visibility <= 0.0)
            // {
            //     continue;
            // }

            neighborIndices[idx * uint(maxNeighbors) + uint(found)] = nid;
            // neighborVisibility[idx * uint(maxNeighbors) + uint(found)] = visibility;
            found++;
        }
    }

    neighborCount[idx] = found;
}