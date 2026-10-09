using MapCompiler.Compilation.GPU.Resources;
using Microsoft.Xna.Framework;
using Rockwall;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapCompiler.Compilation.GPU;
public static class DirectLightPass
{
    public static void Run(GL gl, BvhResources bvh, GBufferResources gbuffer, List<Light> lights, LightmapLayerResources layer)
    {
        using var lightResources = new LightResources(gl, lights);
        using var program = new ComputeProgram(gl, "DirectLight.glsl", "Lightmap.glsl", "BvhTrace.glsl");

        program.Use();

        gbuffer.BindImages();
        layer.Bind();

        lightResources.Bind();
        bvh.Bind();
        gbuffer.BindTexelBuffers();

        program.SetUniform("lightCount", lightResources.Count);

        program.DispatchRowsChunked(gl, layer.Resolution, "rowStart", 8, 8, MemoryBarrierMask.ShaderStorageBarrierBit, rowsPerChunk: 64);
    }
}
public static class SkyLightPass
{
    public static void Run(GL gl, BvhResources bvh, GBufferResources gbuffer, GpuBuffer skyVisibility, int resolution, int sampleCount = 32, int skyLightScale = 4)
    {
        using var program = new ComputeProgram(gl, "SkyLight.glsl", "BvhTrace.glsl");
        program.Use();

        gbuffer.BindImages();
        bvh.Bind();
        gbuffer.BindTexelBuffers();
        skyVisibility.BindBase(GpuBindings.SkyVisibilityLuxel);

        program.SetUniform("sampleCount", sampleCount);
        program.SetUniform("skyLightScale", skyLightScale);

        int gridResolution = (resolution + skyLightScale - 1) / skyLightScale;

        CompilerConsole.Step("Running sky light (GPU)...");
        program.DispatchRowsChunked(gl, gridResolution, "rowStart", 8, 8, MemoryBarrierMask.ShaderStorageBarrierBit, rowsPerChunk: 64);
    }
}

