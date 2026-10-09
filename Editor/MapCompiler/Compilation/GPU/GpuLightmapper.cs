using MapCompiler;
using MapCompiler.Compilation;
using MapCompiler.Compilation.GPU;
using MapCompiler.Compilation.GPU.Resources;
using Microsoft.Xna.Framework;
using Rockwall;
using Silk.NET.OpenAL;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public sealed class GpuLightmapper : IDisposable
{
    public readonly GpuContext Context;
    private BvhResources bvh;
    private GBufferResources gbuffer;

    private GpuLightmapper(GpuContext context)
    {
        this.Context = context;
    }

    public static GpuLightmapper TryCreate()
    {
        var context = GpuContext.TryCreate();
        return context == null ? null : new GpuLightmapper(context);
    }

    public void BuildBvh(Brush[] brushes, Terrain[] terrains, Color[] matColors, List<BvhTriangle> extraTriangles = null)
    {
        bvh = new BvhResources(Context.GL, brushes, terrains, matColors, extraTriangles);
    }

    public void UploadGBuffer(GBufferData data, int resolution)
    {
        gbuffer = new GBufferResources(Context.GL, data, resolution);
    }
    public void RasterGBuffer(Brush[] brushes, Terrain[] terrains,
        Dictionary<(int brush, int face, int vertex), SmoothedVertexData> smoothedNormals,
        int resolution, int dilateIterations = 8)
    {
        gbuffer = GBufferRasterBuilder.Build(Context.GL, brushes, terrains, smoothedNormals, resolution, dilateIterations);
    }
    public PatchResources UploadPatches(Patch[] patches)
    {
        return new PatchResources(Context.GL, patches);
    }
    public LightmapLayerResources CreateLayer(int resolution)
    {
        return new LightmapLayerResources(Context.GL, resolution);
    }
    public GpuBuffer UploadTexelHomePatch(int[] texelHomePatch)
    {
        var buffer = new GpuBuffer(Context.GL);
        buffer.Upload<int>(texelHomePatch);
        return buffer;
    }
    public void RunDirectLightingPass(List<Light> lights, LightmapLayerResources layer)
    {
        DirectLightPass.Run(Context.GL, bvh, gbuffer, lights, layer);
    }
    public float[] RunSkyVisibility(PatchResources patches, int patchCount)
    {
        return SkyVisibilityPass.Run(Context.GL, bvh, patches, patchCount);
    }
    public void RunAmbientOcclusion(LightmapLayerResources layer, float aoRadius = 2f, int sampleCount = 32, int blurRadius = 1, float aoStrength = 1f)
    {
        using var aoResult = new GpuBuffer(Context.GL);
        aoResult.Upload<float>(new float[layer.Resolution * layer.Resolution]);

        AmbientOcclusionPass.Run(Context.GL, bvh, gbuffer, aoResult, layer.Resolution, sampleCount, aoRadius);
        AOApplyPass.Run(Context.GL, gbuffer, aoResult, layer, blurRadius, aoStrength);
    }
    public float[] ComputePatchSkyVisibility(PatchResources patches, GpuBuffer texelHomePatch, int patchCount, int lightmapResolution)
    {
        float[] patchSkyRaw = RunSkyVisibility(patches, patchCount);
        return PatchGatherPass.Run(Context.GL, bvh, patches, texelHomePatch, patchSkyRaw, patchCount, lightmapResolution, missValue: 1.0f, bounces: 2);
    }
    public Vector3[] RunPatchBounce(PatchResources patches, GpuBuffer texelHomePatch, Vector3[] seed, int patchCount, int lightmapResolution)
    {
        return PatchBouncePass.Run(Context.GL, bvh, patches, texelHomePatch, seed, patchCount, lightmapResolution);
    }
    public Vector3[] RunPatchSeed(LightmapLayerResources layer, PatchResources patches, int patchCount, int lightmapResolution)
    {
        return PatchSeedPass.Run(Context.GL, layer, patches, patchCount, lightmapResolution);
    }
    public GpuBuffer UploadPatchValues(Vector3[] patchValues)
    {
        var padded = new Vector4[patchValues.Length];
        for (int i = 0; i < patchValues.Length; i++)
        {
            padded[i] = new Vector4(patchValues[i], 0f);
        }

        var buffer = new GpuBuffer(Context.GL);
        buffer.Upload<Vector4>(padded);
        return buffer;
    }
    public PatchBucketGridResources BuildPatchGrid(Patch[] patches, Vector3 worldMin, Vector3 worldMax, float cellSize)
    {
        return new PatchBucketGridResources(Context.GL, patches, worldMin, worldMax, cellSize);
    }
    //public void BlendPatchesToLuxels(GpuBuffer texelHomePatch, GpuBuffer patchValues, PatchBucketGridResources grid, LightmapLayerResources layer, float blendRadius, int maxContributions = 256)
    //{
    //    PatchLuxelBlendPass.Run(Context.GL, gbuffer, bvh, texelHomePatch, patchValues, grid, layer, blendRadius, maxContributions);
    //}
    public (GpuBuffer counts, GpuBuffer indices) GeneratePatchBlendNeighbors(PatchResources patches, PatchBucketGridResources grid, Patch[] patchArray, int patchCount, float blendRadius, int maxNeighbors = 64)
    {
        return PatchBlendGenerationPass.Run(Context.GL, bvh, patches, grid, patchArray, patchCount, blendRadius, maxNeighbors);
    }
    public void BlendPatchesToLuxels(GpuBuffer texelHomePatch, GpuBuffer patchValues, GpuBuffer neighborCounts, GpuBuffer neighborIndices, PatchResources patches, LightmapLayerResources layer, float blendRadius, int maxNeighbors = 64)
    {
        PatchLuxelBlendPass.Run(Context.GL, gbuffer, bvh, texelHomePatch, patchValues, neighborCounts, neighborIndices, patches, layer, blendRadius, maxNeighbors);
    }

    public LightNodePositions PrepareLightNodePositions(List<LightNodeBundle> lightNodes)
    {
        var flatPos = new List<Vector3>();
        foreach (var node in lightNodes)
            foreach (var child in node.Children)
                flatPos.Add(child.Pos);

        return PrepareFlatPositions(flatPos.ToArray());
    }

    public LightNodePositions PrepareFlatPositions(Vector3[] positions)
    {
        var buffer = new GpuBuffer(Context.GL);
        buffer.Upload<float>(FlattenVector3(positions));

        return new LightNodePositions(buffer, positions.Length);
    }

    public PropVertexResources PrepareVertexResources(Vector3[] positions, Vector3[] normals)
    {
        var posBuffer = new GpuBuffer(Context.GL);
        posBuffer.Upload<float>(FlattenVector3(positions));

        var normBuffer = new GpuBuffer(Context.GL);
        normBuffer.Upload<float>(FlattenVector3(normals));

        return new PropVertexResources(posBuffer, normBuffer, positions.Length);
    }

    public Vector3[] BakePropVertexDirect(PropVertexResources verts, List<Light> lights)
    {
        using var lightResources = new LightResources(Context.GL, lights);
        return PropVertexDirectPass.Run(Context.GL, bvh, verts, lightResources);
    }

    private static float[] FlattenVector3(Vector3[] values)
    {
        var flat = new float[values.Length * 3];
        for (int i = 0; i < values.Length; i++)
        {
            flat[i * 3] = values[i].X;
            flat[i * 3 + 1] = values[i].Y;
            flat[i * 3 + 2] = values[i].Z;
        }
        return flat;
    }

    public bool[][] BakeLightNodeOcclusion(LightNodePositions positions, List<Light> lights)
    {
        using var lightResources = new LightResources(Context.GL, lights);
        return LightNodeOcclusionPass.Run(Context.GL, bvh, positions.Buffer, lightResources, positions.Count, lights.Count);
    }

    public Vector3[][] BakeLightNodeSH(LightNodePositions positions, GpuBuffer texelHomePatch, Vector3[] patchFinalValues, int lightmapResolution, Vector3 ambientColor, float ambientIntensity)
    {
        var patchValuesPadded = new Vector4[patchFinalValues.Length];
        for (int i = 0; i < patchFinalValues.Length; i++)
            patchValuesPadded[i] = new Vector4(patchFinalValues[i], 0f);

        using var patchValuesBuffer = new GpuBuffer(Context.GL);
        patchValuesBuffer.Upload<Vector4>(patchValuesPadded);

        return LightNodeBakePass.Run(Context.GL, bvh, positions.Buffer, patchValuesBuffer, texelHomePatch, positions.Count, lightmapResolution, ambientColor, ambientIntensity);
    }

    public void Checkpoint(string label)
    {
        Context.GL.Finish();
        var error = Context.GL.GetError();
        CompilerConsole.Step($"[GPU checkpoint] {label}: {(error == GLEnum.NoError ? "ok" : error.ToString())}");
    }

    public void Dispose()
    {
        gbuffer?.Dispose();
        bvh?.Dispose();
        Context.Dispose();
    }
}