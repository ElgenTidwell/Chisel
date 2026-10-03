using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapCompiler.Compilation.GPU.Resources;
public sealed class PatchBlendTopology : IDisposable
{
    private readonly GpuBuffer texelHomePatch;

    public PatchBlendTopology(GL gl, int[] texelHomePatchArr, int[] offsets, int[] flat)
    {
        texelHomePatch = new GpuBuffer(gl);
        texelHomePatch.Upload<int>(texelHomePatchArr);
    }

    public void Bind()
    {
        texelHomePatch.BindBase(GpuBindings.TexelHomePatch);
    }

    public void Dispose()
    {
        texelHomePatch.Dispose();
    }
}