public static class SkyBlurPass
{
    public static void Run(GL gl, GBufferResources gbuffer, GpuBuffer skyVisibility, LightmapLayerResources layer, Vector3 ambientColor, float ambientIntensity, int blurRadius = 4)
    {
        using var program = new ComputeProgram(gl, "SkyBlurApply.glsl", "Lightmap.glsl");
        program.Use();

        gbuffer.BindImages();
        layer.Bind();
        skyVisibility.BindBase(GpuBindings.SkyVisibilityLuxel);

        program.SetUniform("ambientColor", ambientColor);
        program.SetUniform("ambientIntensity", ambientIntensity);
        program.SetUniform("blurRadius", blurRadius);

        CompilerConsole.Step("Blurring sky light (GPU)...");
        program.DispatchRowsChunked(gl, layer.Resolution, "rowStart", 8, 8, MemoryBarrierMask.ShaderStorageBarrierBit, rowsPerChunk: 64);
    }
}
public static class SkyVisibilityPass
{
    public static float[] Run(GL gl, BvhResources bvh, PatchResources patches, int patchCount)
    {
        using var skyResult = new GpuBuffer(gl);
        skyResult.Upload<float>(new float[patchCount], usage: BufferUsageARB.StreamRead);

        using var program = new ComputeProgram(gl, "SkyVisibility.glsl", "BvhTrace.glsl");
        program.Use();

        bvh.Bind();
        patches.Bind();
        skyResult.BindBase(GpuBindings.SkyResult);

        program.SetUniform("patchCount", patchCount);

        program.DispatchUnitsChunked(gl, patchCount, "patchOffset", 64, MemoryBarrierMask.ShaderStorageBarrierBit, chunkSize: 65536);

        return skyResult.ReadBack<float>(patchCount);
    }
}
public static class PatchGatherPass
{
    public static float[] Run(GL gl, BvhResources bvh, PatchResources patches, GpuBuffer texelHomePatch, float[] initial, int patchCount, int lightmapResolution, float missValue, int bounces = 2, int sampleCount = 32)
    {
        using var bufA = new GpuBuffer(gl);
        bufA.Upload<float>(initial, usage: BufferUsageARB.StreamRead);
        using var bufB = new GpuBuffer(gl);
        bufB.Upload<float>(new float[patchCount], usage: BufferUsageARB.StreamRead);

        using var program = new ComputeProgram(gl, "PatchGather.glsl", "BvhTrace.glsl", "BvhHitUV.glsl");

        var current = bufA;
        var next = bufB;

        for (int bounce = 0; bounce < bounces; bounce++)
        {
            program.Use();

            bvh.Bind();
            bvh.BindSurfaces();
            patches.Bind();
            current.BindBase(GpuBindings.PatchGatherIn);
            next.BindBase(GpuBindings.PatchGatherOut);
            texelHomePatch.BindBase(GpuBindings.TexelHomePatch);

            program.SetUniform("patchCount", patchCount);
            program.SetUniform("lightmapResolution", lightmapResolution);
            program.SetUniform("sampleSeed", bounce);
            program.SetUniform("sampleCount", sampleCount);
            program.SetUniform("missValue", missValue);

            program.DispatchUnitsChunked(gl, patchCount, "patchOffset", 64, MemoryBarrierMask.ShaderStorageBarrierBit, chunkSize: 65536);

            (current, next) = (next, current);
        }

        return current.ReadBack<float>(patchCount);
    }
}
public static class PatchBouncePass
{
    public static Vector3[] Run(GL gl, BvhResources bvh, PatchResources patches, GpuBuffer texelHomePatch, Vector3[] seedRgb, int patchCount, int lightmapResolution, float bounceRadius = 16f, int bounces = 8, int sampleCount = 256)
    {
        var seedPadded = new Vector4[patchCount];
        for (int i = 0; i < patchCount; i++) seedPadded[i] = new Vector4(seedRgb[i], 0f);

        using var bufA = new GpuBuffer(gl);
        bufA.Upload<Vector4>(seedPadded, usage: BufferUsageARB.StreamRead);
        using var bufB = new GpuBuffer(gl);
        bufB.Upload<Vector4>(new Vector4[patchCount], usage: BufferUsageARB.StreamRead);
        using var accum = new GpuBuffer(gl);
        accum.Upload<Vector4>(new Vector4[patchCount], usage: BufferUsageARB.StreamRead);

        using var program = new ComputeProgram(gl, "PatchBounce.glsl", "BvhTrace.glsl", "BvhHitUV.glsl");

        var shotIn = bufA;
        var shotOut = bufB;

        for (int bounce = 0; bounce < bounces; bounce++)
        {
            program.Use();

            bvh.Bind();
            bvh.BindSurfaces();
            patches.Bind();
            texelHomePatch.BindBase(GpuBindings.TexelHomePatch);
            shotIn.BindBase(GpuBindings.PatchGatherIn);
            shotOut.BindBase(GpuBindings.PatchGatherOut);
            accum.BindBase(GpuBindings.PatchBounceAccum);

            program.SetUniform("patchCount", patchCount);
            program.SetUniform("lightmapResolution", lightmapResolution);
            program.SetUniform("sampleSeed", bounce);
            program.SetUniform("sampleCount", sampleCount);
            program.SetUniform("bounceRadius", bounceRadius);

            program.DispatchUnitsChunked(gl, patchCount, "patchOffset", 64, MemoryBarrierMask.ShaderStorageBarrierBit, chunkSize: 65536);

            (shotIn, shotOut) = (shotOut, shotIn);
        }

        var raw = accum.ReadBack<Vector4>(patchCount);
        var result = new Vector3[patchCount];
        for (int i = 0; i < patchCount; i++)
        {
            result[i] = new Vector3(raw[i].X, raw[i].Y, raw[i].Z);
        }
        return result;
    }
}
public static class PatchSeedPass
{
    public static Vector3[] Run(GL gl, LightmapLayerResources layer, PatchResources patches, int patchCount, int lightmapResolution)
    {
        using var seedBuffer = new GpuBuffer(gl);
        seedBuffer.Upload<Vector4>(new Vector4[patchCount], usage: BufferUsageARB.StreamRead);

        using var program = new ComputeProgram(gl, "PatchSeed.glsl", "Lightmap.glsl");
        program.Use();

        layer.Bind();
        patches.Bind();
        seedBuffer.BindBase(GpuBindings.PatchSeedResult);

        program.SetUniform("patchCount", patchCount);
        program.SetUniform("lightmapResolution", lightmapResolution);

        program.DispatchUnitsChunked(gl, patchCount, "patchOffset", 64, MemoryBarrierMask.ShaderStorageBarrierBit, chunkSize: 65536);

        var raw = seedBuffer.ReadBack<Vector4>(patchCount);
        var result = new Vector3[patchCount];
        for (int i = 0; i < patchCount; i++)
        {
            result[i] = new Vector3(raw[i].X, raw[i].Y, raw[i].Z);
        }
        return result;
    }
}

