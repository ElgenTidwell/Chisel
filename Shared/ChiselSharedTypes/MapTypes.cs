using Chisel.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
#if !rockwall
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
#endif
#if !compiler && !rockwall
using Engine;
#endif
using System.Linq;
using System.Runtime.InteropServices;
using Chisel.EXScript;
using System.Runtime.Serialization;
using Newtonsoft.Json.Linq;

namespace Rockwall
{
    public struct Light
    {
        public enum LightType
        {
            Directional,
            Point,
            SpotLight
        }
        public Color Color;
        public float Intensity, Range, Angle, InnerAngle;
        public Vector3 Position, Rotation;
        public LightType Type;
        public int ID;

        public string TargetName;

#if compiler
        [JsonIgnore] public Tuple<int, int>[] AffectedBrushes;
#endif
    }
    public struct StyleKeyframe
    {
        public float Time;
        public float Intensity;
        public Color Tint;
        public bool Blend;

        public StyleKeyframe(float time, float intensity, bool blend = true)
        {
            this.Time = time;
            this.Intensity = intensity;
            this.Tint = Color.White;
            this.Blend = blend;
        }
        public StyleKeyframe(float time, float intensity, Color tint, bool blend = true)
        {
            this.Time = time;
            this.Intensity = intensity;
            this.Tint = tint;
            this.Blend = blend;
        }
    }
    public struct LightStyle
    {
        public string Name;
        public StyleKeyframe[] Keyframes;  // sorted ascending by time
        public bool Loop;
    }
    public struct StyleSample
    {
        public float Intensity;
        public Color Tint;
    }
    public struct LightGroupBounds
    {
        public string Name;
        public Vector2 UvMin, UvMax;
    }
    public static class LightPageResolver
    {
        /// <summary>
        /// Evaluates a LightStyle at time t. Non-looping styles hold flat at the
        /// first/last keyframe's value past either end instead of extrapolating.
        /// </summary>
        public static StyleSample Evaluate(LightStyle style, float t)
        {
            var kf = style.Keyframes;
            if (kf == null || kf.Length == 0) return new StyleSample { Intensity = 1f, Tint = Color.White };
            if (kf.Length == 1) return new StyleSample { Intensity = kf[0].Intensity, Tint = kf[0].Tint };

            float duration = kf[kf.Length - 1].Time;
            float lt = t;

            if (style.Loop && duration > 0f)
            {
                lt %= duration;
                if (lt < 0f) lt += duration;
            }
            else
            {
                if (lt <= kf[0].Time) return new StyleSample { Intensity = kf[0].Intensity, Tint = kf[0].Tint };
                if (lt >= duration) return new StyleSample { Intensity = kf[kf.Length - 1].Intensity, Tint = kf[kf.Length - 1].Tint };
            }

            int i = FindSegment(kf, lt);
            var a = kf[i];
            var b = kf[i + 1];

            if (!a.Blend)
                return new StyleSample { Intensity = a.Intensity, Tint = a.Tint };

            float span = b.Time - a.Time;
            float frac = span > 0f ? Math.Clamp((lt - a.Time) / span, 0f, 1f) : 0f;

            return new StyleSample
            {
                Intensity = a.Intensity + (b.Intensity - a.Intensity) * frac,
                Tint = Color.Lerp(a.Tint, b.Tint, frac)
            };
        }

        /// <summary>
        /// The keyframe index t currently falls on, with no interpolation.
        /// </summary>
        public static int GetKeyframeIndex(LightStyle style, float t)
        {
            var kf = style.Keyframes;
            if (kf == null || kf.Length == 0) return -1;
            if (kf.Length == 1) return 0;

            float duration = kf[kf.Length - 1].Time;
            float lt = t;

            if (style.Loop && duration > 0f)
            {
                lt %= duration;
                if (lt < 0f) lt += duration;
            }
            else
            {
                if (lt <= kf[0].Time) return 0;
                if (lt >= duration) return kf.Length - 1;
            }

            return FindSegment(kf, lt);
        }

        // Keyframes are sorted ascending by time
        private static int FindSegment(StyleKeyframe[] kf, float lt)
        {
            int i = 0;
            while (i < kf.Length - 2 && lt >= kf[i + 1].Time) i++;
            return i;
        }
    }

    [System.Serializable]
    public class EditorGroup
    {
        //public string GroupName;
        public Guid[] GroupMembers;
    }
    [System.Serializable]
    public class UserVisGroup
    {
        public Guid ID = Guid.NewGuid();
        public string Name = "Group";
        public Guid? ParentID;
        public bool Visible = true;
        public List<Guid> Members = new();
    }
    public struct RawMap
    {
        public Brush[] Brushes;
        public Hint[] Hints;
        public EntityReference[] EntityReferences;
        public Terrain[] Terrains;
        public EditorGroup[] Groups;
        public List<UserVisGroup> VisGroups;
        /// <summary>
        /// Map-format version. 0 (the default for any JSON saved before this field existed) means
        /// "may still have brush-owned-entity data embedded per-brush".
        /// </summary>
        public int FormatVersion;
        public static RawMap CompileRawMap(Hint[] hints, Brush[] brushes, EntityReference[] entityReferences, Terrain[] terrains)
        {
            return new RawMap() { Hints = hints, Brushes = brushes, EntityReferences = entityReferences, Terrains = terrains, FormatVersion = 1 };
        }
    }
    public struct MatGroup
    {
        public int MaterialID;
        public IndexBuffer IndexBuffer;
    }
    public struct Brush
    {
        public Face[] Faces;
        public Vector3[] Vertices;
        public Vector2[] UVs, LightmapUVs;
        public Vector3 Position;
        public float Width, Height, Length;
        public bool Abnormal, IsDetail, IsClip, IsTrigger, IsSkybox, IsLightNodeVolume, IsEntity;
#if !rockwall && !compiler
        [JsonIgnore] public BrushEntity Entity;
        [JsonIgnore] public VertexBuffer BrushVertexBuffer;
        [JsonIgnore] public MatGroup[] MatGroups;
        [JsonIgnore] public bool RenderPiecewise;
#endif
#if rockwall || compiler
        public bool isUsedForTerrain;
        public Brush Clone()
        {
            var b = new Brush
            {
                Position = this.Position,
                Width = this.Width,
                Height = this.Height,
                Length = this.Length,
                Abnormal = this.Abnormal,
                isUsedForTerrain = this.isUsedForTerrain
            };
            b.Faces = new Face[this.Faces.Length];
            b.Vertices = new Vector3[this.Vertices.Length];
            b.UVs = new Vector2[this.UVs.Length];

            Array.Copy(Faces, b.Faces, Faces.Length);
            Array.Copy(Vertices, b.Vertices, Vertices.Length);
            Array.Copy(UVs, b.UVs, UVs.Length);

            // Note: entity ownership is intentionally NOT copied here.

            return b;
        }
#endif
#if rockwall
        public Guid? GroupingID;
#endif
    }
    public struct Material
    {
        public string Name;
        public string SurfaceType;
        public string ShaderName;
        public float Reflectivity;
        public bool Transparent;
        public bool AlphaClip;
        public bool NoCull;
        public int TexelsPerUnit;
        public Dictionary<string, bool> ShaderFlags;

