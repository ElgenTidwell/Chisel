using Chisel.Collision;
using Chisel.Utils;
using Cyotek.Drawing.BitmapFont;
using Engine;
using Engine.Console;
using Engine.Entities;
using Engine.Utils;
using Engine.Utils.Settings;
using FontStashSharp;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Rockwall;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static Engine.MainEngine;

namespace Engine.Rendering
{
    public enum QualityLevel
    {
        Low,
        Medium,
        High
    }
    public static class RenderEngine
    {
        public static CommandBinding cWireframe = new CommandBinding("r_wireframe", (string[] arg) =>
        {
            CurrentWireframeDisplayMode = (arg != null && arg.Length != 0) ? int.Parse(arg[0]) : (CurrentWireframeDisplayMode == 0 ? 1 : 0);
        });
        public static CommandBinding cDrawPortals = new CommandBinding("r_drawportals", (string[] arg) =>
        {
            portalLines = (arg != null && arg.Length != 0) ? int.Parse(arg[0]) : (portalLines == 0 ? 1 : 0);
        });

        public static GraphicsDeviceManager GraphicsDeviceManager;

        public readonly static CVarBool DrawBrushes = new CVarBool("r_drawbrushes", true);
        public readonly static CVarBool ShowFPS = new CVarBool("showfps", false);
        public readonly static CVarBool ShowTimings = new CVarBool("showtimings", false);
        public readonly static CVarBool ShowOctree = new CVarBool("r_drawoctree", false);
        public readonly static CVarBool ShowBSPTree = new CVarBool("r_drawbsp", false);
        public readonly static CVarBool ShowFrozenFrustum = new CVarBool("r_freezefrustum", false);
        public readonly static CVarBool ShowNodes = new CVarBool("r_drawnodes", false);
        public readonly static CVarBool ShowHitboxes = new CVarBool("r_drawhitboxes", false);
        public readonly static CVarBool ShowPhysics = new CVarBool("r_drawphysics", false);
        public readonly static CVarBool ShowBlankTexture = new CVarBool("mat_texnul", false);
        public readonly static CVarBool Show3DSky = new CVarBool("r_3dsky", true);
        public readonly static CVarBool ShowMaterialShine = new CVarBool("mat_wax", false);
        public readonly static CVarBool DrawLightnodes = new CVarBool("r_drawlightnodes", false);
        public readonly static CVarInt LODBias = new CVarInt("r_lodbias", 0);
        public readonly static CVarBool ShowFootIK = new CVarBool("r_drawfootik", false);
        //public readonly static CVarBool DrawOctBounds = new CVarBool("ssportal_debug", false);
        //public readonly static CVarBool UsePortalCulling = new CVarBool("ssportal_enable", false);
        public readonly static CVarBool ShowMultiDrawBatches = new CVarBool("r_showmultidrawbatches", false);
        public static CommandBinding cBuildCubemaps = new CommandBinding("r_buildcubemaps", (string[] arg) =>
        {
            cubemapsNeedCapture = true;
            cubemapCaptureDelay = 0;
        });

        public static int CurrentWireframeDisplayMode = 0;

        private static int currentPortalView = -1;
        private static int portalLines = 0;

        private static VertexBuffer debugBuffer;
        private static VertexBuffer staticGeomVertexBuffer;

        private static int[] multiDrawStartsScratch = new int[256];
        private static int[] multiDrawCountsScratch = new int[256];
        private struct GeomRun { public int MaterialID, CubemapID, VertexStart, VertexCount; }
        private struct MaterialRange
        {
            public int MaterialID;
            public int CubemapID;
            public int StartIndex;
            public int IndexCount;
        }

        private class CachedLeafGeometry
        {
            public int[] Indices = Array.Empty<int>();
            public List<MaterialRange> Ranges = new();
            public IndexBuffer IndexBuffer;
            public bool SkyboxVisible;
        }

        private static Dictionary<int, CachedLeafGeometry> cachedMainGeometry = new();
        private static Dictionary<int, CachedLeafGeometry> cachedSkyGeometry = new();
        private static bool useThirtyTwoBitIndices;

        private static SimpleFps fpsTracker = new();

        public static Vector3 CameraPosition => Matrix.Invert(ViewMatrix).Translation;
        public static Vector3 CameraForward => Matrix.Invert(ViewMatrix).Forward;
        public static Vector3 CameraRight => Matrix.Invert(ViewMatrix).Right;
        public static Vector3 CameraUp => Matrix.Invert(ViewMatrix).Up;
        public static float CameraNear;
        public static float CameraFar;
        public static Matrix ViewMatrix { get; internal set; }
        public static Matrix ProjectionMatrix { get; internal set; }
        public static Matrix WorldMatrix { get; internal set; }

        public static BoundingFrustum CameraBoundingFrustum;

        private static ulong[] mainPvsBits = Array.Empty<ulong>();
        private static ulong[] skyPvsBits = Array.Empty<ulong>();
        private static uint previousCameraLeaf = uint.MaxValue;
        private static uint previousSkyLeaf = uint.MaxValue;

        private static HashSet<uint> modelLeavesVisited = new HashSet<uint>();
        private static List<RenderableMapModel> transparentMapModels = new List<RenderableMapModel>();
        private static List<int> cameraRenderQueue = new List<int>();

        public static DisplayMode ActiveDisplayMode;
        public static List<DisplayMode> ValidDisplayModes;

        private static SamplerState worldTextureSamplerState = new SamplerState { Filter = TextureFilter.Anisotropic, MaxAnisotropy = 8, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap };
        public static SamplerState WorldTextureSamplerState
        {
            get => worldTextureSamplerState;
            set { worldTextureSamplerState = value; ShaderBuilder.RefreshSamplerStates(); }
        }

        private static SamplerState lightmapTextureSamplerState = SamplerState.LinearClamp;
        public static SamplerState LightmapTextureSamplerState
        {
            get => lightmapTextureSamplerState;
            set { lightmapTextureSamplerState = value; ShaderBuilder.RefreshSamplerStates(); }
        }

        public static SamplerState ScreenTextureSamplerState = SamplerState.LinearClamp;
        public static RasterizerState WireframeRasterizerState;
        public static RasterizerState WireframeRasterizerStateScissor;

        private static bool needsDisplayRebuild;
        private static BlendState disableColors = new BlendState();
        private static BlendState enableColors = BlendState.AlphaBlend;
        private static BlendState alphaPrePass = new BlendState
        {
            ColorWriteChannels = ColorWriteChannels.None,
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
        };
        private static BlendState nonPremultiplied = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.InverseSourceAlpha,
        };
        private static DepthStencilState decalState;
        private static DepthStencilState brushDepthEquals = new DepthStencilState
        {
            DepthBufferFunction = CompareFunction.Equal,
            DepthBufferWriteEnable = false,
        };

        public static Texture2D WhiteTexture, DimTexture, BlackTexture, GreenTexture, PurpleTexture, ErrorTexture, BlobShadowTexture, BlankSpecTexture;
        private static Texture2D loadedLightmapB1;
        private static Texture2D loadedLightmapB2;
        private static Texture2D loadedLightmapB3;

        private static VertexBuffer postFXQuad;

        public static ShaderHandle[] LoadedWorldShaders;

        public static RenderTarget2D ScreenRenderTexture;
        private static RenderTarget2D reflectionRenderTexture;
        private static RenderTarget2D refractionRenderTexture;
        private static BasicEffect basicShader;
        private static RasterizerState worldRasterizer;

        private const int CubemapCaptureDelayFrames = 5;
        private static int cubemapCaptureDelay;

        private const int CubemapCaptureSize = 128;
        private static RenderTargetCube cubemapCaptureTarget;
        private static bool cubemapsNeedCapture;

        private static readonly Vector2[] cubeFaceYawPitch =
        {
            new Vector2(90, 0),
            new Vector2(-90, 0),
            new Vector2(180, 90),
            new Vector2(180, -90),
            new Vector2(180, 0),
            new Vector2(0, 0),
        };

        //private static RasterizerState cullClockwiseSkyScissor = new RasterizerState
        //{
        //    CullMode = CullMode.CullClockwiseFace,
        //    ScissorTestEnable = true,
        //};
        //private static RasterizerState cullCounterClockwiseSkyScissor = new RasterizerState
        //{
        //    CullMode = CullMode.CullCounterClockwiseFace,
        //    ScissorTestEnable = true,
        //};
        //private static RasterizerState cullNoneSkyScissor = new RasterizerState
        //{
        //    CullMode = CullMode.None,
        //    ScissorTestEnable = true,
        //};

        private static RasterizerState cullClockwiseSky = new RasterizerState
        {
            CullMode = CullMode.CullClockwiseFace,
            //ScissorTestEnable = true
        };
        private static RasterizerState cullCounterClockwiseSky = new RasterizerState
        {
            CullMode = CullMode.CullCounterClockwiseFace,
            //ScissorTestEnable = true
        };
        private static RasterizerState cullNoneSky = new RasterizerState
        {
            CullMode = CullMode.None,
            //ScissorTestEnable = true
        };

        private static bool isWindingFlipped;

        private static SphericalHarmonicsVisualizer sphericalHarmonicsVisualizer;

        private static float totalTime;

        private static float cameraPreviousCubeBlend;

        public static FontSystem FontSystem;

        private static bool aaEnabled;
        private static int aaCount;

        private static bool skyboxWasVisible;

        public static bool IsLeafInMainPVS(uint leaf) => leaf < (uint)(mainPvsBits.Length << 6) && Bitset.Get(mainPvsBits, (int)leaf);
        public static bool IsLeafInSkyPVS(uint leaf) => leaf < (uint)(skyPvsBits.Length << 6) && Bitset.Get(skyPvsBits, (int)leaf);
        public static bool IsAnyLeafInMainPVS(ulong[] leafBits) => Bitset.Intersects(leafBits, mainPvsBits);
        public static bool IsAnyLeafInSkyPVS(ulong[] leafBits) => Bitset.Intersects(leafBits, skyPvsBits);

        // reflections
        private static List<PlanarReflectorGroup> reflectorGroups = new();
        private static Dictionary<long, int> faceToGroupLookup = new();
        private static long PackFaceKey(int brush, int face)
        {
            return ((long)brush << 32) | (uint)face;
        }

        private const int ReflectionBudgetMedium = 2;
        private const int ReflectionBudgetHigh = 4;
        private static List<PlanarReflectorGroup> reflectionRenderScratch = new();

        // Quality settings and stuff
        public static QualityLevel ShadowQuality { get; private set; }
        public static QualityLevel ShaderQuality { get; private set; }
        public static QualityLevel TextureQuality { get; private set; }
        public static QualityLevel LODQuality { get; private set; }
        public static QualityLevel ReflectionQuality { get; private set; }

        private static readonly string[] techniqueNames =
        {
            "Low", "Low_AlphaClip",
            "Med", "Med_AlphaClip",
            "High", "High_AlphaClip",
        };

        /// <summary>
        /// Sets proper shader parameters for the provided lightmaps, and caches them locally for later use.
        /// </summary>
        /// <param name="lightmapB1">The lightmap for basis1.</param>
        /// <param name="lightmapB2">The lightmap for basis2.</param>
        /// <param name="lightmapB3">The lightmap for basis3.</param>
        public static void CheckInWorldTextures(Texture2D lightmapB1, Texture2D lightmapB2, Texture2D lightmapB3)
        {
            loadedLightmapB1 = lightmapB1;
            loadedLightmapB2 = lightmapB2;
            loadedLightmapB3 = lightmapB3;

            DecalManager.SetLightmaps(lightmapB1,lightmapB2,lightmapB3);

            foreach(var shader in LoadedWorldShaders)
            {
                shader.Param("LightmapB1").SetValue(loadedLightmapB1);
                shader.Param("LightmapB2").SetValue(loadedLightmapB2);
                shader.Param("LightmapB3").SetValue(loadedLightmapB3);
                shader.Param("LightmapSize").SetValue(loadedLightmapB1.Bounds.Size.ToVector2());
            }

            Instance.TerrainShader.Param("LightmapB1").SetValue(loadedLightmapB1);
            Instance.TerrainShader.Param("LightmapB2").SetValue(loadedLightmapB2);
            Instance.TerrainShader.Param("LightmapB3").SetValue(loadedLightmapB3);
            Instance.TerrainShader.Param("LightmapSize").SetValue(loadedLightmapB1.Bounds.Size.ToVector2());
        }

        public static void CreateStaticGeometryBuffer(VertexLightmapped[] vertices)
        {
            staticGeomVertexBuffer?.Dispose();
            if (vertices == null || vertices.Length == 0) { staticGeomVertexBuffer = null; return; }

            staticGeomVertexBuffer = new VertexBuffer(Instance.GraphicsDevice, typeof(VertexLightmapped), vertices.Length, BufferUsage.WriteOnly);
            staticGeomVertexBuffer.SetData(vertices);

            useThirtyTwoBitIndices = vertices.Length > ushort.MaxValue;
        }

        public static void GenRuntimeCubemapAssociation()
        {
            cubemapsNeedCapture = true;
            cubemapCaptureDelay = CubemapCaptureDelayFrames;

            var polys = GlobalMapData.ActiveMap.LeafPolygons;
            var verts = GlobalMapData.ActiveMap.StaticGeomVertices;
            if (polys == null || verts == null) return;

            for (int i = 0; i < polys.Length; i++)
            {
                ref var poly = ref polys[i];
                if (poly.VertexCount == 0)
                {
                    continue;
                }

                Vector3 centroid = Vector3.Zero;

                for (int v = poly.VertexStart; v < poly.VertexStart + poly.VertexCount; v++)
                {
                    centroid += verts[v].Position;
                }
                centroid /= poly.VertexCount;

                poly.RuntimeCubemapID = CubemapHandler.GetNearestCubemapIndex(centroid);
            }

            ClearLeafGeometryCaches();
        }
        private static void CaptureCubemaps(GameTime time)
        {
            cubemapsNeedCapture = false;
            if (EnvCubemap.Cubemaps.Count == 0) return;

            var gd = Instance.GraphicsDevice;
            cubemapCaptureTarget ??= new RenderTargetCube(gd, CubemapCaptureSize, false, SurfaceFormat.Color, DepthFormat.Depth24);

            var savedView = ViewMatrix;
            var savedProjection = ProjectionMatrix;
            var savedWorld = WorldMatrix;
            var savedFrustum = CameraBoundingFrustum;
            var savedSkyboxWasVisible = skyboxWasVisible;

            float near = CameraNear > 0 ? CameraNear : 0.01f;
            float far = CameraFar > near ? CameraFar : 1000f;

            EnvCubemap.cubeRendering = true;

            ProjectionMatrix = Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver2, 1f, near, far);
            WorldMatrix = Matrix.Identity;

            gd.SamplerStates[0] = WorldTextureSamplerState;
            gd.SamplerStates[1] = WorldTextureSamplerState;
            gd.SamplerStates[2] = WorldTextureSamplerState;
            gd.SamplerStates[3] = LightmapTextureSamplerState;
            gd.SamplerStates[4] = LightmapTextureSamplerState;
            gd.SamplerStates[5] = LightmapTextureSamplerState;
            gd.SamplerStates[6] = LightmapTextureSamplerState;