/*
public static class PatchLuxelBlendPass
{
    public static void Run(GL gl, GBufferResources gbuffer, BvhResources bvh, GpuBuffer texelHomePatch, GpuBuffer patchFinalValues, PatchBucketGridResources grid, LightmapLayerResources layer, float blendRadius, int maxContributions = 24)
    {
        int cellSearchRadius = (int)MathF.Ceiling(blendRadius / grid.CellSize);
        var (offsetsFlat, offsetCount) = PatchSystem.BuildSortedCellOffsets(cellSearchRadius);

        using var offsetsBuffer = new GpuBuffer(gl);
        offsetsBuffer.Upload<int>(offsetsFlat);

        using var program = new ComputeProgram(gl, "PatchLuxelBlend.glsl", "BvhTrace.glsl");
        program.Use();

        gbuffer.BindImages();
        layer.Bind();
        bvh.Bind();
        texelHomePatch.BindBase(GpuBindings.TexelHomePatch);
        patchFinalValues.BindBase(GpuBindings.PatchFinalValues);
        grid.Bind();
        offsetsBuffer.BindBase(GpuBindings.PatchBlendCellOffsets);

        program.SetUniform("gridOrigin", grid.GridOrigin);
        program.SetUniform("cellSize", grid.CellSize);
        program.SetUniformInt3("gridDims", grid.DimsX, grid.DimsY, grid.DimsZ);
        program.SetUniform("blendRadius", blendRadius);
        program.SetUniform("cellOffsetCount", offsetCount);
        program.SetUniform("maxContributions", maxContributions);

        CompilerConsole.Step("Blending patches to luxels (GPU)...");
        program.DispatchRowsChunked(gl, layer.Resolution, "rowStart", 8, 8, MemoryBarrierMask.ShaderStorageBarrierBit, rowsPerChunk: 16, label: "Blending patches to luxels");
    }
}

*/
public static class PatchBlendGenerationPass
{
    public static (GpuBuffer counts, GpuBuffer indices) Run(GL gl, BvhResources bvh, PatchResources patches, PatchBucketGridResources grid, Patch[] patchArray, int patchCount, float blendRadius, int maxNeighbors)
    {
        var counts = new GpuBuffer(gl);
        counts.Upload<int>(new int[patchCount]);

        var indices = new GpuBuffer(gl);
        indices.Upload<int>(new int[patchCount * maxNeighbors]);

        float maxExtent = 0f;
        for (int i = 0; i < patchArray.Length; i++)
        {
            var p = patchArray[i];
            float e = 0f;
            e = MathF.Max(e, Vector3.Distance(p.center, p.c0));
            e = MathF.Max(e, Vector3.Distance(p.center, p.c1));
            e = MathF.Max(e, Vector3.Distance(p.center, p.c2));
            e = MathF.Max(e, Vector3.Distance(p.center, p.c3));
            maxExtent = MathF.Max(maxExtent, e);
        }

        float effectiveSearchRadius = blendRadius * 2f + maxExtent;
        int cellSearchRadius = (int)MathF.Ceiling(effectiveSearchRadius / grid.CellSize);
        var (offsetsFlat, offsetCount) = PatchSystem.BuildSortedCellOffsets(cellSearchRadius);

        using var offsetsBuffer = new GpuBuffer(gl);
        offsetsBuffer.Upload<int>(offsetsFlat);

        using var program = new ComputeProgram(gl, "PatchBlendGeneration.glsl", "BvhTrace.glsl");
        program.Use();

        bvh.Bind();
        patches.Bind();
        grid.Bind();
        counts.BindBase(GpuBindings.PatchNeighborCount);
        indices.BindBase(GpuBindings.PatchNeighborIndices);
        offsetsBuffer.BindBase(GpuBindings.PatchBlendCellOffsets);

        program.SetUniform("patchCount", patchCount);
        program.SetUniform("gridOrigin", grid.GridOrigin);
        program.SetUniform("cellSize", grid.CellSize);
        program.SetUniformInt3("gridDims", grid.DimsX, grid.DimsY, grid.DimsZ);
        program.SetUniform("blendRadius", blendRadius);
        program.SetUniform("cellOffsetCount", offsetCount);
        program.SetUniform("maxNeighbors", maxNeighbors);

        program.DispatchUnitsChunked(gl, patchCount, "patchOffset", 64, MemoryBarrierMask.ShaderStorageBarrierBit, chunkSize: 65536, label: "Finding blend neighbors");

        return (counts, indices);
    }
}

