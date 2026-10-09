struct BvhNode
{
    vec3 boundsMin;
    uint leftFirst;
    vec3 boundsMax;
    uint triCount;
    uint missIndex;
};

struct GpuPatch
{
    vec3 center; int excludeBrush;
    vec3 c0; int isTerrain;
    vec3 c1; float pad1;
    vec3 c2; float pad2;
    vec3 c3; float pad3;
    vec3 normal; float pad4;
    vec2 startUV;
    vec2 endUV;
    vec3 albedo; float pad5;
};

float PatchExtent(GpuPatch p)
{
    float e = distance(p.center, p.c0);
    e = max(e, distance(p.center, p.c1));
    e = max(e, distance(p.center, p.c2));
    e = max(e, distance(p.center, p.c3));
    return e;
}

struct GpuLight
{
    vec3 position;
    int type;
    vec3 rotation;
    float intensity;
    vec3 color;
    float range;
    float angle;
    float innerAngle;
    int id;
    float padding;
};

struct BvhTriangle
{
    vec3 v0; int sourceBrush;
    vec3 v1; int entityGroup;
    vec3 v2; int isSkybox;
};

layout(std430, binding = 0) readonly buffer BvhNodesBuffer { BvhNode nodes[]; };
layout(std430, binding = 1) readonly buffer BvhTrianglesBuffer { BvhTriangle triangles[]; };

void BuildOrthonormalBasis(vec3 n, out vec3 t, out vec3 b)
{
    float sign_ = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (sign_ + n.z);
    float bb = n.x * n.y * a;
    t = vec3(1.0 + sign_ * n.x * n.x * a, sign_ * bb, -sign_ * n.x);
    b = vec3(bb, sign_ + n.y * n.y * a, -n.y);
}

bool IntersectAABB(vec3 origin, vec3 dir, vec3 boundsMin, vec3 boundsMax, float maxDist)
{
    vec3 safeDir = vec3(
        dir.x >= 0.0 ? max(dir.x, 1e-8) : min(dir.x, -1e-8),
        dir.y >= 0.0 ? max(dir.y, 1e-8) : min(dir.y, -1e-8),
        dir.z >= 0.0 ? max(dir.z, 1e-8) : min(dir.z, -1e-8)
    );
    vec3 invDir = 1.0 / safeDir;

    vec3 t0 = (boundsMin - origin) * invDir;
    vec3 t1 = (boundsMax - origin) * invDir;
    vec3 tmin = min(t0, t1);
    vec3 tmax = max(t0, t1);
    float tNear = max(max(tmin.x, tmin.y), tmin.z);
    float tFar = min(min(tmax.x, tmax.y), tmax.z);
    return tFar >= max(tNear, 0.0) && tNear <= maxDist;
}

bool IntersectTriangle(vec3 origin, vec3 dir, vec3 v0, vec3 v1, vec3 v2, out float t, out float outU, out float outV)
{
    vec3 absDir = abs(dir);
    int kz = absDir.x > absDir.y
        ? (absDir.x > absDir.z ? 0 : 2)
        : (absDir.y > absDir.z ? 1 : 2);
    int kx = kz + 1; if (kx == 3) kx = 0;
    int ky = kx + 1; if (ky == 3) ky = 0;

    if (dir[kz] < 0.0)
    {
        int tmp = kx; kx = ky; ky = tmp;
    }

    float Sx = dir[kx] / dir[kz];
    float Sy = dir[ky] / dir[kz];
    float Sz = 1.0 / dir[kz];

    vec3 A = v0 - origin;
    vec3 B = v1 - origin;
    vec3 C = v2 - origin;

    float Ax = A[kx] - Sx * A[kz];
    float Ay = A[ky] - Sy * A[kz];
    float Bx = B[kx] - Sx * B[kz];
    float By = B[ky] - Sy * B[kz];
    float Cx = C[kx] - Sx * C[kz];
    float Cy = C[ky] - Sy * C[kz];

    float U = Cx * By - Cy * Bx;
    float V = Ax * Cy - Ay * Cx;
    float W = Bx * Ay - By * Ax;

    if ((U < 0.0 || V < 0.0 || W < 0.0) && (U > 0.0 || V > 0.0 || W > 0.0))
    {
        return false;
    }

    float det = U + V + W;
    if (det == 0.0)
    {
        return false;
    }

    float Az = A[kz] * Sz;
    float Bz = B[kz] * Sz;
    float Cz = C[kz] * Sz;
    float T = U * Az + V * Bz + W * Cz;

    float rcpDet = 1.0 / det;
    t = T * rcpDet;
    outU = V * rcpDet;
    outV = W * rcpDet;

    return t > 1e-2;
}