        // Catches every JSON key that doesn't match a declared field above.
        [JsonExtensionData] private IDictionary<string, JToken> extensionData;

        [JsonIgnore] public Dictionary<string, string> TexturePaths;
        [JsonIgnore] public Dictionary<string, Texture2D> Textures;

        // Legacy
        [JsonIgnore] public string TextureName => GetPath("texture");
        [JsonIgnore] public string SpecularName => GetPath("specular");
        [JsonIgnore] public string NormalName => GetPath("normal");

        // Legacy
        [JsonIgnore] public Texture2D Texture { get => GetTexture("texture"); set => SetTexture("texture", value); }
        [JsonIgnore] public Texture2D Specular { get => GetTexture("specular"); set => SetTexture("specular", value); }
        [JsonIgnore] public Texture2D Normal { get => GetTexture("normal"); set => SetTexture("normal", value); }

        [JsonIgnore] public object Shader;
        [JsonIgnore] public float EffectiveTexelsPerUnit => TexelsPerUnit > 0 ? TexelsPerUnit : 512f;

        private static readonly HashSet<string> legacyMapNames = new() { "texture", "specular", "normal" };

        public IEnumerable<KeyValuePair<string, string>> GetExtraTexturePaths() =>
            TexturePaths?.Where(kv => !legacyMapNames.Contains(kv.Key))
                ?? Enumerable.Empty<KeyValuePair<string, string>>();

        public IEnumerable<KeyValuePair<string, Texture2D>> GetExtraTextures() =>
            Textures?.Where(kv => !legacyMapNames.Contains(kv.Key))
                ?? Enumerable.Empty<KeyValuePair<string, Texture2D>>();

        public Texture2D GetExtraTexture(string key) =>
            !legacyMapNames.Contains(key) ? GetTexture(key) : null;

        public string GetPath(string key) =>
            TexturePaths != null && TexturePaths.TryGetValue(key, out var v) ? v : null;

        public Texture2D GetTexture(string key) =>
            Textures != null && Textures.TryGetValue(key, out var t) ? t : null;

        public void SetTexture(string key, Texture2D value)
        {
            Textures ??= new();
            Textures[key] = value;
        }

        [OnDeserialized]
        void OnDeserialized(StreamingContext ctx)
        {
            if (extensionData == null) return;
            TexturePaths ??= new();
            foreach (var kv in extensionData)
            {
                if (kv.Value.Type == JTokenType.String)
                    TexturePaths[kv.Key] = kv.Value.Value<string>();
            }
            extensionData = null;
        }
    }
    public enum UVProjectionMode
    {
        /// <summary>
        /// Classic axial / "standard" projection.
        /// The U and V axes are the two world axes most perpendicular to the face
        /// normal.  Fast and consistent for axis-aligned geometry; can stretch on
        /// angled faces.
        /// </summary>
        World,

        /// <summary>
        /// Planar / "face" projection (Valve 220 style).
        /// Start from the same axial axes, then project them onto the face's plane
        /// so they always lie parallel to it.  No stretching on angled faces.
        /// Rotation is applied inside the face plane.
        /// </summary>
        Face,
    }

    public struct Face
    {
        public int[] Indices;
        public Vector3 Normal;
        public Vector3 Tangent;
        public Vector3 Binormal;
        public Vector3 Basis1, Basis2, Basis3;
        public bool Drawn;
        public string MaterialName;
        public int Surface;
        public float TOffX, TOffY, TScaleX, TScaleY, LuxelScale;
        public float UvRotation;
        public UVProjectionMode UvProjectionMode;
        public EnvironmentalDecal[] Decals;

        public Plane? Plane;

#if rockwall || compiler
        [JsonIgnore] public List<VertexLightmapped> editorVerts;
        [JsonIgnore] public VertexBuffer vertexBuffer;
        public bool toolFace;
        public int smoothGroup;
#else
        [JsonIgnore] public IndexBuffer FaceIndices;
#endif
    }
    public struct EnvironmentalDecal
    {
        public VertexLightmapped[] vertices;
        public int surface;
    }
    public struct Terrain
    {
        public TerrainVertex[] Vertices;
        public string SurfaceName;
        public string BlendedSurfaceName;
#if rockwall
        public TerrainVertex[] editor_cheat_flipalphavert;
#endif
#if compiler
        public float cmp_avgvertdist;
        public Vector2[] lightmapUvs;
#endif
        public short[] Triangles;
        public int Surface;
        public int BlendedSurface;
        public int BrushSource;
        public int FaceSource;
        public Vector3 SourceNormal;
        public BoundingBox Bounds;
#if rockwall
        public Guid? GroupingID;
        public Guid? BrushOwnerGUID;
#endif
#if !rockwall && !compiler
        public ulong[] LeafBits;
#endif
    }
    public class Hint
    {
        public Vector3 Position;
        public string Header, Body;
#if rockwall
        public Guid? GroupingID;
#endif
    }

    //Compiled map datatypes...
    public struct MapPropModel
    {
        public uint VisLeaf;
        public string Material;
        public int MaterialID;
        public MapPropModelVertex[] Vertices;
        public int[] Indices;
    }
    public class EntityReference
    {
        public Vector3 Position;
        public Vector3 SpawnRotation;
        public Quaternion Rotation;
        public Vector3 Scale;
        public string Name;
        public string EntityName;

        public EntityProperty[] Properties;
        public List<(string, EntityOutput)> EntityOutputs;

        /// <summary>
        /// Indices into the map's Brush[] array that this entity owns and controls. Null or empty
        /// for point entities. When non-empty, this is a "brush entity": one entity controlling one
        /// or more brushes at once (a func_door made of two brushes is one entity owning two
        /// indices, not two separate entities).
        /// </summary>
        public List<int> BrushIndices;
        public string entityMoveParentName;

        /// <summary>True if this is a brush entity (owns at least one brush).</summary>
        [JsonIgnore] public bool IsBrushEntity => BrushIndices != null && BrushIndices.Count > 0;
#if rockwall
        public Guid? GroupingID;
        public List<Guid> brushOwnerGUIDs;
#endif
    }
    [System.Serializable]
    public struct EntityProperty
    {
        private string name, value;
        // ? Why did I write these as getters and setters lmao
        public string Name { get { return name; } set { name = value; } }
        public string Value { get { return value; } set { this.value = value; } }
    }
    public struct EntityPropertyDescriptor
    {
        public string Name;
        public string Hint;
        public EntityPropertyType Type;

