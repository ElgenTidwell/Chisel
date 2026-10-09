namespace MapCompiler.Compilation.GPU;
public static class GpuBindings
{
    public const uint BvhNodes = 0;

    public const uint BvhTriangles = 1;

    public const uint BvhSurfaces = 2;
    public const uint Lightmap = 2;
    public const uint PatchBlendCellOffsets = 2;

    public const uint Patches = 3;
    public const uint Lights = 3;

    public const uint TexelHomePatch = 4;
    public const uint TexelOwner = 4;
    public const uint PatchBucketOffsets = 4;
    public const uint PropVertexPositions = 4;

    public const uint PatchGatherIn = 5;
    public const uint PatchFinalValues = 5;
    public const uint PatchBucketIndices = 5;
    public const uint PatchSeedResult = 5;
    public const uint SkyVisibilityLuxel = 5;
    public const uint SkyResult = 5;
    public const uint AOResult = 5;
    public const uint PropVertexNormals = 5;

    public const uint PatchGatherOut = 6;
    public const uint ChildPositions = 6;
    public const uint PatchNeighborCount = 6;
    public const uint PropVertexDirectOut = 6;

    public const uint PatchBounceAccum = 7;
    public const uint LightNodeSHOut = 7;
    public const uint LightNodeBlocked = 7;
    public const uint PatchNeighborIndices = 7;

    public const uint ImagePosition = 0;
    public const uint ImageNormal = 1;
    public const uint ImageBasis1 = 2;
    public const uint ImageBasis2 = 3;
    public const uint ImageBasis3 = 4;
}