using MapCompiler.Compilation.GPU.Resources;
using Rockwall;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MapCompiler.Compilation.GPU;
public static class GBufferRasterBuilder
{
    public static GBufferResources Build(
        GL gl, Brush[] brushes, Terrain[] terrains,
        Dictionary<(int brush, int face, int vertex), SmoothedVertexData> smoothedNormals,
        int resolution, int dilateIterations = 8)
    {
        var verts = GBufferGeometryBuilder.BuildGBufferGeometry(brushes, terrains, smoothedNormals);

        using var vbo = new GpuVertexBuffer(gl);
        vbo.Upload<GBufferVertex>(verts);

        uint stride = (uint)Marshal.SizeOf<GBufferVertex>();
        using var vao = new GpuVertexArray(gl, vbo,
            new VertexAttribute(0, 3, VertexAttribPointerType.Float, stride, 0),
            new VertexAttribute(1, 3, VertexAttribPointerType.Float, stride, 12),
            new VertexAttribute(2, 3, VertexAttribPointerType.Float, stride, 24),
            new VertexAttribute(3, 3, VertexAttribPointerType.Float, stride, 36),
            new VertexAttribute(4, 3, VertexAttribPointerType.Float, stride, 48),
            new VertexAttribute(5, 2, VertexAttribPointerType.Float, stride, 60),
            new VertexAttribute(6, 1, VertexAttribPointerType.Int, stride, 68, isInteger: true),
            new VertexAttribute(7, 1, VertexAttribPointerType.Int, stride, 72, isInteger: true));

        AttachmentDesc[] formats = {
            AttachmentDesc.Float(GLEnum.Rgba32f), AttachmentDesc.Float(GLEnum.Rgba16f),
            AttachmentDesc.Float(GLEnum.Rgba16f), AttachmentDesc.Float(GLEnum.Rgba16f),
            AttachmentDesc.Float(GLEnum.Rgba16f), AttachmentDesc.Int(), AttachmentDesc.Int()
        };

        GpuTexture[] current;
        using (var renderTarget = new RenderTarget(gl, resolution, resolution, formats))
        using (var program = new GraphicsProgram(gl, "GBufferRaster.vert.glsl", "GBufferRaster.frag.glsl", "GBufferRaster.geom.glsl"))
        {
            renderTarget.Bind();
            renderTarget.Clear();
            program.Use();
            program.SetUniformInt2("targetResolution", resolution, resolution);
            vao.Bind();
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)verts.Length);
            RenderTarget.Unbind(gl);

            current = renderTarget.DetachAttachments();
        }

        GpuTexture[] scratch;
        using (var scratchTarget = new RenderTarget(gl, resolution, resolution, formats))
        {
            scratch = scratchTarget.DetachAttachments();
        }

        uint groups = (uint)((resolution + 7) / 8);

        using var maskCompute = new ComputeProgram(gl, "GBufferDilateMask.glsl");
        using var applyFull = new ComputeProgram(gl, "GBufferDilate.glsl");
        using var applyHalf = new ComputeProgram(gl, "GBufferDilateHalf.glsl");
        using var applyInt = new ComputeProgram(gl, "GBufferDilateInt.glsl");

        for (int iter = 0; iter < dilateIterations; iter++)
        {
            using var maskA = new GpuTexture(gl, resolution, resolution, GLEnum.R32i, default, PixelFormat.RedInteger, PixelType.Int);

            maskCompute.Use();
            current[0].BindImage(0, readOnly: true);
            maskA.BindImage(1, readOnly: false);
            maskCompute.Dispatch(groups, groups);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit);

            applyFull.Use();
            current[0].BindImage(0, readOnly: true);
            maskA.BindImage(1, readOnly: true);
            scratch[0].BindImage(2, readOnly: false);
            applyFull.Dispatch(groups, groups);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit);

            for (int a = 1; a < 5; a++)
            {
                applyHalf.Use();
                current[a].BindImage(0, readOnly: true);
                maskA.BindImage(1, readOnly: true);
                scratch[a].BindImage(2, readOnly: false);
                applyHalf.Dispatch(groups, groups);
                gl.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit);
            }

            for (int a = 5; a < 7; a++)
            {
                applyInt.Use();
                current[a].BindImage(0, readOnly: true);
                maskA.BindImage(1, readOnly: true);
                scratch[a].BindImage(2, readOnly: false);
                applyInt.Dispatch(groups, groups);
                gl.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit);
            }

            (current, scratch) = (scratch, current);
        }

        var texelOwner = new GpuBuffer(gl);
        texelOwner.Upload<int>(new int[resolution * resolution * 2]);

        using (var packProgram = new ComputeProgram(gl, "GBufferPack.glsl"))
        {
            packProgram.Use();
            current[5].BindImage(0, readOnly: true);
            current[6].BindImage(1, readOnly: true);
            texelOwner.BindBase(GpuBindings.TexelOwner);

            packProgram.Dispatch(groups, groups);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        }

        current[5].Dispose();
        current[6].Dispose();
        foreach (var tex in scratch) tex.Dispose();

        return GBufferResources.FromRaster(current[0], current[1], current[2], current[3], current[4], texelOwner);
    }
}