        // Enum options, EntityTarget classname filter, inspector grouping, compiled default, and Float clamp range.
        // All optional.
        public string[] Options;
        public string TargetFilter;
        public string Category;
        public string DefaultValue;
        public float Min, Max;

        public override string ToString()
        {
            return Name;
        }
    }
    public enum EntityPropertyType
    {
        Color,
        Position,
        Direction,
        Float,
        Texture,
        Model,
        Material,
        Sound,
        String,
        Enum,
        EntityTarget,
        Bool
    }
    // "Next"/"previous" property names used to auto-wire duplicated entities together.
    public struct EntityLinkDescriptor
    {
        public string Next;
        public string Previous;
    }
    // Which EntityVisualizer to draw for this class, and which entity property feeds each of its fields.
    public struct EntityVisualizerBinding
    {
        public string VisualizerType;
        public Dictionary<string, string> FieldToProperty;
        public Dictionary<string, string> FieldToLiteral;
    }
    // Everything the compiler knows about one entity class.
    public class EntityClassMetadata
    {
        public List<EntityPropertyDescriptor> Properties = new();
        public List<string> Inputs = new();
        public List<string> Outputs = new();
        public EntityLinkDescriptor? Link;
        public EntityVisualizerBinding? Visualizer;
        public Vector3 BoundsMin, BoundsMax;
        public EntityProperty[] DefaultProperties;
    }
    public struct LeafInfo
    {
        public int Brush, Face;
        public LeafInfo(int brush, int face)
        {
            this.Brush = brush;
            this.Face = face;
        }
    }
    public class VisLeaf
    {
        public int[] Portals;
        public bool IsEmpty;
        public int BspLeafID;
        public uint[] PVS;
        public ushort[] Brushes;
        public bool HasSkybox;
    }
    public class Portal
    {
        public int LeafFront = -1;
        public int LeafBack = -1;
        public Vector3[] Vertices;
        public Plane Plane;
        public ushort[] Brushes;
#if compiler
        public ulong[] mightsee;
        public ulong[] pvs;
        public int worked;
        public bool leafOverlapped;
        public List<(ushort, ushort)> brushFaces = new List<(ushort, ushort)>();
        public int planenum;
#endif
    }

    public static class OctreeRoot
    {
        public static List<Octree> AllNodes = new List<Octree>();
    }
    public class Octree
    {
        const float minSize = 32;

        public int Depth, Id;
        public List<int> Contents = new List<int>();
        public int[] Children = new int[8] {
            -1,-1,
            -1,-1,
            -1,-1,
            -1,-1,
        };
        //public int[] visible;
        //public bool[,] faceToFaceVisibility;
        public BoundingBox Box;
        public BoundingBox[] Corners;
        public bool IsEnd = false;

        public static Vector3[] SearchDirections = new Vector3[6]
        {
            Vector3.Up,
            Vector3.Right,
            Vector3.Forward,

            -Vector3.Up,
            -Vector3.Right,
            -Vector3.Forward
        };
        public Octree() { }

        public Octree(BoundingBox box, int d, int id)
        {
            this.Box = box;
            this.Depth = d + 1;
            this.Id = id;
            var boxQuarter = new BoundingBox(Vector3.Zero, (box.Max - box.Min) * 0.5f);

            //visible = new int[6] { -1,-1,-1,-1,-1,-1 };
            Corners = new BoundingBox[8];

            Vector3[] c = new Vector3[]
            {
                new Vector3(0,0,0),
                new Vector3(1,0,0),
                new Vector3(0,1,0),
                new Vector3(1,1,0),
                new Vector3(0,0,1),
                new Vector3(1,0,1),
                new Vector3(0,1,1),
                new Vector3(1,1,1),
            };

            for (int i = 0; i < 8; i++)
            {
                Vector3 placement = Replace(c[i], boxQuarter.Max);

                Corners[i] = new BoundingBox(box.Min + placement, box.Min + boxQuarter.Max + placement);
            }
        }

        private Vector3 Replace(Vector3 a, Vector3 b)
        {
            return new Vector3(a.X == 1 ? b.X : 0, a.Y == 1 ? b.Y : 0, a.Z == 1 ? b.Z : 0);
        }