struct BvhHit
{
    bool hit;
    float distance;
    bool isSkybox;
    float u;
    float v;
    uint triIdx;
};

bool TraceRayBvhAny(vec3 origin, vec3 dir, float maxDist, int excludeBrush, int excludeEntityGroup)
{
    uint nodeIdx = 0u;
    const uint MissSentinel = 0xFFFFFFFFu;

    while (nodeIdx != MissSentinel)
    {
        BvhNode node = nodes[nodeIdx];

        if (!IntersectAABB(origin, dir, node.boundsMin, node.boundsMax, maxDist))
        {
            nodeIdx = node.missIndex;
            continue;
        }

        if (node.triCount > 0u)
        {
            for (uint i = 0u; i < node.triCount; i++)
            {
                uint triIdx = node.leftFirst + i;
                BvhTriangle tri = triangles[triIdx];

                if (excludeBrush >= 0 && tri.sourceBrush == excludeBrush)
                {
                    continue;
                }
                if (tri.entityGroup >= 0 && tri.entityGroup != excludeEntityGroup)
                {
                    continue;
                }

                float t, u, v;
                if (IntersectTriangle(origin, dir, tri.v0, tri.v1, tri.v2, t, u, v) && t < maxDist)
                {
                    return true;
                }
            }
            nodeIdx = node.missIndex;
        }
        else
        {
            nodeIdx = node.leftFirst;
        }
    }
    return false;
}

BvhHit TraceRayBvh(vec3 origin, vec3 dir, float maxDist, int excludeBrush, int excludeEntityGroup)
{
    BvhHit result;
    result.hit = false;
    result.distance = maxDist;
    result.isSkybox = false;
    result.u = 0.0;
    result.v = 0.0;
    result.triIdx = 0u;

    uint nodeIdx = 0u;
    const uint MissSentinel = 0xFFFFFFFFu;

    while (nodeIdx != MissSentinel)
    {
        BvhNode node = nodes[nodeIdx];

        if (!IntersectAABB(origin, dir, node.boundsMin, node.boundsMax, result.distance))
        {
            nodeIdx = node.missIndex;
            continue;
        }

        if (node.triCount > 0u)
        {
            for (uint i = 0u; i < node.triCount; i++)
            {
                uint triIdx = node.leftFirst + i;
                BvhTriangle tri = triangles[triIdx];

                if (excludeBrush >= 0 && tri.sourceBrush == excludeBrush)
                {
                    continue;
                }
                if (tri.entityGroup >= 0 && tri.entityGroup != excludeEntityGroup)
                {
                    continue;
                }

                float t, u, v;
                if (IntersectTriangle(origin, dir, tri.v0, tri.v1, tri.v2, t, u, v))
                {
                    if (t < result.distance)
                    {
                        result.hit = true;
                        result.distance = t;
                        result.isSkybox = tri.isSkybox != 0;
                        result.u = u;
                        result.v = v;
                        result.triIdx = triIdx;
                    }
                }
            }

            nodeIdx = node.missIndex;
        }
        else
        {
            nodeIdx = node.leftFirst;
        }
    }

    return result;
}

float Halton(int index, int base_)
{
    float f = 1.0;
    float r = 0.0;
    int i = index;
    while (i > 0)
    {
        f = f / float(base_);
        r = r + f * float(i % base_);
        i = i / base_;
    }
    return r;
}

uint HashPatchSeed(uint x, uint salt)
{
    uint h = x * 747796405u + salt * 2891336453u;
    h = (h ^ (h >> 16u)) * 2246822519u;
    h ^= h >> 13u;
    return h;
}

float HashToUnitFloat(uint h)
{
    return float(h & 0xFFFFFFu) / float(0x1000000u);
}