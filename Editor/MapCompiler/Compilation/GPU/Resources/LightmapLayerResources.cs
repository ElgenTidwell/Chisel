using Microsoft.Xna.Framework;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapCompiler.Compilation.GPU.Resources;
public sealed class LightmapLayerResources : IDisposable
{
    private const int VectorBytes = 16;

    private readonly GpuBuffer combined;
    public int Resolution { get; }
    private readonly int total;

    public LightmapLayerResources(GL gl, int resolution)
    {
        Resolution = resolution;
        total = resolution * resolution;

        combined = new GpuBuffer(gl);
        combined.Upload<Vector4>(new Vector4[total * 3], BufferUsageARB.StreamRead);
    }

    public void Bind()
    {
        combined.BindBase(GpuBindings.Lightmap);
    }

    public void ReadBackInto(LightmapColor[] lmB1, LightmapColor[] lmB2, LightmapColor[] lmB3)
    {
        var raw = combined.ReadBack<Vector4>(total * 3);

        CompilerConsole.Info("Writing to lightmap basis arrays...");

        Parallel.For(0, total, i =>
        {
            lmB1[i] = new LightmapColor(raw[i].X, raw[i].Y, raw[i].Z);
            lmB2[i] = new LightmapColor(raw[total + i].X, raw[total + i].Y, raw[total + i].Z);
            lmB3[i] = new LightmapColor(raw[total * 2 + i].X, raw[total * 2 + i].Y, raw[total * 2 + i].Z);
        });
    }

    public void Dispose()
    {
        combined.Dispose();
    }
}