        private void CreateChild(int index, BoundingBox box)
        {
            Children[index] = OctreeRoot.AllNodes.Count;
            OctreeRoot.AllNodes.Add(new Octree(box, Depth, Children[index]));
        }
        public void TestAdd(BoundingBox box, int brush)
        {
            if (Contents.Contains(brush) || this.Box.Contains(box) == ContainmentType.Disjoint) return;

            Contents.Add(brush);

            if (IsEnd || Math.Abs((this.Box.Max - this.Box.Min).X) < minSize)
            {
                IsEnd = true;
                return;
            }

            if (!IsEnd)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (Corners[i].Contains(box) == ContainmentType.Disjoint) continue;

                    if (Children[i] == -1)
                    {
                        CreateChild(i, Corners[i]);
                    }

                    OctreeRoot.AllNodes[Children[i]].TestAdd(box, brush);
                }
            }
        }
        public int Traverse(Vector3 position)
        {
            for (int i = 0; i < 8; i++)
            {
                if (Corners[i].Contains(position) == ContainmentType.Disjoint) continue;

                if (Children[i] >= 0)
                {
                    return OctreeRoot.AllNodes[Children[i]].Traverse(position);
                }
            }

            return Id;
        }
        public int TraverseBox(BoundingBox bounds)
        {
            if (IsEnd) return Id;

            for (int i = 0; i < 8; i++)
            {
                if (Corners[i].Contains(bounds) == ContainmentType.Disjoint) continue;

                if (Children[i] >= 0)
                {
                    return OctreeRoot.AllNodes[Children[i]].TraverseBox(bounds);
                }
            }
            return Id;
        }
        public List<int> Raycast(Ray ray)
        {
            List<int> contents = new List<int>();

            if (IsEnd)
            {
                return this.Contents;
            }

            for (int i = 0; i < 8; i++)
            {
                if (Children[i] > 0)
                {
                    float? res = ray.Intersects(OctreeRoot.AllNodes[Children[i]].Box);

                    if (OctreeRoot.AllNodes[Children[i]].Box.Contains(ray.Position) == ContainmentType.Contains || res.HasValue)
                    {
                        if (OctreeRoot.AllNodes[Children[i]].IsEnd && OctreeRoot.AllNodes[Children[i]].Box.Contains(ray.Position) == ContainmentType.Contains)
                        {
                            contents.AddRange(OctreeRoot.AllNodes[Children[i]].Contents);
                            continue;
                        }
                        else
                        {
                            contents.AddRange(OctreeRoot.AllNodes[Children[i]].Raycast(ray));
                            continue;
                        }
                    }
                }
            }
            return contents;
        }
        public void Iterate(Action<Octree> onLast)
        {
            onLast.Invoke(this);
            for (int i = 0; i < 8; i++)
            {
                if (Children[i] > 0)
                    OctreeRoot.AllNodes[Children[i]].Iterate(onLast);
            }
        }
        //public void CalculateVis_Crude()
        //{
        //    //These are always going to be cubes, so one side length is the same as the rest.
        //    float castDistance = box.Max.X - box.Min.X;
        //    Vector3 boxCenter = (box.Min + box.Max) / 2f;
        //
        //    for (int i = 0; i < 6; i++)
        //    {
        //        visible[i] = OctreeRoot.allNodes[0].Traverse(boxCenter + castDistance * searchDirections[i]);
        //        if (visible[i] == id) visible[i] = -1;
        //    }
        //
        //    //This is the crude and inaccurate version of this function, likely to cull when it shouldnt. I'll write the proper approach with edge intersection at some point
        //    BSPHit hit;
        //    faceToFaceVisibility = new bool[6, 6];
        //    for (int i = 0; i < 6; i++)
        //    {
        //        for (int j = 0; j < 6; j++)
        //        {
        //            if (i == j) continue;
        //
        //            Vector3 targetPoint = castDistance/2f * searchDirections[i] + boxCenter;
        //            Vector3 rayStart = boxCenter - (castDistance * 0.5f) * searchDirections[i];
        //            Vector3 rayDir = targetPoint - boxCenter; rayDir.Normalize();
        //
        //            hit = BSPRoot.TraceRay(new Ray(rayStart, rayDir), Vector3.Distance(rayStart,targetPoint));
        //            faceToFaceVisibility[i, j] = !hit.hit || faceToFaceVisibility[i, j];
        //        }
        //    }
        //}
        //public bool TestVisibility(int sideFrom, int sideTo)
        //{
        //    if (sideFrom == -1 || sideTo == -1) return true;
        //    if (sideFrom == sideTo) return false;
        //
        //    return faceToFaceVisibility[sideFrom,sideTo];
        //}
    }

    public class LightNodeBundle
    {
        public LightNode[] Children;
        public BoundingBox Box;
        public float GridSize = 2f;
        public struct LightData
        {
            public bool LightBlocked;
            public int LightNum;
        }
        public struct LightNode
        {
            public Vector3 Pos;
            public LightData[] Data;
            public Vector3[] IndirectCoefficients;
            public Vector3[][] GroupIndirectCoefficients;
        }

        public LightNodeBundle(BoundingBox box, float gridSize = 2f, bool spreadDistribution = false, bool skipOccluded = true)
        {
            this.Box = box;
            this.GridSize = gridSize;
#if compiler
            List<LightNode> nodes = new List<LightNode>();

            for (int x = 0; x <= (box.Max.X - box.Min.X) / gridSize; x++)
            {
                for (int y = 0; y <= (box.Max.Y - box.Min.Y) / gridSize; y++)
                {
                    for (int z = 0; z <= (box.Max.Z - box.Min.Z) / gridSize; z++)
                    {
                        if (spreadDistribution && ((x - (int)(box.Min.X / gridSize)) + (y - (int)(box.Min.Y / gridSize)) + (z - (int)(box.Min.Z / gridSize))) % 2 != 0) continue;

                        var pos = new Vector3(x * gridSize, y * gridSize, z * gridSize) + box.Min;

                        if (skipOccluded && BSPRoot.Nodes[BSPRoot.Traverse(pos)].solid) continue;
                        if (skipOccluded && BSPRoot.Nodes[BSPRoot.Traverse(pos + new Vector3(0, -1, 0) * 0.025f)].solid) continue;
                        if (skipOccluded && BSPRoot.Nodes[BSPRoot.Traverse(pos + new Vector3(0, 1, 0) * 0.025f)].solid) continue;
                        if (skipOccluded && BSPRoot.Nodes[BSPRoot.Traverse(pos + new Vector3(-1, 0, 0) * 0.025f)].solid) continue;
                        if (skipOccluded && BSPRoot.Nodes[BSPRoot.Traverse(pos + new Vector3(1, 0, 0) * 0.025f)].solid) continue;
                        if (skipOccluded && BSPRoot.Nodes[BSPRoot.Traverse(pos + new Vector3(0, 0, -1) * 0.025f)].solid) continue;
                        if (skipOccluded && BSPRoot.Nodes[BSPRoot.Traverse(pos + new Vector3(0, 0, 1) * 0.025f)].solid) continue;

                        nodes.Add(new LightNode { Pos = CMath.ClampToBoundingBox(pos, box) });
                    }
                }
            }

            Children = nodes.ToArray();
#endif
        }

        private Dictionary<long, List<int>> spatialGrid;
        private readonly object gridBuildLock = new object();
        private void EnsureGridBuilt()
        {
            if (spatialGrid != null) return;
            lock (gridBuildLock)
            {
                if (spatialGrid != null) return;

                float cellSize = GridSize > 0f ? GridSize : EstimateCellSize();
                var grid = new Dictionary<long, List<int>>(Children.Length);

                for (int i = 0; i < Children.Length; i++)
                {
                    long key = CellKey(Children[i].Pos, cellSize);
                    if (!grid.TryGetValue(key, out var list))
                        grid[key] = list = new List<int>(4);
                    list.Add(i);
                }

                this.cellSizeResolved = cellSize;
                spatialGrid = grid; // publish last, after fully built
            }
        }
        private float cellSizeResolved;
        private float EstimateCellSize()
        {
            if (Children.Length < 2) return 2f;
            var size = Box.Max - Box.Min;
            float volume = MathF.Max(size.X, 0.001f) * MathF.Max(size.Y, 0.001f) * MathF.Max(size.Z, 0.001f);
            return MathF.Max(0.1f, MathF.Cbrt(volume / Children.Length));
        }
        private static long CellKey(Vector3 pos, float cellSize)
        {
            const int bias = 1 << 19; // supports cell coords roughly ±500k
            int cx = (int)MathF.Floor(pos.X / cellSize) + bias;
            int cy = (int)MathF.Floor(pos.Y / cellSize) + bias;
            int cz = (int)MathF.Floor(pos.Z / cellSize) + bias;
            return ((long)cx << 42) | ((long)cy << 21) | (uint)cz;
        }
        private List<int> CollectCandidates(Vector3 position, int desiredCount)
        {
            var result = new List<int>(16);
            int cx = (int)MathF.Floor(position.X / cellSizeResolved);
            int cy = (int)MathF.Floor(position.Y / cellSizeResolved);
            int cz = (int)MathF.Floor(position.Z / cellSizeResolved);

            const int maxRadius = 4;
            for (int radius = 1; radius <= maxRadius; radius++)
            {
                result.Clear();
                for (int x = cx - radius; x <= cx + radius; x++)
                    for (int y = cy - radius; y <= cy + radius; y++)
                        for (int z = cz - radius; z <= cz + radius; z++)
                        {
                            long key = CellKey(new Vector3(x, y, z) * cellSizeResolved, cellSizeResolved);
                            if (spatialGrid.TryGetValue(key, out var cell))
                                result.AddRange(cell);
                        }

                if (result.Count >= desiredCount) break;
            }

            if (result.Count == 0)
                for (int i = 0; i < Children.Length; i++) result.Add(i); // pathological fallback

            return result;
        }

        public LightNode Traverse(Vector3 position)
        {
            if (Children.Length == 0) return default;
            EnsureGridBuilt();

            var candidates = CollectCandidates(position, desiredCount: 1);
            float best = float.MaxValue;
            int bestIdx = candidates[0];
            foreach (int i in candidates)
            {
                float d = Vector3.DistanceSquared(Children[i].Pos, position);
                if (d < best) { best = d; bestIdx = i; }
            }
            return Children[bestIdx];
        }
        public LightNode[] GetClosest(Vector3 position)
        {
            if (Children.Length == 0) return Array.Empty<LightNode>();
            EnsureGridBuilt();

            var candidates = CollectCandidates(position, desiredCount: 10);

            Span<float> bestDistSq = stackalloc float[5] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
            Span<int> bestIndex = stackalloc int[5] { -1, -1, -1, -1, -1 };

            foreach (int i in candidates)
            {
                var toNode = Children[i].Pos - position;
                float distSq = toNode.LengthSquared();
                if (distSq >= bestDistSq[4]) continue;

                float dist = MathF.Sqrt(distSq);
                if (dist > 0.001f && BSPRoot.TraceRay(new Ray(position, toNode / dist), dist).Hit)
                    continue; // occluded

                int insertAt = 4;
                while (insertAt > 0 && distSq < bestDistSq[insertAt - 1])
                {
                    bestDistSq[insertAt] = bestDistSq[insertAt - 1];
                    bestIndex[insertAt] = bestIndex[insertAt - 1];
                    insertAt--;
                }
                bestDistSq[insertAt] = distSq;
                bestIndex[insertAt] = i;
            }

            int count = 0;
            while (count < 5 && bestIndex[count] != -1) count++;

            var result = new LightNode[count];
            for (int i = 0; i < count; i++)
                result[i] = Children[bestIndex[i]];

            return result;
        }
        public void Iterate(Action<int> onLast)
        {
            for (int i = 0; i < Children.Length; i++)
            {
                onLast.Invoke(i);
            }
        }
    }

    public struct BSPFile
    {
        public BSPNode[] Nodes;
    }
    public struct VisFile
    {
        public VisLeaf[] Leaves;
        public Portal[] Portals;
    }

    public static class VisRoot
    {
        public static VisLeaf[] VisLeaves;
        public static Portal[] VisPortals;
    }
    public static class BSPRoot
    {
        public static BSPNode[] Nodes;
#if compiler || rockwall
        public static List<BSPNode> tempNodes = new List<BSPNode>();
        public const float EPS = 0.001f;
        static int SamePlane(Plane a, Plane b, float normalEps = EPS, float dEps = EPS)
        {
            var na = a.Normal;
            var nb = b.Normal;

            float la = na.Length();
            float lb = nb.Length();
            if (la < 1e-6f || lb < 1e-6f) return 0;

            na /= la; nb /= lb;
            float da = a.D / la;
            float db = b.D / lb;

            if (Math.Abs(Vector3.Dot(na, nb) - 1f) <= normalEps)
                return Math.Abs(da - db) <= dEps ? 1 : 0;

            if (Math.Abs(Vector3.Dot(na, nb) + 1f) <= normalEps)
            {
                db = -db;
                return Math.Abs(da - db) <= dEps ? -1 : 0;
            }

            return 0;
        }
        public static void Reset()
        {
            Nodes = null;
            tempNodes = new List<BSPNode>();
        }
        public static void Cut(Plane splittingPlane, Brush[] brushes, BoundingBox[] brushBounds, int brushFrom, int face, int pside, bool allowDuplicates = false)
        {
            if (brushes[brushFrom].IsEntity) return;

            Stack<BSPNode> stack = new Stack<BSPNode>();
            stack.Push(tempNodes[0]);

            void AddBrushToNodes(BSPNode node, int brush, bool flipped)
            {
                bool backside = false, frontside = false;

                for (int f = 0; f < brushes[brush].Faces.Length; f++)
                {
                    for (int v = 0; v < brushes[brush].Faces[f].Indices.Length; v++)
                    {
                        Vector3 vertex = brushes[brush].Vertices[brushes[brush].Faces[f].Indices[v]];

                        float dotCoord = splittingPlane.DotCoordinate(vertex + brushes[brush].Position);
                        if (dotCoord < -EPS)
                            backside = true;
                        else if (dotCoord > EPS)
                            frontside = true;

                        if (backside && frontside) break;
                    }
                    if (backside && frontside) break;
                }

                // If the existing split node's plane is flipped relative to ours,
                // what we think is "back" is actually stored as "front" in that node and vice versa
                uint targetBack = flipped ? node.front : node.back;
                uint targetFront = flipped ? node.back : node.front;

                if (backside)
                {
                    Array.Resize(ref tempNodes[(int)targetBack].nodeContents, tempNodes[(int)targetBack].nodeContents.Length + 1);
                    tempNodes[(int)targetBack].nodeContents[tempNodes[(int)targetBack].nodeContents.Length - 1] = (ushort)brush;
                }
                if (frontside)
                {
                    Array.Resize(ref tempNodes[(int)targetFront].nodeContents, tempNodes[(int)targetFront].nodeContents.Length + 1);
                    tempNodes[(int)targetFront].nodeContents[tempNodes[(int)targetFront].nodeContents.Length - 1] = (ushort)brush;
                }
            }

            while (stack.Count > 0)
            {
                var node = stack.Pop();

                if (node.split)
                {
                    // If this plane already exists, and we dont want to create a degenerate node, so we'll check to see if we need to worry about this plane
                    // Then this node will just straddle two solid leaves, or add the brush to the correct side.
                    if (!allowDuplicates && (tempNodes[(int)node.front].nodeContents.Contains((ushort)brushFrom) || tempNodes[(int)node.back].nodeContents.Contains((ushort)brushFrom)))
                    {
                        int planeMatch = SamePlane(node.SplittingPlane, splittingPlane);
                        if (planeMatch != 0)
                        {
                            AddBrushToNodes(node, brushFrom, flipped: planeMatch == -1);
                            continue;
                        }
                    }

                    stack.Push(tempNodes[(int)node.front]);
                    stack.Push(tempNodes[(int)node.back]);
                }
                else
                {
                    node.Cut(splittingPlane, brushes, brushBounds, brushFrom, face, pside);
                }
            }
        }
        public static void DiagnoseUnexpectedSolids(BSPNode[] nodes, Brush[] brushes)
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (n == null) continue;
                int count = n.nodeContents?.Length ?? 0;
                if (n.solid && count == 0)
                {
                    Console.WriteLine($"[BUG] Node {i} marked solid but has 0 contents. parent={n.parent} split={n.split}");
                }
                if (n.solid && count == 1)
                {
                    ushort b = n.nodeContents[0];
                    if (b >= brushes.Length)
                    {
                        Console.WriteLine($"[BUG] Node {i} has invalid brush index {b}. parent={n.parent}");
                    }
                }
            }
        }

        public static void DoubleCheck(Brush[] brushes)
        {
            Stack<BSPNode> stack = new Stack<BSPNode>();
            stack.Push(tempNodes[0]);

            while (stack.Count > 0)
            {
                var node = stack.Pop();

                if (node.split)
                {
                    stack.Push(tempNodes[(int)node.front]);
                    stack.Push(tempNodes[(int)node.back]);
                    node.solid = false;
                }
                else
                {
                    node.solid = node.nodeContents != null && node.nodeContents.Length >= 1;

                    if (node.solid) node.nodeFlag = (byte)(brushes[node.nodeContents[0]].IsSkybox ? BSPNode.SkyboxNode : brushes[node.nodeContents[0]].IsClip ? BSPNode.ClipNode : 0);
                }
            }

            DiagnoseUnexpectedSolids(tempNodes.ToArray(), brushes);
        }
