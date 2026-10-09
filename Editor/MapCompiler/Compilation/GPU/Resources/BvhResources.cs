using Microsoft.Xna.Framework;
using Rockwall;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MapCompiler.Compilation.GPU.Resources;
public struct BvhTriangle
{
    public Vector3 V0, V1, V2;
    public Vector2 Uv0, Uv1, Uv2;
    public Vector3 Albedo;
    public int SourceBrush;
    public int EntityGroup;
    public bool IsSkybox;
}

[StructLayout(LayoutKind.Sequential, Size = 48)]
public struct BvhNode
{
    public Vector3 BoundsMin;
    public uint LeftFirst;
    public Vector3 BoundsMax;
    public uint TriCount;
    public uint MissIndex;
}

[StructLayout(LayoutKind.Sequential, Size = 48)]
public struct GpuBvhTriangle
{
    public Vector3 V0;
    public int SourceBrush;
    public Vector3 V1;
    public int EntityGroup;
    public Vector3 V2;
    public int IsSkybox;
}

[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct GpuBvhSurface
{
    public Vector2 Uv0;
    public Vector2 Uv1;
    public Vector2 Uv2;
    public uint Albedo;
    public uint Padding;
}

public sealed class BvhResources : IDisposable
{
    private readonly GpuBuffer nodes;
    private readonly GpuBuffer triangles;
    private readonly GpuBuffer surfaces;

    public BvhResources(GL gl, Brush[] brushes, Terrain[] terrains, Color[] matColors, List<BvhTriangle> extraTriangles = null)
    {
        var tris = BuildUnifiedTriangleList(brushes, terrains, matColors);
        if (extraTriangles != null) tris.AddRange(extraTriangles);

        var builtNodes = BuildBvh(tris, out int[] triIndices);

        int n = triIndices.Length;
        var gpuTriangles = new GpuBvhTriangle[n];
        var gpuSurfaces = new GpuBvhSurface[n];

        for (int i = 0; i < n; i++)
        {
            var tri = tris[triIndices[i]];

            gpuTriangles[i] = new GpuBvhTriangle
            {
                V0 = tri.V0,
                SourceBrush = tri.SourceBrush,
                V1 = tri.V1,
                EntityGroup = tri.EntityGroup,
                V2 = tri.V2,
                IsSkybox = tri.IsSkybox ? 1 : 0
            };

            gpuSurfaces[i] = new GpuBvhSurface
            {
                Uv0 = tri.Uv0,
                Uv1 = tri.Uv1,
                Uv2 = tri.Uv2,
                Albedo = PackAlbedo(tri.Albedo)
            };
        }

        nodes = new GpuBuffer(gl);
        nodes.Upload<BvhNode>(builtNodes.ToArray());

        triangles = new GpuBuffer(gl);
        triangles.Upload<GpuBvhTriangle>(gpuTriangles);

        surfaces = new GpuBuffer(gl);
        surfaces.Upload<GpuBvhSurface>(gpuSurfaces);
    }

    public void Bind()
    {
        nodes.BindBase(GpuBindings.BvhNodes);
        triangles.BindBase(GpuBindings.BvhTriangles);
    }

    public void BindSurfaces()
    {
        surfaces.BindBase(GpuBindings.BvhSurfaces);
    }

    private static uint PackAlbedo(Vector3 albedo)
    {
        uint r = (uint)Math.Clamp(MathF.Round(albedo.X * 255f), 0f, 255f);
        uint g = (uint)Math.Clamp(MathF.Round(albedo.Y * 255f), 0f, 255f);
        uint b = (uint)Math.Clamp(MathF.Round(albedo.Z * 255f), 0f, 255f);
        return r | (g << 8) | (b << 16) | (255u << 24);
    }

    private static List<BvhTriangle> BuildUnifiedTriangleList(Brush[] brushes, Terrain[] terrains, Color[] matColors)
    {
        var tris = new List<BvhTriangle>(brushes.Length * 4);

        for (int b = 0; b < brushes.Length; b++)
        {
            if (brushes[b].IsClip || brushes[b].IsLightNodeVolume || brushes[b].IsTrigger) continue;

            bool skybox = brushes[b].IsSkybox;

            int entityGroup = TriangleOccluder.GetBrushEntityGroup(b);

            for (int f = 0; f < brushes[b].Faces.Length; f++)
            {
                var face = brushes[b].Faces[f];

                Vector3 albedo = matColors[face.Surface].ToVector3() / 255f;

                for (int t = 0; t < face.Indices.Length; t += 3)
                {
                    int i0 = face.Indices[t];
                    int i1 = face.Indices[t + 1];
                    int i2 = face.Indices[t + 2];

                    Vector3 v0 = brushes[b].Vertices[i0] + brushes[b].Position;
                    Vector3 v1 = brushes[b].Vertices[i1] + brushes[b].Position;
                    Vector3 v2 = brushes[b].Vertices[i2] + brushes[b].Position;

                    tris.Add(new BvhTriangle
                    {
                        V0 = v0,
                        V1 = v1,
                        V2 = v2,
                        Uv0 = brushes[b].LightmapUVs[i0],
                        Uv1 = brushes[b].LightmapUVs[i1],
                        Uv2 = brushes[b].LightmapUVs[i2],
                        Albedo = albedo,
                        SourceBrush = b,
                        EntityGroup = entityGroup,
                        IsSkybox = skybox
                    });
                }
            }
        }

        for (int i = 0; i < terrains.Length; i++)
        {
            Vector3 albedo = matColors[terrains[i].Surface].ToVector3() / 255f;

            for (int t = 0; t < terrains[i].Triangles.Length; t += 3)
            {
                int i0 = terrains[i].Triangles[t];
                int i1 = terrains[i].Triangles[t + 1];
                int i2 = terrains[i].Triangles[t + 2];

                tris.Add(new BvhTriangle
                {
                    V0 = terrains[i].Vertices[i0].Position,
                    V1 = terrains[i].Vertices[i1].Position,
                    V2 = terrains[i].Vertices[i2].Position,
                    Uv0 = terrains[i].lightmapUvs[i0],
                    Uv1 = terrains[i].lightmapUvs[i1],
                    Uv2 = terrains[i].lightmapUvs[i2],
                    Albedo = albedo,
                    SourceBrush = -1,
                    EntityGroup = -1,
                    IsSkybox = false
                });
            }
        }

        return tris;
    }

    private static List<BvhNode> BuildBvh(List<BvhTriangle> tris, out int[] triIndices)
    {
        int n = tris.Count;
        var indices = new int[n];
        for (int i = 0; i < n; i++) indices[i] = i;

        var centroids = new Vector3[n];
        var triBoundsMin = new Vector3[n];
        var triBoundsMax = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var tri = tris[i];
            triBoundsMin[i] = Vector3.Min(tri.V0, Vector3.Min(tri.V1, tri.V2));
            triBoundsMax[i] = Vector3.Max(tri.V0, Vector3.Max(tri.V1, tri.V2));
            centroids[i] = (tri.V0 + tri.V1 + tri.V2) / 3f;
        }

        var nodes = new List<BvhNode>(n * 2);
        nodes.Add(new BvhNode());

        void UpdateBounds(int nodeIdx)
        {
            var node = nodes[nodeIdx];
            node.BoundsMin = new Vector3(float.MaxValue);
            node.BoundsMax = new Vector3(float.MinValue);

            for (uint i = 0; i < node.TriCount; i++)
            {
                int triIdx = indices[node.LeftFirst + i];
                node.BoundsMin = Vector3.Min(node.BoundsMin, triBoundsMin[triIdx]);
                node.BoundsMax = Vector3.Max(node.BoundsMax, triBoundsMax[triIdx]);
            }

            nodes[nodeIdx] = node;
        }

        float SurfaceArea(Vector3 min, Vector3 max)
        {
            Vector3 e = max - min;
            if (e.X < 0f || e.Y < 0f || e.Z < 0f) return 0f;
            return 2f * (e.X * e.Y + e.Y * e.Z + e.Z * e.X);
        }

        const int BinCount = 16;

        bool FindBestSplit(int nodeIdx, out int bestAxis, out float bestSplitPos, out float bestCost)
        {
            var node = nodes[nodeIdx];
            bestAxis = -1;
            bestSplitPos = 0f;
            bestCost = float.MaxValue;

            Vector3 centroidMin = new Vector3(float.MaxValue);
            Vector3 centroidMax = new Vector3(float.MinValue);
            for (uint i = 0; i < node.TriCount; i++)
            {
                int triIdx = indices[node.LeftFirst + i];
                centroidMin = Vector3.Min(centroidMin, centroids[triIdx]);
                centroidMax = Vector3.Max(centroidMax, centroids[triIdx]);
            }

            for (int axis = 0; axis < 3; axis++)
            {
                float axisMin = centroidMin.GetElement(axis);
                float axisMax = centroidMax.GetElement(axis);
                if (axisMax - axisMin < 1e-6f) continue;

                var binCount = new int[BinCount];
                var binMin = new Vector3[BinCount];
                var binMax = new Vector3[BinCount];
                for (int b = 0; b < BinCount; b++)
                {
                    binMin[b] = new Vector3(float.MaxValue);
                    binMax[b] = new Vector3(float.MinValue);
                }

                float scale = BinCount / (axisMax - axisMin);

                for (uint i = 0; i < node.TriCount; i++)
                {
                    int triIdx = indices[node.LeftFirst + i];
                    int bin = Math.Clamp((int)((centroids[triIdx].GetElement(axis) - axisMin) * scale), 0, BinCount - 1);

                    binCount[bin]++;
                    binMin[bin] = Vector3.Min(binMin[bin], triBoundsMin[triIdx]);
                    binMax[bin] = Vector3.Max(binMax[bin], triBoundsMax[triIdx]);
                }

                var leftCount = new int[BinCount - 1];
                var leftArea = new float[BinCount - 1];
                var rightCount = new int[BinCount - 1];
                var rightArea = new float[BinCount - 1];

                Vector3 lMin = new Vector3(float.MaxValue), lMax = new Vector3(float.MinValue);
                int lSum = 0;
                for (int b = 0; b < BinCount - 1; b++)
                {
                    lSum += binCount[b];
                    lMin = Vector3.Min(lMin, binMin[b]);
                    lMax = Vector3.Max(lMax, binMax[b]);
                    leftCount[b] = lSum;
                    leftArea[b] = SurfaceArea(lMin, lMax);
                }

                Vector3 rMin = new Vector3(float.MaxValue), rMax = new Vector3(float.MinValue);
                int rSum = 0;
                for (int b = BinCount - 1; b >= 1; b--)
                {
                    rSum += binCount[b];
                    rMin = Vector3.Min(rMin, binMin[b]);
                    rMax = Vector3.Max(rMax, binMax[b]);
                    rightCount[b - 1] = rSum;
                    rightArea[b - 1] = SurfaceArea(rMin, rMax);
                }

                for (int b = 0; b < BinCount - 1; b++)
                {
                    if (leftCount[b] == 0 || rightCount[b] == 0) continue;

                    float cost = leftCount[b] * leftArea[b] + rightCount[b] * rightArea[b];
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestAxis = axis;
                        bestSplitPos = axisMin + (b + 1) / scale;
                    }
                }
            }

            return bestAxis != -1;
        }

        void Subdivide(int nodeIdx, uint escapeIndex)
        {
            var node = nodes[nodeIdx];
            node.MissIndex = escapeIndex;
            nodes[nodeIdx] = node;

            if (node.TriCount <= 2)
            {
                return;
            }

            float parentCost = node.TriCount * SurfaceArea(node.BoundsMin, node.BoundsMax);

            if (!FindBestSplit(nodeIdx, out int axis, out float splitPos, out float splitCost) || splitCost >= parentCost)
            {
                return;
            }

            int i = (int)node.LeftFirst;
            int j = i + (int)node.TriCount - 1;
            while (i <= j)
            {
                if (centroids[indices[i]].GetElement(axis) < splitPos)
                {
                    i++;
                }
                else
                {
                    (indices[i], indices[j]) = (indices[j], indices[i]);
                    j--;
                }
            }

            int leftCount = i - (int)node.LeftFirst;
            if (leftCount == 0 || leftCount == (int)node.TriCount)
            {
                return;
            }

            int leftIdx = nodes.Count;
            nodes.Add(new BvhNode { LeftFirst = node.LeftFirst, TriCount = (uint)leftCount });
            nodes.Add(new BvhNode { LeftFirst = (uint)i, TriCount = node.TriCount - (uint)leftCount });
            uint rightIdx = (uint)(leftIdx + 1);

            node.LeftFirst = (uint)leftIdx;
            node.TriCount = 0;
            nodes[nodeIdx] = node;

            UpdateBounds(leftIdx);
            UpdateBounds((int)rightIdx);

            Subdivide(leftIdx, rightIdx);
            Subdivide((int)rightIdx, escapeIndex);
        }

        nodes[0] = new BvhNode { LeftFirst = 0, TriCount = (uint)n };
        UpdateBounds(0);
        Subdivide(0, uint.MaxValue);

        triIndices = indices;
        return nodes;
    }

    public void Dispose()
    {
        nodes.Dispose();
        triangles.Dispose();
        surfaces.Dispose();
    }
}