            foreach (var cube in EnvCubemap.Cubemaps)
            {
                for (int i = 0; i < 6; i++)
                {
                    float yaw = MathHelper.ToRadians(cubeFaceYawPitch[i].X);
                    float pitch = MathHelper.ToRadians(cubeFaceYawPitch[i].Y);

                    ViewMatrix = Matrix.CreateTranslation(-cube.Position) * Matrix.CreateScale(-1, 1, 1) * Matrix.Invert(Matrix.CreateFromYawPitchRoll(yaw, pitch, 0));

                    gd.SetRenderTarget(cubemapCaptureTarget, CubeMapFace.PositiveX + i);
                    gd.Clear(ClearOptions.DepthBuffer | ClearOptions.Target, Color.Black, gd.Viewport.MaxDepth, 0);

                    PrepareWorldShaders();

                    bool captureSkyboxVisible = true;
                    skyboxWasVisible = true;

                    gd.BlendState = nonPremultiplied;
                    gd.DepthStencilState = DepthStencilState.Default;
                    gd.RasterizerState = RasterizerState.CullCounterClockwise;

                    RenderWorld(time, cube.Position, renderDecalsAndParticles: false, flipWinding: true,
                                drawSkybox: true, allow3DSkybox: true,
                                ref captureSkyboxVisible,
                                renderEntities: false, skyboxFlipWinding: false);
                }

                gd.SetRenderTarget(null);

                if (cube.diffusionMaps != null)
                {
                    foreach (var map in cube.diffusionMaps)
                    {
                        map?.Dispose();
                    }
                }

                cube.diffusionMaps = new TextureCube[6];
                for (int m = 0; m < 6; m++)
                {
                    cube.diffusionMaps[m] = CubemapMipmapGenerator.ScaleCube(cubemapCaptureTarget, cubemapCaptureTarget.Size >> m);
                }
            }

            foreach (var cube in EnvCubemap.Cubemaps)
            {
                if (!cube.IsDespawned)
                {
                    EntityManager.DespawnEntity(cube);
                }
            }

            EnvCubemap.cubeRendering = false;
            Displayable.FlipWinding = false;
            isWindingFlipped = false;

            ViewMatrix = savedView;
            ProjectionMatrix = savedProjection;
            WorldMatrix = savedWorld;
            CameraBoundingFrustum = savedFrustum;
            skyboxWasVisible = savedSkyboxWasVisible;