#endif
        public static uint Traverse(Vector3 point)
        {
            uint node = 0;
            while (Nodes[node].split)
                node = Nodes[node].SplittingPlane.DotCoordinate(point) >= 0
                    ? Nodes[node].front : Nodes[node].back;
            return Nodes[node].id;
        }

        private struct TraceFrame
        {
            public uint FarNode;
            public Vector3 Mid;
            public Vector3 P2;
            public Vector3 Normal;
            public Vector3 Binormal;
            public int Surf;
        }


        public static BSPHit TraceRay(Ray ray, float distance, bool ignoreClip = true, int ignoreBrush = -1, int ignoreFace = -1)
        {
            BSPHit hit = new BSPHit { Point = ray.Position + ray.Direction * distance, Normal = ray.Direction };

            Vector3 p1 = ray.Position;
            Vector3 p2 = ray.Position + ray.Direction * distance;
            uint nodeNum = 0;
            Vector3 normal = Vector3.One;
            Vector3 binormal = Vector3.One;
            int surf = 0;

            Span<TraceFrame> stack = stackalloc TraceFrame[64];
            int stackTop = 0;
            bool found = false;
            Vector3 intersection = p1;

            while (true)
            {
                BSPNode node = Nodes[nodeNum];

                if (node.solid)
                {
                    intersection = p1;
                    bool rejected = false;
                    if (ignoreBrush >= 0 && ignoreFace == -1 && node.brush == ignoreBrush) rejected = true;
                    if (ignoreBrush >= 0 && ignoreFace >= 0 && node.brush == ignoreBrush && node.face == ignoreFace) rejected = true;
                    if (!rejected && node.nodeFlag == BSPNode.ClipNode && ignoreClip) rejected = true;

                    if (!rejected)
                    {
                        found = true;
                        hit.Node = nodeNum;
                        break;
                    }

                    if (stackTop == 0) break;
                    stackTop--;
                    TraceFrame frame = stack[stackTop];
                    p1 = frame.Mid;
                    p2 = frame.P2;
                    nodeNum = frame.FarNode;
                    normal = frame.Normal;
                    binormal = frame.Binormal;
                    surf = frame.Surf;
                    continue;
                }

                if (!node.split)
                {
                    intersection = p2;
                    surf = 0;

                    if (stackTop == 0) break;
                    stackTop--;
                    TraceFrame frame = stack[stackTop];
                    p1 = frame.Mid;
                    p2 = frame.P2;
                    nodeNum = frame.FarNode;
                    normal = frame.Normal;
                    binormal = frame.Binormal;
                    surf = frame.Surf;
                    continue;
                }

                Plane plane = node.SplittingPlane;
                float t1 = plane.DotCoordinate(p1);
                float t2 = plane.DotCoordinate(p2);

                if (t1 >= 0f && t2 >= 0f)
                {
                    nodeNum = node.front;
                    continue;
                }
                if (t1 < 0f && t2 < 0f)
                {
                    nodeNum = node.back;
                    continue;
                }

                float frac = Math.Clamp(t1 / (t1 - t2), 0f, 1f);
                Vector3 mid = p1 + frac * (p2 - p1);

                int nodeSurf = 0;
#if !rockwall && !compiler
                nodeSurf = string.IsNullOrEmpty(node.surface) ? 0 :
                    GlobalMapData.MaterialNameToIndex.TryGetValue(node.surface, out var val) ? val : 0;
#endif

                stack[stackTop] = new TraceFrame
                {
                    FarNode = t1 >= 0f ? node.back : node.front,
                    Mid = mid,
                    P2 = p2,
                    Normal = plane.Normal,
                    Binormal = node.Binormal,
                    Surf = nodeSurf
                };
                stackTop++;

                nodeNum = t1 >= 0f ? node.front : node.back;
                p2 = mid;
            }

            if (found)
            {
                hit = new BSPHit { Point = intersection, Hit = true, Node = nodeNum, Normal = normal, Surf = surf, Binormal = binormal };
            }

            if (Vector3.Dot(hit.Normal, ray.Direction) > 0) hit.Normal *= -1;
            return hit;
        }
    }
    [System.Serializable]
    public class BSPNode
    {
        public const byte ClipNode = 2;
        public const byte SkyboxNode = 4;

        //public Vector3 splitPos, splitNormal; // Plane that splits the room
        public Plane SplittingPlane;
        public Vector3 Binormal;

        public float spx
        {
            get
            {
                return SplittingPlane.Normal.X;
            }
            set
            {
                SplittingPlane.Normal.X = value;
            }
        }
        public float spy
        {
            get
            {
                return SplittingPlane.Normal.Y;
            }
            set
            {
                SplittingPlane.Normal.Y = value;
            }
        }
        public float spz
        {
            get
            {
                return SplittingPlane.Normal.Z;
            }
            set
            {
                SplittingPlane.Normal.Z = value;
            }
        }
        public float d
        {
            get
            {
                return SplittingPlane.D;
            }
            set
            {
                SplittingPlane.D = value;
            }
        }

        public uint front;
        public uint back;
        public uint parent;
        public uint id = 0;
        public byte nodeFlag;
        public ushort[] nodeContents;
        public bool solid;
        public bool split;
        public ushort brush;
        public byte face;
        public string surface;
        public float bnx
        {
            get
            {
                return Binormal.X;
            }
            set
            {
                Binormal.X = value;
            }
        }
        public float bny
        {
            get
            {
                return Binormal.Y;
            }
            set
            {
                Binormal.Y = value;
            }
        }
        public float bnz
        {
            get
            {
                return Binormal.Z;
            }
            set
            {
                Binormal.Z = value;
            }
        }

#if rockwall || compiler

        public bool dirty;
        List<BSPNode> Nodes => BSPRoot.tempNodes;
        public void Cut(Plane splittingPlane, Brush[] brushes, BoundingBox[] brushBounds, int brushFrom, int face, int pside)
        {
            if (!nodeContents.Contains((ushort)brushFrom)) return;
            split = true;

            this.brush = (ushort)brushFrom;
            this.face = (byte)face;

            this.SplittingPlane = splittingPlane;

            var backContents = new List<ushort>() { (ushort)brushFrom };
            var frontContents = new List<ushort>();

            foreach (int content in nodeContents)
            {
                if (content == brushFrom) continue;
                if (brushes[content].IsEntity) continue;
                if (brushes[content].IsLightNodeVolume) continue;
                if (brushes[content].IsTrigger) continue;
                if (brushes[content].IsClip) continue;

                bool backside = false, frontside = false;

                for (int f = 0; f < brushes[content].Faces.Length; f++)
                {
                    for (int v = 0; v < brushes[content].Faces[f].Indices.Length; v++)
                    {
                        Vector3 vertex = brushes[content].Vertices[brushes[content].Faces[f].Indices[v]];

                        float dotCoord = splittingPlane.DotCoordinate(vertex + brushes[content].Position);
                        if (dotCoord < -BSPRoot.EPS)
                            backside = true;
                        else if (dotCoord > BSPRoot.EPS)
                            frontside = true;

                        //No need to keep checking.
                        if (backside && frontside) break;
                    }
                    if (backside && frontside) break;
                }

                if (backside) backContents.Add((ushort)content);
                if (frontside) frontContents.Add((ushort)content);
            }

            bool skybox = brushes[brushFrom].IsSkybox;
            bool clip = brushes[brushFrom].IsClip;

            var bface = brushes[brushFrom].Faces[face];

            front = (uint)Nodes.Count;
            back = (uint)Nodes.Count + 1;
            Nodes.Add(new BSPNode { nodeContents = frontContents.ToArray(), parent = id, id = front, nodeFlag = (byte)(skybox ? SkyboxNode : clip ? ClipNode : 0), brush = (ushort)brushFrom, face = (byte)face, Binormal = bface.Binormal, surface = bface.MaterialName });
            Nodes.Add(new BSPNode { nodeContents = backContents.ToArray(), solid = true, parent = id, id = back, nodeFlag = (byte)(skybox ? SkyboxNode : clip ? ClipNode : 0), brush = (ushort)brushFrom, face = (byte)face, Binormal = bface.Binormal, surface = bface.MaterialName });
        }
#endif
    }

    public struct BSPHit
    {
        public Vector3 Point, Normal, Binormal;
        public bool Hit;
        public uint Node;
        public int Surf;
    }

    public struct AINode
    {
        public enum NodeType
        {
            Ground,
            Air,
        }
        public NodeType Type;
        public int[] Connections;
        public int Zone;

        public Vector3 Position;
    }

    public struct NodeGraph
    {
        public AINode[] Nodes;
    }

    public struct Map
    {
        public Brush[] Brushes;
        public Terrain[] Terrains;
        public BoundingBox[] BrushBounds;
        public MapPropModel[] MapModels;

        public LeafPolygon[] LeafPolygons;
        public int[] LeafPolyStart, LeafPolyCount;
        public VertexLightmapped[] StaticGeomVertices;

        public bool HasVis;
        public Octree Root;
        public List<Octree> OctreeNodes;
        public EntityReference[] Entities;
        public LightNodeBundle[] LightNodes;
        public string[] LightGroupKeys;
        public NodeGraph Nodegraph;
    }

    public static class GlobalMapData
    {
        public static Map ActiveMap;
        public static Material[] LoadedMaterials;
#if !rockwall
        public static ImmutableDictionary<string, int> MaterialNameToIndex;
#else
        public static Dictionary<string, int> MaterialNameToIndex;
#endif
    }
}

