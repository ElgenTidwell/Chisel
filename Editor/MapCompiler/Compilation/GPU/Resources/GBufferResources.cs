using Microsoft.Xna.Framework;
using Silk.NET.OpenGL;
using System;
using System.Runtime.InteropServices;

namespace MapCompiler.Compilation.GPU.Resources;

[StructLayout(LayoutKind.Sequential)]
public struct GBufferVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Basis1;
    public Vector3 Basis2;
    public Vector3 Basis3;
    public Vector2 LightmapUV;
    public int SourceBrush;
    public int EntityGroup;
}

public struct GBufferData
{
    public Vector4[] PositionAndValid;
    public Vector3[] Normal;
    public Vector3[] Basis1, Basis2, Basis3;
    public int[] SourceBrush;
    public int[] EntityGroup;
}

public sealed class GBufferResources : IDisposable
{
    public GpuTexture Position { get; }
    public GpuTexture Normal { get; }
    public GpuTexture Basis1 { get; }
    public GpuTexture Basis2 { get; }
    public GpuTexture Basis3 { get; }

    private readonly GpuBuffer texelOwner;

    public GBufferResources(GL gl, GBufferData data, int resolution)
    {
        gl.GetInteger(GetPName.MaxTextureSize, out int maxTexSize);
        if (resolution > maxTexSize)
            throw new InvalidOperationException($"Requested resolution {resolution} exceeds GL_MAX_TEXTURE_SIZE ({maxTexSize}).");

        Position = new GpuTexture(gl, resolution, resolution, GLEnum.Rgba32f, MemoryMarshal.AsBytes(data.PositionAndValid.AsSpan()));
        Normal = new GpuTexture(gl, resolution, resolution, GLEnum.Rgba16f, MemoryMarshal.AsBytes(ToVec4(data.Normal).AsSpan()));
        Basis1 = new GpuTexture(gl, resolution, resolution, GLEnum.Rgba16f, MemoryMarshal.AsBytes(ToVec4(data.Basis1).AsSpan()));
        Basis2 = new GpuTexture(gl, resolution, resolution, GLEnum.Rgba16f, MemoryMarshal.AsBytes(ToVec4(data.Basis2).AsSpan()));
        Basis3 = new GpuTexture(gl, resolution, resolution, GLEnum.Rgba16f, MemoryMarshal.AsBytes(ToVec4(data.Basis3).AsSpan()));

        var owner = new int[data.SourceBrush.Length * 2];
        for (int i = 0; i < data.SourceBrush.Length; i++)
        {
            owner[i * 2] = data.SourceBrush[i];
            owner[i * 2 + 1] = data.EntityGroup[i];
        }

        texelOwner = new GpuBuffer(gl);
        texelOwner.Upload<int>(owner);
    }
    private GBufferResources(GpuTexture position, GpuTexture normal, GpuTexture basis1, GpuTexture basis2, GpuTexture basis3, GpuBuffer texelOwner)
    {
        Position = position; Normal = normal; Basis1 = basis1; Basis2 = basis2; Basis3 = basis3;
        this.texelOwner = texelOwner;
    }

    public static GBufferResources FromRaster(GpuTexture position, GpuTexture normal, GpuTexture basis1, GpuTexture basis2, GpuTexture basis3, GpuBuffer texelOwner)
        => new GBufferResources(position, normal, basis1, basis2, basis3, texelOwner);

    public void BindImages()
    {
        Position.BindImage(GpuBindings.ImagePosition, readOnly: true);
        Normal.BindImage(GpuBindings.ImageNormal, readOnly: true);
        Basis1.BindImage(GpuBindings.ImageBasis1, readOnly: true);
        Basis2.BindImage(GpuBindings.ImageBasis2, readOnly: true);
        Basis3.BindImage(GpuBindings.ImageBasis3, readOnly: true);
    }
    public void BindTexelBuffers()
    {
        texelOwner.BindBase(GpuBindings.TexelOwner);
    }

    private static Vector4[] ToVec4(Vector3[] src)
    {
        var dst = new Vector4[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = new Vector4(src[i], 0f);
        }
        return dst;
    }

    public void Dispose()
    {
        Position.Dispose();
        Normal.Dispose();
        Basis1.Dispose();
        Basis2.Dispose();
        Basis3.Dispose();
        texelOwner.Dispose();
    }
}