            gd.SetRenderTarget(null);
            PrepareWorldShaders();
        }

        /// <summary>
        /// Sets the blend-mix for terrains.
        /// </summary>
        /// <param name="blend"></param>
        public static void ChangeTerrainBlendMix(Texture2D blend)
        {
            Instance.TerrainShader.Param("terrainBlendMix").SetValue(blend);
        }
        /// <summary>
        /// To be run in an engine's constructor, initializes rendering variables that might need to be used before completely initializing the rendering engine.
        /// </summary>
        public static void ConstructRenderEngine()
        {
            GraphicsDeviceManager = new GraphicsDeviceManager(Instance);

            ValidDisplayModes = new List<DisplayMode>();

            foreach (var res in GraphicsAdapter.DefaultAdapter.SupportedDisplayModes)
            {
                bool active = false;
                if (res == GraphicsAdapter.DefaultAdapter.CurrentDisplayMode)
                {
                    active = true;
                }
                ValidDisplayModes.Add(res);
            }

            disableColors.ColorWriteChannels = ColorWriteChannels.Alpha;

            WireframeRasterizerState = new RasterizerState();
            WireframeRasterizerState.FillMode = FillMode.WireFrame;
            WireframeRasterizerState.CullMode = CullMode.CullClockwiseFace;

            WireframeRasterizerStateScissor = new RasterizerState();
            WireframeRasterizerStateScissor.FillMode = FillMode.WireFrame;
            WireframeRasterizerStateScissor.CullMode = CullMode.CullClockwiseFace;
            WireframeRasterizerStateScissor.ScissorTestEnable = true;

            WorldTextureSamplerState = new SamplerState()
            {
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                Filter = TextureFilter.Anisotropic,
                MipMapLevelOfDetailBias = -0.1f
            };
            LightmapTextureSamplerState = SamplerState.LinearClamp;

            ShaderQuality = QualityLevel.High;
            ShadowQuality = QualityLevel.High;
            TextureQuality = QualityLevel.High;
            ReflectionQuality = QualityLevel.High;
            LODQuality = QualityLevel.High;
        }
        public static void CreateListedOptions()
        {
            GameSettings.RegisterOption(OptionsTab.Video, new ComboBoxOption
            {
                OptionLabel = "Resolution",
                GameOption = "screenSize",
                Items = ValidDisplayModes.Select(m => $"{m.Width}x{m.Height}").ToArray(),
                GetCurrentIndex = () => ValidDisplayModes.IndexOf(ActiveDisplayMode),
                OnSelectionChanged = i => ChangeDisplayMode(ValidDisplayModes[i])
            });

            GameSettings.RegisterOption(OptionsTab.Video, new ComboBoxOption
            {
                OptionLabel = "Anti-Aliasing",
                GameOption = "aaMode",
                Items = new[] { "None", "2x MSAA", "4x MSAA", "8x MSAA" },
                GetCurrentIndex = () => aaCount switch { 2 => 1, 4 => 2, 8 => 3, _ => 0 },
                OnSelectionChanged = i =>
                {
                    switch (i)
                    {
                        case 0: ChangeAAMode(false, 0); break;
                        case 1: ChangeAAMode(true, 2); break;
                        case 2: ChangeAAMode(true, 4); break;
                        case 3: ChangeAAMode(true, 8); break;
                    }
                }
            });

            GameSettings.RegisterOption(OptionsTab.Video, new ComboBoxOption
            {
                OptionLabel = "Shadow Quality",
                GameOption = "shadowQual",
                Items = new[] { "Low", "Medium", "High" },
                GetCurrentIndex = () => (int)ShadowQuality,
                OnSelectionChanged = i => ChangeShadowQuality((QualityLevel)i)
            });

            GameSettings.RegisterOption(OptionsTab.Video, new ComboBoxOption
            {
                OptionLabel = "Shader Quality",
                GameOption = "shaderQual",
                Items = new[] { "Low", "Medium", "High" },
                GetCurrentIndex = () => (int)ShaderQuality,
                OnSelectionChanged = i => ChangeShaderQuality((QualityLevel)i)
            });

            GameSettings.RegisterOption(OptionsTab.Video, new ComboBoxOption
            {
                OptionLabel = "Texture Quality",
                GameOption = "textureQual",
                Items = new[] { "Low", "Medium", "High" },
                GetCurrentIndex = () => (int)TextureQuality,
                OnSelectionChanged = i => ChangeTextureQuality((QualityLevel)i)
            });

            GameSettings.RegisterOption(OptionsTab.Video, new ComboBoxOption
            {
                OptionLabel = "Model Quality",
                GameOption = "lodBias",
                Items = new[] { "Low", "Medium", "High" },
                GetCurrentIndex = () => (int)LODQuality,
                OnSelectionChanged = i => ChangeModelQuality((QualityLevel)i)
            });

            GameSettings.RegisterOption(OptionsTab.Video, new ComboBoxOption
            {
                OptionLabel = "Reflection Quality",
                GameOption = "reflectQual",
                Items = new[] { "Low", "Medium", "High" },
                GetCurrentIndex = () => (int)ReflectionQuality,
                OnSelectionChanged = i => ChangeReflectionQuality((QualityLevel)i)
            });

            GameSettings.RegisterOption(OptionsTab.Video, new ToggleOption
            {
                OptionLabel = "V-Sync & Frame Cap",
                GameOption = "verticalSync",
                OnLabel = "On",
                OffLabel = "Off",
                GetCurrentValue = () => GameSettings.Settings.TryGetValue("verticalSync", out var v)
                                        && v is bool b && b,
                OnChanged = val => ChangeVsync(val)
            });

            GameSettings.RegisterOption(OptionsTab.Video, new ToggleOption
            {
                OptionLabel = "Windowed Mode",
                GameOption = "windowedMode",
                OnLabel = "On",
                OffLabel = "Off",
                GetCurrentValue = () => GameSettings.Settings.TryGetValue("windowedMode", out var v)
                                        && v is bool b && b,
                OnChanged = val =>
                {
                    //if(val != (bool)Convert.ChangeType(GameSettings.Settings["windowedMode"],typeof(bool)) && val == false)
                    //{
                    //    ChangeDisplayMode(GraphicsAdapter.DefaultAdapter.CurrentDisplayMode);
                    //}

                    Instance.IsFullscreen = !val;
                    GameSettings.Settings["windowedMode"] = val;
                }
            });
        }
        /// <summary>
        /// Initializes all rendering variables, to be run in the Initialize function of the engine. Loads textures, initializes render targets, etc.
        /// </summary>
        public static void InitRenderEngine()
        {
            WhiteTexture  = new Texture2D(Instance.GraphicsDevice, 1, 1);
            DimTexture    = new Texture2D(Instance.GraphicsDevice, 1, 1);
            BlackTexture  = new Texture2D(Instance.GraphicsDevice, 1, 1);
            BlankSpecTexture = new Texture2D(Instance.GraphicsDevice, 1,1);
            GreenTexture  = new Texture2D(Instance.GraphicsDevice, 1, 1);
            PurpleTexture = new Texture2D(Instance.GraphicsDevice, 1, 1);
            ErrorTexture = new Texture2D(Instance.GraphicsDevice, 16, 16);
            ActiveDisplayMode = Instance.GraphicsDevice.Adapter.CurrentDisplayMode;

            WhiteTexture .SetData(0, new Rectangle(0, 0, 1, 1), new Color[1] { Color.White }, 0, 1);
            DimTexture   .SetData(0, new Rectangle(0, 0, 1, 1), new Color[1] { new Color(200, 200, 200) }, 0, 1);
            BlackTexture .SetData(0, new Rectangle(0, 0, 1, 1), new Color[1] { Color.Black }, 0, 1);
            GreenTexture .SetData(0, new Rectangle(0, 0, 1, 1), new Color[1] { Color.Green }, 0, 1);
            PurpleTexture.SetData(0, new Rectangle(0, 0, 1, 1), new Color[1] { Color.Purple }, 0, 1);
            BlankSpecTexture.SetData(0, new Rectangle(0, 0, 1, 1), new Color[1] { new Color(Color.Black, 0f) }, 0, 1);

            var errorColors = new Color[16*16];

            for(int x = 0; x < 16; x++)
            {
                for (int y = 0; y < 16; y++)
                {
                    errorColors[y * 16 + x] = Color.Black;
                    if ((x / 8 + y / 8) % 2 == 0) errorColors[y * 16 + x] = Color.HotPink;
                }
            }
            ErrorTexture.SetData(errorColors, 0, errorColors.Length);

            decalState = new DepthStencilState
            {
                DepthBufferEnable = true,
                DepthBufferWriteEnable = false,
                DepthBufferFunction = CompareFunction.LessEqual,
            };

            RecalculateRenderTargets();

            RebuildDisplay();
            basicShader = new BasicEffect(MainEngine.Instance.GraphicsDevice);
            worldRasterizer = new RasterizerState();

            FontSystem = new FontSystem();
            FontSystem.AddFont(File.ReadAllBytes($"{Instance.Content.RootDirectory}/Fonts/default.ttf"));
        }
        public static void OnMaterialsMounted()
        {
            List<ShaderHandle> shaders = new List<ShaderHandle>() { Instance.WorldShader };
            for (int i = 0; i < GlobalMapData.LoadedMaterials.Length; i++)
            {
                string shaderName = GlobalMapData.LoadedMaterials[i].ShaderName;

                if (string.IsNullOrEmpty(shaderName))
                {
                    GlobalMapData.LoadedMaterials[i].Shader = Instance.WorldShader;
                }
                else
                {
                    // GLSL preferred, falls back to the legacy .fx path - see AssetManager.LoadMaterialShader.
                    ShaderHandle materialShader = AssetManager.LoadMaterialShader(shaderName);
                    GlobalMapData.LoadedMaterials[i].Shader = materialShader;
                    if (!shaders.Contains(materialShader)) shaders.Add(materialShader);
                }
            }

            LoadedWorldShaders = shaders.ToArray();
        }

        public static void ApplyMaterialTechnique(Material material, ShaderHandle shader)
        {
            shader.SetTechnique(techniqueNames[(int)ShaderQuality * 2 + (material.AlphaClip ? 1 : 0)]);
        }

        /// <summary>
        /// Change the active display mode, we assume that <param name="mode"/> is a supported <see cref="DisplayMode"/> by this monitor.
        /// </summary>
        /// <param name="mode">The <see cref="DisplayMode"/> to switch to.</param>
        public static void ChangeDisplayMode(DisplayMode mode)
        {
            needsDisplayRebuild = true;
            ActiveDisplayMode = mode;
            GameSettings.Settings["screenSize"] = (Int64)(RenderEngine.ValidDisplayModes.FindIndex(e => e == ActiveDisplayMode));
        }

        public static void WindowResized(int width, int height)
        {
            Width = width;
            Height = height;

            RecalculateRenderTargets();
        }

        /// <summary>
        /// Changes the AntiAliasing mode for the renderer.
        /// </summary>
        /// <param name="enabled"></param>
        /// <param name="count"></param>
        public static void ChangeAAMode(bool enabled, int count)
        {
            needsDisplayRebuild = true;
            aaEnabled = enabled;
            aaCount = count;
            GameSettings.Settings["aaMode"] = (Int64)count;
        }

        /// <summary>
        /// Changes the quality of the render-target shadows.
        /// </summary>
        /// <param name="level"></param>
        public static void ChangeShadowQuality(QualityLevel level)
        {
            ShadowQuality = level;
            GameSettings.Settings["shadowQual"] = (Int64)level;

            switch(ShadowQuality)
            {
                case QualityLevel.Low:

                    Displayable.ShadowQualityBias = 2;
                    break;
                case QualityLevel.Medium:

                    Displayable.ShadowQualityBias = 1;
                    break;
                case QualityLevel.High:

                    Displayable.ShadowQualityBias = 0;
                    break;
            }
        }
        /// <summary>
        /// Changes the quality of the render-target shadows.
        /// </summary>
        /// <param name="level"></param>
        public static void ChangeShaderQuality(QualityLevel level)
        {
            ShaderQuality = level;
            GameSettings.Settings["shaderQual"] = (Int64)level;

            foreach (var shader in LoadedWorldShaders)
            {
                switch (ShaderQuality)
                {
                    case QualityLevel.Low:

                        shader.SetTechnique("Low");
                        break;
                    case QualityLevel.Medium:

                        shader.SetTechnique("Med");
                        break;
                    case QualityLevel.High:

                        shader.SetTechnique("High");
                        break;
                }
            }
        }
        /// <summary>
        /// Changes the quality of the textures.
        /// </summary>
        /// <param name="level"></param>
        public static void ChangeTextureQuality(QualityLevel level)
        {
            TextureQuality = level;
            GameSettings.Settings["textureQual"] = (Int64)level;

            TextureMipGenerator.ReloadActiveTextures();
        }
        /// <summary>
        /// Changes the quality of the models.
        /// </summary>
        /// <param name="level"></param>
        public static void ChangeModelQuality(QualityLevel level)
        {
            LODQuality = level;
            GameSettings.Settings["lodBias"] = (Int64)level;

            // quality is backwards (0 = low)
            Instance.Console.Execute($"{LODBias.CommandName} {2-((int)LODQuality)}");
        }
        /// <summary>
        /// Changed the quality of planar reflections.
        /// </summary>
        /// <param name="level"></param>
        public static void ChangeReflectionQuality(QualityLevel level)
        {
            ReflectionQuality = level;
            GameSettings.Settings["reflectionQual"] = (Int64)level;
        }

        public static void ChangeVsync(bool val)
        {
            GameSettings.Settings["verticalSync"] = val;
            if (val)
            {
                int maxRefresh = SDLDisplay.GetMaxRefreshRateForCurrentResolution();
                GraphicsDeviceManager.SynchronizeWithVerticalRetrace = true;
                Instance.IsFixedTimeStep = true;

                Instance.TargetElapsedTime = TimeSpan.FromSeconds(1.0 / int.Min(240,maxRefresh * 2));
                GraphicsDeviceManager.ApplyChanges();
            }
            else
            {
                int maxRefresh = SDLDisplay.GetMaxRefreshRateForCurrentResolution();
                GraphicsDeviceManager.SynchronizeWithVerticalRetrace = false;
                Instance.IsFixedTimeStep = false;
                GraphicsDeviceManager.ApplyChanges();
            }
        }


        /// <summary>
        /// Rebuilds the display based on the active <see cref="DisplayMode"/>.
        /// </summary>
        public static void RebuildDisplay()
        {
            Width = ActiveDisplayMode.Width;
            Height = ActiveDisplayMode.Height;

            GraphicsDeviceManager.PreferredBackBufferHeight = Height;
            GraphicsDeviceManager.PreferredBackBufferWidth = Width;

            GraphicsDeviceManager.HardwareModeSwitch = Instance.IsFullscreen;
            GraphicsDeviceManager.IsFullScreen = Instance.IsFullscreen;

            // MSAA is done for the screen textures already
            GraphicsDeviceManager.PreferMultiSampling = false; 
            GraphicsDeviceManager.GraphicsDevice.PresentationParameters.MultiSampleCount = 0;

            RecalculateRenderTargets();

            GraphicsDeviceManager.ApplyChanges();

            needsDisplayRebuild = false;
        }
        /// <summary>
        /// Recomputes the <see cref="RenderTarget2D"/> associated with the final image displayed on screen.
        /// </summary>
        public static void RecalculateRenderTargets()
        {
            ScreenRenderTexture = new RenderTarget2D(Instance.GraphicsDevice, (int)(Width * ResolutionScalar), (int)(Height * ResolutionScalar), false, SurfaceFormat.HdrBlendable, DepthFormat.Depth24, aaCount, RenderTargetUsage.PlatformContents);
            reflectionRenderTexture = new RenderTarget2D(Instance.GraphicsDevice, (int)(Width * ResolutionScalar), (int)(Height * ResolutionScalar), false, SurfaceFormat.HdrBlendable, DepthFormat.Depth24, 0, RenderTargetUsage.PlatformContents);
            refractionRenderTexture = new RenderTarget2D(Instance.GraphicsDevice, (int)(Width * ResolutionScalar), (int)(Height * ResolutionScalar), false, SurfaceFormat.HdrBlendable, DepthFormat.None, 0, RenderTargetUsage.PlatformContents);

            PixelSize = new Vector2
            {
                X = (1f / (Width)),
                Y = (1f / (Height))
            };
        }

        public static Texture2D TakeScreenshot(int? widthOverride = null, int? heightOverride = null)
        {
            int w, h;
            w = widthOverride ?? Instance.GraphicsDevice.PresentationParameters.BackBufferWidth;
            h = heightOverride ?? Instance.GraphicsDevice.PresentationParameters.BackBufferHeight;
            RenderTarget2D screenshot;
            screenshot = new RenderTarget2D(Instance.GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            Instance.GraphicsDevice.SetRenderTarget(screenshot);
            Instance.SpriteBatch.Begin();
            Instance.SpriteBatch.Draw(ScreenRenderTexture,new Rectangle(0,0,w,h),Color.White);
            Instance.SpriteBatch.End();
            Instance.GraphicsDevice.SetRenderTarget(null);
            return screenshot;
        }

        /// <summary>
        /// Renders the debug IMGUI overlay, completely optional, but very handy.
        /// </summary>
        public static void RenderDebugUI()
        {
            if (ShowBSPTree)
            {
                Ray ray = new Ray(CameraPosition, CameraForward);
                var world = Collision.CastBSPWorld(ref ray, 128f);
                //var ent = Collision.CastEntity(ref ray, Vector3.Distance(world.point, ray.Position), out _);

                bool brush = world.Hit;

                ImGui.Begin("Looking At", ImGuiWindowFlags.AlwaysAutoResize);

                if (brush)
                {
                    ImGui.Text($"BSP Hit Position (int):{Vector3.Floor(world.Point)}");
                    ImGui.Text($"BSP Hit info: [brush={BSPRoot.Nodes[world.Node].brush}] [sky={BSPRoot.Nodes[world.Node].nodeFlag == BSPNode.SkyboxNode}]");
                }

                ImGui.End();
            }
            if (ShowNodes)
            {
                ImGui.Begin("Node Data", ImGuiWindowFlags.AlwaysAutoResize);

                int node = AINodeUtils.FindClosestNode(CameraPosition);
                ImGui.Text($"Camera Node: {node}");

                ImGui.End();
            }
            if (portalLines != 0)
            {
                var node = BSPRoot.Traverse(CameraPosition);

                ImGui.Begin("Standing At", ImGuiWindowFlags.AlwaysAutoResize);

                if (!LoadedMapHasVis)
                {
                    ImGui.Text($"BSP node (int):{node}");

                    ImGui.End();
                    return;
                }

                int local = Array.FindIndex(VisRoot.VisLeaves, l => l.BspLeafID == node);

                if (local == -1)
                {
                    ImGui.Text($"BSP node (int):{node}");

                    ImGui.End();
                    return;
                }

                ImGui.Text($"Solid?:{BSPRoot.Nodes[node].solid}");
                ImGui.Text($"Visleaf (int):{local}");

                foreach (var pID in VisRoot.VisLeaves[local].Portals)
                {
                    if (pID == -1) continue;
                    if (!(currentPortalView == -1 || pID == VisRoot.VisLeaves[local].Portals[int.Min(currentPortalView, VisRoot.VisLeaves[local].Portals.Length - 1)])) continue;

                    var portal = VisRoot.VisPortals[pID];
                    ImGui.Text($"Portal: front leaf {portal.LeafFront}, back leaf {portal.LeafBack}");
                }

                ImGui.End();
            }
            if (ShowFPS)
            {
                ImGui.Begin("FPS", ImGuiWindowFlags.AlwaysAutoResize);

                fpsTracker.DrawFps(Vector2.One*24,Color.White);

                ImGui.End();
            }
            if (ShowTimings)
            {
                if (!ShowFPS) fpsTracker.UpdateFPS();

                ImGui.Begin("Render Timings", ImGuiWindowFlags.AlwaysAutoResize);
                RenderTimings.DrawImGui(fpsTracker.lastFps);
                ImGui.End();
            }
        }
        /// <summary>
        /// Called after a map is loaded. No need to call this yourself.
        /// </summary>
        public static void OnMapLoaded()
        {
            sphericalHarmonicsVisualizer = new SphericalHarmonicsVisualizer();

            reflectorGroups.Clear();
            faceToGroupLookup.Clear();

            ClearLeafGeometryCaches();
        }

        public static void RegisterPlanarReflectors()
        {
            foreach (var entity in EntityManager.entities.GetValues())
            {
                if (entity is not BrushEntity brushEntity)
                {
                    continue;
                }
                bool needsDeferredRender = false; 
                foreach (var brushIndex in brushEntity.brushSet)
                {
                    ref var brush = ref GlobalMapData.ActiveMap.Brushes[brushIndex];

                    var offset = Vector3.Transform(brush.Position - brushEntity.SpawnAnchor, brushEntity.WorldRotation);
                    Matrix brushTranslation = Matrix.CreateTranslation(brushEntity.WorldPosition + offset);

                    BoundingBox localSpaceBounds = new BoundingBox(
                        GlobalMapData.ActiveMap.BrushBounds[brushIndex].Min - brush.Position,
                        GlobalMapData.ActiveMap.BrushBounds[brushIndex].Max - brush.Position);

                    for (int f = 0; f < brush.Faces.Length; f++)
                    {
                        ref var face = ref brush.Faces[f];

                        if (face.Surface >= GlobalMapData.LoadedMaterials.Length)
                        {
                            continue;
                        }

                        var material = GlobalMapData.LoadedMaterials[face.Surface];
                        bool wantsReflection = material.GetFlag("receivePlanarReflection");
                        bool wantsRefraction = material.GetFlag("receiveRefractionTexture");

                        if (wantsReflection || wantsRefraction)
                        {
                            needsDeferredRender = true;
                        }
                        if (wantsReflection)
                        {
                            Vector3 worldPoint = Vector3.Transform(brush.Vertices[face.Indices[0]], brushTranslation);
                            Vector3 worldNormal = face.Normal;
                            Plane worldPlane = new Plane(worldPoint, worldNormal);
                            BoundingBox worldBounds = TransformBoundingBox(localSpaceBounds, brushTranslation);

                            AddFaceToGroup(brushIndex, f, worldPlane, worldBounds);
                        }
                    }
                }
                brushEntity.DeferRenderTillLast = needsDeferredRender;
            }
        }
        private static void AddFaceToGroup(int brushIndex, int faceIndex, Plane plane, BoundingBox bounds)
        {
            const float normalTolerance = 0.999f;
            const float distanceTolerance = 4f;

            for (int g = 0; g < reflectorGroups.Count; g++)
            {
                var group = reflectorGroups[g];
                float normalDot = Vector3.Dot(group.ReflectionPlane.Normal, plane.Normal);
                float distanceDelta = MathF.Abs(group.ReflectionPlane.D - plane.D);

                if (normalDot >= normalTolerance && distanceDelta <= distanceTolerance)
                {
                    group.Faces.Add(new PlanarReflectorFace { BrushIndex = brushIndex, FaceIndex = faceIndex });
                    group.Bounds = BoundingBox.CreateMerged(group.Bounds, bounds);
                    faceToGroupLookup[PackFaceKey(brushIndex, faceIndex)] = g;
                    return;
                }
            }

            var newGroup = new PlanarReflectorGroup
            {
                ReflectionPlane = plane,
                Bounds = bounds,
            };
            newGroup.Faces.Add(new PlanarReflectorFace { BrushIndex = brushIndex, FaceIndex = faceIndex });

            reflectorGroups.Add(newGroup);
            faceToGroupLookup[PackFaceKey(brushIndex, faceIndex)] = reflectorGroups.Count - 1;
        }

        private static void EnsureReflectionTarget(PlanarReflectorGroup group)
        {
            float scalar = ReflectionQuality switch
            {
                QualityLevel.High => 0.5f,
                _ => 0.25f
            };
            int targetWidth = Math.Max(1, (int)(Width * ResolutionScalar * scalar));
            int targetHeight = Math.Max(1, (int)(Height * ResolutionScalar * scalar));

            if (group.ReflectionTarget != null && !group.ReflectionTarget.IsDisposed &&
                group.ReflectionTarget.Width == targetWidth && group.ReflectionTarget.Height == targetHeight)
                return;

            group.ReflectionTarget?.Dispose();
            group.ReflectionTarget = new RenderTarget2D(Instance.GraphicsDevice, targetWidth, targetHeight, false, SurfaceFormat.Color, DepthFormat.Depth24, 0, RenderTargetUsage.PlatformContents);
        }
        private static BoundingBox TransformBoundingBox(BoundingBox box, Matrix transform)
        {
            Vector3[] corners = box.GetCorners();
            Vector3 min = Vector3.Transform(corners[0], transform);
            Vector3 max = min;

            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 transformed = Vector3.Transform(corners[i], transform);
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }

            return new BoundingBox(min, max);
        }

        /// <summary>
        /// Handles any action that we have queued up.
        /// </summary>
        public static void Handle()
        {
            if (needsDisplayRebuild)
            {
                RebuildDisplay();
            }

            totalTime += MainEngine.PreviousFrameDelta;

            CameraControl.Evaluate();

            CModelDisplay.LODOffset = LODBias;

            if (cameraPreviousCubeBlend > 0) cameraPreviousCubeBlend -= MainEngine.PreviousFrameDelta;
            else cameraPreviousCubeBlend = 0;
        }
        /// <summary>
        /// Preps the engine for rendering the world later
        /// </summary>
        public static void PrepareRenderEngine()
        {
            Instance.GraphicsDevice.SamplerStates[0] = WorldTextureSamplerState;
            Instance.GraphicsDevice.SamplerStates[1] = WorldTextureSamplerState;
            PrepareWorldShaders();
        }
        public static void PrepareWorldShaders()
        {
            foreach (var shader in LoadedWorldShaders)
            {
                shader.Param("cubeSamplePos").SetValue(CameraPosition);
                shader.Param("View").SetValue(ViewMatrix);
                shader.Param("Projection").SetValue(ProjectionMatrix);
                shader.Param("InverseView").SetValue(Matrix.Invert(ViewMatrix));
                shader.Param("InverseProjection").SetValue(Matrix.Invert(ProjectionMatrix));
                shader.Param("screenSize").SetValue(Instance.GraphicsDevice.Viewport.Bounds.Size.ToVector2());
                shader.Param("time").SetValue(totalTime);
                shader.Param("sunDir").SetValue(MainEngine.Instance.DirectionalLightDirection);
                shader.Param("sunColor").SetValue(MainEngine.Instance.DirectionalLightColor.ToVector3());
            }
        }

        private static void EnsurePostFXQuad(GraphicsDevice gd)
        {
            if (postFXQuad != null) return;
            var verts = new[]
            {
                new VertexPositionTexture(new Vector3(-1, -1, 0), new Vector2(0, 1)),
                new VertexPositionTexture(new Vector3(1, -1, 0), new Vector2(1, 1)),
                new VertexPositionTexture(new Vector3(-1, 1, 0), new Vector2(0, 0)),
                new VertexPositionTexture(new Vector3(-1, 1, 0), new Vector2(0, 0)),
                new VertexPositionTexture(new Vector3(1, -1, 0), new Vector2(1, 1)),
                new VertexPositionTexture(new Vector3(1, 1, 0), new Vector2(1, 0)),
            };
            postFXQuad = new VertexBuffer(gd, typeof(VertexPositionTexture), 6, BufferUsage.WriteOnly);
            postFXQuad.SetData(verts);
        }
        /// <summary>
        /// Displays the final image to the screen.
        /// </summary>
        public static void DisplayImageToScreen()
        {
            Instance.GraphicsDevice.SetRenderTarget(null);

            var shader = Instance.PostProcessingShader;
            shader.Param("ScreenTexture").SetValue(ScreenRenderTexture);
            shader.Param("DepthTexture").SetValue(ScreenRenderTexture.DepthTexture);

            EnsurePostFXQuad(Instance.GraphicsDevice);
            Instance.GraphicsDevice.SetVertexBuffer(postFXQuad);
            Instance.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            Instance.GraphicsDevice.BlendState = BlendState.Opaque;
            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.None;
            Instance.GraphicsDevice.SamplerStates[0] = ScreenTextureSamplerState;
            Instance.GraphicsDevice.Viewport = new Viewport(Instance.GraphicsDevice.PresentationParameters.Bounds);

            shader.RenderEachPass(() => Instance.GraphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, 2));
        }

        private static void RenderToRefraction()
        {
            Instance.GraphicsDevice.SetRenderTarget(refractionRenderTexture);

            Instance.SpriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, ScreenTextureSamplerState, DepthStencilState.None, RasterizerState.CullNone);
            Instance.SpriteBatch.Draw(ScreenRenderTexture, Instance.GraphicsDevice.Viewport.Bounds, Color.White);
            Instance.SpriteBatch.End();

            Instance.GraphicsDevice.SetRenderTargets(ScreenRenderTexture);
        }
        /// <summary>
        /// Render the world, entities, everything.
        /// </summary>
        /// <param name="time">Needed by other functions called here that are rooted in MonoGame itself, not very important.</param>
        public static void Render(GameTime time)
        {
            LightGroupRuntime.Update(Instance.GraphicsDevice, Instance.SpriteBatch, time);
            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            Instance.GraphicsDevice.BlendState = BlendState.Opaque;

            EntityManager.BeforeRenderEntities(time);

            if (ShowFPS || ShowTimings)
                fpsTracker.Update(time);

            if (ShowTimings)
                RenderTimings.Tick(time);

            if (Instance.IsMapLoaded)
            {
                if (cubemapsNeedCapture)
                {
                    if (cubemapCaptureDelay > 0)
                    {
                        cubemapCaptureDelay--;
                    }
                    else
                    {
                        CaptureCubemaps(time);
                    }
                }

                using (RenderTimings.Section(TimingSection.PlanarReflections))
                {
                    RenderPlanarReflections(time);
                }
            }

            Instance.GraphicsDevice.SetRenderTargets(ScreenRenderTexture);
            Instance.GraphicsDevice.Clear(ClearOptions.DepthBuffer | ClearOptions.Target, Color.Transparent, Instance.GraphicsDevice.Viewport.MaxDepth, 0);

            if (Instance.IsLoading && !Instance.splashScreen.IsDisposed)
            {
                return;
            }

            basicShader.World = (RenderEngine.WorldMatrix);
            basicShader.View = (RenderEngine.ViewMatrix);
            basicShader.Projection = (RenderEngine.ProjectionMatrix);

            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            Instance.GraphicsDevice.RasterizerState = RasterizerState.CullClockwise;

            if (Instance.IsMapLoaded && GlobalMapData.ActiveMap.Brushes?.Length > 0)
            {
                Instance.GraphicsDevice.BlendState = nonPremultiplied;
               
                const float skyboxSize = 1 / 16f;
                if (CurrentWireframeDisplayMode >= 3)
                {
                    Instance.GraphicsDevice.Clear(ClearOptions.DepthBuffer | ClearOptions.Target, Color.Black, Instance.GraphicsDevice.Viewport.MaxDepth, 0);
                }

                RenderWorld(time, CameraPosition, renderDecalsAndParticles: true, flipWinding: false, drawSkybox: true, allow3DSkybox: true, ref skyboxWasVisible);

                //PortalCulling.ClearDebugScreenLines();

                using (RenderTimings.Section(TimingSection.RenderToRefraction))
                {
                    RenderToRefraction();
                }

                Instance.GraphicsDevice.SamplerStates[0] = WorldTextureSamplerState;
                Instance.GraphicsDevice.SamplerStates[1] = WorldTextureSamplerState;
                Instance.GraphicsDevice.SamplerStates[2] = WorldTextureSamplerState;
                Instance.GraphicsDevice.SamplerStates[3] = LightmapTextureSamplerState;
                Instance.GraphicsDevice.SamplerStates[4] = LightmapTextureSamplerState;
                Instance.GraphicsDevice.SamplerStates[5] = LightmapTextureSamplerState;
                Instance.GraphicsDevice.SamplerStates[6] = LightmapTextureSamplerState;

                Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                Instance.GraphicsDevice.BlendState = nonPremultiplied;
                Instance.GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;

                using (RenderTimings.Section(TimingSection.Entities))
                {
                    EntityManager.RenderEntities(time, true);
                }

                using (RenderTimings.Section(TimingSection.TransparentModels))
                {
                    TransparentRenderQueue.RenderAll();
                }

                Instance.GraphicsDevice.RasterizerState = RasterizerState.CullClockwise;
                Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            }

            Instance.WorldShader.Param("World").SetValue(WorldMatrix);
            if (ShowPhysics)
            {
                basicShader.VertexColorEnabled = true;
                basicShader.LightingEnabled = false;
                basicShader.Alpha = 1;
            }
            if (ShowHitboxes)
            {
                Instance.WorldShader.Param("DisableLighting").SetValue(true);
                Instance.WorldShader.Param("BrushTex").SetValue(WhiteTexture);
                foreach (var entity in EntityManager.entities.GetValues())
                {
                    if (entity == null) continue;

                    if (entity.PhysicsBody != null && !entity.PhysicsBody.IsActive)
                    {
                        Instance.WorldShader.Param("BrushTex").SetValue(GreenTexture);
                    }

                    DrawBox(entity.OrientedBounds, false);
                    Instance.WorldShader.Param("BrushTex").SetValue(WhiteTexture);
                }
                Instance.GraphicsDevice.SetVertexBuffer(null);
                if (GlobalMapData.ActiveMap.Terrains != null)
                {
                    for (int t = 0; t < GlobalMapData.ActiveMap.Terrains.Length; t++)
                    {
                        DrawBox(GlobalMapData.ActiveMap.Terrains[t].Bounds);
                    }
                }
            }
            if (ShowBSPTree)
            {
                Instance.WorldShader.Param("DisableLighting").SetValue(true);
                List<VertexPosition> vertices = new List<VertexPosition>();

                Instance.WorldShader.Param("BrushTex").SetValue(WhiteTexture);

                var hit = BSPRoot.TraceRay(new Ray(CameraPosition, CameraForward), 50f);

                Stack<BSPNode> stack = new Stack<BSPNode>();
                stack.Push(BSPRoot.Nodes[hit.Node]);

                vertices.Add(new VertexPosition(hit.Point - Vector3.Up * 0.1f));
                vertices.Add(new VertexPosition(hit.Point + Vector3.Up * 0.1f));
                vertices.Add(new VertexPosition(hit.Point - Vector3.Right * 0.1f));
                vertices.Add(new VertexPosition(hit.Point + Vector3.Right * 0.1f));
                vertices.Add(new VertexPosition(hit.Point - Vector3.Forward * 0.1f));
                vertices.Add(new VertexPosition(hit.Point + Vector3.Forward * 0.1f));

                //while (stack.Count > 0)
                //{
                //    var node = stack.Pop();

                //    if (node.split)
                //    {
                //        var face = GlobalMapData.activeMap.brushes[node.brush].faces[node.face];
                //        var brush = GlobalMapData.activeMap.brushes[node.brush];
                //        Vector3 faceCenter = (brush.vertices[face.indices[0]] + brush.vertices[face.indices[1]] + brush.vertices[face.indices[2]] + brush.vertices[face.indices[3]]) / 4 + brush.position;

                //        vertices.Add(new VertexPosition(faceCenter));
                //        vertices.Add(new VertexPosition(node.splittingPlane.Normal * 0.25f + faceCenter));
                //        vertices.Add(new VertexPosition(faceCenter));
                //        vertices.Add(new VertexPosition(face.tangent * 0.25f + faceCenter));
                //        vertices.Add(new VertexPosition(faceCenter));
                //        vertices.Add(new VertexPosition(face.binormal * 0.25f + faceCenter));
                //    }

                //    if (node.parent == 0) break;

                //    stack.Push(BSPRoot.nodes[node.parent]);
                //}

                debugBuffer = new VertexBuffer(Instance.GraphicsDevice, typeof(VertexPosition), vertices.Count, BufferUsage.WriteOnly);
                debugBuffer.SetData(vertices.ToArray());

                Instance.GraphicsDevice.SetVertexBuffer(debugBuffer);
                Instance.WorldShader.ApplyPass(0);
                Instance.GraphicsDevice.DrawPrimitives(PrimitiveType.LineList, 0, vertices.Count / 2);
                Instance.GraphicsDevice.SetVertexBuffer(null);
            }
            if (DrawLightnodes)
            {
                Instance.GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
                //Instance.WorldShader.Param("DisableLighting").SetValue(true);
                var nodeBundle = LightNodeTraversal.GetClosestNodeBundle(CameraPosition);
                LightNodeBundle.LightNode currentNode = nodeBundle.Traverse(CMath.ClampToBoundingBox(CameraPosition, nodeBundle.Box));
                foreach (LightNodeBundle.LightNode node in nodeBundle.Children)
                {
                    float expansion = node.Pos == currentNode.Pos ? 0.2f : 0.05f;

                    sphericalHarmonicsVisualizer?.DrawSphere(node.Pos);

                    //var verts = Collision.GetDebugEdges(new BoundingBox(node.pos - Vector3.One * expansion, node.pos + Vector3.One * expansion));
                    //debugBuffer = new VertexBuffer(Instance.GraphicsDevice, typeof(VertexPosition), verts.Length, BufferUsage.WriteOnly);
                    //debugBuffer.SetData(verts);

                    //Instance.GraphicsDevice.SetVertexBuffer(debugBuffer);

                    //Instance.WorldShader.Param("BrushTex").SetValue(RenderEngine.WhiteTexture);

                    //foreach (var pass in Instance.WorldShader.CurrentTechnique.Passes)
                    //{
                    //    pass.Apply();
                    //    Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, verts, 0, verts.Length / 2);
                    //}
                    //Instance.GraphicsDevice.SetVertexBuffer(null);
                }
            }
            if (ShowNodes)
            {
                Instance.WorldShader.Param("DisableLighting").SetValue(true);
                List<VertexPosition> vertices = new List<VertexPosition>();

                Instance.WorldShader.Param("BrushTex").SetValue(BlackTexture);

                for (int i = 0; i < GlobalMapData.ActiveMap.Nodegraph.Nodes.Length; i++)
                {
                    var nodepos = GlobalMapData.ActiveMap.Nodegraph.Nodes[i].Position;
                    var verts = CMath.GetDebugEdges(new BoundingBox(nodepos - Vector3.One * 0.05f, nodepos + Vector3.One * 0.05f));
                    vertices.AddRange(verts);
                    for (int j = 0; j < GlobalMapData.ActiveMap.Nodegraph.Nodes[i].Connections.Length; j++)
                    {
                        vertices.Add(new VertexPosition(GlobalMapData.ActiveMap.Nodegraph.Nodes[i].Position + Vector3.Up * 0.01f));
                        vertices.Add(new VertexPosition(GlobalMapData.ActiveMap.Nodegraph.Nodes[GlobalMapData.ActiveMap.Nodegraph.Nodes[i].Connections[j]].Position + Vector3.Up * 0.01f));
                    }
                }
                if(vertices.Count > 0)
                {
                    Instance.WorldShader.RenderEachPass(() =>
                        Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, vertices.ToArray(), 0, vertices.Count / 2));
                }
            }
            if (ShowOctree)
            {
                var camnode = OctreeRoot.AllNodes[GlobalMapData.ActiveMap.Root.Traverse(CameraPosition)];

                Instance.WorldShader.Param("DisableLighting").SetValue(true);

                //Render all nodes
                Instance.WorldShader.Param("BrushTex").SetValue(WhiteTexture);

                List<VertexPosition> allNodes = new List<VertexPosition>();
                foreach (var node in OctreeRoot.AllNodes)
                {
                    if (node == camnode) continue;
                    allNodes.AddRange(CMath.GetDebugEdges(node.Box));
                }

                Instance.WorldShader.RenderEachPass(() =>
                    Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, allNodes.ToArray(), 0, allNodes.Count / 2));


                //Render camera node
                var verts = CMath.GetDebugEdges(camnode.Box);

                Instance.WorldShader.Param("BrushTex").SetValue(GreenTexture);

                Instance.WorldShader.RenderEachPass(() =>
                    Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, verts, 0, verts.Length / 2));
            }

            foreach(var point in DebugDrawPositions.GetValues())
            {
                Instance.WorldShader.Param("DisableLighting").SetValue(true);
                List<VertexPosition> vertices = new List<VertexPosition>();

                Instance.WorldShader.Param("BrushTex").SetValue(WhiteTexture);

                vertices.Add(new VertexPosition(point - Vector3.Up * 0.1f));
                vertices.Add(new VertexPosition(point + Vector3.Up * 0.1f));
                vertices.Add(new VertexPosition(point - Vector3.Right * 0.1f));
                vertices.Add(new VertexPosition(point + Vector3.Right * 0.1f));
                vertices.Add(new VertexPosition(point - Vector3.Forward * 0.1f));
                vertices.Add(new VertexPosition(point + Vector3.Forward * 0.1f));

                Instance.WorldShader.RenderEachPass(() =>
                    Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, vertices.ToArray(), 0, vertices.Count / 2));
            }
            foreach (var point in DebugDrawRays.GetValues())
            {
                Instance.WorldShader.Param("DisableLighting").SetValue(true);
                List<VertexPosition> vertices = new List<VertexPosition>();

                Instance.WorldShader.Param("BrushTex").SetValue(WhiteTexture);

                vertices.Add(new VertexPosition(point.a));
                vertices.Add(new VertexPosition(point.b));

                Instance.WorldShader.RenderEachPass(() =>
                    Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, vertices.ToArray(), 0, vertices.Count / 2));
            }

            if (MainEngine.DeveloperMode > 2)
            {
                Instance.SpriteBatch.Begin(blendState:BlendState.NonPremultiplied);
                foreach (var ent in EntityManager.entities.GetValues())
                {
                    StringBuilder sb = new StringBuilder();

                    if(!string.IsNullOrEmpty(ent.Name)) sb.AppendLine($"targetname: {ent.Name}");

                    if(ent.IsSimulated)
                    {
                        sb.AppendLine($"physics ent");
                        sb.AppendLine($"cur velocity: {ent.Velocity.X:0.00}, {ent.Velocity.Y:0.00}, {ent.Velocity.Z:0.00}");
                    }
                    else
                    {
                        sb.AppendLine($"static ent");
                    }
                    float alpha = 1-(float.Min(Vector3.Distance(ent.Position, CameraPosition), 20f) / 20f);
                    var pos = new Vector3(ent.Position.X, ent.Position.Y+1, ent.Position.Z);
                    DrawDebugString(pos, sb.ToString(),alpha:alpha);
                }
                Instance.SpriteBatch.End();
            }
        }
        private static void RenderWorld(GameTime time, Vector3 pvsOrigin, bool renderDecalsAndParticles, bool flipWinding, bool drawSkybox, bool allow3DSkybox, ref bool cachedSkyboxWasVisible, Matrix? skyboxProjection = null, bool renderEntities = true, bool? skyboxFlipWinding = null)
        {
            const float skyboxSize = 1 / 16f;
            bool skyFlip = skyboxFlipWinding ?? flipWinding;

            Displayable.FlipWinding = flipWinding;
            isWindingFlipped = flipWinding;

            Matrix skyCameraMatrix = Matrix.CreateTranslation(-SkyCamera.activeSkyCamera?.Position ?? Vector3.Zero) *
                                     Matrix.CreateWorld(ViewMatrix.Translation * skyboxSize, ViewMatrix.Forward, ViewMatrix.Up);

            BuildPVSLookups(pvsOrigin, false);
            if (allow3DSkybox) BuildPVSLookups(Matrix.Invert(skyCameraMatrix).Translation, true);

            cachedSkyboxWasVisible = skyboxWasVisible;

            foreach (var shader in LoadedWorldShaders)
            {
                shader.Param("cameraPos").SetValue(CameraPosition);
                shader.Param("cameraNear").SetValue(CameraNear);
                shader.Param("cameraFar").SetValue(CameraFar);
            }
            Instance.TerrainShader.Param("cameraPos").SetValue(CameraPosition);
            Instance.TerrainShader.Param("cameraNear")?.SetValue(CameraNear);
            Instance.TerrainShader.Param("cameraFar")?.SetValue(CameraFar);

            using (RenderTimings.Section(TimingSection.Skybox3DRender))
            {
                if (drawSkybox && cachedSkyboxWasVisible)
                {
                    if (CurrentWireframeDisplayMode < 3)
                    {
                        var oldRaster = Instance.GraphicsDevice.RasterizerState;
                        Instance.GraphicsDevice.RasterizerState = cullNoneSky;
                        Instance.GraphicsDevice.DepthStencilState = DepthStencilState.None;

                        Skybox.Draw(ViewMatrix, skyboxProjection ?? ProjectionMatrix);

                        Instance.GraphicsDevice.RasterizerState = oldRaster;
                        Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                    }

                    if (allow3DSkybox && SkyCamera.activeSkyCamera != null && Show3DSky)
                    {
                        Displayable.FlipWinding = skyFlip;
                        isWindingFlipped = skyFlip;

                        foreach (var shader in LoadedWorldShaders)
                        {
                            shader.Param("Skybox3DView").SetValue(true);
                            shader.Param("SkyboxViewTranslation").SetValue(SkyCamera.activeSkyCamera.Position);
                        }

                        Instance.TerrainShader.Param("Skybox3DView").SetValue(true);
                        Instance.TerrainShader.Param("SkyboxViewTranslation").SetValue(SkyCamera.activeSkyCamera.Position);

                        foreach (var shader in LoadedWorldShaders)
                        {
                            shader.Param("Projection").SetValue(ProjectionMatrix);
                            shader.Param("View").SetValue(skyCameraMatrix);
                        }

                        Instance.GraphicsDevice.RasterizerState = skyFlip ? cullCounterClockwiseSky : cullClockwiseSky;
                        Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;

                        // Pre-pass the depth so that we can render with depthequals
                        RenderMapDepth(true, Matrix.Invert(skyCameraMatrix).Translation);

                        Instance.TerrainShader.Param("Projection").SetValue(ProjectionMatrix);
                        Instance.TerrainShader.Param("View").SetValue(skyCameraMatrix);

                        if (!ShowFrozenFrustum) CameraBoundingFrustum = new BoundingFrustum(skyCameraMatrix * ProjectionMatrix * WorldMatrix);

                        var oldview = ViewMatrix;
                        ViewMatrix = skyCameraMatrix;

                        Instance.GraphicsDevice.RasterizerState = skyFlip ? cullCounterClockwiseSky : cullClockwiseSky;
                        Instance.GraphicsDevice.DepthStencilState = brushDepthEquals;
                        if (GlobalMapData.ActiveMap.Brushes?.Length > 0)
                        {
                            RenderMap(true, Matrix.Invert(skyCameraMatrix).Translation);

                            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                            if (GlobalMapData.ActiveMap.Terrains != null)
                            {
                                for (int t = 0; t < GlobalMapData.ActiveMap.Terrains.Length; t++)
                                {
                                    if (!IsAnyLeafInSkyPVS(GlobalMapData.ActiveMap.Terrains[t].LeafBits)) continue;
                                    DrawTerrain(t);
                                }
                            }
                        }

                        TransparentRenderQueue.BuildLeafOrder(previousSkyLeaf);

                        Instance.GraphicsDevice.BlendState = nonPremultiplied;
                        Instance.GraphicsDevice.RasterizerState = skyFlip ? cullClockwiseSky : cullCounterClockwiseSky;

                        if (renderEntities)
                        {
                            EntityManager.RenderEntities(time, false, skyFlip, useSkyboxVisibility: true);
                        }

                        TransparentRenderQueue.RenderAll();

                        if (renderDecalsAndParticles)
                        {
                            Instance.GraphicsDevice.DepthStencilState = decalState;
                            DecalManager.RenderAllDecals();
                            Instance.GraphicsDevice.RasterizerState = cullNoneSky;
                            ParticleManager.RenderSystems();
                        }

                        ViewMatrix = oldview;

                        Displayable.FlipWinding = flipWinding;
                        isWindingFlipped = flipWinding;

                        Instance.GraphicsDevice.RasterizerState = flipWinding ? cullCounterClockwiseSky : cullClockwiseSky;
                    }
                }
            }

            TransparentRenderQueue.BuildLeafOrder(previousCameraLeaf);

            Instance.GraphicsDevice.Clear(ClearOptions.DepthBuffer, Color.Black, Instance.GraphicsDevice.Viewport.MaxDepth, 0);
            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;

            foreach (var shader in LoadedWorldShaders)
            {
                shader.Param("World").SetValue(WorldMatrix);
                shader.Param("View").SetValue(ViewMatrix);
                shader.Param("Projection").SetValue(ProjectionMatrix);
                shader.Param("Skybox3DView").SetValue(false);
            }

            Instance.GraphicsDevice.RasterizerState = flipWinding ? RasterizerState.CullCounterClockwise : RasterizerState.CullClockwise;

            // Pre-pass the depth so that we can render with depthequals
            using (RenderTimings.Section(TimingSection.RenderMapDepth))
            {
                RenderMapDepth(false, pvsOrigin);
            }

            Instance.TerrainShader.Param("World").SetValue(WorldMatrix);
            Instance.TerrainShader.Param("View").SetValue(ViewMatrix);
            Instance.TerrainShader.Param("Projection").SetValue(ProjectionMatrix);
            Instance.TerrainShader.Param("Skybox3DView").SetValue(false);

            if (!ShowFrozenFrustum)
                CameraBoundingFrustum = new BoundingFrustum(WorldMatrix * ViewMatrix * ProjectionMatrix);

            Instance.GraphicsDevice.RasterizerState = flipWinding ? RasterizerState.CullCounterClockwise : RasterizerState.CullClockwise;

            using (RenderTimings.Section(TimingSection.RenderMap))
            {
                RenderMap(false, pvsOrigin);
            }

            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;

            if (GlobalMapData.ActiveMap.Terrains != null)
            {
                using (RenderTimings.Section(TimingSection.Terrain))
                {
                    for (int t = 0; t < GlobalMapData.ActiveMap.Terrains.Length; t++)
                    {
                        if (!IsAnyLeafInMainPVS(GlobalMapData.ActiveMap.Terrains[t].LeafBits)) continue;
                        DrawTerrain(t);
                    }
                }
            }

            Instance.GraphicsDevice.BlendState = nonPremultiplied;
            Instance.GraphicsDevice.RasterizerState = flipWinding ? RasterizerState.CullClockwise : RasterizerState.CullCounterClockwise;

            if (renderEntities)
            {
                using (RenderTimings.Section(TimingSection.Entities))
                {
                    EntityManager.RenderEntities(time, false, flipWinding);
                }
            }

            using (RenderTimings.Section(TimingSection.TransparentModels))
            {
                TransparentRenderQueue.RenderAll();
            }

            if (renderDecalsAndParticles)
            {
                using (RenderTimings.Section(TimingSection.DecalsAndParticles))
                {
                    Instance.GraphicsDevice.DepthStencilState = decalState;
                    DecalManager.RenderAllDecals();
                    Instance.GraphicsDevice.SamplerStates[0] = WorldTextureSamplerState;
                    Instance.GraphicsDevice.BlendState = nonPremultiplied;
                    Instance.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
                    ParticleManager.RenderSystems();
                }
            }

            Instance.GraphicsDevice.RasterizerState = flipWinding ? RasterizerState.CullCounterClockwise : RasterizerState.CullClockwise;
            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        }
        private static void RenderPlanarReflections(GameTime time)
        {
            if (ReflectionQuality == QualityLevel.Low || reflectorGroups == null || reflectorGroups.Count == 0)
                return;
            if (CameraBoundingFrustum == null) return;

            reflectionRenderScratch.Clear();
            foreach (var group in reflectorGroups)
            {
                if (CameraBoundingFrustum.Contains(group.Bounds) == ContainmentType.Disjoint)
                    continue;
                reflectionRenderScratch.Add(group);
            }
            if (reflectionRenderScratch.Count == 0)
                return;

            reflectionRenderScratch.Sort((a, b) =>
            {
                Vector3 centerA = (a.Bounds.Min + a.Bounds.Max) * 0.5f;
                Vector3 centerB = (b.Bounds.Min + b.Bounds.Max) * 0.5f;
                return Vector3.DistanceSquared(centerA, CameraPosition).CompareTo(Vector3.DistanceSquared(centerB, CameraPosition));
            });

            int budget = ReflectionQuality == QualityLevel.High ? ReflectionBudgetHigh : ReflectionBudgetMedium;

            var savedView = ViewMatrix;
            var savedProjection = ProjectionMatrix;
            var savedFrustum = CameraBoundingFrustum;
            var savedSkyboxWasVisible = skyboxWasVisible;

            if (ProjectionMatrix.Forward == Vector3.Zero) return;

            int rendered = 0;
            for (int i = 0; i < reflectionRenderScratch.Count && rendered < budget; i++)
            {
                if (reflectionRenderScratch[i].ReflectionPlane.DotCoordinate(CameraPosition) < 0) continue;

                var group = reflectionRenderScratch[i];
                EnsureReflectionTarget(group);

                Matrix mirroredView = Matrix.CreateReflection(group.ReflectionPlane) * savedView;
                ViewMatrix = mirroredView;

                Matrix clippedProjection = CMath.ObliqueClipProjection(savedProjection, mirroredView, group.ReflectionPlane);
                ProjectionMatrix = clippedProjection;

                Instance.GraphicsDevice.SetRenderTarget(group.ReflectionTarget);
                Instance.GraphicsDevice.Clear(ClearOptions.DepthBuffer | ClearOptions.Target, Color.Transparent, Instance.GraphicsDevice.Viewport.MaxDepth, 0);

                Vector3 pvsOrigin = (group.Bounds.Min + group.Bounds.Max) * 0.5f;
                bool allow3DSkyForReflections = ReflectionQuality == QualityLevel.High;

                RenderWorld(time, pvsOrigin, renderDecalsAndParticles: false, flipWinding: true,
                            drawSkybox: true, allow3DSkybox: allow3DSkyForReflections,
                            ref group.SkyboxWasVisible, skyboxProjection: savedProjection);

                rendered++;
            }

            ViewMatrix = savedView;
            ProjectionMatrix = savedProjection;
            CameraBoundingFrustum = savedFrustum;
            skyboxWasVisible = savedSkyboxWasVisible;
        }
        /// <summary>
        /// Draw a wireframe of a <see cref="BoundingBox"/>.
        /// </summary>
        /// <param name="box">The box to render.</param>
        public static void DrawBox(BoundingBox box)
        {
            Instance.WorldShader.Param("World").SetValue(WorldMatrix);

            var verts = CMath.GetDebugEdges(box);
            debugBuffer = new VertexBuffer(Instance.GraphicsDevice, typeof(VertexPosition), verts.Length, BufferUsage.WriteOnly);
            debugBuffer.SetData(verts);

            Instance.GraphicsDevice.SetVertexBuffer(debugBuffer);

            Instance.WorldShader.Param("BrushTex").SetValue(WhiteTexture);

            Instance.WorldShader.RenderEachPass(() =>
                    Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, verts, 0, verts.Length / 2));
        }
        /// <summary>
        /// Draw a wireframe of a <see cref="OrientedBoundingBox"/>, translated according to the translation of the box.
        /// </summary>
        /// <param name="box">The box to render.</param>
        public static void DrawBox(OrientedBoundingBox box, bool setTex = true)
        {
            Instance.WorldShader.Param("World").SetValue(WorldMatrix);

            var verts = CMath.GetDebugEdges(box);
            debugBuffer = new VertexBuffer(Instance.GraphicsDevice, typeof(VertexPosition), verts.Length, BufferUsage.WriteOnly);
            debugBuffer.SetData(verts);

            Instance.GraphicsDevice.SetVertexBuffer(debugBuffer);

            if(setTex) Instance.WorldShader.Param("BrushTex").SetValue(WhiteTexture);

            Instance.WorldShader.RenderEachPass(() =>
                    Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, verts, 0, verts.Length / 2));
        }
        struct PlaneBasis
        {
            public Vector3 origin, u, v;
        }
        static Stack<uint> leafStack = new Stack<uint>();

        public static void DrawDebugString(Vector3 at, string line, float fontsize = 14, float alpha = 1f)
        {
            var viewport = MainEngine.Instance.GraphicsDevice.Viewport;
            var screen = viewport.Project(at, ProjectionMatrix, ViewMatrix, WorldMatrix);

            if (screen.Z < 0f || screen.Z > 1f) return;

            var font = FontSystem?.GetFont(fontsize);
            if (font == null) return;

            var size = font.MeasureString(line);
            var pos = new Vector2(screen.X - size.X * 0.5f, screen.Y - size.Y - 4f);

            MainEngine.Instance.SpriteBatch.DrawString(font, line, pos, new Color(Color.White,alpha),effect:FontSystemEffect.Stroked,effectAmount:1);
        }

        private static Dictionary<uint, int> batchColorIndices = new Dictionary<uint, int>();
        private static int nextBatchColorIndex = 0;
        private const float GoldenAngle = 0.6180339887f;
        private static uint HashBatchKey(int materialId, int firstVertexStart)
        {
            unchecked
            {
                uint h = (uint)materialId * 2654435761u;
                h ^= (uint)firstVertexStart * 2246822519u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                h *= 3266489917u;
                h ^= h >> 16;
                return h;
            }
        }
        private static Vector3 ColorFromBatchKey(uint key)
        {
            if (!batchColorIndices.TryGetValue(key, out int index))
            {
                index = nextBatchColorIndex++;
                batchColorIndices[key] = index;
            }

            float hue = (index * GoldenAngle) % 1f;

            uint mixHash = key * 668265263u;
            mixHash ^= mixHash >> 13;
            float sat = 0.65f + 0.35f * ((mixHash & 0xFF) / 255f);        // 0.65-1.0
            float val = 0.55f + 0.45f * (((mixHash >> 8) & 0xFF) / 255f); // 0.55-1.0

            return HsvToRgb(hue, sat, val);
        }
        private static Vector3 HsvToRgb(float h, float s, float v)
        {
            int i = (int)(h * 6f);
            float f = h * 6f - i;
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);
            switch (i % 6)
            {
                case 0: return new Vector3(v, t, p);
                case 1: return new Vector3(q, v, p);
                case 2: return new Vector3(p, v, t);
                case 3: return new Vector3(p, q, v);
                case 4: return new Vector3(t, p, v);
                default: return new Vector3(v, p, q);
            }
        }
        private static Rectangle OctBoundsToScissorRect(in PortalCulling.OctBounds b, Rectangle fallback)
        {
            if (!PortalCulling.IsValid(b)) return fallback;

            int x = (int)MathF.Floor(b.minX);
            int y = (int)MathF.Floor(b.minY);
            int right = (int)MathF.Ceiling(b.maxX);
            int bottom = (int)MathF.Ceiling(b.maxY);

            x = Math.Clamp(x, fallback.Left, fallback.Right);
            y = Math.Clamp(y, fallback.Top, fallback.Bottom);
            right = Math.Clamp(right, x, fallback.Right);
            bottom = Math.Clamp(bottom, y, fallback.Bottom);

            return new Rectangle(x, y, right - x, bottom - y);
        }

        private static CachedLeafGeometry GetOrBuildLeafGeometry(bool isSkybox, int local)
        {
            var cache = isSkybox ? cachedSkyGeometry : cachedMainGeometry;

            if (cache.TryGetValue(local, out var existing))
            {
                return existing;
            }

            var geometry = BuildLeafGeometry(local);
            cache[local] = geometry;
            return geometry;
        }

        private static CachedLeafGeometry BuildLeafGeometry(int local)
        {
            var polys = GlobalMapData.ActiveMap.LeafPolygons;
            var runs = new List<GeomRun>();
            bool skyboxVisible = false;

            foreach (var visible in VisRoot.VisLeaves[local].PVS)
            {
                skyboxVisible |= VisRoot.VisLeaves[visible].HasSkybox;

                int start = GlobalMapData.ActiveMap.LeafPolyStart[visible];
                int count = GlobalMapData.ActiveMap.LeafPolyCount[visible];

                for (int i = start; i < start + count;)
                {
                    var poly = polys[i];
                    int runStart = poly.VertexStart, runCount = poly.VertexCount, matId = poly.MaterialID, cubemapIdx = poly.RuntimeCubemapID;
                    int j = i + 1;

                    while (j < start + count && polys[j].MaterialID == matId && polys[j].RuntimeCubemapID == cubemapIdx && polys[j].VertexStart == runStart + runCount)
                    {
                        runCount += polys[j].VertexCount;
                        j++;
                    }

                    runs.Add(new GeomRun { MaterialID = matId, VertexStart = runStart, VertexCount = runCount, CubemapID = cubemapIdx });
                    i = j;
                }
            }

            runs.Sort((a, b) =>
            {
                int matCompare = a.MaterialID.CompareTo(b.MaterialID);
                return matCompare != 0 ? matCompare : a.CubemapID.CompareTo(b.CubemapID);
            });

            int w = 0;
            for (int r = 1; r < runs.Count; r++)
            {
                var prev = runs[w];
                var cur = runs[r];
                if (cur.MaterialID == prev.MaterialID && cur.CubemapID == prev.CubemapID && cur.VertexStart == prev.VertexStart + prev.VertexCount)
                {
                    prev.VertexCount += cur.VertexCount;
                    runs[w] = prev;
                }
                else
                {
                    w++;
                    runs[w] = cur;
                }
            }
            if (runs.Count > 0)
            {
                runs.RemoveRange(w + 1, runs.Count - w - 1);
            }

            var geometry = new CachedLeafGeometry { SkyboxVisible = skyboxVisible };
            var indices = new List<int>();

            int i2 = 0;
            while (i2 < runs.Count)
            {
                int mat = runs[i2].MaterialID;
                int cubemapIdx = runs[i2].CubemapID;
                int startIndex = indices.Count;

                int j2 = i2;
                while (j2 < runs.Count && runs[j2].MaterialID == mat && runs[j2].CubemapID == cubemapIdx)
                {
                    var run = runs[j2];
                    for (int v = run.VertexStart; v < run.VertexStart + run.VertexCount; v++)
                    {
                        indices.Add(v);
                    }
                    j2++;
                }

                geometry.Ranges.Add(new MaterialRange
                {
                    MaterialID = mat,
                    CubemapID = cubemapIdx,
                    StartIndex = startIndex,
                    IndexCount = indices.Count - startIndex
                });

                i2 = j2;
            }

            geometry.Indices = indices.ToArray();

            if (geometry.Indices.Length > 0)
            {
                if (useThirtyTwoBitIndices)
                {
                    geometry.IndexBuffer = new IndexBuffer(Instance.GraphicsDevice, IndexElementSize.ThirtyTwoBits, geometry.Indices.Length, BufferUsage.WriteOnly);
                    geometry.IndexBuffer.SetData(geometry.Indices);
                }
                else
                {
                    var shortIndices = new ushort[geometry.Indices.Length];
                    for (int k = 0; k < shortIndices.Length; k++)
                    {
                        shortIndices[k] = (ushort)geometry.Indices[k];
                    }
                    geometry.IndexBuffer = new IndexBuffer(Instance.GraphicsDevice, IndexElementSize.SixteenBits, shortIndices.Length, BufferUsage.WriteOnly);
                    geometry.IndexBuffer.SetData(shortIndices);
                }
            }

            return geometry;
        }

        private static void ClearLeafGeometryCaches()
        {
            foreach (var geometry in cachedMainGeometry.Values)
            {
                geometry.IndexBuffer?.Dispose();
            }
            foreach (var geometry in cachedSkyGeometry.Values)
            {
                geometry.IndexBuffer?.Dispose();
            }
            cachedMainGeometry.Clear();
            cachedSkyGeometry.Clear();
        }
        public static void BuildPVSLookups(Vector3 pvsOrigin, bool isSkybox)
        {
            var node = BSPRoot.Traverse(pvsOrigin);

            if (portalLines != 0) leafStack.Push((uint)node);

            // >> 6 is / 64, so this is computing the number of longs needed to store this bitset.
            // same thing happens in the actual bitset class, but ain nobody reading that
            int neededWords = (VisRoot.VisLeaves.Length + 63) >> 6;

            if (mainPvsBits.Length < neededWords) mainPvsBits = new ulong[neededWords];
            if (skyPvsBits.Length < neededWords) skyPvsBits = new ulong[neededWords];

            ref uint prevLeaf = ref isSkybox ? ref previousSkyLeaf : ref previousCameraLeaf;
            ulong[] bits = isSkybox ? skyPvsBits : mainPvsBits;

            if (prevLeaf != node)
            {
                Array.Clear(bits);
                Bitset.Set(bits, (int)node);

                foreach (var visible in VisRoot.VisLeaves[node].PVS)
                    Bitset.Set(bits, (int)visible);

                prevLeaf = (uint)node;
            }
        }
        public static void RenderMapDepth(bool isSkybox, Vector3? pvsOrigin = null)
        {
            Instance.GraphicsDevice.BlendState = alphaPrePass;
            if (LoadedMapHasVis && GlobalMapData.ActiveMap.LeafPolyStart != null && GlobalMapData.ActiveMap.LeafPolyCount != null)
            {
                var node = BSPRoot.Traverse(pvsOrigin ?? CameraPosition);

                ref uint local = ref isSkybox ? ref previousSkyLeaf : ref previousCameraLeaf;

                if (VisRoot.VisLeaves[local].PVS.Length == 0)
                {
                    return;
                }

                CachedLeafGeometry geometry = null;

                geometry = GetOrBuildLeafGeometry(isSkybox, (int)local);
                skyboxWasVisible = geometry.SkyboxVisible;

                Instance.GraphicsDevice.SetVertexBuffer(staticGeomVertexBuffer);

                if (geometry != null && geometry.IndexBuffer != null)
                {
                    if (CurrentWireframeDisplayMode < 3)
                    {
                        Instance.GraphicsDevice.Indices = geometry.IndexBuffer;

                        DepthStencilState savedDepth = null;

                        ShaderHandle activeShader;

                        activeShader = Instance.WorldShader;
                        activeShader.Param("World")?.SetValue(WorldMatrix);
                        activeShader.SetTechnique("DepthOnly");

                        activeShader.ApplyPass(0);
                        Instance.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geometry.Indices.Length / 3);
                    }
                }
            }
        }
        public static void RenderMap(bool isSkybox, Vector3? pvsOrigin = null)
        {
            //modelLeavesVisited.Clear();

            List<VertexPosition> vertices = null;
            if(portalLines != 0) vertices = new List<VertexPosition>();

            basicShader.Alpha = 0.2f;
            basicShader.DiffuseColor = Color.FloralWhite.ToVector3();

            Instance.GraphicsDevice.BlendState = BlendState.AlphaBlend;
            if (portalLines != 0) leafStack.Clear();

            skyboxWasVisible = false;
            
            if (LoadedMapHasVis && GlobalMapData.ActiveMap.LeafPolyStart != null && GlobalMapData.ActiveMap.LeafPolyCount != null)
            {
                var node = BSPRoot.Traverse(pvsOrigin ?? CameraPosition);

                ref uint local = ref isSkybox ? ref previousSkyLeaf : ref previousCameraLeaf;

                if (portalLines != 0) leafStack.Push(local);

                if (VisRoot.VisLeaves[local].PVS.Length == 0)
                {
                    OctreeMapRender(OctreeRoot.AllNodes[0], CameraBoundingFrustum);
                    return;
                }

                CachedLeafGeometry geometry = null;

                foreach (var visible in VisRoot.VisLeaves[local].PVS)
                {
                    DrawPropModelsForLeaf(visible);
                }

                geometry = GetOrBuildLeafGeometry(isSkybox, (int)local);
                skyboxWasVisible = geometry.SkyboxVisible;

                Instance.GraphicsDevice.SetVertexBuffer(staticGeomVertexBuffer);

                if (geometry != null && geometry.IndexBuffer != null)
                {
                    if (CurrentWireframeDisplayMode < 3)
                    {
                        Instance.GraphicsDevice.Indices = geometry.IndexBuffer;

                        BlendState savedBlend = null;
                        if (ShowMultiDrawBatches)
                        {
                            savedBlend = Instance.GraphicsDevice.BlendState;
                            Instance.GraphicsDevice.BlendState = BlendState.Opaque;
                        }

                        foreach (var range in geometry.Ranges)
                        {
                            ShaderHandle activeShader;
                            if (ShowMultiDrawBatches)
                            {
                                uint key = HashBatchKey(range.MaterialID, range.StartIndex);
                                basicShader.DiffuseColor = ColorFromBatchKey(key);
                                basicShader.Alpha = 1f;
                                basicShader.VertexColorEnabled = false;
                                basicShader.LightingEnabled = false;
                                basicShader.TextureEnabled = false;
                                basicShader.World = WorldMatrix;
                                basicShader.View = ViewMatrix;
                                basicShader.Projection = ProjectionMatrix;
                                activeShader = basicShader;
                            }
                            else
                            {
                                ApplyMaterialState(range.MaterialID, cubemapIndex: range.CubemapID);
                                activeShader = (range.MaterialID >= 0 && range.MaterialID < GlobalMapData.LoadedMaterials.Length)
                                    ? (ShaderHandle)GlobalMapData.LoadedMaterials[range.MaterialID].Shader
                                    : Instance.WorldShader;
                            }

                            // Brushes that get to this point should never be transparent, and we shouldnt allow for that.
                            // Transparency is only handled properly when the brushes become entities.
                            Instance.GraphicsDevice.DepthStencilState = brushDepthEquals;

                            activeShader.ApplyPass(0);
                            Instance.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, range.StartIndex, range.IndexCount / 3);
                        }

                        if (ShowMultiDrawBatches)
                        {
                            Instance.GraphicsDevice.BlendState = savedBlend;
                        }
                    }
                    if (CurrentWireframeDisplayMode > 0)
                    {
                        var old = Instance.GraphicsDevice.RasterizerState;
                        Instance.GraphicsDevice.RasterizerState = (isSkybox ? WireframeRasterizerStateScissor : WireframeRasterizerState);
                        Instance.GraphicsDevice.DepthStencilState = DepthStencilState.None;

                        Instance.WorldShader.Param("BrushTex").SetValue(WhiteTexture);
                        Instance.WorldShader.Param("ExpandWireframe").SetValue(true);
                        Instance.WorldShader.Param("DisableLighting").SetValue(true);

                        Instance.GraphicsDevice.Indices = geometry.IndexBuffer;

                        foreach (var range in geometry.Ranges)
                        {
                            Instance.WorldShader.ApplyPass(0);
                            Instance.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, range.StartIndex, range.IndexCount / 3);
                        }
                        Instance.WorldShader.Param("ExpandWireframe").SetValue(false);
                        Instance.WorldShader.Param("DisableLighting").SetValue(false);
                        Instance.GraphicsDevice.RasterizerState = old;
                        Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                    }
                }

                // All of the below is just for debug visualizations.
                if (portalLines == 0) return;

                bool first = true;
                while (leafStack.Count != 0)
                {
                    uint id = leafStack.Pop();
                    var leaf = VisRoot.VisLeaves[id];
                    //if (BSPRoot.nodes[id].parent != id) leafStack.Push(BSPRoot.nodes[id].parent);

                    if (leaf == null || leaf.Portals == null || leaf.Portals.Length == 0) continue;

                    int[] p;

                    if(portalLines == 1)
                    {
                        p = leaf.Portals.Where(portal =>
                        {
                            return !BSPRoot.Nodes[VisRoot.VisPortals[portal].LeafBack].solid && !BSPRoot.Nodes[VisRoot.VisPortals[portal].LeafFront].solid &&
                                   !BSPRoot.Nodes[VisRoot.VisPortals[portal].LeafBack].split && !BSPRoot.Nodes[VisRoot.VisPortals[portal].LeafFront].split;
                        }).ToArray();
                    }
                    else
                    {
                        p = leaf.Portals.Where(portal =>
                        {
                            int id = VisRoot.VisPortals[portal].LeafBack == leaf.BspLeafID ? VisRoot.VisPortals[portal].LeafFront : VisRoot.VisPortals[portal].LeafBack;

                            return !BSPRoot.Nodes[id].split && BSPRoot.Nodes[id].solid && VisRoot.VisPortals[portal].Brushes.Length>0;
                        }).ToArray();
                    }

                    foreach (var i in p)
                    {
                        var colorRandom = new Random(i);

                        if (i == -1) continue;
                        if ((currentPortalView == -1 || i == p[int.Min(currentPortalView, p.Length - 1)]) || !first)
                        {
                            basicShader.DiffuseColor = new Vector3(colorRandom.NextSingle(), colorRandom.NextSingle(), colorRandom.NextSingle());
                            Instance.GraphicsDevice.BlendState = BlendState.AlphaBlend;
                            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.None;
                            Instance.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
                            basicShader.Alpha = 0.1f;

                            bool flip = VisRoot.VisPortals[i].LeafBack == leaf.BspLeafID;
                            if (BSPRoot.Nodes[(flip ? VisRoot.VisPortals[i].LeafFront : VisRoot.VisPortals[i].LeafBack)].split) continue;

                            vertices.Clear();
                            for (int v = 1; v < VisRoot.VisPortals[i].Vertices.Length - 1; v++)
                            {
                                vertices.Add(new VertexPosition(VisRoot.VisPortals[i].Vertices[0]));
                                vertices.Add(new VertexPosition(VisRoot.VisPortals[i].Vertices[v]));
                                vertices.Add(new VertexPosition(VisRoot.VisPortals[i].Vertices[v + 1]));
                            }
                            Vector3[] mids = new Vector3[vertices.Count/3];

                            for (int t = 0; t < mids.Length; t++)
                            {
                                for (int v = 0; v < 3; v++)
                                {
                                    mids[t] += vertices[v+t * 3].Position;
                                }
                                mids[t] /= 3f;
                            }

                            if (currentPortalView != -1 && first)
                            {
                                leafStack.Push((uint)(flip ? VisRoot.VisPortals[i].LeafFront : VisRoot.VisPortals[i].LeafBack));
                            }

                            foreach (var pass in basicShader.CurrentTechnique.Passes)
                            {
                                pass.Apply();
                                Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, vertices.ToArray(), 0, vertices.Count / 3);
                            }
                            float SignedWindingArea(Vector3[] verts, Plane plane)
                            {
                                PlaneBasis BuildPlaneBasis(Vector3 normal, float d)
                                {
                                    Vector3 arbitrary = (Math.Abs(normal.X) > 0.9f)
                                                        ? new Vector3(0, 1, 0)
                                                        : new Vector3(1, 0, 0);
                                    Vector3 u = Vector3.Cross(arbitrary, normal);
                                    u.Normalize();
                                    Vector3 v = Vector3.Cross(normal, u);
                                    v.Normalize();
                                    Vector3 origin = normal * -(d);

                                    return new PlaneBasis { origin = origin, u = u, v = v };
                                }
                                var basis = BuildPlaneBasis(plane.Normal, plane.D);
                                var pts2D = verts.Select(v => new Vector2(
                                    Vector3.Dot(v - basis.origin, basis.u),
                                    Vector3.Dot(v - basis.origin, basis.v)
                                )).ToArray();

                                float area = 0;
                                for (int i = 0, n = pts2D.Length; i < n; i++)
                                {
                                    var a = pts2D[i];
                                    var b = pts2D[(i + 1) % n];
                                    area += a.X * b.Y - b.X * a.Y;
                                }
                                return area * 0.5f;
                            }
                            basicShader.Alpha = 1;
                            basicShader.DiffuseColor = Color.Green.ToVector3();

                            for (int t = 0; t < mids.Length; t++)
                            {
                                vertices.Clear();
                                vertices.Add(new VertexPosition(mids[t]));
                                vertices.Add(new VertexPosition(mids[t] + VisRoot.VisPortals[i].Plane.Normal));
                                foreach (var pass in basicShader.CurrentTechnique.Passes)
                                {
                                    pass.Apply();
                                    Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, vertices.ToArray(), 0, vertices.Count / 2);
                                }
                            }

                            Instance.GraphicsDevice.BlendState = BlendState.AlphaBlend;
                            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                            Instance.GraphicsDevice.SamplerStates[0] = WorldTextureSamplerState;
                        }
                    }
                    first = false;
                }
            }
            else
            {
                OctreeMapRender(OctreeRoot.AllNodes[0], CameraBoundingFrustum);
            }
        }
        private static void ApplyMaterialState(int materialId, ShaderHandle shaderOverrideForWorld = null, int cubemapIndex = -1)
        {
            if (GlobalMapData.LoadedMaterials == null) return;

            bool validMaterial = materialId >= 0 && materialId < GlobalMapData.LoadedMaterials.Length;
            var material = GlobalMapData.LoadedMaterials[materialId];

            var shader = (ShaderHandle)material.Shader ?? Instance.WorldShader;

            ApplyMaterialTechnique(material, shader);

            shader.Param("DisableLighting").SetValue(false);

            var cubemapTexture = (!EnvCubemap.cubeRendering && cubemapIndex >= 0 && cubemapIndex < EnvCubemap.Cubemaps.Count && EnvCubemap.Cubemaps[cubemapIndex].diffusionMaps != null)
                ? EnvCubemap.Cubemaps[cubemapIndex].diffusionMaps[ShowMaterialShine ? 0 : 3]
                : Skybox.GetSkyTexture();

            shader.Param("cubemap").SetValue(cubemapTexture);
            shader.Param("cubemapSize").SetValue(128 >> (ShowMaterialShine ? 0 : 3));

            shader.Param("World").SetValue(WorldMatrix);

            shader.Param("BrushTex").SetValue(ShowBlankTexture ? DimTexture : validMaterial ? material.Texture : ErrorTexture);
            shader.Param("BrushNorm").SetValue(validMaterial ? material.Normal : WhiteTexture);
            shader.Param("BrushSpec").SetValue(!validMaterial ? WhiteTexture : ShowMaterialShine ? WhiteTexture : material.Specular);

            // Anything that isnt the standard tex/spec/normal just gets passed along
            foreach(var tex in material.GetExtraTextures())
            {
                shader.Param(tex.Key).SetValue(tex.Value);
            }
        }

        /// <summary>
        /// Renders map geometry by stepping through the octree.
        /// </summary>
        /// <param name="node">The node to search from, usually the root.</param>
        /// <param name="frustum">The camera frustum by which to cull invisible nodes.</param>
        public static void OctreeMapRender(Octree node, BoundingFrustum frustum)
        {
            cameraRenderQueue.Clear();
            OctreeMarkNodes(node,frustum, ref cameraRenderQueue);

            cameraRenderQueue.Sort((a, b) =>
            {
                var pos = CameraPosition;
                Collision.ClosestPointBoxPoint(ref GlobalMapData.ActiveMap.OctreeNodes[a].Box, ref pos, out Vector3 pA);
                Collision.ClosestPointBoxPoint(ref GlobalMapData.ActiveMap.OctreeNodes[b].Box, ref pos, out Vector3 pB);

                return Vector3.DistanceSquared(pB, CameraPosition).CompareTo(Vector3.DistanceSquared(pA, CameraPosition));
            });

            var toDraw = OctreeMarkBrushes(ref cameraRenderQueue);

            while (toDraw.Count > 0)
            {
                DrawBrush(toDraw.Dequeue(), false);
            }
        }
        static Queue<int> OctreeMarkBrushes(ref List<int> nodes)
        {
            Queue<int> brushesToRender = new Queue<int>();
            HashSet<int> seen = new HashSet<int>();

            foreach (int nodeId in nodes)
            {
                var octreeNode = GlobalMapData.ActiveMap.OctreeNodes[nodeId];
                foreach (var c in octreeNode.Contents)
                {
                    if (seen.Add(c))
                        brushesToRender.Enqueue(c);
                }
            }
            return brushesToRender;
        }

        static void OctreeMarkNodes(Octree node, BoundingFrustum frustum, ref List<int> nodes)
        {
            var containment = frustum.Contains(node.Box);
            if (containment == ContainmentType.Disjoint) return;

            if (node.IsEnd && node.Contents.Count > 0)
            {
                nodes.Add(node.Id);
                return;
            }

            if (containment == ContainmentType.Contains)
            {
                // Add all leaf descendants without further frustum checks
                AddAllLeafNodes(node, ref nodes);
                return;
            }

            // Otherwise, traverse children
            for (int i = 0; i < 8; i++)
            {
                if (node.Children[i] > 0)
                {
                    OctreeMarkNodes(OctreeRoot.AllNodes[node.Children[i]], frustum, ref nodes);
                }
            }
        }

        static void AddAllLeafNodes(Octree node, ref List<int> nodes)
        {
            if (node.IsEnd && node.Contents.Count > 0)
            {
                nodes.Add(node.Id);
                return;
            }
            for (int i = 0; i < 8; i++)
            {
                if (node.Children[i] > 0)
                {
                    AddAllLeafNodes(OctreeRoot.AllNodes[node.Children[i]], ref nodes);
                }
            }
        }

        private static void DrawPropModelsForLeaf(uint leafID)
        {
            var models = MapModelManager.GetModelsAtLeaf(leafID);

            if (models == null) return;

            var callerRasterizerState = Instance.GraphicsDevice.RasterizerState;
            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;

            Instance.GraphicsDevice.RasterizerState = isWindingFlipped ? RasterizerState.CullClockwise : RasterizerState.CullCounterClockwise;

            Instance.PropModelShader.Param("World").SetValue(WorldMatrix);
            Instance.PropModelShader.Param("View").SetValue(ViewMatrix);
            Instance.PropModelShader.Param("Projection").SetValue(ProjectionMatrix);

            foreach (var mdl in models)
            {
                Instance.PropModelShader.Param("MainTex").SetValue(GlobalMapData.LoadedMaterials[mdl.MaterialID].Texture);
                Instance.PropModelShader.Param("Transparent").SetValue(false);

                bool ignoreCull = GlobalMapData.LoadedMaterials[mdl.MaterialID].NoCull;

                if (ignoreCull)
                    Instance.GraphicsDevice.RasterizerState = RasterizerState.CullNone;

                if (!GlobalMapData.LoadedMaterials[mdl.MaterialID].Transparent)
                {
                    Instance.GraphicsDevice.SetVertexBuffer(mdl.VertexBuffer);
                    Instance.GraphicsDevice.Indices = mdl.IndexBuffer;

                    Instance.PropModelShader.ApplyPass(0);

                    Instance.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, mdl.PrimitiveCount);
                }
                else
                {
                    var registeredModel = mdl;
                    uint registeredLeaf = leafID;
                    bool wasWindingFlipped = isWindingFlipped;

                    TransparentRenderQueue.RegisterLeafBound(registeredLeaf, RenderEngine.CameraPosition, () =>
                    {
                        DrawTransparentMapModel(registeredModel, wasWindingFlipped);
                    });
                }
                if (ignoreCull)
                    Instance.GraphicsDevice.RasterizerState = isWindingFlipped ? RasterizerState.CullClockwise : RasterizerState.CullCounterClockwise;
            }

            Instance.GraphicsDevice.RasterizerState = callerRasterizerState;
        }
        private static void DrawTransparentMapModel(RenderableMapModel mdl, bool wasWindingFlipped)
        {
            var graphicsDevice = Instance.GraphicsDevice;
            var shader = Instance.PropModelShader;
            var material = GlobalMapData.LoadedMaterials[mdl.MaterialID];

            var oldBlendState = graphicsDevice.BlendState;
            var oldDepthState = graphicsDevice.DepthStencilState;
            var oldRasterizerState = graphicsDevice.RasterizerState;

            var baseCull = wasWindingFlipped ? RasterizerState.CullCounterClockwise : RasterizerState.CullClockwise;
            graphicsDevice.RasterizerState = material.NoCull ? RasterizerState.CullNone : baseCull;

            shader.Param("MainTex").SetValue(material.Texture);

            graphicsDevice.BlendState = alphaPrePass;
            shader.ApplyPass(1);

            graphicsDevice.SetVertexBuffer(mdl.VertexBuffer);
            graphicsDevice.Indices = mdl.IndexBuffer;
            graphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, mdl.PrimitiveCount);

            graphicsDevice.BlendState = oldBlendState;
            graphicsDevice.DepthStencilState = DepthStencilState.DepthRead;

            shader.Param("Transparent").SetValue(true);
            shader.ApplyPass(0);

            graphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, mdl.PrimitiveCount);

            shader.Param("Transparent").SetValue(false);

            graphicsDevice.BlendState = oldBlendState;
            graphicsDevice.DepthStencilState = oldDepthState;
            graphicsDevice.RasterizerState = oldRasterizerState;
        }

        /// <summary>
        /// Draws a <see cref="Terrain"/> mesh.
        /// </summary>
        /// <param name="i">The index of the terrain in the map file.</param>
        private static void DrawTerrain(int i)
        {
            Instance.TerrainShader.Param("mainTexture").SetValue(GlobalMapData.LoadedMaterials[GlobalMapData.ActiveMap.Terrains[i].Surface].Texture);
            Instance.TerrainShader.Param("blendTexture").SetValue(GlobalMapData.LoadedMaterials[GlobalMapData.ActiveMap.Terrains[i].BlendedSurface].Texture);
            Instance.TerrainShader.Param("normalTexture").SetValue(GlobalMapData.LoadedMaterials[GlobalMapData.ActiveMap.Terrains[i].Surface].Normal);
            Instance.TerrainShader.Param("World").SetValue(WorldMatrix);
            Instance.TerrainShader.Param("View").SetValue(ViewMatrix);
            Instance.TerrainShader.Param("Projection").SetValue(ProjectionMatrix);

            if (ShowBlankTexture)
            {
                Instance.TerrainShader.Param("mainTexture").SetValue(DimTexture);
                Instance.TerrainShader.Param("blendTexture").SetValue(DimTexture);
            }

            if (CameraBoundingFrustum.Contains(GlobalMapData.ActiveMap.Terrains[i].Bounds) == ContainmentType.Disjoint) return;

            if (CurrentWireframeDisplayMode < 3)
            {
                var old = Instance.GraphicsDevice.DepthStencilState;
                Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                Instance.TerrainShader.RenderEachPass(() =>
                    Instance.GraphicsDevice.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, GlobalMapData.ActiveMap.Terrains[i].Vertices, 0, GlobalMapData.ActiveMap.Terrains[i].Vertices.Length,
                                                             GlobalMapData.ActiveMap.Terrains[i].Triangles, 0, GlobalMapData.ActiveMap.Terrains[i].Triangles.Length / 3));
                Instance.GraphicsDevice.DepthStencilState = old;
            }
            if (CurrentWireframeDisplayMode != 0)
            {
                var oldrasterizer = Instance.GraphicsDevice.RasterizerState;
                Instance.TerrainShader.Param("mainTexture").SetValue(WhiteTexture);

                Instance.GraphicsDevice.RasterizerState = WireframeRasterizerState;
                Instance.TerrainShader.RenderEachPass(() =>
                    Instance.GraphicsDevice.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, GlobalMapData.ActiveMap.Terrains[i].Vertices, 0, GlobalMapData.ActiveMap.Terrains[i].Vertices.Length,
                                                             GlobalMapData.ActiveMap.Terrains[i].Triangles, 0, GlobalMapData.ActiveMap.Terrains[i].Triangles.Length / 3));
                Instance.GraphicsDevice.RasterizerState = oldrasterizer;
            }
        }

        /// <summary>
        /// Draws a <see cref="Brush"/>.
        /// </summary>
        /// <param name="brush">Index of the brush in the map file.</param>
        public static void DrawBrush(int brush, bool drawWireframe)
        {
            if (!DrawBrushes || brush >= GlobalMapData.ActiveMap.Brushes.Length || brush == -1)
                return;

            ref var brushData = ref GlobalMapData.ActiveMap.Brushes[brush];

            var cullmode = Instance.GraphicsDevice.RasterizerState;

            if (brushData.IsSkybox) return;
            if (!brushData.IsEntity &&
                CameraBoundingFrustum.Contains(GlobalMapData.ActiveMap.BrushBounds[brush]) == ContainmentType.Disjoint)
                return;

            var brushTranslation = Matrix.CreateTranslation(GlobalMapData.ActiveMap.Brushes[brush].Position);

            if (GlobalMapData.ActiveMap.Brushes[brush].IsEntity)
            {
                var entity = GlobalMapData.ActiveMap.Brushes[brush].Entity;
                if (entity != null)
                {
                    var offset = Vector3.Transform(brushData.Position - entity.SpawnAnchor, entity.WorldRotation);
                    brushTranslation = Matrix.CreateScale(entity.WorldScale) * Matrix.CreateFromQuaternion(entity.WorldRotation) * Matrix.CreateTranslation(entity.WorldPosition + offset);
                }
            }

            int transparentLeafRank = 0;
            Vector3 transparentSortPosition = brushTranslation.Translation;

            if (brushData.IsEntity && brushData.Entity != null)
            {
                transparentLeafRank = TransparentRenderQueue.RankFromLeafBits(brushData.Entity.GetLeafBits());
                transparentSortPosition = brushData.Entity.WorldPosition;
            }

            if (CurrentWireframeDisplayMode < 3 && !drawWireframe)
            {
                int f = -1;

                Instance.GraphicsDevice.SetVertexBuffer(GlobalMapData.ActiveMap.Brushes[brush].BrushVertexBuffer);

                if (brushData.RenderPiecewise)
                {
                    foreach (ref readonly Face face in GlobalMapData.ActiveMap.Brushes[brush].Faces.AsSpan())
                    {
                        f++;
                        if (face.FaceIndices == null) continue;
                        if (!face.Drawn) continue;

                        var mat = GlobalMapData.LoadedMaterials[face.Surface];

                        if (!mat.Transparent)
                        {
                            Instance.GraphicsDevice.Indices = face.FaceIndices;

                            var shader = GlobalMapData.LoadedMaterials.Length <= face.Surface ? Instance.WorldShader : (ShaderHandle)GlobalMapData.LoadedMaterials[face.Surface].Shader;

                            ApplyMaterialTechnique(mat, shader);
                            shader.Param("DisableLighting").SetValue(false);

                            var cubemapTexture = Skybox.GetSkyTexture();

                            if (EnvCubemap.Cubemaps != null && EnvCubemap.Cubemaps.Count > 0 && !EnvCubemap.cubeRendering)
                            {
                                var cube = CubemapHandler.GetFaceCubemap(brush, f);

                                if (cube != null && cube.diffusionMaps != null)
                                {
                                    cubemapTexture = cube.diffusionMaps[ShowMaterialShine ? 0 : 3];
                                }
                            }

                            shader.Param("cubemap").SetValue(cubemapTexture);
                            shader.Param("cubemapSize").SetValue(128 >> (ShowMaterialShine ? 0 : 3));

                            shader.Param("World").SetValue(brushTranslation * WorldMatrix);

                            shader.Param("BrushTex").SetValue(ShowBlankTexture ? DimTexture : GlobalMapData.LoadedMaterials.Length <= face.Surface ? ErrorTexture : GlobalMapData.LoadedMaterials[face.Surface].Texture);
                            shader.Param("BrushNorm").SetValue(GlobalMapData.LoadedMaterials.Length <= face.Surface ? WhiteTexture : GlobalMapData.LoadedMaterials[face.Surface].Normal);
                            shader.Param("BrushSpec").SetValue(GlobalMapData.LoadedMaterials.Length <= face.Surface ? WhiteTexture : ShowMaterialShine ? WhiteTexture : GlobalMapData.LoadedMaterials[face.Surface].Specular);

                            foreach (var tex in GlobalMapData.LoadedMaterials[face.Surface].GetExtraTextures())
                            {
                                shader.Param(tex.Key).SetValue(tex.Value);
                            }

                            if (GlobalMapData.LoadedMaterials.Length > face.Surface && GlobalMapData.LoadedMaterials[face.Surface].GetFlag("receiveRefractionTexture"))
                            {
                                shader.Param("RefractionTex").SetValue(refractionRenderTexture);
                                shader.Param("RefractionDepth").SetValue(ScreenRenderTexture.DepthTexture);
                            }

                            if (GlobalMapData.LoadedMaterials.Length > face.Surface && GlobalMapData.LoadedMaterials[face.Surface].GetFlag("receivePlanarReflection"))
                            {
                                if (faceToGroupLookup.TryGetValue(PackFaceKey(brush, f), out int groupIndex))
                                {
                                    var group = reflectorGroups[groupIndex];
                                    shader.Param("ReflectionTex").SetValue(group.ReflectionTarget);
                                }
                            }

                            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;

                            if (mat.NoCull && !isWindingFlipped)
                            {
                                Instance.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
                            }
                            var faceCopy = face;
                            shader.RenderEachPass(() =>
                            {
                                Instance.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, faceCopy.Indices.Length / 3);
                            });

                            if (mat.NoCull && !isWindingFlipped)
                            {
                                Instance.GraphicsDevice.RasterizerState = cullmode;
                            }
                        }
                        else
                        {
                            Face faceCopy = face;
                            int faceIndex = f;
                            bool isEntityOwned = brushData.IsEntity;
                            bool wasWindingFlipped = isWindingFlipped;

                            TransparentRenderQueue.RegisterBrushFace(isEntityOwned, transparentLeafRank, transparentSortPosition, () =>
                            {
                                Instance.GraphicsDevice.SetVertexBuffer(GlobalMapData.ActiveMap.Brushes[brush].BrushVertexBuffer);
                                Instance.GraphicsDevice.Indices = faceCopy.FaceIndices;

                                var shader = GlobalMapData.LoadedMaterials.Length <= faceCopy.Surface ? Instance.WorldShader : (ShaderHandle)GlobalMapData.LoadedMaterials[faceCopy.Surface].Shader;

                                ApplyMaterialTechnique(mat, shader);
                                shader.Param("DisableLighting").SetValue(false);

                                var cubemapTexture = Skybox.GetSkyTexture();

                                if (EnvCubemap.Cubemaps != null && EnvCubemap.Cubemaps.Count > 0 && !EnvCubemap.cubeRendering)
                                {
                                    var cube = CubemapHandler.GetFaceCubemap(brush, faceIndex);

                                    if (cube != null && cube.diffusionMaps != null)
                                    {
                                        cubemapTexture = cube.diffusionMaps[ShowMaterialShine ? 0 : 3];
                                    }
                                }

                                shader.Param("cubemap").SetValue(cubemapTexture);
                                shader.Param("cubemapSize").SetValue(128 >> (ShowMaterialShine ? 0 : 3));

                                shader.Param("World").SetValue(brushTranslation * WorldMatrix);

                                shader.Param("BrushTex").SetValue(ShowBlankTexture ? DimTexture : GlobalMapData.LoadedMaterials.Length <= faceCopy.Surface ? ErrorTexture : GlobalMapData.LoadedMaterials[faceCopy.Surface].Texture);
                                shader.Param("BrushNorm").SetValue(GlobalMapData.LoadedMaterials.Length <= faceCopy.Surface ? WhiteTexture : GlobalMapData.LoadedMaterials[faceCopy.Surface].Normal);
                                shader.Param("BrushSpec").SetValue(GlobalMapData.LoadedMaterials.Length <= faceCopy.Surface ? WhiteTexture : ShowMaterialShine ? WhiteTexture : GlobalMapData.LoadedMaterials[faceCopy.Surface].Specular);

                                foreach (var tex in GlobalMapData.LoadedMaterials[faceCopy.Surface].GetExtraTextures())
                                {
                                    shader.Param(tex.Key).SetValue(tex.Value);
                                }

                                if (GlobalMapData.LoadedMaterials.Length > faceCopy.Surface && GlobalMapData.LoadedMaterials[faceCopy.Surface].GetFlag("receiveRefractionTexture"))
                                {
                                    shader.Param("RefractionTex").SetValue(refractionRenderTexture);
                                    shader.Param("RefractionDepth").SetValue(ScreenRenderTexture.DepthTexture);
                                }

                                if (GlobalMapData.LoadedMaterials.Length > faceCopy.Surface && GlobalMapData.LoadedMaterials[faceCopy.Surface].GetFlag("receivePlanarReflection"))
                                {
                                    if (faceToGroupLookup.TryGetValue(PackFaceKey(brush, faceIndex), out int groupIndex))
                                    {
                                        var group = reflectorGroups[groupIndex];
                                        shader.Param("ReflectionTex").SetValue(group.ReflectionTarget);
                                    }
                                }

                                Instance.GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;

                                var baseCull = wasWindingFlipped ? RasterizerState.CullCounterClockwise : RasterizerState.CullClockwise;
                                bool applyNoCull = mat.NoCull && !wasWindingFlipped;

                                Instance.GraphicsDevice.RasterizerState = applyNoCull ? RasterizerState.CullNone : baseCull;

                                shader.RenderEachPass(() =>
                                {
                                    Instance.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, faceCopy.Indices.Length / 3);
                                });

                                Instance.GraphicsDevice.RasterizerState = baseCull;

                                Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                            });
                        }
                    }
                }
                else
                {
                    foreach (ref readonly MatGroup group in GlobalMapData.ActiveMap.Brushes[brush].MatGroups.AsSpan())
                    {
                        var mat = GlobalMapData.LoadedMaterials[group.MaterialID];

                        if (!mat.Transparent)
                        {
                            Instance.GraphicsDevice.Indices = group.IndexBuffer;

                            var shader = GlobalMapData.LoadedMaterials.Length <= group.MaterialID ? Instance.WorldShader :
                                                                        (ShaderHandle)GlobalMapData.LoadedMaterials[group.MaterialID].Shader;

                            ApplyMaterialTechnique(mat, shader);
                            shader.Param("DisableLighting").SetValue(false);

                            var cubemapTexture = Skybox.GetSkyTexture();

                            if (EnvCubemap.Cubemaps != null && EnvCubemap.Cubemaps.Count > 0 && !EnvCubemap.cubeRendering)
                            {
                                var cube = CubemapHandler.GetFaceCubemap(brush, f);

                                if (cube != null && cube.diffusionMaps != null)
                                {
                                    cubemapTexture = cube.diffusionMaps[ShowMaterialShine ? 0 : 3];
                                }
                            }

                            shader.Param("cubemap").SetValue(cubemapTexture);
                            shader.Param("cubemapSize").SetValue(128 >> (ShowMaterialShine ? 0 : 3));

                            shader.Param("World").SetValue(brushTranslation * WorldMatrix);

                            shader.Param("BrushTex").SetValue(ShowBlankTexture ? DimTexture : GlobalMapData.LoadedMaterials.Length <= group.MaterialID ? ErrorTexture : GlobalMapData.LoadedMaterials[group.MaterialID].Texture);
                            shader.Param("BrushNorm").SetValue(GlobalMapData.LoadedMaterials.Length <= group.MaterialID ? WhiteTexture : GlobalMapData.LoadedMaterials[group.MaterialID].Normal);
                            shader.Param("BrushSpec").SetValue(GlobalMapData.LoadedMaterials.Length <= group.MaterialID ? WhiteTexture : ShowMaterialShine ? WhiteTexture : GlobalMapData.LoadedMaterials[group.MaterialID].Specular);

                            foreach (var tex in GlobalMapData.LoadedMaterials[group.MaterialID].GetExtraTextures())
                            {
                                shader.Param(tex.Key).SetValue(tex.Value);
                            }

                            if (GlobalMapData.LoadedMaterials.Length > group.MaterialID &&
                                GlobalMapData.LoadedMaterials[group.MaterialID].GetFlag("receiveRefractionTexture"))
                            {
                                shader.Param("RefractionTex").SetValue(refractionRenderTexture);
                                shader.Param("RefractionDepth").SetValue(ScreenRenderTexture.DepthTexture);
                            }

                            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;

                            if (mat.NoCull && !isWindingFlipped)
                            {
                                Instance.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
                            }
                            var groupCopy = group;
                            shader.RenderEachPass(() =>
                            {
                                Instance.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, groupCopy.IndexBuffer.IndexCount / 3);
                            });

                            if (mat.NoCull && !isWindingFlipped)
                            {
                                Instance.GraphicsDevice.RasterizerState = cullmode;
                            }

                            Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                        }
                        else
                        {
                            MatGroup groupCopy = group;
                            bool isEntityOwned = brushData.IsEntity;
                            bool wasWindingFlipped = isWindingFlipped;

                            TransparentRenderQueue.RegisterBrushFace(isEntityOwned, transparentLeafRank, transparentSortPosition, () =>
                            {
                                Instance.GraphicsDevice.SetVertexBuffer(GlobalMapData.ActiveMap.Brushes[brush].BrushVertexBuffer);
                                Instance.GraphicsDevice.Indices = groupCopy.IndexBuffer;

                                var shader = GlobalMapData.LoadedMaterials.Length <= groupCopy.MaterialID ? Instance.WorldShader :
                                                                            (ShaderHandle)GlobalMapData.LoadedMaterials[groupCopy.MaterialID].Shader;

                                ApplyMaterialTechnique(mat, shader);
                                shader.Param("DisableLighting").SetValue(false);

                                var cubemapTexture = Skybox.GetSkyTexture();

                                if (EnvCubemap.Cubemaps != null && EnvCubemap.Cubemaps.Count > 0 && !EnvCubemap.cubeRendering)
                                {
                                    var cube = CubemapHandler.GetFaceCubemap(brush, f);

                                    if (cube != null && cube.diffusionMaps != null)
                                    {
                                        cubemapTexture = cube.diffusionMaps[ShowMaterialShine ? 0 : 3];
                                    }
                                }

                                shader.Param("cubemap").SetValue(cubemapTexture);
                                shader.Param("cubemapSize").SetValue(128 >> (ShowMaterialShine ? 0 : 3));

                                shader.Param("World").SetValue(brushTranslation * WorldMatrix);

                                shader.Param("BrushTex").SetValue(ShowBlankTexture ? DimTexture : GlobalMapData.LoadedMaterials.Length <= groupCopy.MaterialID ? ErrorTexture : GlobalMapData.LoadedMaterials[groupCopy.MaterialID].Texture);
                                shader.Param("BrushNorm").SetValue(GlobalMapData.LoadedMaterials.Length <= groupCopy.MaterialID ? WhiteTexture : GlobalMapData.LoadedMaterials[groupCopy.MaterialID].Normal);
                                shader.Param("BrushSpec").SetValue(GlobalMapData.LoadedMaterials.Length <= groupCopy.MaterialID ? WhiteTexture : ShowMaterialShine ? WhiteTexture : GlobalMapData.LoadedMaterials[groupCopy.MaterialID].Specular);

                                foreach (var tex in GlobalMapData.LoadedMaterials[groupCopy.MaterialID].GetExtraTextures())
                                {
                                    shader.Param(tex.Key).SetValue(tex.Value);
                                }

                                if (GlobalMapData.LoadedMaterials.Length > groupCopy.MaterialID &&
                                    GlobalMapData.LoadedMaterials[groupCopy.MaterialID].GetFlag("receiveRefractionTexture"))
                                {
                                    shader.Param("RefractionTex").SetValue(refractionRenderTexture);
                                    shader.Param("RefractionDepth").SetValue(ScreenRenderTexture.DepthTexture);
                                }

                                Instance.GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;

                                var baseCull = wasWindingFlipped ? RasterizerState.CullCounterClockwise : RasterizerState.CullClockwise;
                                bool applyNoCull = mat.NoCull && !wasWindingFlipped;

                                Instance.GraphicsDevice.RasterizerState = applyNoCull ? RasterizerState.CullNone : baseCull;

                                shader.RenderEachPass(() =>
                                {
                                    Instance.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, groupCopy.IndexBuffer.IndexCount / 3);
                                });

                                Instance.GraphicsDevice.RasterizerState = baseCull;

                                Instance.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                            });
                        }
                    }
                }
            }

            Instance.WorldShader.Param("DisableLighting")?.SetValue(true);

            if (CurrentWireframeDisplayMode != 0 && drawWireframe)
            {
                var old = Instance.GraphicsDevice.DepthStencilState;
                Instance.GraphicsDevice.DepthStencilState = DepthStencilState.None;
                if (CurrentWireframeDisplayMode == 1 || CurrentWireframeDisplayMode == 3)
                {
                    var oldrasterizer = Instance.GraphicsDevice.RasterizerState;
                    Instance.WorldShader.Param("World").SetValue(brushTranslation * WorldMatrix);
                    Instance.WorldShader.Param("View").SetValue(ViewMatrix);
                    Instance.WorldShader.Param("Projection").SetValue(ProjectionMatrix);
                    Instance.WorldShader.Param("BrushTex").SetValue(WhiteTexture);
                    Instance.WorldShader.Param("ExpandWireframe").SetValue(true);

                    Instance.GraphicsDevice.RasterizerState = WireframeRasterizerState;
                    Instance.GraphicsDevice.SetVertexBuffer(GlobalMapData.ActiveMap.Brushes[brush].BrushVertexBuffer);
                    foreach (ref readonly Face face in GlobalMapData.ActiveMap.Brushes[brush].Faces.AsSpan())
                    {
                        if (face.FaceIndices == null) continue;

                        Instance.GraphicsDevice.Indices = face.FaceIndices;

                        var faceCopy = face;
                        Instance.WorldShader.RenderEachPass(() => Instance.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, faceCopy.Indices.Length / 3));
                    }
                    Instance.GraphicsDevice.RasterizerState = oldrasterizer;
                    Instance.WorldShader.Param("ExpandWireframe").SetValue(false);
                }
                else
                {
                    var verts = CMath.GetDebugEdges(GlobalMapData.ActiveMap.BrushBounds[brush]);
                    debugBuffer = new VertexBuffer(Instance.GraphicsDevice, typeof(VertexPosition), verts.Length, BufferUsage.WriteOnly);
                    debugBuffer.SetData(verts);
                    Instance.WorldShader.RenderEachPass(() =>
                    Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, verts, 0, verts.Length / 2));
                }
                Instance.GraphicsDevice.DepthStencilState = old;
                Instance.GraphicsDevice.Indices = null;
            }

            Instance.GraphicsDevice.SetVertexBuffer(null);
        }
    }
}

public class SimpleFps
{
    private double frames = 0;
    private double elapsed = 0;
    private double last = 0;
    private double now = 0;

    public double msgFrequency = 1.0f;
    public string msg = "";

    public double lastFps = 0;

    public double instantFps = 0;

    public void Update(GameTime gameTime)
    {
        now = gameTime.TotalGameTime.TotalSeconds;
        elapsed = now - last;

        double frameTime = gameTime.ElapsedGameTime.TotalSeconds;
        if (frameTime > 0)
        {
            instantFps = 1.0 / frameTime;
        }

        if (elapsed > msgFrequency)
        {
            lastFps = frames / elapsed;

            elapsed = 0;
            frames = 0;
            last = now;
        }
    }

    public void DrawFps(Vector2 fpsDisplayPosition, Color fpsTextColor)
    {
        msg = $" Fps (instant): {instantFps:F1}\n" +
              $" Fps (smooth): {lastFps:F1}\n" +
              $" Fps max setting: {MainEngine.MaxFPS.GetValue():F1}\n";

        ImGui.Text(msg);
        frames++;
    }

    public void UpdateFPS()
    {
        frames++;
    }
}