[System.Serializable]
public struct EntityOutput
{
    public string EntityTarget;
    public string EntityInputTarget;
    public string InputParameters;

    // scripting rocks and is awesome and epic
    public string ScriptSource;
    [JsonIgnore()]
    public EXScript Script;

    public float Delay;
    public int Refire;
}

[System.Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VertexLightmapped : IVertexType
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Tangent;
    public Vector3 Binormal;
    public Vector2 TextureCoordinate;
    public Vector2 LightmapCoordinate;

    public static readonly VertexDeclaration VertexDeclaration;
    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

    public VertexLightmapped(Vector3 position, Vector3 normal, Vector2 textureCoordinate, Vector2 lightmapCoordinate)
    {
        Position = position;
        Normal = normal;
        Tangent = Vector3.Zero;
        Binormal = Vector3.Zero;
        TextureCoordinate = textureCoordinate;
        LightmapCoordinate = lightmapCoordinate;
    }

    public VertexLightmapped(Vector3 position, Vector3 normal, Vector3 tangent, Vector3 binormal, Vector2 textureCoordinate, Vector2 lightmapCoordinate)
    {
        Position = position;
        Normal = normal;
        Tangent = tangent;
        Binormal = binormal;
        TextureCoordinate = textureCoordinate;
        LightmapCoordinate = lightmapCoordinate;
    }

    public override int GetHashCode()
    {
        return (((Position.GetHashCode() * 397) ^ Normal.GetHashCode()) * (397 ^ LightmapCoordinate.GetHashCode())) ^ TextureCoordinate.GetHashCode();
    }

    public override string ToString()
    {
        string[] obj = new string[7] { "{{Position:", null, null, null, null, null, null };
        Vector3 position = Position;
        obj[1] = position.ToString();
        obj[2] = " Normal:";
        position = Normal;
        obj[3] = position.ToString();
        obj[4] = " TextureCoordinate:";
        Vector2 textureCoordinate = TextureCoordinate;
        obj[5] = textureCoordinate.ToString();
        obj[6] = "}}";
        return string.Concat(obj);
    }

    public static bool operator ==(VertexLightmapped left, VertexLightmapped right)
    {
        if (left.Position == right.Position && left.Normal == right.Normal &&
            left.Tangent == right.Tangent && left.Binormal == right.Binormal)
        {
            return left.TextureCoordinate == right.TextureCoordinate && left.LightmapCoordinate == right.LightmapCoordinate;
        }
        return false;
    }
    public static bool operator !=(VertexLightmapped left, VertexLightmapped right) => !(left == right);

    public override bool Equals(object obj)
    {
        if (obj == null || obj.GetType() != GetType()) return false;
        return this == (VertexLightmapped)obj;
    }

    static VertexLightmapped()
    {
        VertexDeclaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Vector3, VertexElementUsage.Tangent, 0),
            new VertexElement(36, VertexElementFormat.Vector3, VertexElementUsage.Binormal, 0),
            new VertexElement(48, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(56, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 1));
    }
}

