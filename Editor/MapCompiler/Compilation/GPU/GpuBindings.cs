namespace MapCompiler.Compilation.GPU;
public static class GpuBindings
{
    public const uint Lights = 0;
    public const uint BvhNodes = 1;
    public const uint TriV0 = 2;
    public const uint TriV1 = 3;
    public const uint TriV2 = 4;
    public const uint TriSourceBrush = 5;
    public const uint TriEntityGroup = 6;
    public const uint TriIsSkybox = 7;
    public const uint TexelSourceBrush = 8;
    public const uint TexelEntityGroup = 9;
    public const uint Patches = 10;

    public const uint PatchBounceAccum = 11;
    public const uint PatchBucketOffsets = 12;
    public const uint PatchBucketIndices = 13;

    public const uint PropVertexPositions = 11;
    public const uint PropVertexNormals = 12;
    public const uint PropVertexDirectOut = 13;

    public const uint SkyResult = 14;
    public const uint AOResult = 14;

    public const uint ChildPositions = 15;

    public const uint PatchSeedResult = 16;

    public const uint LightNodeSHOut = 16;
    public const uint LightNodeBlocked = 17;

    public const uint TexelHomePatch = 18;

    public const uint PatchNeighborCount = 19;
    public const uint PatchNeighborIndices = 20;

    public const uint LayerB1 = 21;
    public const uint LayerB2 = 22;
    public const uint LayerB3 = 23;

    public const uint TriUv0 = 24;
    public const uint TriUv1 = 25;
    public const uint TriUv2 = 26;
    public const uint TriAlbedo = 27;
    public const uint SkyVisibilityLuxel = 28;

    public const uint PatchGatherIn = 29;
    public const uint PatchBlendCellOffsets = 29;
    public const uint PatchGatherOut = 30;
    public const uint PatchNeighborVisibility = 30;
    public const uint PatchFinalValues = 31;

    public const uint ImagePosition = 0;
    public const uint ImageNormal = 1;
    public const uint ImageBasis1 = 2;
    public const uint ImageBasis2 = 3;
    public const uint ImageBasis3 = 4;
}