public static class PatchLuxelBlendPass
{
    public static void Run(GL gl, GBufferResources gbuffer, BvhResources bvh, GpuBuffer texelHomePatch, GpuBuffer patchFinalValues, GpuBuffer neighborCounts, GpuBuffer neighborIndices, PatchResources patches, LightmapLayerResources layer, float blendRadius, int maxNeighbors)
    {
        using var program = new ComputeProgram(gl, "PatchLuxelBlend.glsl", "Lightmap.glsl", "BvhTrace.glsl");
        program.Use();

        gbuffer.BindImages();
        layer.Bind();
        bvh.Bind();
        patches.Bind();
        texelHomePatch.BindBase(GpuBindings.TexelHomePatch);
        patchFinalValues.BindBase(GpuBindings.PatchFinalValues);
        neighborCounts.BindBase(GpuBindings.PatchNeighborCount);
        neighborIndices.BindBase(GpuBindings.PatchNeighborIndices);

        program.SetUniform("blendRadius", blendRadius);
        program.SetUniform("maxNeighbors", maxNeighbors);

        program.DispatchRowsChunked(gl, layer.Resolution, "rowStart", 8, 8, MemoryBarrierMask.ShaderStorageBarrierBit, rowsPerChunk: 64, label: "Blending patches to luxels");
    }
}

public static class AmbientOcclusionPass
{
    public static void Run(GL gl, BvhResources bvh, GBufferResources gbuffer, GpuBuffer aoResult, int resolution, int sampleCount = 64, float aoRadius = 2f)
    {
        using var program = new ComputeProgram(gl, "AmbientOcclusion.glsl", "BvhTrace.glsl");
        program.Use();

        gbuffer.BindImages();
        bvh.Bind();
        gbuffer.BindTexelBuffers();
        aoResult.BindBase(GpuBindings.AOResult);

        program.SetUniform("sampleCount", sampleCount);
        program.SetUniform("aoRadius", aoRadius);

        CompilerConsole.Step("Computing ambient occlusion (GPU)...");
        program.DispatchRowsChunked(gl, resolution, "rowStart", 8, 8, MemoryBarrierMask.ShaderStorageBarrierBit, rowsPerChunk: 64);
    }
}

public static class AOApplyPass
{
    public static void Run(GL gl, GBufferResources gbuffer, GpuBuffer aoResult, LightmapLayerResources layer, int blurRadius = 3, float aoStrength = 1f)
    {
        using var program = new ComputeProgram(gl, "AOBlurApply.glsl", "Lightmap.glsl");
        program.Use();

        gbuffer.BindImages();
        layer.Bind();
        aoResult.BindBase(GpuBindings.AOResult);

        program.SetUniform("blurRadius", blurRadius);
        program.SetUniform("aoStrength", aoStrength);

        CompilerConsole.Step("Applying ambient occlusion (GPU)...");
        program.DispatchRowsChunked(gl, layer.Resolution, "rowStart", 8, 8, MemoryBarrierMask.ShaderStorageBarrierBit, rowsPerChunk: 64);
    }
}