[System.Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VertexModelLightmapped : IVertexType
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Tangent;
    public Vector3 Binormal;
    public Vector2 TextureCoordinate;
    public Vector2 LightmapCoordinate;

    public static readonly VertexDeclaration VertexDeclaration;
    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

    public VertexModelLightmapped(
        Vector3 position,
        Vector3 normal,
        Vector3 tangent,
        Vector3 binormal,
        Vector2 textureCoordinate,
        Vector2 lightmapCoordinate)
    {
        Position = position;
        Normal = normal;
        Tangent = tangent;
        Binormal = binormal;
        TextureCoordinate = textureCoordinate;
        LightmapCoordinate = lightmapCoordinate;
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = Position.GetHashCode();
            hash = (hash * 397) ^ Normal.GetHashCode();
            hash = (hash * 397) ^ Tangent.GetHashCode();
            hash = (hash * 397) ^ Binormal.GetHashCode();
            hash = (hash * 397) ^ TextureCoordinate.GetHashCode();
            hash = (hash * 397) ^ LightmapCoordinate.GetHashCode();
            return hash;
        }
    }

    public override string ToString()
    {
        return $"{{Position:{Position} Normal:{Normal} Tangent:{Tangent} Binormal:{Binormal} TextureCoordinate:{TextureCoordinate}}}";
    }

    public static bool operator ==(VertexModelLightmapped left, VertexModelLightmapped right)
    {
        return left.Position == right.Position
            && left.Normal == right.Normal
            && left.Tangent == right.Tangent
            && left.Binormal == right.Binormal
            && left.TextureCoordinate == right.TextureCoordinate
            && left.LightmapCoordinate == right.LightmapCoordinate;
    }

    public static bool operator !=(VertexModelLightmapped left, VertexModelLightmapped right)
    {
        return !(left == right);
    }

    public override bool Equals(object obj)
    {
        if (obj == null || obj.GetType() != GetType())
            return false;
        return this == (VertexModelLightmapped)obj;
    }

    static VertexModelLightmapped()
    {
        VertexDeclaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Vector3, VertexElementUsage.Tangent, 0),
            new VertexElement(36, VertexElementFormat.Vector3, VertexElementUsage.Binormal, 0),
            new VertexElement(48, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(56, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 1)
        );
    }
}
[System.Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TerrainVertex : IVertexType
{
    public Vector3 Position;

    public Vector2 LightmapCoordinate;

    public Vector3 Normal;

    public Vector3 TextureCoordinate;

    public NormalizedShort4 Tangent;

    public static readonly VertexDeclaration VertexDeclaration;

    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

    public TerrainVertex(Vector3 position, Vector2 lightmapCoordinate,
                         Vector3 normal, Vector3 textureCoordinate,
                         Vector3 tangent, float handedness)
    {
        Position = position;
        LightmapCoordinate = lightmapCoordinate;
        Normal = normal;
        TextureCoordinate = textureCoordinate;
        Tangent = new NormalizedShort4(tangent.X, tangent.Y, tangent.Z, handedness);
    }

    public override int GetHashCode()
    {
        return (((((Position.GetHashCode() * 397) ^ LightmapCoordinate.GetHashCode()) * 397) ^ Normal.GetHashCode()) * 397) ^ TextureCoordinate.GetHashCode();
    }

    //public override string ToString()
    //{
    //    string[] obj = new string[9] { "{{Position:", null, null, null, null, null, null, null, null };
    //    Vector3 position = Position;
    //    obj[1] = position.ToString();
    //    obj[2] = " Color:";
    //    Color color = LightmapCoordinate;
    //    obj[3] = color.ToString();
    //    obj[4] = " Normal:";
    //    position = Normal;
    //    obj[5] = position.ToString();
    //    obj[6] = " TextureCoordinate:";
    //    Vector2 textureCoordinate = TextureCoordinate;
    //    obj[7] = textureCoordinate.ToString();
    //    obj[8] = "}}";
    //    return string.Concat(obj);
    //}

    public static bool operator ==(TerrainVertex left, TerrainVertex right)
    {
        if (left.Position == right.Position && left.LightmapCoordinate == right.LightmapCoordinate && left.Normal == right.Normal)
        {
            return left.TextureCoordinate == right.TextureCoordinate;
        }

        return false;
    }

    public static bool operator !=(TerrainVertex left, TerrainVertex right)
    {
        return !(left == right);
    }

    public override bool Equals(object obj)
    {
        if (obj == null)
        {
            return false;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return this == (TerrainVertex)obj;
    }

    static TerrainVertex()
    {
        VertexDeclaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 1),
            new VertexElement(20, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(32, VertexElementFormat.Vector3, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(44, VertexElementFormat.NormalizedShort4, VertexElementUsage.Tangent, 0));
    }
}
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MapPropModelVertex : IVertexType
{
    public Vector3 Position;

    public Vector3 Color;

    public Vector3 Normal;

    public Vector2 TextureCoordinate;

    public static readonly VertexDeclaration VertexDeclaration;

    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

    public MapPropModelVertex(Vector3 position, Vector3 color, Vector3 normal, Vector2 textureCoordinate)
    {
        Position = position;
        Color = color;
        Normal = normal;
        TextureCoordinate = textureCoordinate;
    }

    public override int GetHashCode()
    {
        return (((((Position.GetHashCode() * 397) ^ Color.GetHashCode()) * 397) ^ Normal.GetHashCode()) * 397) ^ TextureCoordinate.GetHashCode();
    }

    public override string ToString()
    {
        string[] obj = new string[9] { "{{Position:", null, null, null, null, null, null, null, null };
        Vector3 position = Position;
        obj[1] = position.ToString();
        obj[2] = " Color:";
        Vector3 color = Color;
        obj[3] = color.ToString();
        obj[4] = " Normal:";
        position = Normal;
        obj[5] = position.ToString();
        obj[6] = " TextureCoordinate:";
        Vector2 textureCoordinate = TextureCoordinate;
        obj[7] = textureCoordinate.ToString();
        obj[8] = "}}";
        return string.Concat(obj);
    }

    public static bool operator ==(MapPropModelVertex left, MapPropModelVertex right)
    {
        if (left.Position == right.Position && left.Color == right.Color && left.Normal == right.Normal)
        {
            return left.TextureCoordinate == right.TextureCoordinate;
        }

        return false;
    }

    public static bool operator !=(MapPropModelVertex left, MapPropModelVertex right)
    {
        return !(left == right);
    }

    public override bool Equals(object obj)
    {
        if (obj == null)
        {
            return false;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return this == (MapPropModelVertex)obj;
    }

    static MapPropModelVertex()
    {
        VertexDeclaration = new VertexDeclaration(new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
                                                  new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Color, 0),
                                                  new VertexElement(24, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
                                                  new VertexElement(36, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));
    }
}