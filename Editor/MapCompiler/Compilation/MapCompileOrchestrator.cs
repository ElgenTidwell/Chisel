using Chisel.Formatter;
using MapCompiler.Compilation;
using MapCompiler.Compilation.GPU;
using MapCompiler.Compilation.GPU.Resources;
using MessagePack;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics.PackedVector;
using Newtonsoft.Json;
using Rockwall;
using Silk.NET.OpenAL;
using Silk.NET.OpenGL;
using SimpleImageIO;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MapCompiler
{
    public static class MapCompileOrchestrator
    {
        public static readonly Vector3 B1 = new(MathF.Sqrt(2f / 3f), 0f, 1f / MathF.Sqrt(3f));
        public static readonly Vector3 B2 = new(-1f / MathF.Sqrt(6f), 1f / MathF.Sqrt(2f), 1f / MathF.Sqrt(3f));
        public static readonly Vector3 B3 = new(-1f / MathF.Sqrt(6f), -1f / MathF.Sqrt(2f), 1f / MathF.Sqrt(3f));

        private static EntityReference? skyCamera = null;
        private struct PatchBlendCorners
        {
            public Vector3 B1_c0, B1_c1, B1_c2, B1_c3, B1_center;
            public Vector3 B2_c0, B2_c1, B2_c2, B2_c3, B2_center;
            public Vector3 B3_c0, B3_c1, B3_c2, B3_c3, B3_center;
        }

        public static void Compile(
            Brush[] _brushes,
            EntityReference[] entities,
            Terrain[] terrains,
            System.Drawing.Bitmap[] textures,
            Color[] matColors,
            string mapPath,
            int lightmapUnitSize,
            bool fastVis)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            CompilerConsole.Header("Geometry");
            CompilerConsole.Step("Sorting brushes by volume...");

            List<(int ta, int tb, List<int> localIndices, List<int> neighborIndices)> terrainSeams = terrains != null ? FindAllTouchingTerrainSeams(terrains, _brushes) : null;

            Brush[] brushes = SortBrushesByVolume(_brushes, out int[] sortOldToNew);

            var keptBrushes = new List<Brush>(brushes.Length);
            var sortedToFinal = new int[brushes.Length];
            for (int i = 0; i < brushes.Length; i++)
            {
                if (brushes[i].isUsedForTerrain) { sortedToFinal[i] = -1; continue; }
                sortedToFinal[i] = keptBrushes.Count;
                keptBrushes.Add(brushes[i]);
            }
            brushes = keptBrushes.ToArray();

            int[] origToFinal = new int[_brushes.Length];
            for (int i = 0; i < _brushes.Length; i++) origToFinal[i] = sortedToFinal[sortOldToNew[i]];

            RemapEntityBrushOwnership(entities, origToFinal);
            RemapTerrainBrushSources(terrains, origToFinal);

            BoundingBox[] brushBounds = new BoundingBox[brushes.Length];

            var allFaces = new List<LeafInfo>();
            var lightNodes = new List<LightNodeBundle>();
            var aiNodes = new List<AINode>();
            var finalFaces = new Dictionary<int, List<Face>>();

            ushort[] brushIDs = new ushort[brushes.Length];
            for (ushort i = 0; i < brushes.Length; i++)
            {
                brushIDs[i] = i;
                finalFaces.Add(i, new List<Face>(brushes[i].Faces));
            }
            for (int i = 0; i < brushes.Length; i++)
            {
                brushes[i].Faces = finalFaces[i].ToArray();
            }

            int[] brushOwner = Rockwall.EntityOwnership.Sync(brushes, entities);

            if (entities != null)
            {
                foreach (var e in entities)
                {
                    if (e?.BrushIndices == null || e.BrushIndices.Count == 0) continue;
                    int anchor = e.BrushIndices[0];
                    if (anchor >= 0 && anchor < brushes.Length) e.Position = brushes[anchor].Position;
                }
            }
            if (terrains != null)
            {
                for (int i = 0; i < terrains.Length; i++)
                {
                    BrushOperations.UpdateTerrain(ref terrains[i]);
                }
                WeldTerrainSeamNormals(terrains, terrainSeams);
            }

            CompilerConsole.Step("Merging FuncDetail brush groups...");
            DetailBrushCSG.MergeDetailGroups(brushes, entities);

            CompilerConsole.Step("Computing brush boundaries...");

            Vector3 absoluteMin = new(float.MaxValue), absoluteMax = new(float.MinValue);
            LightNodeBundle tempNodes;

            for (int i = 0; i < brushes.Length; i++)
            {
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                brushes[i].Abnormal = BrushOperations.CheckIsAbnormal(brushes[i], out _, out _);
                brushes[i].IsSkybox = false;
                brushes[i].IsLightNodeVolume = false;
                brushes[i].IsTrigger = false;
                brushes[i].IsClip = false;
                brushes[i].IsDetail = false;

                var brush = brushes[i];
                for (int j = brushes[i].Faces.Length - 1; j >= 0; j--)
                {
                    ref var face = ref brushes[i].Faces[j];

                    // Make sure we're all synced up for later, too.
                    if(GlobalMapData.MaterialNameToIndex.ContainsKey(face.MaterialName))
                        face.Surface = GlobalMapData.MaterialNameToIndex[face.MaterialName];

                    face.toolFace = !string.IsNullOrEmpty(face.MaterialName) && face.MaterialName.StartsWith("tool");
                    brushes[i].IsSkybox |= face.MaterialName == "tool_skybox";
                    brushes[i].IsLightNodeVolume |= face.MaterialName == "tool_lightnodevolume";
                    brushes[i].IsTrigger |= face.MaterialName == "tool_trigger";
                    brushes[i].IsClip |= face.MaterialName == "tool_clip";

                    allFaces.Add(new LeafInfo(i, j));

                    Vector3 faceNormal = Vector3.Normalize(face.Normal);
                    UvCalculator.GetUVAxes(face, out Vector3 uAxis, out Vector3 vAxis);
                    Vector3 faceTangent = Vector3.Normalize(uAxis - faceNormal * Vector3.Dot(uAxis, faceNormal));
                    Vector3 faceBinormal = Vector3.Normalize(vAxis - faceNormal * Vector3.Dot(vAxis, faceNormal));

                    var tbn = new Matrix(
                        faceTangent.X, faceTangent.Y, faceTangent.Z, 0f,
                        faceBinormal.X, faceBinormal.Y, faceBinormal.Z, 0f,
                        faceNormal.X, faceNormal.Y, faceNormal.Z, 0f,
                        0f, 0f, 0f, 1f);
                    face.Basis1 = Vector3.TransformNormal(B1, tbn);
                    face.Basis2 = Vector3.TransformNormal(B2, tbn);
                    face.Basis3 = Vector3.TransformNormal(B3, tbn);
                    face.Drawn = !face.toolFace;

                    foreach (int t in face.Indices)
                    {
                        Vector3 v = brushes[i].Vertices[t] + brushes[i].Position;
                        min = Vector3.Min(v, min);
                        max = Vector3.Max(v, max);
                    }
                }
                // brushOwner/isEntity were already derived by EntityOwnership.Sync above; isDetail
                // just needs to know *which* entity, to check its class name.
                bool isPartOfEntity = Rockwall.EntityOwnership.IsPartOfEntity(brushOwner, i);
                brushes[i].IsDetail = isPartOfEntity && entities[brushOwner[i]].EntityName == "FuncDetail";

                if (brushes[i].IsLightNodeVolume)
                {
                    lightNodes.Add(new LightNodeBundle(new BoundingBox(min, max), skipOccluded: false));
                }
                else
                {
                    brushBounds[i] = new BoundingBox(min, max);
                    absoluteMin = Vector3.Min(absoluteMin, min);
                    absoluteMax = Vector3.Max(absoluteMax, max);
                }
            }

            CompilerConsole.Header("Spatial Structures");
            CompilerConsole.Step("Building octree...");

            float big = MathF.Max(absoluteMax.X, MathF.Max(absoluteMax.Y, absoluteMax.Z));
            float small = MathF.Min(absoluteMin.X, MathF.Min(absoluteMin.Y, absoluteMin.Z));
            var mapBounds = new BoundingBox(absoluteMin, absoluteMax);

            var octreeRoot = new Octree(new BoundingBox(Vector3.One * (small - 1), Vector3.One * (big + 1)), 0, 0);
            OctreeRoot.AllNodes.Add(octreeRoot);

            var splits = new List<(int brush, int face, Plane plane, int pside)>(brushes.Length * 6);
            for (int i = 0; i < brushBounds.Length; i++)
            {
                if (brushes[i].IsLightNodeVolume || brushes[i].IsEntity || brushes[i].IsClip) continue;

                octreeRoot.TestAdd(brushBounds[i], i);

                for (int f = 0; f < brushes[i].Faces.Length; f++)
                {
                    var face = brushes[i].Faces[f];
                    var plane = new Plane(brushes[i].Vertices[face.Indices[0]] + brushes[i].Position,
                                         Vector3.Normalize(face.Normal));
                    Portalizer.FindPlane(ref plane, out _);
                    splits.Add((i, f, plane, 0));
                }
            }

            CompilerConsole.Stat("Total planes", Portalizer.planes.Count);

            splits.Sort((a, b) =>
            {
                float sA = FaceSize(brushes[a.brush].Faces[a.face], brushes[a.brush].Vertices);
                float sB = FaceSize(brushes[b.brush].Faces[b.face], brushes[b.brush].Vertices);
                return sB.CompareTo(sA);
            });

            CompilerConsole.Step("Building BSP tree...");
            BSPRoot.tempNodes = new List<BSPNode> { new BSPNode() };
            BSPRoot.tempNodes[0].nodeContents = brushIDs;
            foreach (var s in splits)
                BSPRoot.Cut(s.plane, brushes, brushBounds, s.brush, s.face, 0);
            BSPRoot.DoubleCheck(brushes);
            BSPRoot.Nodes = BSPRoot.tempNodes.ToArray();

            CompilerConsole.Header("Visibility");
            CompilerConsole.Step("Creating vis leaves...");

            Portalizer.MakeHeadnodePortals(mapBounds);
            Portalizer.CutNodePortals();
            Portalizer.CalculateLeafCenters();
            Portalizer.MergePortals();
            Portalizer.MarkBrushesOnPortals(brushes, brushBounds);
            Portalizer.FillOutside(entities, mapPath);
            Portalizer.MergePortals();
            Portalizer.MarkBrushesOnPortals(brushes, brushBounds);

            Portalizer.InitPortalBitsets();

            CompilerConsole.Step("Calculating coarse PVS...");
            Portalizer.CalculateCoarsePVS();

            if (!fastVis)
            {
                CompilerConsole.Step("Calculating fine PVS (fastVis=false)...");
                Portalizer.CalculateFinePVS();
            }
            else
            {
                CompilerConsole.Warn("Skipping fine PVS (fastVis=true).");
            }

            foreach (var node in OctreeRoot.AllNodes)
            {
                if (!node.IsEnd) continue;
                lightNodes.Add(new LightNodeBundle(node.Box, 2f, false));
            }

            Portalizer.Finalize(brushes);

            CompilerConsole.Step("Collecting vis data...");
            var visData = new VisFile
            {
                Leaves = Portalizer.GetVisLeaves(brushes).ToArray(),
                Portals = Portalizer.GetPortals().ToArray()
            };

            for (int b = 0; b < brushes.Length; b++)
            {
                var lightmapUvs = new List<Vector2>(brushes[b].UVs);

                var triangles = brushes[b].Faces.SelectMany(f => f.Indices.Select(i => (short)i)).ToArray();

                brushes[b].LightmapUVs = UvCalculator.CalculateUVs(brushes[b].Vertices, triangles, 1f, brushes[b].Position);
            }
            for (int t = 0; t < terrains.Length; t++)
            {
                terrains[t].lightmapUvs = UvCalculator.CalculateUVs(
                    terrains[t].Vertices.Select(i => i.Position).ToArray(),
                    terrains[t].Triangles, 1f, projectionNormal: terrains[t].SourceNormal);
            }

            CompilerConsole.Header("Lightmap UVs");
            CompilerConsole.Step("Rebuilding brush geometry from CSG results...");
            BrushCSGReconstructor.RebuildBrushesFromPortals(ref brushes, out var workingLeafPolys, out var pendingToFinalFace, out var pendingToBaseVertex, visData.Portals);

            CompilerConsole.Step("Computing smoothing groups...");
            var smoothedNormals = SmoothGroups.Compute(brushes, smoothAngleDegrees: 80f);

            CompilerConsole.Step("Applying smoothed normals to leaf polygons...");
            BrushCSGReconstructor.ApplySmoothedNormalsToLeafPolys(workingLeafPolys, brushes, pendingToFinalFace, smoothedNormals);

            CompilerConsole.Step("Packing lightmap UVs...");
            LightmapUVPacker.PackUVs(ref brushes, ref terrains, textures, lightmapUnitSize, out int lightmapResolution, out float lmax);
            CompilerConsole.Stat("Atlas resolution", $"{lightmapResolution}x{lightmapResolution}");

            CompilerConsole.Step("Computing leaf polygon lightmap UVs...");
            BrushCSGReconstructor.ComputeLeafPolygonLightmapUVs(workingLeafPolys, brushes, pendingToBaseVertex);

            var finalLeafPolys = CompileLeafPolygons(workingLeafPolys, visData.Leaves.Length);
            CompilerConsole.Stat("Leaf polys", finalLeafPolys.polys.Length);

            CompilerConsole.Header("Radiosity Patches");
            CompilerConsole.Step("Computing patches...");

            Patch[] patches = PatchSystem.BuildPatches(brushes, terrains, lightmapResolution, lightmapUnitSize, matColors);
            CompilerConsole.Stat("Total patches", patches.Length);


            CompilerConsole.Header("Lighting Preamble");
            CompilerConsole.Step("Parsing light entities...");

            var allLights = ParseLights(entities, brushes, brushBounds, aiNodes, smoothedNormals);
            CompilerConsole.Stat("Lights found", allLights.Count);

            CompilerConsole.Step("Parsing light groups...");
            var lightGroups = ParseLightGroups(entities);
            CompilerConsole.Stat("Light groups found", lightGroups.Count);

            var (staticLights, groupedLights) = BucketLightsByTarget(allLights, lightGroups);
            CompilerConsole.Stat("Static lights", staticLights.Count);


            var faceVBounds = PrecomputeFaceBounds(brushes, lightmapResolution);
            GlobalMapData.ActiveMap = new Map { Brushes = brushes, BrushBounds = brushBounds };

            int totalLuxels = lightmapResolution * lightmapResolution;

            CompilerConsole.Step("Building non-BSP shadow mesh...");
            TriangleOccluder.Build(terrains, brushes, entities, brushOwner);

            Vector3[] patchCenters = new Vector3[patches.Length];
            for (int p = 0; p < patches.Length; p++)
                patchCenters[p] = patches[p].center;

            var spatialGrid = new PatchSpatialGrid(patchCenters, 16f);

            GpuLightmapper gpu = GpuLightmapper.TryCreate();
            bool useGPU = gpu != null;

            ModelTracker.LoadAll(entities);


            CompilerConsole.Header("Baking Light");

            var lmB1 = new LightmapColor[totalLuxels];
            var lmB2 = new LightmapColor[totalLuxels];
            var lmB3 = new LightmapColor[totalLuxels];

            var lightBakeCtx = new LightBakeContext
            {
                Brushes = brushes,
                Terrains = terrains,
                MatColors = matColors,
                BrushBounds = brushBounds,
                LightmapResolution = lightmapResolution,
                LightmapUnitSize = lightmapUnitSize,
                FaceVBounds = faceVBounds,
                SmoothedNormals = smoothedNormals,
                PatchCenters = patchCenters,
                SpatialGrid = spatialGrid,
                LightNodes = lightNodes,
                AllLights = allLights,
                StaticLights = staticLights,
                LightGroups = lightGroups,
                GroupedLights = groupedLights,
                VisData = visData,
            };

            BakedLighting baked;
            GpuBuffer modelTexelHomePatchBuffer = null;
            Vector3[] modelPatchFinalArr = null;
            Vector3 modelAmbientColor = Vector3.Zero;
            float modelAmbientIntensity = 0f;

            if (useGPU)
            {
                var gpuResult = BakeLightingGpu(gpu, lightBakeCtx, patches, absoluteMin, absoluteMax, lmB1, lmB2, lmB3);
                baked = gpuResult.Baked;
                modelTexelHomePatchBuffer = gpuResult.TexelHomePatchBuffer;
                modelPatchFinalArr = gpuResult.PatchFinalArr;
                modelAmbientColor = gpuResult.AmbientColor;
                modelAmbientIntensity = gpuResult.AmbientIntensity;
            }
            else
            {
                baked = BakeLightingCpu(lightBakeCtx, ref patches, lmB1, lmB2, lmB3);
            }

            //CompilerConsole.Step("Stitching lightmap seams...");
            //LightmapSeamStitcher.Stitch(brushes, lmB1, lmB2, lmB3, lightmapResolution);

            CompilerConsole.Step("Writing...");

            var (lmNormal, lmTangent, lmBinormal) = LightmapWriter.WritePixels(lmB1, lmB2, lmB3, lightmapResolution);
            (lmNormal, lmTangent, lmBinormal) = LightmapWriter.ApplyBilateralFilter(lmNormal, lmTangent, lmBinormal);

            CompilerConsole.Header("Output");
            CompilerConsole.Step("Saving lightmap archive (.clm)...");
            LightmapWriter.SaveLightmapArchive(lmNormal, lmTangent, lmBinormal, baked.GroupLayers, mapPath);


            GlobalMapData.ActiveMap.LightNodes = lightNodes.ToArray();
            List<MapPropModel> models = null;
            if(ModelTracker.Any)
            {
                CompilerConsole.Step("Baking detail models...");

                models = useGPU
                    ? ModelTracker.ConcatModelsGpu(gpu, allLights, modelTexelHomePatchBuffer, modelPatchFinalArr, lightmapResolution, modelAmbientColor, modelAmbientIntensity)
                    : ModelTracker.ConcatModels(allLights);

                if (useGPU)
                {
                    modelTexelHomePatchBuffer.Dispose();
                    gpu.Dispose();
                }
            }

            CompilerConsole.Step("Building AI node graph...");
            var graph = new NodeGraph { Nodes = aiNodes.ToArray() };
            RegenerateNodegraph(ref graph);

            CompilerConsole.Step("Writing map archive (.cmap)...");
            var compiledMap = new Map
            {
                Brushes = brushes,
                BrushBounds = brushBounds,
                Terrains = terrains,
                HasVis = true,
                Root = octreeRoot,
                OctreeNodes = OctreeRoot.AllNodes,
                Entities = entities,
                LightNodes = lightNodes.ToArray(),
                LightGroupKeys = baked.LightGroupKeys,
                MapModels = (models != null ? models.ToArray() : Array.Empty<MapPropModel>()),

                StaticGeomVertices = finalLeafPolys.vertices,
                LeafPolyStart = finalLeafPolys.leafPolyStart,
                LeafPolyCount = finalLeafPolys.leafPolyCount,
                LeafPolygons = finalLeafPolys.polys,

                Nodegraph = graph
            };

            WriteMapArchive(compiledMap, mapPath, visData);

            timer.Stop();

            CompilerConsole.Success($"Map compiled. {brushes.Length} brushes, vis enabled. (In {timer.Elapsed.TotalSeconds:F2} seconds!)");
        }



        // Everything the GPU and CPU lighting backends need in common. Built once in Compile()
        // and handed to whichever backend function runs.
        private sealed class LightBakeContext
        {
            public Brush[] Brushes;
            public Terrain[] Terrains;
            public Color[] MatColors;
            public BoundingBox[] BrushBounds;
            public int LightmapResolution;
            public float LightmapUnitSize;
            public List<(Vector2 min, Vector2 max)>[] FaceVBounds;
            public Dictionary<(int brush, int face, int vertex), SmoothedVertexData> SmoothedNormals;
            public Vector3[] PatchCenters;
            public PatchSpatialGrid SpatialGrid;
            public List<LightNodeBundle> LightNodes;
            public List<Light> AllLights;
            public List<Light> StaticLights;
            public List<LightGroupInfo> LightGroups;
            public Dictionary<string, List<Light>> GroupedLights;
            public VisFile VisData;
        }

        private readonly struct BakedLighting
        {
            public readonly List<LightmapWriter.GroupLayer> GroupLayers;
            public readonly string[] LightGroupKeys;

            public BakedLighting(List<LightmapWriter.GroupLayer> groupLayers, string[] lightGroupKeys)
            {
                GroupLayers = groupLayers;
                LightGroupKeys = lightGroupKeys;
            }
        }

        // A light group bakes with all its members forced to white so the group's own contribution
        // can be captured (and later re-tinted/toggled) independently of its configured color.
        private static List<Light> GetWhiteGroupMembers(LightGroupInfo group, Dictionary<string, List<Light>> groupedLights)
        {
            if (!groupedLights.TryGetValue(group.archiveKey, out var members) || members.Count == 0)
                return null;

            return members.Select(l => { l.Color = Color.White; return l; }).ToList();
        }

        private static (BakedLighting Baked, GpuBuffer TexelHomePatchBuffer, Vector3[] PatchFinalArr, Vector3 AmbientColor, float AmbientIntensity) BakeLightingGpu(
            GpuLightmapper gpu,
            LightBakeContext ctx,
            Patch[] patches,
            Vector3 absoluteMin,
            Vector3 absoluteMax,
            LightmapColor[] lmB1, LightmapColor[] lmB2, LightmapColor[] lmB3)
        {
            int lightmapResolution = ctx.LightmapResolution;
            int totalLuxels = lightmapResolution * lightmapResolution;

            CompilerConsole.Info("GPU baking enabled.");

            gpu.BuildBvh(ctx.Brushes, ctx.Terrains, ctx.MatColors, ModelTracker.BuildBvhTriangles());

            CompilerConsole.Step("Building GBuffer...");
            //var gbuffer = GBufferBuilder.Build(ctx.Brushes, lightmapResolution, ctx.FaceVBounds, ctx.SmoothedNormals);
            //GBufferBuilder.AddTerrain(gbuffer, ctx.Terrains, lightmapResolution);
            //gpu.UploadGBuffer(gbuffer, lightmapResolution);
            gpu.RasterGBuffer(ctx.Brushes, ctx.Terrains, ctx.SmoothedNormals, ctx.LightmapResolution);

            var gpuLayer = gpu.CreateLayer(lightmapResolution);
            CompilerConsole.Step("Baking index layer (static lights, GPU)...");
            gpu.RunDirectLightingPass(ctx.StaticLights, gpuLayer);

            CompilerConsole.Header("Radiosity (GPU)");

            var patchResources = gpu.UploadPatches(patches);
            int[] texelHomePatchArr = PatchSystem.BuildTexelHomePatch(patches, lightmapResolution);
            var texelHomePatchBuffer = gpu.UploadTexelHomePatch(texelHomePatchArr);

            CompilerConsole.Step("Computing sky visibility (GPU)...");
            float[] patchSky = gpu.ComputePatchSkyVisibility(patchResources, texelHomePatchBuffer, patches.Length, lightmapResolution);

            CompilerConsole.Step("Seeding patches (GPU)...");
            Vector3[] patchSeed = gpu.RunPatchSeed(gpuLayer, patchResources, patches.Length, lightmapResolution);

            CompilerConsole.Step("Bouncing patches (GPU)...");
            Vector3[] patchIndirect = gpu.RunPatchBounce(patchResources, texelHomePatchBuffer, patchSeed, patches.Length, lightmapResolution);

            var ambientColor = LightCalculator.AmbientColor.ToVector3();
            float ambientIntensity = LightCalculator.AmbientIntensity * 0.25f; // idk why, otherwise it just dont look right

            var patchFinalArr = new Vector3[patches.Length];
            for (int i = 0; i < patches.Length; i++)
                patchFinalArr[i] = patchIndirect[i] + ambientColor * ambientIntensity * patchSky[i];

            using var patchValuesBuffer = gpu.UploadPatchValues(patchFinalArr);

            CompilerConsole.Step("Building grid...");
            float blendRadius = 8f;
            var patchGrid = gpu.BuildPatchGrid(patches, absoluteMin, absoluteMax, 4f);
            var (neighborCounts, neighborIndices) = gpu.GeneratePatchBlendNeighbors(patchResources, patchGrid, patches, patches.Length, blendRadius);

            CompilerConsole.Step("Blending patches...");
            gpu.BlendPatchesToLuxels(texelHomePatchBuffer, patchValuesBuffer, neighborCounts, neighborIndices, patchResources, gpuLayer, blendRadius);
            gpu.RunAmbientOcclusion(gpuLayer);

            CompilerConsole.Step("Reading...");
            gpuLayer.ReadBackInto(lmB1, lmB2, lmB3);

            var groupLayers = new List<LightmapWriter.GroupLayer>();
            var groupPatchColorsPerLayer = new List<Vector3[]>();
            var lightGroupKeysList = new List<string>();

            foreach (var group in ctx.LightGroups)
            {
                var whiteMembers = GetWhiteGroupMembers(group, ctx.GroupedLights);
                if (whiteMembers == null) continue;

                CompilerConsole.Step($"Baking light group '{group.archiveKey}'...");

                var gB1 = new LightmapColor[totalLuxels];
                var gB2 = new LightmapColor[totalLuxels];
                var gB3 = new LightmapColor[totalLuxels];

                using var groupLayer = gpu.CreateLayer(lightmapResolution);
                gpu.RunDirectLightingPass(whiteMembers, groupLayer);

                Vector3[] groupSeed = gpu.RunPatchSeed(groupLayer, patchResources, patches.Length, lightmapResolution);
                Vector3[] groupPatchColors = gpu.RunPatchBounce(patchResources, texelHomePatchBuffer, groupSeed, patches.Length, lightmapResolution);

                CompilerConsole.Step("Blending patches...");
                using var groupPatchValuesBuffer = gpu.UploadPatchValues(groupPatchColors);
                gpu.BlendPatchesToLuxels(texelHomePatchBuffer, groupPatchValuesBuffer, neighborCounts, neighborIndices, patchResources, groupLayer, blendRadius);

                //gpu.BlendPatchesToLuxels(texelHomePatchBuffer, patchValuesBuffer, patchGrid, gpuLayer, blendRadius);

                groupLayer.ReadBackInto(gB1, gB2, gB3);

                groupLayers.Add(LightmapWriter.PackGroupLayer(group.archiveKey, gB1, gB2, gB3, lightmapResolution));
                groupPatchColorsPerLayer.Add(groupPatchColors);
                lightGroupKeysList.Add(group.archiveKey);
            }

            BakeLightNodesGpu(gpu, ctx.LightNodes, ctx.AllLights, texelHomePatchBuffer, patchFinalArr, groupPatchColorsPerLayer, lightmapResolution);

            neighborCounts.Dispose();
            neighborIndices.Dispose();
            patchResources.Dispose();
            patchGrid.Dispose();

            return (new BakedLighting(groupLayers, lightGroupKeysList.ToArray()), texelHomePatchBuffer, patchFinalArr, ambientColor, ambientIntensity);
        }

        private static void BakeLightNodesGpu(
            GpuLightmapper gpu,
            List<LightNodeBundle> lightNodes,
            List<Light> allLights,
            GpuBuffer texelHomePatchBuffer,
            Vector3[] patchFinalArr,
            List<Vector3[]> groupPatchColorsPerLayer,
            int lightmapResolution)
        {
            var ambientColorForNodes = LightCalculator.AmbientColor.ToVector3();

            using var lightNodePositions = gpu.PrepareLightNodePositions(lightNodes);

            bool[][] nodeBlocked = gpu.BakeLightNodeOcclusion(lightNodePositions, allLights);
            Vector3[][] indexNodeCoeffs = gpu.BakeLightNodeSH(lightNodePositions, texelHomePatchBuffer, patchFinalArr, lightmapResolution, ambientColorForNodes, LightCalculator.AmbientIntensity);

            var groupNodeCoeffsPerLayer = new Vector3[groupPatchColorsPerLayer.Count][][];
            for (int g = 0; g < groupPatchColorsPerLayer.Count; g++)
                groupNodeCoeffsPerLayer[g] = gpu.BakeLightNodeSH(lightNodePositions, texelHomePatchBuffer, groupPatchColorsPerLayer[g], lightmapResolution, Vector3.Zero, 0f);

            int flatIdx = 0;
            foreach (var node in lightNodes)
            {
                for (int i = 0; i < node.Children.Length; i++)
                {
                    node.Children[i].IndirectCoefficients = indexNodeCoeffs[flatIdx];

                    var data = new List<LightNodeBundle.LightData>(allLights.Count);
                    for (int li = 0; li < allLights.Count; li++)
                        data.Add(new LightNodeBundle.LightData { LightNum = allLights[li].ID, LightBlocked = nodeBlocked[flatIdx][li] });
                    node.Children[i].Data = data.ToArray();

                    var groupCoeffs = new Vector3[groupPatchColorsPerLayer.Count][];
                    for (int g = 0; g < groupPatchColorsPerLayer.Count; g++)
                        groupCoeffs[g] = groupNodeCoeffsPerLayer[g][flatIdx];
                    node.Children[i].GroupIndirectCoefficients = groupCoeffs;

                    flatIdx++;
                }
            }
        }

        private static BakedLighting BakeLightingCpu(
            LightBakeContext ctx,
            ref Patch[] patches,
            LightmapColor[] lmB1, LightmapColor[] lmB2, LightmapColor[] lmB3)
        {
            int lightmapResolution = ctx.LightmapResolution;
            int totalLuxels = lightmapResolution * lightmapResolution;

            CompilerConsole.Info("GPU baking not supported. Falling back to CPU implementation. This will be a slow compile!");

            CompilerConsole.Step("Computing transfer functions...");
            PatchSystem.ComputeTransferFunctions(ref patches, ctx.Brushes, ctx.LightNodes, ctx.SpatialGrid, ctx.PatchCenters, ctx.VisData.Leaves);

            CompilerConsole.Step("Baking index layer (static lights)...");
            RunDirectLightingPass(ctx.StaticLights, ctx.Brushes, ctx.BrushBounds, lightmapResolution, ctx.FaceVBounds, ctx.SmoothedNormals, lmB1, lmB2, lmB3);
            RunDirectLightingPassTerrain(ctx.StaticLights, ctx.Terrains, ctx.Brushes, ctx.BrushBounds, lightmapResolution, lmB1, lmB2, lmB3);

            Patch[] indexPatches = PatchSystem.CloneMutableState(patches);
            PatchSystem.SeedPatchesFromLightmap(ref indexPatches, lmB1, lmB2, lmB3, lightmapResolution, ctx.FaceVBounds);
            PatchSystem.RunBouncePass(ref indexPatches);

            CompilerConsole.Step("Applying patch contribution...");
            ApplyPatchContributionToLightmap(
                indexPatches, ctx.PatchCenters, ctx.Brushes, ctx.Terrains, ctx.FaceVBounds, ctx.SmoothedNormals,
                ctx.SpatialGrid, lightmapResolution, ctx.LightmapUnitSize, lmB1, lmB2, lmB3, includeAmbient: true);

            var groupLayers = new List<LightmapWriter.GroupLayer>();
            var groupPatchColorsPerLayer = new List<Vector3[]>();
            var lightGroupKeysList = new List<string>();

            foreach (var group in ctx.LightGroups)
            {
                var whiteMembers = GetWhiteGroupMembers(group, ctx.GroupedLights);
                if (whiteMembers == null) continue;

                CompilerConsole.Step($"Baking light group '{group.archiveKey}'...");

                var gB1 = new LightmapColor[totalLuxels];
                var gB2 = new LightmapColor[totalLuxels];
                var gB3 = new LightmapColor[totalLuxels];

                RunDirectLightingPass(whiteMembers, ctx.Brushes, ctx.BrushBounds, lightmapResolution, ctx.FaceVBounds, ctx.SmoothedNormals, gB1, gB2, gB3);
                RunDirectLightingPassTerrain(whiteMembers, ctx.Terrains, ctx.Brushes, ctx.BrushBounds, lightmapResolution, gB1, gB2, gB3);

                Patch[] groupPatches = PatchSystem.CloneMutableState(patches);
                PatchSystem.SeedPatchesFromLightmap(ref groupPatches, gB1, gB2, gB3, lightmapResolution, ctx.FaceVBounds);
                PatchSystem.RunBouncePass(ref groupPatches);

                ApplyPatchContributionToLightmap(
                    groupPatches, ctx.PatchCenters, ctx.Brushes, ctx.Terrains, ctx.FaceVBounds, ctx.SmoothedNormals,
                    ctx.SpatialGrid, lightmapResolution, ctx.LightmapUnitSize, gB1, gB2, gB3, includeAmbient: false);

                var groupPatchColors = BuildPatchColorArray(groupPatches, includeAmbient: false);

                groupLayers.Add(LightmapWriter.PackGroupLayer(group.archiveKey, gB1, gB2, gB3, lightmapResolution));
                groupPatchColorsPerLayer.Add(groupPatchColors);
                lightGroupKeysList.Add(group.archiveKey);
            }

            CompilerConsole.Step("Baking light nodes (SH)...");

            var patchColorsPerLayer = new Vector3[1 + groupPatchColorsPerLayer.Count][];
            patchColorsPerLayer[0] = BuildPatchColorArray(indexPatches, includeAmbient: true);
            for (int g = 0; g < groupPatchColorsPerLayer.Count; g++)
                patchColorsPerLayer[g + 1] = groupPatchColorsPerLayer[g];

            BakeLightNodes(ctx.LightNodes, ctx.AllLights, ctx.SpatialGrid, ctx.PatchCenters, indexPatches,
                           indexPatches.Select(p => p.normal).ToArray(), patchColorsPerLayer);

            return new BakedLighting(groupLayers, lightGroupKeysList.ToArray());
        }


        private static void RegenerateNodegraph(ref NodeGraph graph)
        {
            for (int i = 0; i < graph.Nodes.Length; i++)
            {
                Vector3 mainNodePos = graph.Nodes[i].Position;
                List<int> connections = new List<int>();
                if (graph.Nodes[i].Connections != null) connections.AddRange(graph.Nodes[i].Connections);

                for (int j = 0; j < graph.Nodes.Length; j++)
                {
                    if (i == j) continue;
                    if (connections.Contains(j)) continue;
                    Vector3 secondaryNodePos = graph.Nodes[j].Position;
                    float distance = Vector3.Distance(mainNodePos, secondaryNodePos);

                    const int mDist = 25;

                    if (distance > mDist) continue;

                    if (connections.Count > 0)
                    {
                        bool notviable = false;
                        for (int c = 0; c < connections.Count; c++)
                        {
                            Vector3 dirToOld = graph.Nodes[connections[c]].Position - mainNodePos; dirToOld.Y = 0; dirToOld.Normalize();
                            Vector3 dirToNew = secondaryNodePos - mainNodePos; dirToNew.Y = 0; dirToNew.Normalize();

                            float angToOldConnection = (float)Math.Atan2(dirToOld.X, dirToOld.Z);
                            float angToNewConnection = (float)Math.Atan2(dirToNew.X, dirToNew.Z);

                            // We discard creating a new connection where the angle to a pre-existing one is less
                            // than 10 degrees different than the angle to the new connection.
                            //
                            // We do this for 2 reasons..
                            // 1: Less connections = less computation when moving later
                            // 2: It's redundant, obviously we can just get to this node by the pre-existing connection.
                            float diff = angToOldConnection - angToNewConnection;
                            while (diff > Math.PI) diff -= 2f * (float)Math.PI;
                            while (diff < -Math.PI) diff += 2f * (float)Math.PI;
                            if (Math.Abs(diff) <= 0.174533f)
                            {
                                notviable = true;
                                break;
                            }
                        }
                        if (notviable) continue;
                    }

                    // Line of sight check. If we cant see the node, then we cant connect to it.
                    var pTestA = (mainNodePos + Vector3.Up * 0.4f);
                    var pTestB = (secondaryNodePos + Vector3.Up * 0.4f);

                    float dst = Vector3.Distance(pTestA, pTestB);
                    BSPHit hit = BSPRoot.TraceRay(new Ray(pTestA, Vector3.Normalize(pTestB - pTestA)), dst);
                    var triangleHit = TriangleOccluder.TraceRay(new Ray(pTestA, Vector3.Normalize(pTestB - pTestA)), dst);
                    bool pathClear = !hit.Hit && !triangleHit;

                    if (!pathClear)
                    {
                        continue;
                    }

                    // FINALLY, test points along this path to make sure it doesnt float.
                    for (int d = 0; d < 32; d++)
                    {
                        Vector3 pointOnPath = Vector3.Lerp(mainNodePos, secondaryNodePos, d / 32f) + Vector3.Up * 0.1f;

                        hit = BSPRoot.TraceRay(new Ray(pointOnPath, Vector3.Down), 0.8f);
                        triangleHit = TriangleOccluder.TraceRay(new Ray(pointOnPath, Vector3.Down), 0.8f);
                        pathClear = hit.Hit || triangleHit;

                        if (!pathClear) break;
                    }

                    if (!pathClear) continue;

                    connections.Add(j);

                    // Inherently, if we can connect to this node, this node can connect to us.
                    graph.Nodes[j].Connections = (graph.Nodes[j].Connections ?? new int[0]).Append(i).ToArray();
                }
                graph.Nodes[i].Connections = connections.ToArray();
            }
        }

        public static (VertexLightmapped[] vertices, LeafPolygon[] polys, int[] leafPolyStart, int[] leafPolyCount) CompileLeafPolygons(List<WorkingLeafPoly> workingPolys, int totalLeafCount)
        {
            // Try to best organize the data to be memory-efficient for later down the line.
            var sorted = workingPolys.OrderBy(p => p.LeafIndex).ThenBy(p => p.MaterialID).ToList();

            var vertices = new List<VertexLightmapped>();
            var polys = new LeafPolygon[sorted.Count];

            var leafPolyStart = new int[totalLeafCount];
            var leafPolyCount = new int[totalLeafCount];

            int currentLeaf = -1;

            for (int i = 0; i < sorted.Count; i++)
            {
                var wp = sorted[i];

                // We're already sorted incrementally in leafs, so we dont have to worry about that.
                if (wp.LeafIndex != currentLeaf)
                {
                    currentLeaf = wp.LeafIndex;
                    leafPolyStart[currentLeaf] = i;
                }
                leafPolyCount[currentLeaf]++;

                int start = vertices.Count;

                foreach (var idx in wp.Indices)
                {
                    bool hasSmoothed = wp.VertexNormals.Count == wp.Vertices.Count;

                    vertices.Add(new VertexLightmapped
                    {
                        Position = wp.Vertices[idx],
                        Normal = hasSmoothed ? wp.VertexNormals[idx] : wp.Normal,
                        Tangent = hasSmoothed ? wp.VertexTangents[idx] : wp.Tangent,
                        Binormal = hasSmoothed ? wp.VertexBinormals[idx] : wp.Binormal,
                        TextureCoordinate = wp.UVs[idx],
                        LightmapCoordinate = wp.LightmapUVs[idx]
                    });
                }

                polys[i] = new LeafPolygon
                {
                    VertexStart = start,
                    VertexCount = wp.Indices.Count,
                    MaterialName = wp.MaterialName,
                    Normal = wp.Normal,
                    Tangent = wp.Tangent,
                    Binormal = wp.Binormal,
                    B1 = wp.B1,
                    B2 = wp.B2,
                    B3 = wp.B3
                };
            }

            return (vertices.ToArray(), polys, leafPolyStart, leafPolyCount);
        }
        private static bool FaceCouldReceiveLight(
            Brush brush, int brushIdx, int faceIdx, Face face, Light light,
            Dictionary<(int brush, int face, int vertex), SmoothedVertexData> smoothed)
        {
            if (face.Indices == null || face.Indices.Length == 0) return false;

            if (face.smoothGroup != 0) return true;

            foreach (var vertIdx in face.Indices.Distinct())
            {
                Vector3 worldPos = brush.Vertices[vertIdx] + brush.Position;
                Vector3 normal = face.Normal;

                Vector3 toLight = light.Type == Light.LightType.Directional
                    ? light.Rotation
                    : Vector3.Normalize(light.Position - worldPos);

                if (Vector3.Dot(normal, toLight) > -0.1f)
                    return true;
            }

            return false;
        }

        private static Brush[] SortBrushesByVolume(Brush[] src, out int[] oldToNew)
        {
            float VolOf(Brush br)
            {
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                foreach (var f in br.Faces)
                    foreach (int t in f.Indices)
                    {
                        var v = br.Vertices[t] + br.Position;
                        min = Vector3.Min(v, min); max = Vector3.Max(v, max);
                    }
                var s = max - min;
                return MathF.Max(s.X, MathF.Max(s.Y, s.Z));
            }

            var volumes = new float[src.Length];
            for (int i = 0; i < src.Length; i++) volumes[i] = VolOf(src[i]);

            var order = new int[src.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => volumes[a].CompareTo(volumes[b]));

            var copy = new Brush[src.Length];
            oldToNew = new int[src.Length];
            for (int newIdx = 0; newIdx < order.Length; newIdx++)
            {
                int oldIdx = order[newIdx];
                copy[newIdx] = src[oldIdx];
                oldToNew[oldIdx] = newIdx;
            }
            return copy;
        }

        private static void RemapEntityBrushOwnership(EntityReference[] entities, int[] origToFinal)
        {
            if (entities == null) return;

            foreach (var entity in entities)
            {
                if (entity?.BrushIndices == null || entity.BrushIndices.Count == 0) continue;

                var remapped = new List<int>(entity.BrushIndices.Count);
                foreach (var idx in entity.BrushIndices)
                {
                    if (idx < 0 || idx >= origToFinal.Length) continue;
                    int final = origToFinal[idx];
                    if (final >= 0) remapped.Add(final);
                }
                entity.BrushIndices = remapped;
            }
        }

        private static void RemapTerrainBrushSources(Terrain[] terrains, int[] origToFinal)
        {
            if (terrains == null) return;

            for (int t = 0; t < terrains.Length; t++)
            {
                int src = terrains[t].BrushSource;
                if (src < 0 || src >= origToFinal.Length) continue;
                terrains[t].BrushSource = origToFinal[src];
            }
        }

        internal static float FaceSize(Face face, Vector3[] verts)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (int i in face.Indices) { min = Vector3.Min(verts[i], min); max = Vector3.Max(verts[i], max); }
            return (max - min).Length();
        }

        private static List<Light> ParseLights(
            EntityReference[] entities, Brush[] brushes,
            BoundingBox[] brushBounds, List<AINode> aiNodes,
            Dictionary<(int brush, int face, int vertex), SmoothedVertexData> smoothNormals)
        {
            var lights = new List<Light>();
            int lightId = 0;

            for (int i = 0; i < entities.Length; i++)
            {
                var ent = entities[i];

                if (ent.EntityName.Contains("SkyCamera")) { skyCamera = ent; continue; }

                if (ent.EntityName == "LightGroup") continue;

                if (!ent.EntityName.Contains("Light"))
                {
                    if (ent.EntityName == "GroundNode")
                        aiNodes.Add(new AINode { Position = ent.Position, Type = AINode.NodeType.Ground });
                    continue;
                }

                var light = new Light();

                switch (ent.EntityName)
                {
                    case "PointLight":
                        light.Type = Light.LightType.Point;
                        break;
                    case "DirectionalLight":
                        light.Type = Light.LightType.Directional;
                        ParseAmbient(ent);
                        break;
                    case "SpotLight":
                        light.Type = Light.LightType.SpotLight;
                        break;
                }

                string[] col = FindProp(ent, "Color").Split(',');
                light.Color = new Color(byte.Parse(col[0]), byte.Parse(col[1]), byte.Parse(col[2]), (byte)255);
                light.Intensity = float.Parse(FindProp(ent, "Intensity"));
                light.Range = float.Parse(FindProp(ent, "Range", "0"));
                light.Position = ent.Position;
                light.Rotation = Matrix.CreateFromYawPitchRoll(
                    MathHelper.ToRadians(ent.SpawnRotation.X),
                    MathHelper.ToRadians(ent.SpawnRotation.Y),
                    MathHelper.ToRadians(ent.SpawnRotation.Z)).Forward;
                light.Angle = float.Parse(FindProp(ent, "Spot Angle", "0"));
                light.InnerAngle = float.Parse(FindProp(ent, "Inner Angle", "0"));
                light.TargetName = ent.Name;

                var affected = new List<Tuple<int, int>>();
                for (int b = 0; b < brushes.Length; b++)
                {
                    if (brushes[b].IsClip || brushes[b].IsLightNodeVolume || brushes[b].IsSkybox) continue;
                    if (light.Type != Light.LightType.Directional
                        && !brushBounds[b].Intersects(new BoundingSphere(light.Position, light.Range))) continue;

                    for (int f = 0; f < brushes[b].Faces.Length; f++)
                    {
                        var face = brushes[b].Faces[f];
                        if (!FaceCouldReceiveLight(brushes[b], b, f, face, light, smoothNormals)) continue;
                        affected.Add(Tuple.Create(b, f));
                    }
                }

                light.AffectedBrushes = affected.ToArray();
                light.ID = lightId;

                var props = entities[i].Properties.ToList();
                props.Add(new EntityProperty { Name = "ID", Value = lightId.ToString() });
                entities[i].Properties = props.ToArray();

                lights.Add(light);
                lightId++;
            }

            return lights;
        }

        private static void ParseAmbient(EntityReference ent)
        {
            string[] col = FindProp(ent, "Ambient Color").Split(',');
            LightCalculator.AmbientColor = new Color(byte.Parse(col[0]), byte.Parse(col[1]), byte.Parse(col[2]), (byte)255);
            LightCalculator.AmbientIntensity = float.Parse(FindProp(ent, "Ambient Intensity"));
        }

        private struct LightGroupInfo
        {
            public string name;
            public string archiveKey;
            public string target;
            public string style;
            public Color defaultColor;
            public float defaultIntensity;
            public bool startEnabled;
        }

        private static List<LightGroupInfo> ParseLightGroups(EntityReference[] entities)
        {
            var groups = new List<LightGroupInfo>();
            var seenNames = new HashSet<string>();
            int unnamedIndex = 0;

            foreach (var ent in entities)
            {
                if (ent.EntityName != "LightGroup") continue;

                string name = ent.Name;
                string archiveKey;

                if (string.IsNullOrEmpty(name))
                {
                    // No name means no I/O (buttons, etc.) can ever address this group,
                    // but it can still bake and still run its own style
                    archiveKey = $"unnamed{unnamedIndex++}";
                }
                else
                {
                    if (!seenNames.Add(name))
                    {
                        CompilerConsole.Error($"Duplicate LightGroup name '{name}'. Entity names must be unique.");
                        throw new InvalidDataException($"Duplicate LightGroup name '{name}'.");
                    }
                    archiveKey = name;
                }

                string target = FindProp(ent, "Target");
                if (string.IsNullOrEmpty(target))
                    CompilerConsole.Warn($"LightGroup '{archiveKey}' has no Target set. It won't match any lights.");

                string[] col = FindProp(ent, "Default Color", "255,255,255").Split(',');

                var val = FindProp(ent, "Start Enabled", "true");

                bool isEnabled = true;
                if (bool.TryParse(val, out var result)) isEnabled = result;
                if (int.TryParse(val, out var iresult)) isEnabled = iresult == 1;

                groups.Add(new LightGroupInfo
                {
                    name = name,
                    archiveKey = archiveKey,
                    target = target,
                    style = FindProp(ent, "Style"),
                    defaultColor = new Color(byte.Parse(col[0]), byte.Parse(col[1]), byte.Parse(col[2]), (byte)255),
                    defaultIntensity = float.Parse(FindProp(ent, "Default Intensity", "1")),
                    startEnabled = isEnabled,
                });
            }

            return groups;
        }

        private static (List<Light> staticLights, Dictionary<string, List<Light>> groups) BucketLightsByTarget(
            List<Light> lights, List<LightGroupInfo> lightGroups)
        {
            var targetToGroupKey = new Dictionary<string, string>();
            foreach (var g in lightGroups)
            {
                if (string.IsNullOrEmpty(g.target)) continue;

                if (!targetToGroupKey.TryAdd(g.target, g.archiveKey))
                    CompilerConsole.Warn($"LightGroups '{targetToGroupKey[g.target]}' and '{g.archiveKey}' both target '{g.target}'. " +
                        $"Those lights will only join '{targetToGroupKey[g.target]}'.");
            }

            var staticLights = new List<Light>();
            var groups = new Dictionary<string, List<Light>>();

            foreach (var light in lights)
            {
                if (!string.IsNullOrEmpty(light.TargetName) && targetToGroupKey.TryGetValue(light.TargetName, out var groupKey))
                {
                    if (!groups.TryGetValue(groupKey, out var list))
                        groups[groupKey] = list = new List<Light>();
                    list.Add(light);
                }
                else
                {
                    staticLights.Add(light);
                }
            }

            foreach (var group in lightGroups)
            {
                if (!groups.ContainsKey(group.archiveKey))
                    CompilerConsole.Warn($"LightGroup '{group.archiveKey}' (target '{group.target}') has no lights targeting it (empty group).");
            }

            return (staticLights, groups);
        }

        private static string FindProp(EntityReference ent, string name, string fallback = "")
        {
            var p = Array.Find(ent.Properties, x => x.Name == name);
            return p.Value ?? fallback;
        }

        private static List<(Vector2 min, Vector2 max)>[] PrecomputeFaceBounds(
            Brush[] brushes, int lightmapResolution)
        {
            var bounds = new List<(Vector2, Vector2)>[brushes.Length];
            Parallel.For(0, brushes.Length, i =>
            {
                bounds[i] = new List<(Vector2, Vector2)>();
                for (int f = 0; f < brushes[i].Faces.Length; f++)
                {
                    Vector2 vmin = new(float.MaxValue), vmax = new(float.MinValue);
                    foreach (int v in brushes[i].Faces[f].Indices)
                    {
                        float x = brushes[i].LightmapUVs[v].X;
                        float y = brushes[i].LightmapUVs[v].Y;
                        vmin = new Vector2(MathF.Min(vmin.X, x), MathF.Min(vmin.Y, y));
                        vmax = new Vector2(MathF.Max(vmax.X, x), MathF.Max(vmax.Y, y));
                    }
                    bounds[i].Add((vmin, vmax));
                }
            });
            return bounds;
        }

        private static void RunDirectLightingPass(
            List<Light> lights,
            Brush[] brushes, BoundingBox[] brushBounds,
            int lightmapResolution,
            List<(Vector2 min, Vector2 max)>[] faceVBounds,
            Dictionary<(int brush, int face, int vertex), SmoothedVertexData> smoothNormals,
            LightmapColor[] lmB1, LightmapColor[] lmB2, LightmapColor[] lmB3)
        {
            var faceGridCache = new System.Collections.Concurrent.ConcurrentDictionary<(int brush, int face), GeometryUtils.FaceUvGrid>();
            float uvCellSize = MathF.Max(8f / lightmapResolution, 0.001f);

            const int SampleCount = 4;
            var offsets = BuildJitterOffsets(SampleCount);

            // Group by (brush, face) instead of (light, brush, face). A face hit by
            // N lights used to have its world position / smoothed normal-tangent
            // frame resolved (SmoothGroups.SampleAt + EscapeSolid - the expensive
            // part) N separate times, once per light, for the exact same points.
            // Grouping here means that resolve happens once per face regardless of
            // light count; every light in the list below is then evaluated against
            // the same already-resolved samples. The per-light shadow-ray-and-color
            // accumulation logic itself is untouched - same formulas, same order.
            var faceLights = new Dictionary<(int b, int f), List<Light>>();
            foreach (var light in lights)
                foreach (var (b, f) in light.AffectedBrushes)
                {
                    var key = (b, f);
                    if (!faceLights.TryGetValue(key, out var list))
                        faceLights[key] = list = new List<Light>();
                    list.Add(light);
                }

            var faceTasks = faceLights.Keys.ToArray();
            int done = 0, total = faceTasks.Length;

            // Striped locks around the lmB* accumulation: two different faces'
            // padded footprints can overlap the same texel (already possible before
            // this change, since different faces were always separate parallel
            // tasks) - rather than leave that a silent race, lock the stripe that
            // texel falls into before the read-modify-write.
            var texelLocks = new object[4096];
            for (int i = 0; i < texelLocks.Length; i++) texelLocks[i] = new object();

            using var progress = CompilerConsole.StartProgress("Direct light");
            progress.Report(0);

            Parallel.ForEach(faceTasks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, key =>
            {
                var (i, f) = key;
                var faceLightList = faceLights[key];

                var (vmin, vmax) = faceVBounds[i][f];
                var faceGrid = faceGridCache.GetOrAdd((i, f), k => new GeometryUtils.FaceUvGrid(brushes[k.brush].LightmapUVs, brushes[k.brush].Faces[k.face].Indices, uvCellSize));
                var faceLoop = SmoothGroups.BuildFaceLoop(brushes[i], f);

                int xMin = (int)MathF.Floor(vmin.X * lightmapResolution) - 3;
                int xMax = (int)MathF.Ceiling(vmax.X * lightmapResolution) + 3;
                int yMin = (int)MathF.Floor(vmin.Y * lightmapResolution) - 3;
                int yMax = (int)MathF.Ceiling(vmax.Y * lightmapResolution) + 3;

                Span<bool> valid = stackalloc bool[SampleCount];
                Span<Vector3> sampleWorld = stackalloc Vector3[SampleCount];
                Span<Vector3> sampleNormal = stackalloc Vector3[SampleCount];
                Span<Vector3> sampleBasis1 = stackalloc Vector3[SampleCount];
                Span<Vector3> sampleBasis2 = stackalloc Vector3[SampleCount];
                Span<Vector3> sampleBasis3 = stackalloc Vector3[SampleCount];

                Span<float> facingAtten = stackalloc float[SampleCount];
                Span<Vector3> sampleRayOrigin = stackalloc Vector3[SampleCount];
                Span<Vector3> sampleRayDir = stackalloc Vector3[SampleCount];
                Span<float> sampleMaxDist = stackalloc float[SampleCount];
                Span<bool> bspShadow = stackalloc bool[SampleCount];
                Span<bool> occHit = stackalloc bool[SampleCount];
                Span<Vector3> occHitPos = stackalloc Vector3[SampleCount];

                for (int x = xMin; x < xMax; x++)
                {
                    for (int y = yMin; y < yMax; y++)
                    {
                        if (x < 0 || x >= lightmapResolution || y < 0 || y >= lightmapResolution) continue;

                        var texPoint = new Vector2((float)x / lightmapResolution, (float)y / lightmapResolution);

                        // Resolved once per texel, shared by every light below -
                        // this is the part that used to run once per (light, face).
                        for (int s = 0; s < SampleCount; s++)
                        {
                            var sp = Vector2.Clamp(texPoint + offsets[s] / lightmapResolution, vmin, vmax);

                            Vector3 worldFlat = GeometryUtils.LightmapUvTo3D(sp, brushes[i], f, faceGrid, out bool inside);
                            var vd = SmoothGroups.SampleAt(brushes[i], i, f, worldFlat, smoothNormals, faceLoop);

                            Vector3 world = worldFlat + vd.Normal * 0.001f;
                            var escaped = GeometryUtils.EscapeSolid(world, vd.Normal, vd.Tangent, vd.Binormal);
                            if (escaped == null) { valid[s] = false; continue; }

                            valid[s] = true;
                            sampleWorld[s] = escaped.Value;
                            sampleNormal[s] = vd.Normal;
                            sampleBasis1[s] = vd.Basis1;
                            sampleBasis2[s] = vd.Basis2;
                            sampleBasis3[s] = vd.Basis3;
                        }

                        bool anyValid = false;
                        for (int s = 0; s < SampleCount; s++) anyValid |= valid[s];
                        if (!anyValid) continue;

                        int idx = y * lightmapResolution + x;

                        // Same per-light accumulation as before - one light at a
                        // time, one AddColor call per light - just reading the
                        // samples resolved once above instead of recomputing them.
                        foreach (var light in faceLightList)
                        {
                            for (int s = 0; s < SampleCount; s++)
                            {
                                if (!valid[s])
                                {
                                    sampleRayOrigin[s] = Vector3.Zero; sampleRayDir[s] = Vector3.UnitX; sampleMaxDist[s] = 0f;
                                    facingAtten[s] = 0f;
                                    continue;
                                }

                                Vector3 toLight = light.Type == Light.LightType.Directional
                                    ? light.Rotation
                                    : Vector3.Normalize(light.Position - sampleWorld[s]);

                                float facing = Vector3.Dot(sampleNormal[s], toLight);
                                const float facingCutoff = 0;
                                const float facingSoftness = 0.02f;
                                facingAtten[s] = float.Clamp((facing - facingCutoff) / facingSoftness, 0f, 1f);

                                if (facingAtten[s] <= 0f)
                                {
                                    sampleRayOrigin[s] = Vector3.Zero; sampleRayDir[s] = Vector3.UnitX; sampleMaxDist[s] = 0f;
                                    continue;
                                }

                                if (light.Type == Light.LightType.Directional)
                                {
                                    sampleRayOrigin[s] = sampleWorld[s];
                                    sampleRayDir[s] = light.Rotation;
                                    sampleMaxDist[s] = 512f;

                                    var ray = new Ray(sampleWorld[s], light.Rotation);
                                    BSPHit hit = BSPRoot.TraceRay(ray, 512f, default, i);
                                    bspShadow[s] = hit.Hit ? BSPRoot.Nodes[hit.Node].nodeFlag != BSPNode.SkyboxNode : true;
                                }
                                else
                                {
                                    float dist = Vector3.Distance(sampleWorld[s], light.Position);
                                    bool outOfRange = dist > light.Range || dist == 0f;

                                    if (!outOfRange && light.Type == Light.LightType.SpotLight)
                                    {
                                        float pdot = Vector3.Dot(Vector3.Normalize(sampleWorld[s] - light.Position), -light.Rotation);
                                        float angle = (float)Math.Acos(pdot);
                                        outOfRange = angle > MathHelper.ToRadians(light.Angle);
                                    }

                                    if (outOfRange)
                                    {
                                        sampleRayOrigin[s] = Vector3.Zero; sampleRayDir[s] = Vector3.UnitX; sampleMaxDist[s] = 0f;
                                        facingAtten[s] = 0f;
                                        continue;
                                    }

                                    sampleRayOrigin[s] = sampleWorld[s];
                                    sampleRayDir[s] = light.Position - sampleWorld[s];
                                    sampleMaxDist[s] = dist;

                                    var ray = new Ray(sampleWorld[s], light.Position - sampleWorld[s]);
                                    BSPHit hit = BSPRoot.TraceRay(ray, dist, default, i);
                                    bspShadow[s] = hit.Hit && Vector3.Distance(hit.Point, sampleWorld[s]) < dist;
                                }

                                if (bspShadow[s])
                                {
                                    sampleRayOrigin[s] = Vector3.Zero; sampleRayDir[s] = Vector3.UnitX; sampleMaxDist[s] = 0f;
                                }
                            }

                            TriangleOccluder.TraceRayPacket4(
                                sampleRayOrigin, sampleRayDir, sampleMaxDist,
                                occHit, occHitPos,
                                excludeBrush: i, selfEntityGroup: TriangleOccluder.GetBrushEntityGroup(i));

                            float red = 0, grn = 0, blu = 0;
                            float tred = 0, tgrn = 0, tblu = 0;
                            float bred = 0, bgrn = 0, bblu = 0;
                            int validSamples = 0;

                            for (int s = 0; s < SampleCount; s++)
                            {
                                if (!valid[s] || facingAtten[s] <= 0f) continue;

                                bool inShadow = bspShadow[s] || occHit[s];

                                LightSample cols = light.Type switch
                                {
                                    Light.LightType.Point => LightCalculator.FromPointBrushShadowKnown(light, sampleWorld[s], sampleBasis1[s], sampleBasis2[s], sampleBasis3[s], inShadow),
                                    Light.LightType.Directional => LightCalculator.FromDirectionalBrushShadowKnown(light, sampleWorld[s], sampleBasis1[s], sampleBasis2[s], sampleBasis3[s], inShadow),
                                    Light.LightType.SpotLight => LightCalculator.FromSpotBrushShadowKnown(light, sampleWorld[s], sampleBasis1[s], sampleBasis2[s], sampleBasis3[s], inShadow),
                                    _ => default
                                };

                                float fa = facingAtten[s];

                                red += cols.B1.R * fa / 255f; grn += cols.B1.G * fa / 255f; blu += cols.B1.B * fa / 255f;
                                tred += cols.B2.R * fa / 255f; tgrn += cols.B2.G * fa / 255f; tblu += cols.B2.B * fa / 255f;
                                bred += cols.B3.R * fa / 255f; bgrn += cols.B3.G * fa / 255f; bblu += cols.B3.B * fa / 255f;
                                validSamples++;
                            }

                            if (validSamples == 0) continue;

                            float inv = 1f / (validSamples * 255f);
                            var add1 = new LightmapColor(red * inv, grn * inv, blu * inv);
                            var add2 = new LightmapColor(tred * inv, tgrn * inv, tblu * inv);
                            var add3 = new LightmapColor(bred * inv, bgrn * inv, bblu * inv);

                            var texLock = texelLocks[idx % texelLocks.Length];
                            lock (texLock)
                            {
                                lmB1[idx] = AddColor(lmB1[idx], add1);
                                lmB2[idx] = AddColor(lmB2[idx], add2);
                                lmB3[idx] = AddColor(lmB3[idx], add3);
                            }
                        }
                    }
                }

                int cur = Interlocked.Increment(ref done);
                if (cur % 10 == 0 || cur == total) progress.Report((float)cur / total);
            });
        }

        private static void RunDirectLightingPassTerrain(
            List<Light> lights,
            Terrain[] terrains,
            Brush[] brushes, BoundingBox[] brushBounds,
            int lightmapResolution,
            LightmapColor[] lmB1, LightmapColor[] lmB2, LightmapColor[] lmB3)
        {
            const int SampleCount = 4;
            var offsets = BuildJitterOffsets(SampleCount);

            var terrainLights = new List<Light>[terrains.Length];
            for (int t = 0; t < terrains.Length; t++)
            {
                var list = new List<Light>();
                foreach (var light in lights)
                {
                    if (light.Type != Light.LightType.Directional &&
                        !terrains[t].Bounds.Intersects(new BoundingSphere(light.Position, light.Range)))
                        continue;
                    list.Add(light);
                }
                terrainLights[t] = list;
            }

            var tasks = new List<(int t, int triStart)>();
            for (int t = 0; t < terrains.Length; t++)
            {
                if (terrainLights[t].Count == 0) continue;
                for (int tri = 0; tri < terrains[t].Triangles.Length; tri += 3)
                    tasks.Add((t, tri));
            }

            int done = 0, total = tasks.Count;
            var partitioner = System.Collections.Concurrent.Partitioner.Create(tasks,
                System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering);

            using var progress = CompilerConsole.StartProgress("Direct light (terrain)");
            progress.Report(0);

            var texelAccum = new System.Collections.Concurrent.ConcurrentDictionary<(int x, int y, int l), (Vector3 b1, Vector3 b2, Vector3 b3, int count)>();

            Parallel.ForEach(partitioner,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                task =>
                {
                    var (t, triStart) = task;
                    var affectingLights = terrainLights[t];

                    int i0 = terrains[t].Triangles[triStart];
                    int i1 = terrains[t].Triangles[triStart + 1];
                    int i2 = terrains[t].Triangles[triStart + 2];

                    Vector2 uv0 = terrains[t].lightmapUvs[i0];
                    Vector2 uv1 = terrains[t].lightmapUvs[i1];
                    Vector2 uv2 = terrains[t].lightmapUvs[i2];

                    Vector3 p0 = terrains[t].Vertices[i0].Position;
                    Vector3 p1 = terrains[t].Vertices[i1].Position;
                    Vector3 p2 = terrains[t].Vertices[i2].Position;

                    Vector3 n0 = terrains[t].Vertices[i0].Normal;
                    Vector3 n1 = terrains[t].Vertices[i1].Normal;
                    Vector3 n2 = terrains[t].Vertices[i2].Normal;

                    Vector4 tv0 = terrains[t].Vertices[i0].Tangent.ToVector4();
                    Vector4 tv1 = terrains[t].Vertices[i1].Tangent.ToVector4();
                    Vector4 tv2 = terrains[t].Vertices[i2].Tangent.ToVector4();

                    float area2D = GeometryUtils.TriArea2D(uv0, uv1, uv2);
                    if (MathF.Abs(area2D) < 1e-10f) return;

                    Vector2 uvMin = Vector2.Min(Vector2.Min(uv0, uv1), uv2);
                    Vector2 uvMax = Vector2.Max(Vector2.Max(uv0, uv1), uv2);

                    int xMin = (int)MathF.Floor(uvMin.X * lightmapResolution) - 1;
                    int xMax = (int)MathF.Ceiling(uvMax.X * lightmapResolution) + 1;
                    int yMin = (int)MathF.Floor(uvMin.Y * lightmapResolution) - 1;
                    int yMax = (int)MathF.Ceiling(uvMax.Y * lightmapResolution) + 1;

                    Span<bool> valid = stackalloc bool[SampleCount];
                    Span<Vector3> sampleWorld = stackalloc Vector3[SampleCount];
                    Span<Vector3> sampleBasis1 = stackalloc Vector3[SampleCount];
                    Span<Vector3> sampleBasis2 = stackalloc Vector3[SampleCount];
                    Span<Vector3> sampleBasis3 = stackalloc Vector3[SampleCount];

                    for (int x = xMin; x <= xMax; x++)
                    {
                        for (int y = yMin; y <= yMax; y++)
                        {
                            if (x < 0 || x >= lightmapResolution || y < 0 || y >= lightmapResolution) continue;

                            var texPoint = new Vector2((float)x / lightmapResolution, (float)y / lightmapResolution);

                            // Barycentric world position / normal / tangent frame
                            // resolved once per sample here, shared by every light
                            // in affectingLights below.
                            for (int s = 0; s < SampleCount; s++)
                            {
                                var sp = texPoint + offsets[s] / lightmapResolution;

                                float b0 = GeometryUtils.TriArea2D(uv1, uv2, sp) / area2D;
                                float b1 = GeometryUtils.TriArea2D(uv2, uv0, sp) / area2D;
                                float b2 = GeometryUtils.TriArea2D(uv0, uv1, sp) / area2D;

                                float penalty = MathF.Max(0f, -b0) + MathF.Max(0f, -b1) + MathF.Max(0f, -b2);
                                const float maxExtrapolatePenalty = 0.05f;
                                if (penalty > maxExtrapolatePenalty) { valid[s] = false; continue; }

                                float cb0 = MathF.Max(0f, b0);
                                float cb1 = MathF.Max(0f, b1);
                                float cb2 = MathF.Max(0f, b2);
                                float bsum = cb0 + cb1 + cb2;
                                if (bsum < 1e-8f) { valid[s] = false; continue; }
                                cb0 /= bsum; cb1 /= bsum; cb2 /= bsum;

                                Vector3 worldPos = cb0 * p0 + cb1 * p1 + cb2 * p2;
                                Vector3 normal = Vector3.Normalize(cb0 * n0 + cb1 * n1 + cb2 * n2);

                                Vector4 tangent4 = cb0 * tv0 + cb1 * tv1 + cb2 * tv2;
                                Vector3 T_raw = Vector3.Normalize(new Vector3(tangent4.X, tangent4.Y, tangent4.Z));
                                float hand = tangent4.W >= 0f ? 1f : -1f;

                                Vector3 T = Vector3.Normalize(T_raw - normal * Vector3.Dot(normal, T_raw));
                                Vector3 Bv = Vector3.Cross(normal, T) * hand;

                                sampleWorld[s] = worldPos + normal * 0.001f;
                                sampleBasis1[s] = Vector3.Normalize(B1.X * T + B1.Y * Bv + B1.Z * normal);
                                sampleBasis2[s] = Vector3.Normalize(B2.X * T + B2.Y * Bv + B2.Z * normal);
                                sampleBasis3[s] = Vector3.Normalize(B3.X * T + B3.Y * Bv + B3.Z * normal);
                                valid[s] = true;
                            }

                            // Same per-light accumulation as before - one light at a
                            // time, same ConcurrentDictionary merge-by-(x,y,lightId) -
                            // just reusing the samples resolved once above.
                            foreach (var light in affectingLights)
                            {
                                float red = 0, grn = 0, blu = 0;
                                float tred = 0, tgrn = 0, tblu = 0;
                                float bred = 0, bgrn = 0, bblu = 0;
                                int validSamples = 0;

                                for (int s = 0; s < SampleCount; s++)
                                {
                                    if (!valid[s]) continue;

                                    LightSample? _cols = light.Type switch
                                    {
                                        Light.LightType.Point => LightCalculator.FromPoint(
                                            light, sampleWorld[s], sampleBasis1[s], sampleBasis2[s], sampleBasis3[s], t),
                                        Light.LightType.Directional => LightCalculator.FromDirectional(
                                            light, sampleWorld[s], sampleBasis1[s], sampleBasis2[s], sampleBasis3[s], t),
                                        Light.LightType.SpotLight => LightCalculator.FromSpot(
                                            light, sampleWorld[s], sampleBasis1[s], sampleBasis2[s], sampleBasis3[s], t),
                                        _ => null
                                    };
                                    if (!_cols.HasValue) continue;

                                    var cols = _cols.Value;
                                    red += cols.B1.R / 255f; grn += cols.B1.G / 255f; blu += cols.B1.B / 255f;
                                    tred += cols.B2.R / 255f; tgrn += cols.B2.G / 255f; tblu += cols.B2.B / 255f;
                                    bred += cols.B3.R / 255f; bgrn += cols.B3.G / 255f; bblu += cols.B3.B / 255f;
                                    validSamples++;
                                }

                                if (validSamples == 0) continue;

                                // thanks younger me for writing that dogshit util that makes me have to divide by 255 everywhere.
                                float inv = 1f / (validSamples * 255f);
                                var sample1 = new Vector3(red, grn, blu) * inv;
                                var sample2 = new Vector3(tred, tgrn, tblu) * inv;
                                var sample3 = new Vector3(bred, bgrn, bblu) * inv;

                                var key = (x, y, light.ID);
                                texelAccum.AddOrUpdate(key,
                                    _ => (sample1, sample2, sample3, 1),
                                    (_, existing) => (existing.b1 + sample1, existing.b2 + sample2, existing.b3 + sample3, existing.count + 1));
                            }
                        }
                    }

                    int cur = Interlocked.Increment(ref done);
                    if (cur % 50 == 0 || cur == total) progress.Report((float)cur / total);
                }
            );

            foreach (var kvp in texelAccum)
            {
                var (x, y, _) = kvp.Key;
                var (sumB1, sumB2, sumB3, count) = kvp.Value;
                int idx = y * lightmapResolution + x;

                Vector3 avgB1 = sumB1 / count;
                Vector3 avgB2 = sumB2 / count;
                Vector3 avgB3 = sumB3 / count;

                lmB1[idx] = AddColor(lmB1[idx], new LightmapColor(avgB1.X, avgB1.Y, avgB1.Z));
                lmB2[idx] = AddColor(lmB2[idx], new LightmapColor(avgB2.X, avgB2.Y, avgB2.Z));
                lmB3[idx] = AddColor(lmB3[idx], new LightmapColor(avgB3.X, avgB3.Y, avgB3.Z));
            }
        }
        private static void WeldTerrainSeamNormals(Terrain[] terrains, List<(int ta, int tb, List<int> localIndices, List<int> neighborIndices)> seams)
        {
            if (seams == null) return;
            foreach (var (ta, tb, localIndices, neighborIndices) in seams)
                WeldTerrainSeamIndices(terrains, ta, tb, localIndices, neighborIndices);
        }

        private static List<(int ta, int tb, List<int> localIndices, List<int> neighborIndices)> FindAllTouchingTerrainSeams(
            Terrain[] terrains, Brush[] rawBrushes)
        {
            var results = new List<(int, int, List<int>, List<int>)>();

            for (int ta = 0; ta < terrains.Length; ta++)
            {
                foreach (var (tb, localIndices, neighborIndices) in FindTouchingTerrainSeamsForOne(terrains, rawBrushes, ta))
                {
                    results.Add((ta, tb, localIndices, neighborIndices));
                }
            }

            return results;
        }

        private static List<(int neighborTerrain, List<int> localIndices, List<int> neighborIndices)> FindTouchingTerrainSeamsForOne(
            Terrain[] terrains, Brush[] rawBrushes, int terrainIdx, float epsilon = 0.05f)
        {
            var results = new List<(int, List<int>, List<int>)>();

            var srcTerrain = terrains[terrainIdx];
            int srcBrushIdx = srcTerrain.BrushSource;
            if (srcBrushIdx < 0 || srcBrushIdx >= rawBrushes.Length) return results;

            var srcBrush = rawBrushes[srcBrushIdx];
            if (srcTerrain.FaceSource < 0 || srcTerrain.FaceSource >= srcBrush.Faces.Length) return results;

            int srcRes = GetTerrainGridResolution(srcTerrain);
            if (srcRes < 2) return results;

            var srcCorners = GetTerrainSourceCorners(srcBrush, srcBrush.Faces[srcTerrain.FaceSource]);
            if (srcCorners.Count != 4) return results;
            var srcEdges = GetTerrainCanonicalEdges(srcCorners, srcRes);

            for (int bi = 0; bi < rawBrushes.Length; bi++)
            {
                var otherBrush = rawBrushes[bi];

                for (int ofi = 0; ofi < otherBrush.Faces.Length; ofi++)
                {
                    if (bi == srcBrushIdx && ofi == srcTerrain.FaceSource) continue;

                    for (int ti = 0; ti < terrains.Length; ti++)
                    {
                        if (ti == terrainIdx) continue;
                        if (terrains[ti].BrushSource != bi) continue;
                        if (terrains[ti].FaceSource != ofi) continue;

                        var otherTerrain = terrains[ti];
                        int otherRes = GetTerrainGridResolution(otherTerrain);
                        if (otherRes != srcRes) continue;

                        var otherCorners = GetTerrainSourceCorners(otherBrush, otherBrush.Faces[ofi]);
                        if (otherCorners.Count != 4) continue;
                        var otherEdges = GetTerrainCanonicalEdges(otherCorners, otherRes);

                        foreach (var eA in srcEdges)
                        {
                            foreach (var eB in otherEdges)
                            {
                                bool sameDir = Vector3.DistanceSquared(eA.p0, eB.p0) < epsilon * epsilon
                                            && Vector3.DistanceSquared(eA.p1, eB.p1) < epsilon * epsilon;
                                bool oppDir = Vector3.DistanceSquared(eA.p0, eB.p1) < epsilon * epsilon
                                           && Vector3.DistanceSquared(eA.p1, eB.p0) < epsilon * epsilon;
                                if (!sameDir && !oppDir) continue;

                                var localIdx = GetTerrainGridEdgeIndices(srcRes, eA.cornerA, eA.cornerB);
                                var neighborIdx = sameDir
                                    ? GetTerrainGridEdgeIndices(otherRes, eB.cornerA, eB.cornerB)
                                    : GetTerrainGridEdgeIndices(otherRes, eB.cornerB, eB.cornerA);

                                if (localIdx == null || neighborIdx == null) continue;

                                results.Add((ti, localIdx, neighborIdx));
                            }
                        }
                    }
                }
            }

            return results;
        }
        private const float MinSeamSmoothWeight = 0.05f;
        private const float SeamSmoothSharpness = 8f;

        private static float SeamSmoothWeight(Vector3 normalA, Vector3 normalB)
        {
            float cosAngle = Math.Clamp(Vector3.Dot(normalA, normalB), -1f, 1f);
            float angle = MathF.Acos(cosAngle);
            float t = Math.Clamp(angle / (MathF.PI * 0.5f), 0f, 1f);
            float smoothT = 1f - MathF.Pow(t, SeamSmoothSharpness);
            return MathHelper.Lerp(MinSeamSmoothWeight, 1f, MathF.Max(0f, smoothT));
        }
        private static void WeldTerrainSeamIndices(Terrain[] terrains, int ta, int tb, List<int> localIndices, List<int> neighborIndices, float positionEpsilon = 0.05f)
        {
            if (localIndices.Count != neighborIndices.Count) return;

            var a = terrains[ta].Vertices;
            var b = terrains[tb].Vertices;

            var weldedA = new List<int>();
            var weldedB = new List<int>();
            var weightA = new Dictionary<int, float>();
            var weightB = new Dictionary<int, float>();

            for (int k = 0; k < localIndices.Count; k++)
            {
                int i = localIndices[k];
                int j = neighborIndices[k];

                if (Vector3.DistanceSquared(a[i].Position, b[j].Position) > positionEpsilon * positionEpsilon) continue;

                Vector3 normalA = a[i].Normal;
                Vector3 normalB = b[j].Normal;
                Vector3 target = Vector3.Normalize(normalA + normalB);
                float weight = SeamSmoothWeight(normalA, normalB);

                Vector3 blendedA = Vector3.Normalize(Vector3.Lerp(normalA, target, weight));
                Vector3 blendedB = Vector3.Normalize(Vector3.Lerp(normalB, target, weight));

                var va = a[i]; va.Normal = blendedA; a[i] = va;
                var vb = b[j]; vb.Normal = blendedB; b[j] = vb;

                weldedA.Add(i); weightA[i] = weight;
                weldedB.Add(j); weightB[j] = weight;
            }

            if (weldedA.Count > 0) FeatherNormalsFromSeam(terrains[ta], weldedA, weightA);
            if (weldedB.Count > 0) FeatherNormalsFromSeam(terrains[tb], weldedB, weightB);
        }
        private const int SeamFeatherRings = 3;

        private static readonly (int dr, int dc)[] GridNeighborOffsets = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        private static void FeatherNormalsFromSeam(Terrain terrain, List<int> seamIndices, Dictionary<int, float> seamWeight)
        {
            int res = GetTerrainGridResolution(terrain);
            if (res < 2) return;

            var verts = terrain.Vertices;
            var ringOf = new int[verts.Length];
            var originWeight = new float[verts.Length];
            Array.Fill(ringOf, -1);

            var frontier = new List<int>(seamIndices);
            foreach (int idx in seamIndices)
            {
                ringOf[idx] = 0;
                originWeight[idx] = seamWeight.TryGetValue(idx, out float w) ? w : 1f;
            }

            for (int ring = 1; ring <= SeamFeatherRings && frontier.Count > 0; ring++)
            {
                var next = new List<int>();
                foreach (int idx in frontier)
                {
                    int row = idx / res, col = idx % res;
                    foreach (var (dr, dc) in GridNeighborOffsets)
                    {
                        int nr = row + dr, nc = col + dc;
                        if (nr < 0 || nr >= res || nc < 0 || nc >= res) continue;
                        int nIdx = nr * res + nc;
                        if (ringOf[nIdx] != -1) continue;
                        ringOf[nIdx] = ring;
                        originWeight[nIdx] = originWeight[idx];
                        next.Add(nIdx);
                    }
                }
                frontier = next;
            }

            for (int ring = 1; ring <= SeamFeatherRings; ring++)
            {
                float ringFalloff = 1f - (float)ring / (SeamFeatherRings + 1);

                for (int idx = 0; idx < verts.Length; idx++)
                {
                    if (ringOf[idx] != ring) continue;

                    float weight = ringFalloff * originWeight[idx];
                    if (weight <= 0f) continue;

                    int row = idx / res, col = idx % res;
                    Vector3 normalSum = Vector3.Zero;
                    int count = 0;

                    foreach (var (dr, dc) in GridNeighborOffsets)
                    {
                        int nr = row + dr, nc = col + dc;
                        if (nr < 0 || nr >= res || nc < 0 || nc >= res) continue;
                        normalSum += verts[nr * res + nc].Normal;
                        count++;
                    }
                    if (count == 0) continue;

                    Vector3 neighborNormal = Vector3.Normalize(normalSum / count);
                    Vector3 blendedNormal = Vector3.Normalize(Vector3.Lerp(verts[idx].Normal, neighborNormal, weight));

                    var v = verts[idx];
                    v.Normal = blendedNormal;
                    verts[idx] = v;
                }
            }
        }
        private static int GetTerrainGridResolution(Terrain terrain)
        {
            int res = (int)MathF.Round(MathF.Sqrt(terrain.Vertices.Length));
            return (res >= 2 && res * res == terrain.Vertices.Length) ? res : -1;
        }

        private static int[] GetTerrainGridCornerIndices(int res) => new[] { 0, res - 1, res * res - 1, (res - 1) * res };

        private static List<(Vector3 p0, Vector3 p1, int cornerA, int cornerB)> GetTerrainCanonicalEdges(List<Vector3> corners4, int res)
        {
            var gridCorners = GetTerrainGridCornerIndices(res);
            var edges = new List<(Vector3, Vector3, int, int)>();
            for (int i = 0; i < 4; i++)
            {
                int next = (i + 1) % 4;
                edges.Add((corners4[i], corners4[next], gridCorners[i], gridCorners[next]));
            }
            return edges;
        }

        private static List<int> GetTerrainGridEdgeIndices(int res, int fromCornerIdx, int toCornerIdx)
        {
            var c = GetTerrainGridCornerIndices(res);
            int c0 = c[0], c1 = c[1], c2 = c[2], c3 = c[3];

            List<int> BuildRange(bool isRow, int fixedCoord, bool ascending)
            {
                var list = new List<int>();
                for (int i = 0; i < res; i++)
                {
                    int t = ascending ? i : (res - 1 - i);
                    list.Add(isRow ? fixedCoord * res + t : t * res + fixedCoord);
                }
                return list;
            }

            if (fromCornerIdx == c0 && toCornerIdx == c1) return BuildRange(true, 0, true);
            if (fromCornerIdx == c1 && toCornerIdx == c0) return BuildRange(true, 0, false);

            if (fromCornerIdx == c1 && toCornerIdx == c2) return BuildRange(false, res - 1, true);
            if (fromCornerIdx == c2 && toCornerIdx == c1) return BuildRange(false, res - 1, false);

            if (fromCornerIdx == c3 && toCornerIdx == c2) return BuildRange(true, res - 1, true);
            if (fromCornerIdx == c2 && toCornerIdx == c3) return BuildRange(true, res - 1, false);

            if (fromCornerIdx == c0 && toCornerIdx == c3) return BuildRange(false, 0, true);
            if (fromCornerIdx == c3 && toCornerIdx == c0) return BuildRange(false, 0, false);

            return null;
        }

        private static List<Vector3> GetTerrainSourceCorners(Brush brush, Face face)
        {
            if (face.Indices == null) return new List<Vector3>();

            var uniqueVertices = new HashSet<Vector3>();
            for (int i = 0; i < face.Indices.Length; i++)
                uniqueVertices.Add(brush.Vertices[face.Indices[i]] + brush.Position);

            if (uniqueVertices.Count != 4) return new List<Vector3>();

            var corners = uniqueVertices.ToList();
            Vector3 normal = Vector3.Normalize(-Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]));
            corners.Sort((x, y) =>
            {
                Vector3 cross = Vector3.Cross(x - corners[0], y - corners[0]);
                float dot = Vector3.Dot(cross, normal);
                return dot > 0 ? -1 : 1;
            });
            return corners;
        }

        static Vector2[] BuildJitterOffsets(int count)
        {
            var offsets = new Vector2[count];
            for (int s = 0; s < count; s++)
                offsets[s] = new Vector2(GeometryUtils.Halton(s + 1, 2),
                                         GeometryUtils.Halton(s + 1, 3));
            return offsets;
        }

        private static LightmapColor AddColor(LightmapColor a, LightmapColor b)
            => new LightmapColor((a.R + b.R) / 255f, (a.G + b.G) / 255f, (a.B + b.B) / 255f);

        private static Vector3 QuadrantBilerp(
            Vector3 c0, Vector3 c1, Vector3 c2, Vector3 c3, Vector3 center,
            float u, float v)
        {
            Vector3 bottomMid = (c0 + c3) * 0.5f;
            Vector3 rightMid = (c3 + c1) * 0.5f;
            Vector3 topMid = (c1 + c2) * 0.5f;
            Vector3 leftMid = (c2 + c0) * 0.5f;

            Vector3 q00, q10, q11, q01;
            float lu, lv;

            if (u < 0.5f && v < 0.5f)
            {
                q00 = c0; q10 = bottomMid; q11 = center; q01 = leftMid;
                lu = u * 2f; lv = v * 2f;
            }
            else if (v < 0.5f)
            {
                q00 = bottomMid; q10 = c3; q11 = rightMid; q01 = center;
                lu = (u - 0.5f) * 2f; lv = v * 2f;
            }
            else if (u >= 0.5f)
            {
                q00 = center; q10 = rightMid; q11 = c1; q01 = topMid;
                lu = (u - 0.5f) * 2f; lv = (v - 0.5f) * 2f;
            }
            else
            {
                q00 = leftMid; q10 = center; q11 = topMid; q01 = c2;
                lu = u * 2f; lv = (v - 0.5f) * 2f;
            }

            return Vector3.Lerp(Vector3.Lerp(q00, q10, lu), Vector3.Lerp(q01, q11, lu), lv);
        }

        private static void ApplyPatchContributionToLightmap(
            Patch[] patches,
            Vector3[] patchCenters,
            Brush[] brushes,
            Terrain[] terrains,
            List<(Vector2 min, Vector2 max)>[] faceVBounds,
            Dictionary<(int brush, int face, int vertex), SmoothedVertexData> smoothNormals,
            PatchSpatialGrid grid,
            int lightmapResolution,
            float lightmapUnitSize,
            LightmapColor[] lmB1, LightmapColor[] lmB2, LightmapColor[] lmB3,
            bool includeAmbient)
        {
            const float NeighborRadius = 4f, NeighborScale = 1f, NormalPower = 6f, MinNdot = 0f, ModulationAmount = 1f;
            int totalLuxels = lightmapResolution * lightmapResolution;

            float[] pR = new float[patches.Length], pG = new float[patches.Length], pB = new float[patches.Length];
            var patchNormals = new Vector3[patches.Length];
            var patchColors = new Vector3[patches.Length];
            int[] patchBrush = new int[patches.Length], patchFace = new int[patches.Length];

            var patchExtent = new float[patches.Length];
            Parallel.For(0, patches.Length, p =>
            {
                var c = patches[p].center;
                float r = 0f;
                r = MathF.Max(r, Vector3.Distance(c, patches[p].c0));
                r = MathF.Max(r, Vector3.Distance(c, patches[p].c1));
                r = MathF.Max(r, Vector3.Distance(c, patches[p].c2));
                r = MathF.Max(r, Vector3.Distance(c, patches[p].c3));
                patchExtent[p] = r;
            });

            for (int p = 0; p < patches.Length; p++)
            {
                pR[p] = MathF.Max(patches[p].r, 0f);
                pG[p] = MathF.Max(patches[p].g, 0f);
                pB[p] = MathF.Max(patches[p].b, 0f);
                patchNormals[p] = patches[p].normal;
                patchColors[p] = new Vector3(pR[p], pG[p], pB[p]) / 255f
                    + (includeAmbient ? LightCalculator.AmbientColor.ToVector3() * LightCalculator.AmbientIntensity * patches[p].sky : Vector3.Zero);
                patchBrush[p] = patches[p].id1;
                patchFace[p] = patches[p].id2;
            }

            const float DilationMargin = 0.25f;

            BuildPatchNeighborCSR(grid, patchCenters, patchExtent, patches.Length,
                NeighborRadius, DilationMargin, out int[] nbrOffsets, out int[] flatNbrs);

            float invRad = 1f / NeighborRadius;
            float radSq = NeighborRadius * NeighborRadius;

            (Vector3 b1, Vector3 b2, Vector3 b3) EvaluateBlendAtPoint(
                int pid, Vector3 worldPos, Vector3 worldNormal,
                Vector3 worldBasis1, Vector3 worldBasis2, Vector3 worldBasis3)
            {
                Vector3 accum1 = patchColors[pid];
                Vector3 accum2 = patchColors[pid];
                Vector3 accum3 = patchColors[pid];
                float sumW1 = 1f, sumW2 = 1f, sumW3 = 1f;

                int nStart = nbrOffsets[pid], nEnd = nbrOffsets[pid + 1];
                for (int k = nStart; k < nEnd; k++)
                {
                    int nPid = flatNbrs[k];
                    if (nPid == pid) continue;

                    Vector3 diff = patchCenters[nPid] - worldPos;
                    float d2 = diff.LengthSquared();
                    if (d2 > radSq) continue;

                    float ndot = Vector3.Dot(worldNormal, patchNormals[nPid]);
                    float effectiveNdot = ndot;

                    if (!patches[pid].isTerrain && !patches[nPid].isTerrain)
                    {
                        int myGroup = brushes[patchBrush[pid]].Faces[patchFace[pid]].smoothGroup;
                        int otherGroup = brushes[patchBrush[nPid]].Faces[patchFace[nPid]].smoothGroup;
                        bool sameGroup = myGroup != 0 && (myGroup & otherGroup) != 0;
                        effectiveNdot = sameGroup ? 1f : ndot;
                    }

                    if (effectiveNdot < MinNdot) continue;

                    if (!IsUnoccluded(worldPos + worldNormal * 0.01f, patchCenters[nPid] + patchNormals[nPid] * 0.01f))
                        continue;

                    float dist = MathF.Sqrt(d2);
                    float distClamped = MathF.Max(dist, NeighborRadius * 0.1f);
                    float spatialW = 1f - distClamped * invRad;
                    float normalW = MathF.Pow(MathF.Max(0f, effectiveNdot), NormalPower);
                    float baseW = spatialW * normalW * NeighborScale;

                    Vector3 dirToPatch = diff / distClamped;
                    float dirFactor1 = MathF.Max(0f, 1f + Vector3.Dot(worldBasis1, dirToPatch));
                    float dirFactor2 = MathF.Max(0f, 1f + Vector3.Dot(worldBasis2, dirToPatch));
                    float dirFactor3 = MathF.Max(0f, 1f + Vector3.Dot(worldBasis3, dirToPatch));

                    accum1 += patchColors[nPid] * baseW * dirFactor1;
                    accum2 += patchColors[nPid] * baseW * dirFactor2;
                    accum3 += patchColors[nPid] * baseW * dirFactor3;

                    sumW1 += baseW * dirFactor1;
                    sumW2 += baseW * dirFactor2;
                    sumW3 += baseW * dirFactor3;
                }

                return (accum1 / MathF.Max(sumW1, 1e-8f), accum2 / MathF.Max(sumW2, 1e-8f), accum3 / MathF.Max(sumW3, 1e-8f));
            }

            var cornerBlend = new PatchBlendCorners[patches.Length];

            int patchChunk = 64, numPatchChunks = (patches.Length + patchChunk - 1) / patchChunk;
            int patchProgress = 0;

            using (var bar = CompilerConsole.StartProgress("Blending patches"))
            {
                bar.Report(0);

                Parallel.For(0, numPatchChunks, chunkIdx =>
                {
                    int start = chunkIdx * patchChunk;
                    int end = Math.Min(start + patchChunk, patches.Length);

                    for (int pid = start; pid < end; pid++)
                    {
                        Vector3 worldNormal = patchNormals[pid];
                        Vector3 worldBasis1 = worldNormal, worldBasis2 = worldNormal, worldBasis3 = worldNormal;

                        if (!patches[pid].isTerrain)
                        {
                            var pFace = brushes[patchBrush[pid]].Faces[patchFace[pid]];
                            worldBasis1 = pFace.Basis1;
                            worldBasis2 = pFace.Basis2;
                            worldBasis3 = pFace.Basis3;
                        }

                        var (b1c0, b2c0, b3c0) = EvaluateBlendAtPoint(pid, patches[pid].c0, worldNormal, worldBasis1, worldBasis2, worldBasis3);
                        var (b1c1, b2c1, b3c1) = EvaluateBlendAtPoint(pid, patches[pid].c1, worldNormal, worldBasis1, worldBasis2, worldBasis3);
                        var (b1c2, b2c2, b3c2) = EvaluateBlendAtPoint(pid, patches[pid].c2, worldNormal, worldBasis1, worldBasis2, worldBasis3);
                        var (b1c3, b2c3, b3c3) = EvaluateBlendAtPoint(pid, patches[pid].c3, worldNormal, worldBasis1, worldBasis2, worldBasis3);
                        var (b1ct, b2ct, b3ct) = EvaluateBlendAtPoint(pid, patches[pid].center, worldNormal, worldBasis1, worldBasis2, worldBasis3);

                        cornerBlend[pid] = new PatchBlendCorners
                        {
                            B1_c0 = b1c0,
                            B1_c1 = b1c1,
                            B1_c2 = b1c2,
                            B1_c3 = b1c3,
                            B1_center = b1ct,
                            B2_c0 = b2c0,
                            B2_c1 = b2c1,
                            B2_c2 = b2c2,
                            B2_c3 = b2c3,
                            B2_center = b2ct,
                            B3_c0 = b3c0,
                            B3_c1 = b3c1,
                            B3_c2 = b3c2,
                            B3_c3 = b3c3,
                            B3_center = b3ct,
                        };
                    }

                    int cur = Interlocked.Add(ref patchProgress, end - start);
                    bar.Report(cur / (float)patches.Length);
                });
            }

            float[] aoRaw = new float[totalLuxels];
            Vector3[] texelNormal = new Vector3[totalLuxels];

            int[] texelPatch = new int[totalLuxels];
            Array.Fill(texelPatch, -1);
            for (int pid = 0; pid < patches.Length; pid++)
            {
                int xS = (int)MathF.Max(0, MathF.Floor(patches[pid].startUV.X * lightmapResolution) - 3);
                int xE = (int)MathF.Min(lightmapResolution - 1, MathF.Ceiling(patches[pid].endUV.X * lightmapResolution) + 3);
                int yS = (int)MathF.Max(0, MathF.Floor(patches[pid].startUV.Y * lightmapResolution) - 3);
                int yE = (int)MathF.Min(lightmapResolution - 1, MathF.Ceiling(patches[pid].endUV.Y * lightmapResolution) + 3);

                for (int y = (int)yS; y <= (int)yE; y++)
                {
                    for (int x = (int)xS; x <= (int)xE; x++)
                    {
                        int idx = y * lightmapResolution + x;
                        if (texelPatch[idx] == -1) texelPatch[idx] = pid;
                    }
                }
            }

            float invRes = 1f / lightmapResolution;

            int chunkSize = 512, numChunks = (totalLuxels + chunkSize - 1) / chunkSize;
            int progress = 0;
            object pLock = new();

            using var texelBar = CompilerConsole.StartProgress("Interpolating patches / AO");
            texelBar.Report(0);

            Parallel.For(0, numChunks, chunkIdx =>
            {
                int start = chunkIdx * chunkSize;
                int end = Math.Min(start + chunkSize, totalLuxels);

                for (int idx = start; idx < end; idx++)
                {
                    int pid = texelPatch[idx];
                    if (pid == -1) continue;

                    int x = idx % lightmapResolution, y = idx / lightmapResolution;
                    var texPoint = new Vector2(x * invRes, y * invRes);

                    var p = patches[pid];
                    float uSpan = MathF.Max(p.endUV.X - p.startUV.X, 1e-8f);
                    float vSpan = MathF.Max(p.endUV.Y - p.startUV.Y, 1e-8f);
                    float u = Math.Clamp((texPoint.X - p.startUV.X) / uSpan, 0f, 1f);
                    float v = Math.Clamp((texPoint.Y - p.startUV.Y) / vSpan, 0f, 1f);

                    Vector3 worldNormal = patchNormals[pid];
                    var cb = cornerBlend[pid];

                    Vector3 worldPos = QuadrantBilerp(p.c0, p.c1, p.c2, p.c3, p.center, u, v);
                    Vector3 blended1 = QuadrantBilerp(cb.B1_c0, cb.B1_c1, cb.B1_c2, cb.B1_c3, cb.B1_center, u, v);
                    Vector3 blended2 = QuadrantBilerp(cb.B2_c0, cb.B2_c1, cb.B2_c2, cb.B2_c3, cb.B2_center, u, v);
                    Vector3 blended3 = QuadrantBilerp(cb.B3_c0, cb.B3_c1, cb.B3_c2, cb.B3_c3, cb.B3_center, u, v);

                    var nCol1 = new LightmapColor(blended1.X, blended1.Y, blended1.Z);
                    var nCol2 = new LightmapColor(blended2.X, blended2.Y, blended2.Z);
                    var nCol3 = new LightmapColor(blended3.X, blended3.Y, blended3.Z);

                    lmB1[idx] = BlendAdd(nCol1, lmB1[idx]);
                    lmB2[idx] = BlendAdd(nCol2, lmB2[idx]);
                    lmB3[idx] = BlendAdd(nCol3, lmB3[idx]);

                    if (!p.isTerrain)
                        aoRaw[idx] = LightCalculator.CastCheckDirtFine(worldPos + worldNormal * 0.001f, worldNormal, x, y);
                    texelNormal[idx] = worldNormal;
                }

                lock (pLock)
                {
                    progress += (end - start);
                    texelBar.Report(progress / (float)totalLuxels);
                }
            });

            var aoBlurred = new float[totalLuxels];
            Parallel.For(0, lightmapResolution, y =>
            {
                for (int x = 0; x < lightmapResolution; x++)
                {
                    int idx = y * lightmapResolution + x;
                    if (texelPatch[idx] == -1) continue;

                    float sum = aoRaw[idx];
                    float weight = 1f;
                    Vector3 n0 = texelNormal[idx];

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;

                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || nx >= lightmapResolution || ny < 0 || ny >= lightmapResolution) continue;

                            int nIdx = ny * lightmapResolution + nx;
                            if (texelPatch[nIdx] == -1) continue;
                            if (Vector3.Dot(n0, texelNormal[nIdx]) < 0.9f) continue;

                            sum += aoRaw[nIdx];
                            weight += 1f;
                        }
                    }

                    aoBlurred[idx] = sum / weight;
                }
            });

            Parallel.For(0, totalLuxels, idx =>
            {
                if (texelPatch[idx] == -1) return;

                float aoInterp = aoBlurred[idx] * ModulationAmount;
                lmB1[idx] *= (1f - aoInterp);
                lmB2[idx] *= (1f - aoInterp);
                lmB3[idx] *= (1f - aoInterp);
            });
        }

        private static void BuildPatchNeighborCSR(
            PatchSpatialGrid grid, Vector3[] queryCenters, float[] extents, int count,
            float baseRadius, float dilationMargin, out int[] offsets, out int[] flat)
        {
            var perPatch = new List<int>[count];
            var counts = new int[count];

            Parallel.For(0, count, p =>
            {
                var list = new List<int>(32);
                grid.QueryNeighborsNonAlloc(queryCenters[p], baseRadius + extents[p] + dilationMargin, list);
                perPatch[p] = list;
                counts[p] = list.Count;
            });

            var localOffsets = new int[count + 1];
            for (int p = 0; p < count; p++) localOffsets[p + 1] = localOffsets[p] + counts[p];

            var localFlat = new int[localOffsets[count]];
            Parallel.For(0, count, p => perPatch[p].CopyTo(localFlat, localOffsets[p]));

            offsets = localOffsets;
            flat = localFlat;
        }
        private static LightmapColor BlendAdd(LightmapColor a, LightmapColor b)
            => new LightmapColor((a.R + b.R) / 255f, (a.G + b.G) / 255f, (a.B + b.B) / 255f);

        private static bool IsUnoccluded(Vector3 a, Vector3 b)
        {
            float dist = Vector3.Distance(a, b);
            var hit = BSPRoot.TraceRay(new Ray(a, Vector3.Normalize(b - a)), dist);
            //var nonBSPhit = TriangleOccluder.TraceRay(new Ray(a, Vector3.Normalize(b - a)), dist);
            //return (!hit.hit || Vector3.Distance(a, hit.point) > dist) && !nonBSPhit;
            return (!hit.Hit || Vector3.Distance(a, hit.Point) > dist);
        }
        private static Vector3[] BuildPatchColorArray(Patch[] patches, bool includeAmbient = true)
        {
            var colors = new Vector3[patches.Length];
            for (int p = 0; p < patches.Length; p++)
                colors[p] = new Vector3(
                    MathF.Max(patches[p].r, 0f),
                    MathF.Max(patches[p].g, 0f),
                    MathF.Max(patches[p].b, 0f)) / 255f +
                    (includeAmbient ? LightCalculator.AmbientColor.ToVector3() * LightCalculator.AmbientIntensity * patches[p].sky : Vector3.Zero);
            return colors;
        }

        private static Vector3[] CombineDefaultPatchColorsForProbes(
            Patch[] indexPatches,
            List<(LightGroupInfo group, Patch[] patches)> groupBakes)
        {
            var colors = BuildPatchColorArray(indexPatches, includeAmbient: true);

            foreach (var (group, groupPatches) in groupBakes)
            {
                if (!group.startEnabled) continue;

                var groupColors = BuildPatchColorArray(groupPatches, includeAmbient: false);
                var tint = group.defaultColor.ToVector3() / 255f * group.defaultIntensity;

                for (int p = 0; p < colors.Length; p++)
                    colors[p] += groupColors[p] * tint;
            }

            return colors;
        }

        private static void BakeLightNodes(
            List<LightNodeBundle> lightNodes,
            List<Light> lights,
            PatchSpatialGrid grid,
            Vector3[] patchCenters,
            Patch[] patches,
            Vector3[] patchNormals,
            Vector3[][] patchColorsPerLayer)
        {
            const int NumSamples = 128;
            const float NeighborRadius = 8f, NeighborScale = 1f, NormalPower = 6f, MinNdot = 0f;
            float invRad = 1f / NeighborRadius;
            int layerCount = patchColorsPerLayer.Length;

            Parallel.ForEach(lightNodes, node =>
            {
                var nbrs = new List<int>(64);
                var nbrDir = new List<Vector3>(64);
                var nbrWeight = new List<float>(64);
                var accumPerLayer = new Vector3[layerCount];

                for (int i = 0; i < node.Children.Length; i++)
                {
                    var data = new List<LightNodeBundle.LightData>(lights.Count);
                    var worldPos = node.Children[i].Pos;

                    foreach (var light in lights)
                    {
                        bool blocked = LightCalculator.TestPointOcclusion(light, worldPos);
                        data.Add(new LightNodeBundle.LightData { LightNum = light.ID, LightBlocked = blocked });
                    }

                    grid.QueryNeighborsNonAlloc(worldPos, NeighborRadius, nbrs);

                    nbrDir.Clear();
                    nbrWeight.Clear();

                    for (int k = 0; k < nbrs.Count; k++)
                    {
                        int nPid = nbrs[k];
                        Vector3 diff = patchCenters[nPid] - worldPos;
                        float dist = diff.Length();

                        if (!IsUnoccluded(worldPos, patchCenters[nPid] + patchNormals[nPid] * 0.01f))
                        {
                            nbrDir.Add(Vector3.Zero);
                            nbrWeight.Add(0f);
                            continue;
                        }

                        nbrDir.Add(diff / dist);
                        nbrWeight.Add((1f - dist * invRad) * NeighborScale);
                    }

                    var coefficientsPerLayer = new Vector3[layerCount][];
                    for (int l = 0; l < layerCount; l++) coefficientsPerLayer[l] = new Vector3[9];
                    float weightSum = 0f;

                    for (int s = 0; s < NumSamples; s++)
                    {
                        var dir = SphericalHarmonicsUtils.UniformSampleSphere(s, NumSamples);

                        Array.Clear(accumPerLayer, 0, layerCount);
                        float sumW = 0f;

                        for (int k = 0; k < nbrs.Count; k++)
                        {
                            float baseW = nbrWeight[k];
                            if (baseW <= 0f) continue;

                            float ndot = Vector3.Dot(dir, nbrDir[k]);
                            if (ndot < MinNdot) continue;

                            float normalW = MathF.Pow(MathF.Max(0f, ndot), NormalPower);
                            float w = baseW * normalW;

                            int nPid = nbrs[k];
                            for (int l = 0; l < layerCount; l++)
                                accumPerLayer[l] += patchColorsPerLayer[l][nPid] * w;
                            sumW += w;
                        }

                        float[] basis = SphericalHarmonicsUtils.EvaluateSHBasis(dir);

                        for (int l = 0; l < layerCount; l++)
                        {
                            Vector3 blended = accumPerLayer[l] / MathF.Max(sumW, 1e-8f);
                            for (int coeff = 0; coeff < 9; coeff++)
                                coefficientsPerLayer[l][coeff] += blended * basis[coeff];
                        }

                        for (int coeff = 0; coeff < 9; coeff++)
                        {
                            coefficientsPerLayer[0][coeff] += basis[coeff]
                                * LightCalculator.AmbientColor.ToVector3()
                                * LightCalculator.AmbientIntensity
                                * LightCalculator.CastCheckAmbientDir(worldPos, dir);
                        }

                        weightSum += 1f;
                    }

                    float norm = 4f * MathF.PI / weightSum;
                    for (int l = 0; l < layerCount; l++)
                        for (int s2 = 0; s2 < 9; s2++)
                            coefficientsPerLayer[l][s2] *= norm;

                    node.Children[i].IndirectCoefficients = coefficientsPerLayer[0];
                    node.Children[i].GroupIndirectCoefficients = coefficientsPerLayer[1..];
                    node.Children[i].Data = data.ToArray();
                }
            });
        }

        private static void WriteMapArchive(Map compiledMap, string mapPath, VisFile visData)
        {
            string rootDir = Path.GetDirectoryName(mapPath)!;
            string filename = Path.GetFileNameWithoutExtension(mapPath);

            var mapData = MapFormatter.WriteMapData(compiledMap, new BSPFile { Nodes = BSPRoot.Nodes }, visData);

            File.WriteAllBytes(Path.Combine(rootDir, filename + ".cmap"), mapData);
        }
    }
}