public static class LightNodeBakePass
{
    public static Vector3[][] Run(GL gl, BvhResources bvh, GpuBuffer childPositions, GpuBuffer patchValues, GpuBuffer texelHomePatch, int childCount, int lightmapResolution, Vector3 ambientColor, float ambientIntensity, int sampleCount = 128)
    {
        using var shOut = new GpuBuffer(gl);
        shOut.Upload<Vector4>(new Vector4[childCount * 9], usage: BufferUsageARB.StreamRead);

        using var program = new ComputeProgram(gl, "NodeBake.glsl", "BvhTrace.glsl", "BvhHitUV.glsl");
        program.Use();

        bvh.Bind();
        bvh.BindSurfaces();
        texelHomePatch.BindBase(GpuBindings.TexelHomePatch);
        patchValues.BindBase(GpuBindings.PatchFinalValues);
        childPositions.BindBase(GpuBindings.ChildPositions);
        shOut.BindBase(GpuBindings.LightNodeSHOut);

        program.SetUniform("childCount", childCount);
        program.SetUniform("sampleCount", sampleCount);
        program.SetUniform("lightmapResolution", lightmapResolution);
        program.SetUniform("ambientColor", ambientColor);
        program.SetUniform("ambientIntensity", ambientIntensity);

        CompilerConsole.Step("Baking light node SH (GPU)...");
        program.DispatchUnitsChunked(gl, childCount, "childOffset", 8, MemoryBarrierMask.ShaderStorageBarrierBit, chunkSize: 65536);

        var raw = shOut.ReadBack<Vector4>(childCount * 9);
        var result = new Vector3[childCount][];
        for (int c = 0; c < childCount; c++)
        {
            var coeffs = new Vector3[9];
            for (int k = 0; k < 9; k++)
            {
                var v = raw[c * 9 + k];
                coeffs[k] = new Vector3(v.X, v.Y, v.Z);
            }
            result[c] = coeffs;
        }
        return result;
    }
}

public static class LightNodeOcclusionPass
{
    public static bool[][] Run(GL gl, BvhResources bvh, GpuBuffer childPositions, LightResources lightResources, int childCount, int lightCount)
    {
        using var blockedBuffer = new GpuBuffer(gl);
        blockedBuffer.Upload<int>(new int[childCount * lightCount], usage: BufferUsageARB.StreamRead);

        using var program = new ComputeProgram(gl, "NodeOcclusion.glsl", "BvhTrace.glsl");
        program.Use();

        bvh.Bind();
        lightResources.Bind();
        childPositions.BindBase(GpuBindings.ChildPositions);
        blockedBuffer.BindBase(GpuBindings.LightNodeBlocked);

        program.SetUniform("childCount", childCount);
        program.SetUniform("lightCount", lightCount);

        CompilerConsole.Step("Testing light node occlusion (GPU)...");
        program.DispatchUnitsChunked(gl, childCount, "childOffset", 8, MemoryBarrierMask.ShaderStorageBarrierBit, chunkSize: 65536);

        var raw = blockedBuffer.ReadBack<int>(childCount * lightCount);
        var result = new bool[childCount][];
        for (int c = 0; c < childCount; c++)
        {
            var row = new bool[lightCount];
            for (int i = 0; i < lightCount; i++)
            {
                row[i] = raw[c * lightCount + i] != 0;
            }
            result[c] = row;
        }
        return result;
    }
}

public static class PropVertexDirectPass
{
    public static Vector3[] Run(GL gl, BvhResources bvh, PropVertexResources verts, LightResources lightResources)
    {
        using var directOut = new GpuBuffer(gl);
        directOut.Upload<Vector4>(new Vector4[verts.Count], usage: BufferUsageARB.StreamRead);

        using var program = new ComputeProgram(gl, "PropVertexDirect.glsl", "BvhTrace.glsl");
        program.Use();

        bvh.Bind();
        lightResources.Bind();
        verts.Bind();
        directOut.BindBase(GpuBindings.PropVertexDirectOut);

        program.SetUniform("lightCount", lightResources.Count);
        program.SetUniform("vertCount", verts.Count);

        CompilerConsole.Step("Baking prop vertex direct light (GPU)...");
        program.DispatchUnitsChunked(gl, verts.Count, "vertOffset", 8, MemoryBarrierMask.ShaderStorageBarrierBit, chunkSize: 65536);

        var raw = directOut.ReadBack<Vector4>(verts.Count);
        var result = new Vector3[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            result[i] = new Vector3(raw[i].X, raw[i].Y, raw[i].Z);
        }
        return result;
    }
}