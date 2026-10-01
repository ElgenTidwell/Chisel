using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaInside.MonoGame;
using Chisel;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGame.Extended;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rockwall;
using Rockwall2.Editor.Common;
using Rockwall2.Editor.Common.Input;
using Rockwall2.Editor.Common.Utils;
using Rockwall2.Editor.Mapper.Utils;
using Rockwall2.Editor.Mapper.ViewportManagement;
using Rockwall2.Tools;
using Rockwall2.Views;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using static Rockwall2.Editor.Common.EditorOverrides;

namespace Rockwall2.Editor.Mapper;

public class MapperView : IEditorScene
{
    public static MapperView Instance;

    private EditorHost host;
    public SpriteBatch SpriteBatch = default!;
    public BasicEffect BasicEffect;
    public FontSystem FontSystem;

    private Texture2D nomaptex;
    private Texture2D hintTex;
    private Vector2 screenCenter;
    private Effect worldShader;
    private Effect terrainShader;
    private RasterizerState worldRasterizer;
    private RasterizerState wireRasterizer;
    private RasterizerState normalRasterizer;
    private RasterizerState spriteRasterizer;
    private RasterizerState dualRasterizer;
    private List<(Brush b, Face f, int bID, int fID)> transparentFaceQueue = new List<(Brush b, Face f, int bID, int fID)>();
    private BoundingFrustum frustum;

    private readonly HashSet<int> selectedBrushes = new();
    private readonly HashSet<(int, int)> selectedFaces = new();

    private readonly Dictionary<int, (Vector3[] verts, (int, int)[] edges)> edgeCache = new();

    public static Vector3 HighlightColor = new Vector3(1.0f, 0.64f, 0.6f);
    public static Vector3 SelectionColorNormal = new Vector3(1.0f, 0.24f, 0.2f);
    public static Vector3 SelectionColor;
    static Queue<(Texture2D tex, Vector3 pos, float scale, Color tint, BlendState blend, DepthStencilState depth)> sprites = new();

    static Queue<(Face face, int brush)> faceLines = new Queue<(Face face, int brush)>();
    static Queue<(Face face, int brush)> skyboxLines = new Queue<(Face face, int brush)>();
    static Queue<(Vector2 a, Vector2 b, Vector4 color, float thick)> edgeLines = new Queue<(Vector2 a, Vector2 b, Vector4 color, float thick)>();
    static Queue<(Matrix world, string mdlName)> modelsToDraw = new Queue<(Matrix world, string mdlName)>();
    static Queue<EntityReference> decalsToDraw = new Queue<EntityReference>();
    static VertexBuffer billSpriteBuffer;
    static VertexPositionTexture[] billSprite = new VertexPositionTexture[6]
    {
        new VertexPositionTexture(new Vector3(0,0,0), new Vector2(1,0)),
        new VertexPositionTexture(new Vector3(1,0,0), new Vector2(0,0)),
        new VertexPositionTexture(new Vector3(0,1,0), new Vector2(1,1)),

        new VertexPositionTexture(new Vector3(0,1,0), new Vector2(1,1)),
        new VertexPositionTexture(new Vector3(1,0,0), new Vector2(0,0)),
        new VertexPositionTexture(new Vector3(1,1,0), new Vector2(0,1)),
    };
    static VertexPosition[] brushEntitySphere = BrushOperations.GenerateSphereVerticesDirect(Vector3.Zero, 1, 8, 16).Reverse().ToArray();
    public static List<VertexPositionColor> arrowVerts = new();
    public static List<int> arrowTris = new();

    // Flattened, cached copies of arrowVerts/arrowTris built once in LoadContent, so
    // DrawEntities doesn't re-allocate arrays via ToArray() for every entity every frame.
    static VertexPositionColor[] cachedArrowVerts;
    static int[] cachedArrowTris;

    // Per-classname entity bounds cache, same idea as resolvedIconCache below.
    static Dictionary<string, BoundingBox> boundsCache = new Dictionary<string, BoundingBox>();

    // Reflection field lookups for visualizer types are stable for the lifetime of the
    // process, so cache them instead of calling GetFields() per entity per frame.
    static Dictionary<Type, FieldInfo[]> visualizerFieldCache = new Dictionary<Type, FieldInfo[]>();

    private readonly List<VertexPositionColor> wireframeLines = new(1024);
    private readonly List<VertexPositionColor> wireframeSelLines = new(256);
    private readonly List<VertexPositionColor> entityLines = new(256);
    private readonly List<VertexPositionColor> gridLines = new(512);

    static Dictionary<string, Texture2D> entitySpriteIcons = new Dictionary<string, Texture2D>();
    static Dictionary<string, Texture2D> resolvedIconCache = new Dictionary<string, Texture2D>();
    static Dictionary<string, ModelDisplay> entityModels = new Dictionary<string, ModelDisplay>();

    static PlaneGrid defaultGridViewer;

    bool prevLeftDown;
    float trackTime = 0f;
    Vector3 trackTarget = Vector3.Zero;

    int targWidth;
    int targHeight;

    List<(int, int)[]> savedFaceEdges = new List<(int, int)[]>();

    EditorViewport midPanViewport;
    bool midPanSuppressed;

    double totalTime;

    public MapperView()
    {
        Instance = this;
    }

    public void Attach(EditorHost host)
    {
        this.host = host;
        SpriteBatch = new SpriteBatch(host.GraphicsDevice);
        BasicEffect = new BasicEffect(host.GraphicsDevice);
        BasicEffect.TextureEnabled = false;
        BasicEffect.LightingEnabled = false;
        BasicEffect.DirectionalLight0.Enabled = true;
        BasicEffect.DirectionalLight0.Direction = Vector3.Normalize(-Vector3.One);
        BasicEffect.DirectionalLight0.DiffuseColor = Vector3.One;

        LoadContent();
    }
    public void Detach()
    {
        // This should hopefully let models """hot reload"""
        entityModels.Clear();
    }
    public void Resize(int w, int h)
    {
        targWidth = w;
        targHeight = h;
        if (host.GraphicsDevice != null) host.GraphicsDevice.Viewport = new Viewport(0, 0, w, h);
        if (ViewportManager.Layout != null)
        {
            Viewport3DCamera.RebuildMatrix();
            ViewportManager.Recompute(w, h);
        }
        else
        {
            Console.WriteLine("RecalculateSize: Viewport not ready.");
        }
    }

    private void LoadContent()
    {
        Instance = this;

        //Yucky that this one does all of this init stuff... Moving it anywhere else somehow broke?

        GlobalEditorData.EDSFile = ConfigManager.currentConfig.GameEDF;

        if(File.Exists($"{ConfigManager.currentConfig.EditorAssetsPath}/editor_settings.cfg"))
        {
            GlobalEditorData.EditorOverrides = JsonConvert.DeserializeObject<EditorOverrides>(File.ReadAllText($"{ConfigManager.currentConfig.EditorAssetsPath}/editor_settings.cfg"));

            EditorOverrides defaultSettings = JsonConvert.DeserializeObject<EditorOverrides>(File.ReadAllText($"{host.Content.RootDirectory}/def_conf.cfg"));

            GlobalEditorData.EditorOverrides.overrides = GlobalEditorData.EditorOverrides.overrides.Concat(defaultSettings.overrides.Where(e => !GlobalEditorData.EditorOverrides.overrides.Contains(e))).ToArray();

            GlobalEditorData.WorkingDirectory = Path.IsPathRooted(GlobalEditorData.EditorOverrides.workingdir) ? GlobalEditorData.EditorOverrides.workingdir : Path.GetFullPath(Path.Combine(ConfigManager.currentConfig.EditorAssetsPath, GlobalEditorData.EditorOverrides.workingdir));
        }
        else
        {
            GlobalEditorData.EditorOverrides = default;
            GlobalEditorData.EditorOverrides.overrides = Array.Empty<EntityOverrideAsset>();

            GlobalEditorData.WorkingDirectory = ConfigManager.currentConfig.EditorAssetsPath;
        }

        worldShader = host.Content.Load<Effect>("Shaders/WorldShader");
        terrainShader = host.Content.Load<Effect>("Shaders/TerrainShader");
        nomaptex = host.Content.Load<Texture2D>("rockwall2_shillouette");

        ErrorModel.Create(host.GraphicsDevice);

        entitySpriteIcons.Add("GroundNode", host.Content.Load<Texture2D>("Graphics/ainode"));
        entitySpriteIcons.Add("PointLight", host.Content.Load<Texture2D>("Graphics/plight"));
        entitySpriteIcons.Add("SpotLight", host.Content.Load<Texture2D>("Graphics/slight"));
        entitySpriteIcons.Add("DirectionalLight", host.Content.Load<Texture2D>("Graphics/dlight"));
        entitySpriteIcons.Add("Soundscape", host.Content.Load<Texture2D>("Graphics/soundscape"));
        entitySpriteIcons.Add("RelayEntity", host.Content.Load<Texture2D>("Graphics/relay"));
        entitySpriteIcons.Add("LogicCompare", host.Content.Load<Texture2D>("Graphics/compare"));
        entitySpriteIcons.Add("LogicPrint", host.Content.Load<Texture2D>("Graphics/print"));
        entitySpriteIcons.Add("LogicArithmetic", host.Content.Load<Texture2D>("Graphics/add"));

        hintTex = host.Content.Load<Texture2D>("Graphics/hint");

        LoadAllExternalGameData();

        GlobalEditorData.LoadTex();

        // Reset content directory to our local content.
        host.Content.RootDirectory = "Content";

        MainWindow.Instance.MapEditor.RefreshViews();

        //MainWindow.Instance.UpdateInfo();

        //RokMessages.Append("{ Rockwall : Internal System }" + $" MG initialized OK!");

        ViewportManager.Initialize();
        ViewportManager.Recompute(host.GraphicsDevice.Viewport.Width, host.GraphicsDevice.Viewport.Height);

        Viewport3DCamera.position = new Vector3(4, 4, 4);

        Viewport3DCamera.RebuildMatrix();

        worldRasterizer = new RasterizerState { CullMode = CullMode.CullClockwiseFace };
        normalRasterizer = new RasterizerState { CullMode = CullMode.CullCounterClockwiseFace };
        dualRasterizer = new RasterizerState { CullMode = CullMode.None };
        wireRasterizer = new RasterizerState { CullMode = CullMode.CullClockwiseFace, FillMode = FillMode.WireFrame };
        spriteRasterizer = new RasterizerState { CullMode = CullMode.None };

        FontSystem = new FontSystem();
        FontSystem.AddFont(File.ReadAllBytes(@$"{host.Content.RootDirectory}/default.ttf"));

        GizmoTranslate.Mode = GizmoTranslate.GizmoMode.Translate;
        GizmoTranslate.GenerateGeometry();
        GizmoRotate.GenerateGeometry();

        billSpriteBuffer = new VertexBuffer(host.GraphicsDevice, typeof(VertexPositionTexture), 6, BufferUsage.WriteOnly);
        billSpriteBuffer.SetData(billSprite);

        BrushOperations.AddAxis(arrowVerts, arrowTris, -Vector3.Forward, Color.CornflowerBlue, 0.6f, 0.1f, 0.02f, 0.07f);

        // Flatten once here; arrowVerts/arrowTris never change after this point.
        cachedArrowVerts = arrowVerts.ToArray();
        cachedArrowTris = arrowTris.ToArray();

        defaultGridViewer = new PlaneGrid(host.GraphicsDevice, 1, 2, new Plane(Vector3.Up, 0));
        defaultGridViewer.SetColor(Vector3.One * 0.2f);
    }
    // .editorIcons/{classname}.png overrides the built-in sprite for ANY classname
    public Texture2D IconFor(string classname)
    {
        if (resolvedIconCache.TryGetValue(classname, out var cached)) return cached;

        Texture2D resolved = null;
        var overridePath = Path.Combine(GlobalEditorData.WorkingDirectory, ".editorIcons", $"{classname}.png");
        if (File.Exists(overridePath))
        {
            using var stream = File.OpenRead(overridePath);
            resolved = Texture2D.FromStream(host.GraphicsDevice, stream);
        }
        else entitySpriteIcons.TryGetValue(classname, out resolved);

        resolvedIconCache[classname] = resolved;
        return resolved;
    }
    public static BoundingBox BoundsFor(string classname)
    {
        if (boundsCache.TryGetValue(classname, out var cachedBox)) return cachedBox;

        BoundingBox result;

        var overrideID = Array.FindIndex(GlobalEditorData.EditorOverrides.overrides, o => o.name == classname);
        if (overrideID != -1)
        {
            result = new BoundingBox(GlobalEditorData.EditorOverrides.overrides[overrideID].boundsMin, GlobalEditorData.EditorOverrides.overrides[overrideID].boundsMax);
        }
        else if (GlobalEditorData.RegisteredEntityMeta.TryGetValue(classname, out var meta) && meta.BoundsMax != Vector3.Zero)
        {
            result = new BoundingBox(meta.BoundsMin, meta.BoundsMax);
        }
        else
        {
            result = new BoundingBox(Vector3.One * -0.25f, Vector3.One * 0.25f);
        }

        boundsCache[classname] = result;
        return result;
    }
    public void DrawBillSprite(Texture2D tex, Vector3 pos, float scale = 1f, Color? tint = null, BlendState blend = null, DepthStencilState depth = null)
    {
        sprites.Enqueue((tex, pos, scale, tint ?? Color.White, blend ?? BlendState.NonPremultiplied, depth ?? DepthStencilState.Default));
    }
    public void Track(Vector3 point)
    {
        trackTime = 1;
        trackTarget = point;
    }
    public void Update(GameTime gameTime)
    {
        if (MainWindow.Instance.MapEditor == null || !MainWindow.Instance.MapEditor.IsVisible)
        {
            return;
        }

        var rawMouse = MouseManager.GetXnaPositionRelativeTo(MainWindow.Instance.GameView);
        var mouseGlobal = new Vector2(rawMouse.X, rawMouse.Y);

        if (midPanViewport != null)
        {
            var rect = midPanViewport.PixelRect;
            bool inside = mouseGlobal.X >= rect.X && mouseGlobal.X <= rect.X + rect.Width &&
                          mouseGlobal.Y >= rect.Y && mouseGlobal.Y <= rect.Y + rect.Height;

            if (!inside)
            {
                midPanViewport = null;
                midPanSuppressed = true;
            }
        }

        float delta = (float)gameTime.ElapsedGameTime.TotalSeconds;
        totalTime = gameTime.TotalGameTime.TotalSeconds;

        screenCenter = host.GraphicsDevice.Viewport.Bounds.Center.ToVector2();
        SelectionColor = SelectionColorNormal * (MathF.Sin((float)gameTime.TotalGameTime.TotalSeconds * 4) * 0.2f + 0.95f);
        defaultGridViewer.SetSpacing(Transformable.GridSize);

        ViewportManager.Recompute(targWidth, targHeight);

        if (KeyboardManager.IsDown(Key.LeftCtrl) && KeyboardManager.IsPressed(Key.S) && MapTools.MapLoaded)
        {
            FileHandler.SaveCurrentMap();
        }
        if (KeyboardManager.IsDown(Key.LeftCtrl) && KeyboardManager.IsPressed(Key.Z) && Toolbelt.UndoManager.CanUndo)
        {
            Toolbelt.UndoManager.Undo();
        }
        if (KeyboardManager.IsDown(Key.LeftCtrl) && KeyboardManager.IsPressed(Key.Y) && Toolbelt.UndoManager.CanRedo)
        {
            Toolbelt.UndoManager.Redo();
        }
        if (KeyboardManager.IsReleased(Key.Tab) || (ViewportManager.Active.IsPerspective && MouseManager.IsDoubleClicked(MouseButton.Left)))
        {
            var entityRefs = Toolbelt.SelectedObjects.OfType<EntityMoveable>()
                .Select(i => MapTools.Entities[i.entity])
                .Concat(Toolbelt.SelectedObjects.OfType<BrushMoveable>()
                    .Select(b => MapTools.GetOwningEntity(b.brush)))
                .Concat(Toolbelt.SelectedObjects.OfType<FaceMoveable>()
                    .Select(f => MapTools.GetOwningEntity(f.brush)))
                .Where(e => e != null)
                .Distinct();
            if (entityRefs.Any())
            {
                OpenEntityInspector(entityRefs);
            }
            else
            {
                var hintRef = Toolbelt.SelectedObjects.OfType<HintMoveable>().Select(h => MapTools.Hints[h.hint]).FirstOrDefault();

                if (hintRef != null)
                {
                    Toolbelt.OpenHint(hintRef, () => { });
                }
            }
        }
        if (KeyboardManager.IsPressed(Key.B) && KeyboardManager.IsDown(Key.LeftCtrl)) { MapTools.BuildMap(KeyboardManager.IsDown(Key.LeftShift)); }
        if (KeyboardManager.IsPressed(Key.OemOpenBrackets)) { Transformable.GridSize /= 2f; Dispatcher.UIThread.Post(() => MainWindow.Instance.mapEditor.RefreshGridSize()); }
        if (KeyboardManager.IsPressed(Key.OemCloseBrackets)) { Transformable.GridSize *= 2f; Dispatcher.UIThread.Post(() => MainWindow.Instance.mapEditor.RefreshGridSize()); }

        for (int i = 0; i < 9; i++)
        {
            if (KeyboardManager.IsPressed(Key.D1 + i))
                Toolbelt.ActivateHotbarSlot(i);
        }

        if (trackTime > 0)
        {
            trackTime -= delta;
            if (trackTime < 0) trackTime = 0;

            Viewport3DCamera.position = Vector3.Lerp(trackTarget - Matrix.Invert(Viewport3DCamera.viewMatrix).Forward, Viewport3DCamera.position, trackTime * trackTime);
        }

        for (int i = 0; i < ViewportManager.Layout.All.Length; i++)
        {
            var vp = ViewportManager.Layout.All[i];

            float zoomDelta = float.Abs(vp.TargetZoom - vp.Zoom);

            if (zoomDelta > 0)
            {
                float oldZoom = vp.Zoom;
                vp.Zoom = OtherMath.MoveTowards(vp.Zoom, vp.TargetZoom, delta * float.Max(zoomDelta, 0.1f) * 15f);

                if (vp.Is2D)
                {
                    var local = vp.GlobalToLocal(mouseGlobal);
                    if (local.HasValue)
                    {
                        var offset = local.Value - new Vector2(vp.Width, vp.Height) * 0.5f;
                        float zoomShift = 1f / oldZoom - 1f / vp.Zoom;
                        vp.Pan = new Vector2(vp.Pan.X + offset.X * zoomShift, vp.Pan.Y - offset.Y * zoomShift);
                    }
                }
            }

            if (trackTime > 0 && vp.Is2D)
            {
                var targ = new Vector2(Vector3.Dot(vp.RightAxis, trackTarget), Vector3.Dot(vp.UpAxis, trackTarget));
                vp.Pan = Vector2.Lerp(targ, vp.Pan, trackTime * trackTime);
            }
        }

        if (!MainWindow.Instance.IsActive || !MapTools.MapLoaded) return;

        if (!MainWindow.Instance.GameView.IsPointerOver && ViewportDragLock.Locked == null) return;

        ViewportManager.MouseGlobal = mouseGlobal;

        ViewportManager.UpdateMatrices(host.GraphicsDevice);

        bool leftDown = MouseManager.IsDown(MouseButton.Left);
        ViewportManager.Layout.UpdateDrag(mouseGlobal, leftDown, prevLeftDown);

        Viewport3DCamera.AspectRatio = ViewportManager.Perspective.PixelRect.Width / (float)ViewportManager.Perspective.PixelRect.Height;

        var splitterCursor = ViewportManager.Layout.CursorFor(mouseGlobal);
        if (splitterCursor != StandardCursorType.Arrow)
        {
            MouseManager.SetCursor(MainWindow.Instance, splitterCursor);
            prevLeftDown = leftDown;
            if (ViewportManager.Layout.IsDraggingSplitter) return;
        }
        else
        {
            if (!Viewport3DCamera.flyPanMode)
                MouseManager.ResetCursor(MainWindow.Instance);
        }

        var hovered = ViewportManager.Layout.HitTest(mouseGlobal);
        bool midMouseDown = MouseManager.IsDown(Avalonia.Input.MouseButton.Middle);

        if (midMouseDown)
        {
            ViewportDragLock.Locked = null;
            ViewportManager.Active = hovered ?? ViewportManager.Active;
        }
        else
        {
            ViewportDragLock.Tick(hovered, mouseGlobal, delta);
            ViewportManager.Active = ViewportDragLock.Locked ?? hovered ?? ViewportManager.Active;
        }

        var active = ViewportManager.Active;

        Viewport3DCamera.Update(gameTime);

        if (KeyboardManager.IsDown(Key.Space) || Viewport3DCamera.flyPanMode)
        {
            Toolbelt.HighlightedObject = null;
            prevLeftDown = leftDown;
            return;
        }

        if (KeyboardManager.IsDown(Key.F) && !KeyboardManager.IsHeld(Key.LeftCtrl) && Toolbelt.SelectedObjects.Count > 0)
        {
            trackTime = 1;

            var pos = Vector3.Zero;
            foreach (var obj in Toolbelt.SelectedObjects) pos += obj.GetPosition();
            pos /= Toolbelt.SelectedObjects.Count;

            trackTarget = pos;
        }

        var panActive = midPanViewport ?? active;

        if (panActive.Is2D)
        {
            var localMouse = panActive.GlobalToLocal(mouseGlobal) ?? Vector2.Zero;
            HandlePan2D(panActive, localMouse);

            int wheel = float.Sign(MouseManager.ScrollDelta);

            if (wheel > 0) panActive.TargetZoom *= 1.25f;
            if (wheel < 0) panActive.TargetZoom /= 1.25f;

            Toolbelt.ActiveTool?.OnUpdate(delta);
            prevLeftDown = leftDown;
            return;
        }

        Toolbelt.ActiveTool?.OnUpdate(delta);
        prevLeftDown = leftDown;
    }

    Vector2 panStart;
    Vector2 panStartWorld;
    bool wasMidDown;

    void HandlePan2D(EditorViewport vp, Vector2 localMouse)
    {
        bool midDown = MouseManager.IsDown(Avalonia.Input.MouseButton.Middle);

        if (!midDown)
        {
            midPanSuppressed = false;
            if (wasMidDown) midPanViewport = null;
            wasMidDown = false;
            return;
        }

        if (midPanSuppressed)
        {
            wasMidDown = midDown;
            return;
        }

        if (!wasMidDown)
        {
            panStart = localMouse;
            panStartWorld = vp.Pan;
            midPanViewport = vp;
        }

        var d = localMouse - panStart;
        vp.Pan = panStartWorld + new Vector2(-d.X / vp.Zoom, d.Y / vp.Zoom);

        wasMidDown = midDown;
    }

    public void Draw(GameTime gameTime)
    {
        if (!MainWindow.Instance.mapEditor.IsVisible) return;

        host.GraphicsDevice.Viewport = new Viewport(0, 0, targWidth, targHeight);

        float delta = (float)gameTime.ElapsedGameTime.TotalSeconds;

        host.GraphicsDevice.Clear(new Color(14, 14, 14));

        if (!MapTools.MapLoaded)
        {
            var center = host.GraphicsDevice.Viewport.Bounds.Center.ToVector2();
            SpriteBatch.Begin();
            SpriteBatch.Draw(nomaptex,
                new RectangleF(center - Vector2.One * 512, new SizeF(1024, 1024)).ToRectangle(),
                Color.White);
            SpriteBatch.End();
            return;
        }

        RefreshSavedFaceEdges();

        ViewportManager.UpdateMatrices(host.GraphicsDevice);

        for (int i = Toolbelt.ActiveBuilders.Count - 1; i >= 0; i--)
        {
            if (!Toolbelt.ActiveBuilders[i].IsOpen) Toolbelt.RemoveBuilder(Toolbelt.ActiveBuilders[i]);
        }

        for (int i = 0; i < ViewportManager.Layout.All.Length; i++)
        {
            var vp = ViewportManager.Layout.All[i];
            if (vp.Width <= 0 || vp.Height <= 0) continue;

            host.GraphicsDevice.Viewport = new Viewport(vp.PixelRect.X, vp.PixelRect.Y, vp.PixelRect.Width, vp.PixelRect.Height);

            ViewportManager.Rendering = vp;

            if (vp.IsPerspective)
                Draw3DViewport(vp, delta);
            else
            {
                Draw2DViewport(vp, delta);
            }

            Toolbelt.ActiveTool?.OnRender(delta);
            foreach (var b in Toolbelt.ActiveBuilders)
            {
                b.OnRender(delta, host.GraphicsDevice, BasicEffect);
            }

            SpriteBatch.Begin(blendState: BlendState.AlphaBlend);

            var font = FontSystem.GetFont(11);
            var size = font.MeasureString(vp.Label);

            SpriteBatch.FillRectangle(0, 0, size.X + 8, size.Y + 8, new Color(0, 0, 0, 100));

            SpriteBatch.DrawString(font, vp.Label, new Vector2(5, 4), Color.White);

            SpriteBatch.End();
        }
        host.GraphicsDevice.Viewport = new Viewport(0, 0, targWidth, targHeight);

        DrawSplitters();
    }
    // Rebuilds savedFaceEdges, reusing the cached per-brush edge list whenever that
    // brush's vertex array reference hasn't changed since the last frame (RebuildBrush
    // always assigns a fresh array on any real geometry change, so this is a cheap and
    // reliable dirty check without needing any explicit dirty flag from BrushOperations).
    void RefreshSavedFaceEdges()
    {
        savedFaceEdges.Clear();
        for (int bi = 0; bi < MapTools.Brushes.Length; bi++)
        {
            var verts = MapTools.Brushes[bi].Vertices;

            if (edgeCache.TryGetValue(bi, out var cached) && ReferenceEquals(cached.verts, verts))
            {
                savedFaceEdges.Add(cached.edges);
                continue;
            }

            var edges = OtherMath.GetUniqueEdges(bi);
            edgeCache[bi] = (verts, edges);
            savedFaceEdges.Add(edges);
        }
    }
    void UpdateFrustum()
    {
        frustum = new BoundingFrustum(Viewport3DCamera.worldMatrix * Viewport3DCamera.viewMatrix * Viewport3DCamera.projectionMatrix);
    }
    void Draw3DViewport(EditorViewport vp, float delta)
    {
        UpdateFrustum();

        BasicEffect.World = Viewport3DCamera.worldMatrix;
        BasicEffect.View = Viewport3DCamera.viewMatrix;
        BasicEffect.Projection = Viewport3DCamera.projectionMatrix;

        worldShader.Parameters["cameraPos"].SetValue(Viewport3DCamera.position);
        worldShader.Parameters["fogIntensity"].SetValue(0);
        if (Toolbelt.ShowFog)
        {
            var entity = MapTools.Entities.FirstOrDefault((e) => e.EntityName == "FogController");
            if (entity != null)
            {
                float fogStart = EntityTools.GetFloatProperty(entity, "Fog Start", 0f);
                float fogEnd = EntityTools.GetFloatProperty(entity, "Fog End", 0f);
                float fogIntensity = EntityTools.GetFloatProperty(entity, "Fog Intensity", 0f);
                Color fogColorXNA = EntityTools.GetColorProperty(entity, "Fog Color", Color.Black);

                worldShader.Parameters["fogIntensity"].SetValue(fogIntensity);
                worldShader.Parameters["fogColor"].SetValue(fogColorXNA.ToVector4());
                worldShader.Parameters["fogStart"].SetValue(fogStart);
                worldShader.Parameters["fogEnd"].SetValue(fogEnd);
            }
        }

        host.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        host.GraphicsDevice.BlendState = BlendState.Opaque;
        host.GraphicsDevice.SamplerStates[0] = SamplerState.AnisotropicWrap;

        DrawMapGeom();
        DrawEntities();
        DrawHints();
        DrawEntityLinks();
        DrawTerrain();
        DrawEdges();
        DrawModels();

        DrawTransparentMapGeom();

        DrawDecals();

        while (sprites.Count > 0)
        {
            var element = sprites.Dequeue();
            host.GraphicsDevice.BlendState = element.blend;
            host.GraphicsDevice.DepthStencilState = element.depth;
            host.GraphicsDevice.RasterizerState = spriteRasterizer;

            BasicEffect.DiffuseColor = Vector3.One;
            RenderBillSprite(element.tex, element.pos, element.scale, element.tint);

            host.GraphicsDevice.SetVertexBuffer(null);
            host.GraphicsDevice.BlendState = BlendState.AlphaBlend;
            host.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            host.GraphicsDevice.RasterizerState = worldRasterizer;
        }
        while (faceLines.Count > 0)
        {
            host.GraphicsDevice.BlendState = BlendState.AlphaBlend;
            host.GraphicsDevice.RasterizerState = spriteRasterizer;
            host.GraphicsDevice.DepthStencilState = DepthStencilState.None;

            BasicEffect.DiffuseColor = Vector3.One;

            var element = faceLines.Dequeue();

            DrawFaceOutline(element.face, element.brush);

            host.GraphicsDevice.BlendState = BlendState.AlphaBlend;
            host.GraphicsDevice.RasterizerState = worldRasterizer;
            host.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        }

        DrawBrushEntityIndicators();

        DrawHintsText();

        DrawLeakLine();

        BasicEffect.World = Viewport3DCamera.worldMatrix;
        BasicEffect.View = Viewport3DCamera.viewMatrix;
        BasicEffect.Projection = Viewport3DCamera.projectionMatrix;

        host.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        host.GraphicsDevice.BlendState = BlendState.NonPremultiplied;

        defaultGridViewer.DrawBoundedGrid(Viewport3DCamera.viewMatrix, Viewport3DCamera.projectionMatrix, Viewport3DCamera.position - Vector3.One * 20, Viewport3DCamera.position + Vector3.One * 20, 1f / (float.Abs(Viewport3DCamera.position.Y) + 1), true);

        host.GraphicsDevice.BlendState = BlendState.AlphaBlend;
        host.GraphicsDevice.DepthStencilState = DepthStencilState.None;

        SpriteBatch.Begin(blendState: BlendState.NonPremultiplied);

        if (Viewport3DCamera.flyPanMode)
        {
            SpriteBatch.DrawCircle(new CircleF(ViewportManager.Perspective.PixelRect.Center.ToVector2(), 4), 16, Color.White);
        }
        while (edgeLines.Count > 0)
        {
            var element = edgeLines.Dequeue();

            SpriteBatch.DrawLine(element.a, element.b, new Color(element.color), element.thick);
        }

        SpriteBatch.End();
    }
    void Draw2DViewport(EditorViewport vp, float delta)
    {
        host.GraphicsDevice.DepthStencilState = DepthStencilState.None;
        host.GraphicsDevice.BlendState = BlendState.AlphaBlend;

        BasicEffect.World = vp.WorldMatrix;
        BasicEffect.View = vp.ViewMatrix;
        BasicEffect.Projection = vp.ProjectionMatrix;
        BasicEffect.DiffuseColor = Vector3.One;
        BasicEffect.Alpha = 1;

        Draw2DGrid(vp);
        Draw2DWireframes(vp);
        Draw2DEntities(vp);
        Draw2DTerrain(vp);
    }
    void Draw2DWireframes(EditorViewport vp)
    {
        if (MapTools.Brushes == null) return;
        wireframeLines.Clear();
        wireframeSelLines.Clear();
        Color normal = new Color(160, 160, 160);
        Color selected = new Color(255, 80, 50);
        Color highlight = new Color(255, 160, 140);

        for (int bi = 0; bi < MapTools.Brushes.Length; bi++)
        {
            if (VisGroupManager.IsBrushHidden(bi)) continue;

            var brush = MapTools.Brushes[bi];
            if (brush.Vertices == null) continue;

            bool isSel = false, isHi = false;
            for (int si = 0; si < Toolbelt.SelectedObjects.Count; si++)
            {
                var o = Toolbelt.SelectedObjects[si];
                if ((o is BrushMoveable bm && bm.brush == bi) || (o is FaceMoveable fm && fm.brush == bi) ||
                    (o is TerrainMoveable tm && tm.terrain >= 0 && tm.terrain < MapTools.Terrains.Length && MapTools.Terrains[tm.terrain].BrushSource == bi))
                { isSel = true; break; }
            }
            if (!isSel)
                isHi = (Toolbelt.HighlightedObject is BrushMoveable hb && hb.brush == bi) ||
                       (Toolbelt.HighlightedObject is FaceMoveable hf && hf.brush == bi) ||
                       (Toolbelt.HighlightedObject is TerrainMoveable ht && ht.terrain >= 0 && ht.terrain < MapTools.Terrains.Length && MapTools.Terrains[ht.terrain].BrushSource == bi);

            Color c = isSel ? selected : isHi ? highlight : normal;
            var target = (isSel || isHi) ? wireframeSelLines : wireframeLines;

            foreach (var line in savedFaceEdges[bi])
            {
                target.Add(new VertexPositionColor(brush.Vertices[line.Item1] + brush.Position, c));
                target.Add(new VertexPositionColor(brush.Vertices[line.Item2] + brush.Position, c));
            }
        }

        BasicEffect.VertexColorEnabled = true;
        if (wireframeLines.Count > 1)
        {
            var arr = wireframeLines.ToArray();
            foreach (var pass in BasicEffect.CurrentTechnique.Passes) { pass.Apply(); host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, arr, 0, arr.Length / 2); }
        }
        if (wireframeSelLines.Count > 1)
        {
            var arr = wireframeSelLines.ToArray();
            foreach (var pass in BasicEffect.CurrentTechnique.Passes) { pass.Apply(); host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, arr, 0, arr.Length / 2); }
        }
        BasicEffect.VertexColorEnabled = false;
    }
    void Draw2DEntities(EditorViewport vp)
    {
        if (MapTools.Entities == null) return;
        entityLines.Clear();
        Color c = new Color(100, 200, 255);
        float s = 4f / vp.Zoom;
        float arrow = 1f;
        SpriteBatch.Begin(blendState: BlendState.NonPremultiplied);
        for (int i = 0; i < MapTools.Entities.Length; i++)
        {
            var ent = MapTools.Entities[i];
            if (ent.IsBrushEntity) continue;
            if (VisGroupManager.IsEntityHidden(ent)) continue;

            var box = BoundsFor(ent.EntityName);

            bool isHighlighted = false;
            if (Toolbelt.HighlightedObject is EntityMoveable entityMoveable)
            {
                isHighlighted = entityMoveable.entity == i;
            }
            bool isSelected = Toolbelt.SelectedObjects.Any(m =>
            {
                if (m is EntityMoveable e) return e.entity == i;
                return false;
            });

            var sprite = IconFor(ent.EntityName);
            if (sprite != null)
            {
                Vector2 pos = ViewportManager.Rendering.WorldToLocal(ent.Position);
                float w = vp.Zoom;
                var color = isSelected ? SelectionColor : isHighlighted ? HighlightColor : Vector3.One;
                SpriteBatch.Draw(sprite, new RectangleF(pos - Vector2.One * w * 0.5f, Vector2.One * w).ToRectangle(), new Color(color.X, color.Y, color.Z, 0.2f));
            }

            var p = ent.Position;
            foreach (var point in BrushOperations.GetDebugEdges(box))
            {
                entityLines.Add(new VertexPositionColor(point.Position + p, isSelected ? new Color(SelectionColor) : isHighlighted ? new Color(HighlightColor) : c));
            }
            entityLines.Add(new VertexPositionColor(p - vp.RightAxis * s, c));
            entityLines.Add(new VertexPositionColor(p + vp.RightAxis * s, c));
            entityLines.Add(new VertexPositionColor(p - vp.UpAxis * s, c));
            entityLines.Add(new VertexPositionColor(p + vp.UpAxis * s, c));

            var fwd = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(ent.SpawnRotation.X), MathHelper.ToRadians(ent.SpawnRotation.Y), MathHelper.ToRadians(ent.SpawnRotation.Z));

            entityLines.Add(new VertexPositionColor(p, c));
            entityLines.Add(new VertexPositionColor(p - fwd.Forward * arrow, c));

            entityLines.Add(new VertexPositionColor(p - fwd.Forward * arrow, c));
            entityLines.Add(new VertexPositionColor(p - fwd.Forward * (arrow * 0.8f) + Vector3.TransformNormal(vp.UpAxis, fwd) * arrow * 0.25f, c));
            entityLines.Add(new VertexPositionColor(p - fwd.Forward * arrow, c));
            entityLines.Add(new VertexPositionColor(p - fwd.Forward * (arrow * 0.8f) - Vector3.TransformNormal(vp.UpAxis, fwd) * arrow * 0.25f, c));
        }
        SpriteBatch.End();
        if (entityLines.Count < 2) return;
        BasicEffect.VertexColorEnabled = true;
        foreach (var pass in BasicEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, entityLines.ToArray(), 0, entityLines.Count / 2);
        }
        BasicEffect.VertexColorEnabled = false;
    }
    void Draw2DGrid(EditorViewport vp)
    {
        gridLines.Clear();
        host.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        float gs = Transformable.GridSize;
        float hw = vp.Width * 0.5f / vp.Zoom;
        float hh = vp.Height * 0.5f / vp.Zoom;
        float minR = vp.Pan.X - hw - gs, maxR = vp.Pan.X + hw + gs;
        float minU = vp.Pan.Y - hh - gs, maxU = vp.Pan.Y + hh + gs;
        float startR = MathF.Floor(minR / gs) * gs;
        float startU = MathF.Floor(minU / gs) * gs;

        Color minor = new Color(25, 25, 25);
        Color major = new Color(45, 45, 45);
        Color axis = new Color(50, 50, 100);

        for (float r = startR; r <= maxR; r += gs)
        {
            bool isAxis = MathF.Abs(r) < gs * 0.5f;
            Color c = isAxis ? axis : (MathF.Abs(r % (gs * 8)) < gs * 0.5f ? major : minor);
            float d = isAxis ? 0 : (MathF.Abs(r % (gs * 8)) < gs * 0.5f ? 0.5f : 1f);
            gridLines.Add(new VertexPositionColor(r * vp.RightAxis + minU * vp.UpAxis - d * vp.ViewAxis, c));
            gridLines.Add(new VertexPositionColor(r * vp.RightAxis + maxU * vp.UpAxis - d * vp.ViewAxis, c));
        }
        for (float u = startU; u <= maxU; u += gs)
        {
            bool isAxis = MathF.Abs(u) < gs * 0.5f;
            Color c = isAxis ? axis : (MathF.Abs(u % (gs * 8)) < gs * 0.5f ? major : minor);
            float d = isAxis ? 0 : (MathF.Abs(u % (gs * 8)) < gs * 0.5f ? 0.5f : 1f);
            gridLines.Add(new VertexPositionColor(minR * vp.RightAxis + u * vp.UpAxis - d * vp.ViewAxis, c));
            gridLines.Add(new VertexPositionColor(maxR * vp.RightAxis + u * vp.UpAxis - d * vp.ViewAxis, c));
        }

        if (gridLines.Count < 2) return;
        BasicEffect.VertexColorEnabled = true;
        foreach (var pass in BasicEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, gridLines.ToArray(), 0, gridLines.Count / 2);
        }
        BasicEffect.VertexColorEnabled = false;
        host.GraphicsDevice.DepthStencilState = DepthStencilState.None;
    }
    void DrawSplitters()
    {
        var layout = ViewportManager.Layout;
        int sx = layout.SplitterXPx;
        int sy = layout.SplitterYPx;
        int w = host.GraphicsDevice.Adapter.CurrentDisplayMode.Width;
        int h = host.GraphicsDevice.Adapter.CurrentDisplayMode.Height;
        const int ST = 4;

        SpriteBatch.Begin();
        SpriteBatch.FillRectangle(new Rectangle(sx, 0, ST, h), new Color(40, 40, 40));
        SpriteBatch.FillRectangle(new Rectangle(0, sy, w, ST), new Color(40, 40, 40));
        SpriteBatch.End();
    }
    void RenderBillSprite(Texture2D tex, Vector3 displayPos, float scale, Color tint)
    {
        var mat = Matrix.Invert(ViewportManager.Rendering.ViewMatrix);
        var camForward = mat.Forward;
        var camUp = mat.Up;

        BasicEffect.World = Matrix.CreateTranslation(-0.5f, -0.5f, 0.0f) * Matrix.CreateScale(-0.5f * scale) * Matrix.CreateWorld(Vector3.Zero, camForward, camUp) * Matrix.CreateWorld(displayPos, Vector3.Forward, Vector3.Up);
        BasicEffect.TextureEnabled = true;
        BasicEffect.Texture = tex;
        BasicEffect.DiffuseColor = tint.ToVector3();
        BasicEffect.Alpha = tint.A / 255f;

        host.GraphicsDevice.SetVertexBuffer(billSpriteBuffer);
        foreach (var pass in BasicEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            host.GraphicsDevice.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 4);
        }

        BasicEffect.TextureEnabled = false;
        BasicEffect.DiffuseColor = Vector3.One;
        BasicEffect.Alpha = 1f;
    }
    public void DrawGizmo()
    {
        host.GraphicsDevice.RasterizerState = normalRasterizer;
        GizmoTranslate.Draw(host.GraphicsDevice, BasicEffect);
    }
    public void DrawRotationGizmo()
    {
        host.GraphicsDevice.RasterizerState = dualRasterizer;
        bool drawAnchor = Toolbelt.SelectedObjects.Count > 0 && (Toolbelt.SelectedObjects[0] is not EntityMoveable || Toolbelt.SelectedObjects.Count > 1);
        GizmoRotate.Draw(host.GraphicsDevice, BasicEffect, drawAnchor);
    }
    void DrawEntityVisualizer(EntityReference ent)
    {
        if (!GlobalEditorData.RegisteredEntityMeta.TryGetValue(ent.EntityName, out var meta) || meta.Visualizer is not { } binding) return;

        var visualizer = VisualizerRegistry.Create(binding.VisualizerType);
        if (visualizer == null) return;

        if (!visualizerFieldCache.TryGetValue(visualizer.GetType(), out var fields))
        {
            fields = visualizer.GetType().GetFields();
            visualizerFieldCache[visualizer.GetType()] = fields;
        }

        foreach (var field in fields)
        {
            if (binding.FieldToProperty.TryGetValue(field.Name, out var propName))
            {
                var raw = ent.Properties?.FirstOrDefault(p => p.Name == propName).Value;
                var parsed = ParseVisualizerValue(field.FieldType, raw);
                if (parsed != null)
                    field.SetValue(visualizer, parsed);
            }
            else if (binding.FieldToLiteral != null && binding.FieldToLiteral.TryGetValue(field.Name, out var literal))
            {
                var parsed = ParseVisualizerValue(field.FieldType, literal);
                if (parsed != null)
                    field.SetValue(visualizer, parsed);
            }
        }
        if (visualizer is SpriteVisualizer sv)
        {
            DrawSpriteVisualizer(ent, sv);
            return;
        }
        if (visualizer is ModelVisualizer mv)
        {
            if (!string.IsNullOrEmpty(mv.Model))
            {
                var filepath = Path.Combine(ConfigManager.currentConfig.EditorAssetsPath, Path.ChangeExtension(mv.Model, "ccmdl"));
                if (File.Exists(filepath))
                {
                    var mat = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(ent.SpawnRotation.X), MathHelper.ToRadians(ent.SpawnRotation.Y), MathHelper.ToRadians(ent.SpawnRotation.Z)) * Matrix.CreateWorld(ent.Position, Vector3.Forward, Vector3.Up);
                    modelsToDraw.Enqueue((mat, filepath));
                }
            }
            return;
        }

        var fwd = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(ent.SpawnRotation.X), MathHelper.ToRadians(ent.SpawnRotation.Y), MathHelper.ToRadians(ent.SpawnRotation.Z));

        VertexPositionColor[] lines = visualizer switch
        {
            SphereVisualizer s => GizmoShapes.WireSphere(ent.Position, s.Radius, s.Color),
            ConeVisualizer c => GizmoShapes.WireCone(ent.Position, -fwd.Forward, c.Length, c.Angle, c.Color),
            ArrowVisualizer a => GizmoShapes.WireArrow(ent.Position, a.Direction, a.Length, a.Color),
            _ => null
        };
        if (lines == null || lines.Length < 2) return;

        BasicEffect.World = Matrix.Identity;
        BasicEffect.VertexColorEnabled = true;
        BasicEffect.Alpha = 0.3f;
        foreach (var pass in BasicEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, lines, 0, lines.Length / 2);
        }
        BasicEffect.Alpha = 1f;
    }
    void DrawSpriteVisualizer(EntityReference ent, SpriteVisualizer sv)
    {
        if (string.IsNullOrEmpty(sv.Material) || !GlobalMapData.MaterialNameToIndex.TryGetValue(sv.Material, out int matIdx)) return;
        var tex = GlobalMapData.LoadedMaterials[matIdx].Texture;
        if (tex == null) return;

        var blend = sv.RenderMode == "Additive" ? BlendState.Additive : BlendState.NonPremultiplied;
        DrawBillSprite(tex, ent.Position, sv.Size, sv.Color, blend, DepthStencilState.DepthRead);
    }
    // Same string parsing rules as EntityManager.ReadProperty, just targeting a reflected field type instead of a switch.
    static object ParseVisualizerValue(Type fieldType, string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        if (fieldType == typeof(float))
        {
            return float.TryParse(raw, out var f) ? f : (object)null;
        }
        if (fieldType == typeof(Color))
        {
            var c = raw.Split(',');
            if (c.Length < 3) return null;
            if (!byte.TryParse(c[0], out var r) || !byte.TryParse(c[1], out var g) || !byte.TryParse(c[2], out var b)) return null;
            return new Color(r, g, b, (byte)255);
        }
        if (fieldType == typeof(Vector3))
        {
            var v = raw.Split(',');
            if (v.Length < 3) return null;
            if (!float.TryParse(v[0], out var x) || !float.TryParse(v[1], out var y) || !float.TryParse(v[2], out var z)) return null;
            return new Vector3(x, y, z);
        }
        return raw;
    }
    // Draws a line from every entity with an [AutoConnectOnDuplicate] "next" property to whatever it targets.
    void DrawEntityLinks()
    {
        BasicEffect.DiffuseColor = Vector3.One;
        BasicEffect.Alpha = 1f;
        BasicEffect.VertexColorEnabled = true;

        foreach (var ent in MapTools.Entities)
        {
            if (ent.IsBrushEntity) continue;
            if (!GlobalEditorData.RegisteredEntityMeta.TryGetValue(ent.EntityName, out var meta) || meta.Link is not { } link) continue;

            var targetName = ent.Properties?.FirstOrDefault(p => p.Name == link.Next).Value;
            if (string.IsNullOrEmpty(targetName)) continue;

            var target = MapTools.Entities.FirstOrDefault(o => o.Name == targetName && !o.IsBrushEntity);
            if (target == null) continue;

            if (VisGroupManager.IsEntityHidden(ent) || VisGroupManager.IsEntityHidden(target)) continue;

            var lines = new[] { new VertexPositionColor(ent.Position, Color.Goldenrod), new VertexPositionColor(target.Position, Color.Goldenrod) };
            BasicEffect.World = Matrix.Identity;
            foreach (var pass in BasicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, lines, 0, 1);
            }
        }
        BasicEffect.VertexColorEnabled = false;
    }
    void DrawHints()
    {
        host.GraphicsDevice.RasterizerState = normalRasterizer;
        for (int i = 0; i < MapTools.Hints.Length; i++)
        {
            if (VisGroupManager.IsHintHidden(i)) continue;

            var hint = MapTools.Hints[i];

            DrawBillSprite(hintTex, hint.Position);

            var box = new BoundingBox(-Vector3.One * 0.25f, Vector3.One * 0.25f);
            var lines = BrushOperations.GetDebugEdges(box);

            BasicEffect.World = Matrix.CreateWorld(hint.Position, Vector3.Forward, Vector3.Up);

            bool isHighlighted = false;
            if (Toolbelt.HighlightedObject is HintMoveable hintMove)
            {
                isHighlighted = hintMove.hint == i;
            }
            bool isSelected = Toolbelt.SelectedObjects.Any(m =>
            {
                if (m is HintMoveable h) return h.hint == i;
                return false;
            });
            BasicEffect.DiffuseColor = isSelected ? SelectionColor : isHighlighted ? HighlightColor : new Vector3(0, 0.5f, 1);

            BasicEffect.Alpha = 0.5f;

            foreach (var pass in BasicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, lines, 0, lines.Length / 2);
            }
        }
        host.GraphicsDevice.RasterizerState = worldRasterizer;
    }
    void DrawHintsText()
    {
        SpriteBatch.Begin(blendState: BlendState.NonPremultiplied);
        for (int i = 0; i < MapTools.Hints.Length; i++)
        {
            if (VisGroupManager.IsHintHidden(i)) continue;

            var hint = MapTools.Hints[i];

            var pos = host.GraphicsDevice.Viewport.Project(hint.Position + Vector3.Up * 0.6f, Viewport3DCamera.projectionMatrix, Viewport3DCamera.viewMatrix, Viewport3DCamera.worldMatrix);

            if (pos.Z > 1) continue;

            float dst = Vector3.Distance(Viewport3DCamera.position, hint.Position);

            if (dst > 16f) continue;

            float alpha = float.Min((1f - (dst / 16f)) * 3, 1f);
            const float referenceDistance = 4f;
            float scale = referenceDistance / dst;
            scale = MathHelper.Clamp(scale, 0.35f, 1.5f);

            var fontH = FontSystem.GetFont(10);
            var fontP = FontSystem.GetFont(6);

            const float maxWidth = 150f;
            var lines = WrapText(fontH, hint.Header ?? "???", maxWidth);

            float lineHeight = fontH.FontSize * scale;
            float totalHeight = lines.Count * lineHeight;
            float y = pos.Y;

            float boxWidth = 0f;
            foreach (var line in lines)
            {
                boxWidth = float.Max(boxWidth, fontH.MeasureString(line).X * scale);
            }

            const float padding = 4f;
            var boxPos = new Vector2(pos.X - boxWidth * 0.5f - padding, y - padding);
            var boxSize = new SizeF(boxWidth + padding * 2, totalHeight + padding * 2);
            SpriteBatch.FillRectangle(boxPos, boxSize, new Color(Color.Black, alpha * 0.5f));

            foreach (var line in lines)
            {
                var lineWidth = fontH.MeasureString(line) * scale;
                SpriteBatch.DrawString(
                    fontH,
                    line,
                    new(pos.X - lineWidth.X * 0.5f, y),
                    new Color(Color.White, alpha),
                    scale: new Vector2(scale, scale),
                    effect: FontSystemEffect.Stroked,
                    effectAmount: 1);
                y += lineHeight;
            }
        }

        SpriteBatch.End();
        host.GraphicsDevice.RasterizerState = worldRasterizer;
        host.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        host.GraphicsDevice.BlendState = BlendState.AlphaBlend;
    }
    static List<string> WrapText(SpriteFontBase font, string text, float maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ');
        var currentLine = "";
        foreach (var word in words)
        {
            var testLine = currentLine.Length == 0 ? word : currentLine + " " + word;
            if (font.MeasureString(testLine).X > maxWidth && currentLine.Length > 0)
            {
                lines.Add(currentLine);
                currentLine = word;
            }
            else
            {
                currentLine = testLine;
            }
        }
        if (currentLine.Length > 0) lines.Add(currentLine);
        return lines;
    }
    void DrawEntities()
    {
        host.GraphicsDevice.RasterizerState = normalRasterizer;
        for (int i = 0; i < MapTools.Entities.Length; i++)
        {
            var ent = MapTools.Entities[i];
            if (ent.IsBrushEntity) continue;
            if (VisGroupManager.IsEntityHidden(ent)) continue;

            var box = BoundsFor(ent.EntityName);
            var worldBox = new BoundingBox(box.Min + ent.Position, box.Max + ent.Position);
            //if (frustum.Contains(worldBox) == ContainmentType.Disjoint) continue;

            bool isHighlighted = false;
            if (Toolbelt.HighlightedObject is EntityMoveable entityMoveable)
            {
                isHighlighted = entityMoveable.entity == i;
            }
            bool isSelected = Toolbelt.SelectedObjects.Any(m =>
            {
                if (m is EntityMoveable e) return e.entity == i;
                return false;
            });

            var sprite = IconFor(ent.EntityName);
            if (sprite != null)
            {
                DrawBillSprite(sprite, ent.Position);
            }

            DrawEntityVisualizer(ent);

            if (ent.EntityName == "EnvDecal")
            {
                decalsToDraw.Enqueue(ent);
            }

            if (ent.EntityName == "DetailModel")
            {
                if (ent.Properties != null && ent.Properties.Length > 0)
                {
                    var filepath = Path.Combine(ConfigManager.currentConfig.EditorAssetsPath, Path.ChangeExtension(ent.Properties[0].Value, "ccmdl"));
                    if (File.Exists(filepath) && Path.GetExtension(filepath) == ".ccmdl")
                    {
                        var mat = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(ent.SpawnRotation.X), MathHelper.ToRadians(ent.SpawnRotation.Y), MathHelper.ToRadians(ent.SpawnRotation.Z)) * Matrix.CreateWorld(ent.Position, Vector3.Forward, Vector3.Up);
                        modelsToDraw.Enqueue((mat, filepath));
                    }
                    else
                    {
                        BasicEffect.World = Matrix.Identity * Matrix.CreateTranslation(ent.Position);
                        ErrorModel.Draw(BasicEffect, host.GraphicsDevice);
                    }
                }
                else
                {
                    BasicEffect.World = Matrix.Identity * Matrix.CreateTranslation(ent.Position);
                    ErrorModel.Draw(BasicEffect, host.GraphicsDevice);
                }
            }

            var lines = BrushOperations.GetDebugEdges(box);

            BasicEffect.World = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(ent.SpawnRotation.X), MathHelper.ToRadians(ent.SpawnRotation.Y), MathHelper.ToRadians(ent.SpawnRotation.Z)) * Matrix.CreateWorld(ent.Position, Vector3.Forward, Vector3.Up);

            BasicEffect.VertexColorEnabled = false;
            BasicEffect.DiffuseColor = isSelected ? SelectionColor : isHighlighted ? HighlightColor : Vector3.One;

            foreach (var pass in BasicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, lines, 0, lines.Length / 2);
            }
            BasicEffect.World = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(ent.SpawnRotation.X), MathHelper.ToRadians(ent.SpawnRotation.Y), MathHelper.ToRadians(ent.SpawnRotation.Z)) *
                                Matrix.CreateWorld(ent.Position, Vector3.Forward, Vector3.Up);
            BasicEffect.DiffuseColor = Vector3.One;
            BasicEffect.VertexColorEnabled = true;

            foreach (var pass in BasicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                host.GraphicsDevice.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, cachedArrowVerts, 0, cachedArrowVerts.Length, cachedArrowTris, 0, cachedArrowTris.Length / 3);
            }
            BasicEffect.VertexColorEnabled = false;
        }
        host.GraphicsDevice.RasterizerState = worldRasterizer;
    }
    void DrawTerrain()
    {
        if (MapTools.Terrains == null || MapTools.Terrains.Length == 0) return;

        terrainShader.Parameters["tint"].SetValue(Vector3.One);
        terrainShader.Parameters["WorldViewProjection"].SetValue(Viewport3DCamera.worldMatrix * Viewport3DCamera.viewMatrix * Viewport3DCamera.projectionMatrix);

        for (int i = 0; i < MapTools.Terrains.Length; i++)
        {
            var terrain = MapTools.Terrains[i];

            if (VisGroupManager.IsTerrainHidden(i)) continue;

            if (frustum.Contains(terrain.Bounds) == ContainmentType.Disjoint) continue;

            bool isHighlighted = false;
            if (Toolbelt.HighlightedObject is TerrainMoveable terrainMoveable)
            {
                isHighlighted = terrainMoveable.terrain == i;
            }
            bool isSelected = Toolbelt.SelectedObjects.Any(m =>
            {
                if (m is TerrainMoveable t) return t.terrain == i;
                return false;
            });

            terrainShader.Parameters["texture1"].SetValue(GlobalMapData.LoadedMaterials[terrain.Surface].Texture);
            terrainShader.Parameters["texture2"].SetValue(GlobalMapData.LoadedMaterials[terrain.BlendedSurface].Texture);

            terrainShader.Parameters["tint"].SetValue(isSelected ? SelectionColor : isHighlighted ? HighlightColor : Vector3.One);
            terrainShader.Parameters["selected"].SetValue(isHighlighted || isSelected);

            foreach (var pass in terrainShader.CurrentTechnique.Passes)
            {
                pass.Apply();
                host.GraphicsDevice.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, terrain.Vertices, 0, terrain.Vertices.Length, terrain.Triangles, 0, terrain.Triangles.Length / 3);
            }

            if (isSelected)
            {
                host.GraphicsDevice.RasterizerState = wireRasterizer;
                host.GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;

                BasicEffect.DiffuseColor = Vector3.One;
                BasicEffect.Alpha = 0.5f;
                BasicEffect.World = Matrix.Identity;
                foreach (var pass in BasicEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    host.GraphicsDevice.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, terrain.Vertices, 0, terrain.Vertices.Length, terrain.Triangles, 0, terrain.Triangles.Length / 3);
                }

                host.GraphicsDevice.RasterizerState = worldRasterizer;
                host.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            }
        }
    }
    void Draw2DTerrain(EditorViewport vp)
    {
        if (MapTools.Terrains == null || MapTools.Terrains.Length == 0) return;

        for (int i = 0; i < MapTools.Terrains.Length; i++)
        {
            if (VisGroupManager.IsTerrainHidden(i)) continue;

            var terrain = MapTools.Terrains[i];

            bool isHighlighted = false;
            if (Toolbelt.HighlightedObject is TerrainMoveable terrainMoveable)
            {
                isHighlighted = terrainMoveable.terrain == i;
            }
            bool isSelected = Toolbelt.SelectedObjects.Any(m =>
            {
                if (m is TerrainMoveable t) return t.terrain == i;
                return false;
            });

            host.GraphicsDevice.RasterizerState = wireRasterizer;
            host.GraphicsDevice.DepthStencilState = DepthStencilState.None;

            BasicEffect.DiffuseColor = isSelected ? SelectionColor : isHighlighted ? HighlightColor : Vector3.One;
            BasicEffect.Alpha = 0.5f;
            BasicEffect.World = vp.WorldMatrix;
            BasicEffect.View = vp.ViewMatrix;
            BasicEffect.Projection = vp.ProjectionMatrix;
            foreach (var pass in BasicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                host.GraphicsDevice.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, terrain.Vertices, 0, terrain.Vertices.Length, terrain.Triangles, 0, terrain.Triangles.Length / 3);
            }

            BasicEffect.Alpha = 1;

            host.GraphicsDevice.RasterizerState = worldRasterizer;
            host.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        }
    }
    void DrawMapGeom()
    {
        host.GraphicsDevice.RasterizerState = worldRasterizer;

        transparentFaceQueue.Clear();
        worldShader.Parameters["faceAlpha"].SetValue(1);
        worldShader.Parameters["faceTint"].SetValue(Vector3.One);

        selectedBrushes.Clear();
        selectedFaces.Clear();
        foreach (var o in Toolbelt.SelectedObjects)
        {
            if (o is BrushMoveable bm) selectedBrushes.Add(bm.brush);
            if (o is FaceMoveable fm) selectedFaces.Add((fm.brush, fm.face));
        }

        int highlightedBrush = Toolbelt.HighlightedObject is BrushMoveable hbm ? hbm.brush : -1;
        var highlightedFace = Toolbelt.HighlightedObject is FaceMoveable hfm ? (hfm.brush, hfm.face) : (-1, -1);

        for (int i = 0; i < MapTools.Brushes.Length; i++)
        {
            var b = MapTools.Brushes[i];

            if (b.isUsedForTerrain) continue;
            if (VisGroupManager.IsBrushHidden(i)) continue;
            if (i < MapTools.BrushBounds.Length && frustum.Contains(MapTools.BrushBounds[i]) == ContainmentType.Disjoint) continue;

            var world = Matrix.CreateWorld(b.Position, Vector3.Forward, Vector3.Up);
            worldShader.Parameters["World"].SetValue(world * Viewport3DCamera.worldMatrix);
            worldShader.Parameters["View"].SetValue(Viewport3DCamera.viewMatrix);
            worldShader.Parameters["Projection"].SetValue(Viewport3DCamera.projectionMatrix);

            bool isAccidentalSkybox = b.Faces.Any(f => f.MaterialName.Contains("skybox"));
            bool brushSelected = selectedBrushes.Contains(i);
            bool brushHighlighted = highlightedBrush == i;

            for (int j = 0; j < b.Faces.Length; j++)
            {
                var f = b.Faces[j];

                if (GlobalMapData.LoadedMaterials[f.Surface].Name.StartsWith("tool_") || isAccidentalSkybox)
                {
                    transparentFaceQueue.Add((b, f, i, j));
                    continue;
                }

                bool isHighlighted = brushHighlighted || highlightedFace == (i, j);
                bool isSelected = brushSelected || selectedFaces.Contains((i, j));

                if (isHighlighted || isSelected)
                {
                    faceLines.Enqueue((f, i));
                }

                worldShader.Parameters["faceTint"].SetValue(isSelected ? SelectionColor : isHighlighted ? HighlightColor : Vector3.One);
                worldShader.Parameters["faceSelected"].SetValue((isHighlighted || isSelected) ? 1f : 0f);

                DrawFace(f);
            }
        }
    }
    void DrawBrushEntityIndicators()
    {
        if (MapTools.Entities == null || MapTools.BrushBounds == null) return;

        var fillColor = new Color(120, 170, 255, 20);
        var borderColor = new Color(140, 190, 255, 80);

        foreach (var ent in MapTools.Entities)
        {
            if (!ent.IsBrushEntity) continue;
            if (VisGroupManager.IsEntityHidden(ent)) continue;

            BoundingBox? unionBounds = null;
            foreach (var bi in ent.BrushIndices)
            {
                if (bi < 0 || bi >= MapTools.BrushBounds.Length) continue;
                unionBounds = unionBounds.HasValue
                    ? BoundingBox.CreateMerged(unionBounds.Value, MapTools.BrushBounds[bi])
                    : MapTools.BrushBounds[bi];
            }
            if (!unionBounds.HasValue) continue;

            var box = unionBounds.Value;
            box.Min -= Vector3.One * 0.001f;
            box.Max += Vector3.One * 0.001f;

            var pos = box.Min;

            // Marker sphere
            var sphereWorld = Matrix.CreateScale(float.Min(0.1f * Vector3.Distance(pos, Viewport3DCamera.position), 0.35f))
                * Matrix.CreateWorld(pos, Vector3.Forward, Vector3.Up);
            BasicEffect.World = sphereWorld;
            BasicEffect.DiffuseColor = Color.CornflowerBlue.ToVector3();
            BasicEffect.Alpha = 1f;
            BasicEffect.VertexColorEnabled = false;
            host.GraphicsDevice.BlendState = BlendState.Opaque;
            host.GraphicsDevice.RasterizerState = worldRasterizer;
            foreach (var pass in BasicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, brushEntitySphere, 0, brushEntitySphere.Length / 3);
            }
        }

        BasicEffect.Alpha = 1f;
        host.GraphicsDevice.BlendState = BlendState.Opaque;
        host.GraphicsDevice.RasterizerState = worldRasterizer;
    }
    void DrawTransparentMapGeom()
    {
        host.GraphicsDevice.RasterizerState = worldRasterizer;

        worldShader.Parameters["faceAlpha"].SetValue(1);
        worldShader.Parameters["faceTint"].SetValue(Vector3.One);
        host.GraphicsDevice.BlendState = BlendState.NonPremultiplied;

        foreach (var element in transparentFaceQueue)
        {
            bool isAccidentalSkybox = element.b.Faces.Any(f => f.MaterialName.Contains("skybox")) && !element.f.MaterialName.Contains("skybox");

            var world = Matrix.CreateWorld(element.b.Position, Vector3.Forward, Vector3.Up);
            worldShader.Parameters["World"].SetValue(world * Viewport3DCamera.worldMatrix);
            worldShader.Parameters["View"].SetValue(Viewport3DCamera.viewMatrix);
            worldShader.Parameters["Projection"].SetValue(Viewport3DCamera.projectionMatrix);

            worldShader.Parameters["faceAlpha"].SetValue(GlobalMapData.LoadedMaterials[element.f.Surface].Name.StartsWith("tool_") ? 0.8f : 1);

            bool isHighlighted = false;
            if (Toolbelt.HighlightedObject is FaceMoveable faceMoveable)
            {
                isHighlighted = faceMoveable.brush == element.bID && faceMoveable.face == element.fID;
            }
            if (Toolbelt.HighlightedObject is BrushMoveable brushMoveable)
            {
                isHighlighted = brushMoveable.brush == element.bID;
            }
            bool isSelected = Toolbelt.SelectedObjects.Any(m =>
            {
                if (m is FaceMoveable fm) return fm.brush == element.bID && fm.face == element.fID;
                if (m is BrushMoveable bm) return bm.brush == element.bID;
                return false;
            });

            if (isHighlighted || isSelected)
            {
                faceLines.Enqueue((element.f, element.bID));
            }

            if (GlobalMapData.LoadedMaterials[element.f.Surface].Name.StartsWith("tool_") && Toolbelt.HideToolFaces && !isSelected) continue;

            Vector3 defaultColor = Vector3.One;

            if (isAccidentalSkybox)
            {
                worldShader.Parameters["faceAlpha"].SetValue(float.Abs((float)double.Sin(totalTime * 2)) * 0.2f + 0.6f);
                defaultColor = Vector3.Lerp(Vector3.One, Vector3.UnitZ, 1 - float.Abs((float)double.Sin(totalTime * 2)));
            }

            worldShader.Parameters["faceTint"].SetValue(isSelected ? SelectionColor : isHighlighted ? HighlightColor : defaultColor);
            worldShader.Parameters["faceSelected"].SetValue((isHighlighted || isSelected) ? 0f : 1f);

            DrawFace(element.f);
        }

        host.GraphicsDevice.RasterizerState = normalRasterizer;
    }
    void DrawFace(Face f)
    {
        worldShader.Parameters["faceTexture"].SetValue(GlobalMapData.LoadedMaterials[f.Surface].Texture);

        host.GraphicsDevice.SetVertexBuffer(f.vertexBuffer);
        foreach (var pass in worldShader.CurrentTechnique.Passes)
        {
            pass.Apply();
            host.GraphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, f.vertexBuffer.VertexCount / 3);
        }
    }
    void DrawDecals()
    {
        host.GraphicsDevice.RasterizerState = normalRasterizer;
        while (decalsToDraw.Count > 0)
        {
            var ent = decalsToDraw.Dequeue();
            EditorDecalPreview.DrawForEntity(host.GraphicsDevice, BasicEffect, ent);
        }
    }
    void DrawModels()
    {
        host.GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
        while (modelsToDraw.Count > 0)
        {
            var mdl = modelsToDraw.Dequeue();

            if (!entityModels.TryGetValue(mdl.mdlName, out var modelDisplay))
            {
                modelDisplay = new ModelDisplay(mdl.mdlName, host);
                entityModels.Add(mdl.mdlName, modelDisplay);
            }

            modelDisplay.Draw(mdl.world, Viewport3DCamera.viewMatrix, Viewport3DCamera.projectionMatrix);
        }
        host.GraphicsDevice.RasterizerState = RasterizerState.CullClockwise;
    }
    void DrawEdges()
    {
        var edges = Toolbelt.SelectedObjects.Where(t => t is BrushEdgeMoveable).Select(t => t as BrushEdgeMoveable);

        foreach (var edge in edges)
        {
            var brush = MapTools.Brushes[edge.brush];
            var vA = brush.Vertices[edge.vertA] + brush.Position;
            var vB = brush.Vertices[edge.vertB] + brush.Position;

            if (!OtherMath.ClipSegmentToNearPlane(vA, vB,
                    Viewport3DCamera.viewMatrix,
                    Viewport3DCamera.projectionMatrix,
                    out var clippedA, out var clippedB))
                continue;

            var pA = host.GraphicsDevice.Viewport.Project(clippedA, Viewport3DCamera.projectionMatrix, Viewport3DCamera.viewMatrix, Viewport3DCamera.worldMatrix);
            var pB = host.GraphicsDevice.Viewport.Project(clippedB, Viewport3DCamera.projectionMatrix, Viewport3DCamera.viewMatrix, Viewport3DCamera.worldMatrix);

            var sA = new Vector2(pA.X, pA.Y);
            var sB = new Vector2(pB.X, pB.Y);
            Color color = Color.MonoGameOrange;

            edgeLines.Enqueue((sA, sB, new Vector4(SelectionColor, 0.8f), 2f));
        }
    }
    void DrawFaceOutline(Face f, int brushIndex)
    {
        var vertices = MapTools.Brushes[brushIndex].Vertices;

        var polygon = f.Indices.Distinct().ToList();

        var center = polygon.Aggregate(Vector3.Zero, (c, v) => c + vertices[v]) / polygon.Count;
        var refAxis = Vector3.Cross(f.Normal, Vector3.UnitZ);
        if (refAxis.LengthSquared() < 1e-6f)
            refAxis = Vector3.Cross(f.Normal, Vector3.UnitX);
        refAxis = Vector3.Normalize(refAxis);
        var perpAxis = Vector3.Normalize(Vector3.Cross(f.Normal, refAxis));

        polygon.Sort((a, b) => {
            var da = vertices[a] - center; var db = vertices[b] - center;
            float aAng = MathF.Atan2(Vector3.Dot(da, perpAxis), Vector3.Dot(da, refAxis));
            float bAng = MathF.Atan2(Vector3.Dot(db, perpAxis), Vector3.Dot(db, refAxis));
            return aAng.CompareTo(bAng);
        });

        var indices = polygon.ToArray();

        int edgeCount = indices.Length;
        if (edgeCount < 2)
            return; // nothing to draw

        VertexPositionColorNormalTexture[] lineVerts = new VertexPositionColorNormalTexture[edgeCount * 2];

        Color outlineColor = Color.White;

        for (int e = 0; e < edgeCount; e++)
        {
            int currIndex = indices[e];
            int nextIndex = indices[(e + 1) % edgeCount];

            Vector3 p0 = vertices[currIndex];
            Vector3 p1 = vertices[nextIndex];

            lineVerts[e * 2 + 0] = new VertexPositionColorNormalTexture(p0, outlineColor, new(), new());
            lineVerts[e * 2 + 1] = new VertexPositionColorNormalTexture(p1, outlineColor, new(), new());
        }

        BasicEffect.World = Matrix.CreateWorld(MapTools.Brushes[brushIndex].Position, Vector3.Forward, Vector3.Up);
        BasicEffect.View = Viewport3DCamera.viewMatrix;
        BasicEffect.Projection = Viewport3DCamera.projectionMatrix;
        BasicEffect.Alpha = 0.25f;

        foreach (var pass in BasicEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, lineVerts, 0, lineVerts.Length / 2);
        }
        BasicEffect.Alpha = 1;
    }
    void DrawLeakLine()
    {
        if (MapTools.LeakPoints == null || MapTools.LeakPoints.Length < 2) return;

        var lineVerts = new List<VertexPositionColor>();
        for (int i = 0; i < MapTools.LeakPoints.Length - 1; i++)
        {
            lineVerts.Add(new VertexPositionColor(MapTools.LeakPoints[i].Position, Color.Red));
            lineVerts.Add(new VertexPositionColor(MapTools.LeakPoints[i + 1].Position, Color.Red));
        }

        BasicEffect.World = Matrix.Identity;
        BasicEffect.VertexColorEnabled = true;
        //host.GraphicsDevice.DepthStencilState = DepthStencilState.None;

        var arr = lineVerts.ToArray();
        foreach (var pass in BasicEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            host.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, arr, 0, arr.Length / 2);
        }

        BasicEffect.VertexColorEnabled = false;
        //host.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
    }
    public Vector3 Unproject(Vector3 val)
    {
        return host.GraphicsDevice.Viewport.Unproject(val, Viewport3DCamera.projectionMatrix, Viewport3DCamera.viewMatrix, Viewport3DCamera.worldMatrix);
    }
    public Ray CalculateRay(Vector2 mouseLocation)
    {
        Vector3 nearPoint = host.GraphicsDevice.Viewport.Unproject(new Vector3(mouseLocation.X,
                mouseLocation.Y, 0.1f),
                Viewport3DCamera.projectionMatrix,
                Viewport3DCamera.viewMatrix,
                Viewport3DCamera.worldMatrix);

        Vector3 farPoint = host.GraphicsDevice.Viewport.Unproject(new Vector3(mouseLocation.X,
                mouseLocation.Y, 1.0f),
                Viewport3DCamera.projectionMatrix,
                Viewport3DCamera.viewMatrix,
                Viewport3DCamera.worldMatrix);

        Vector3 direction = farPoint - nearPoint;
        direction.Normalize();

        return new Ray(nearPoint, direction);
    }
    public void LoadAllExternalGameData()
    {
        var index = EntityDataIndex.Read(GlobalEditorData.EDSFile);

        MaterialLoader.MountMaterials(index.MaterialsPath);
        GlobalMapData.MaterialNameToIndex = new Dictionary<string, int>();

        GlobalEditorData.RegisteredClassnames = JsonConvert.DeserializeObject<string[]>(File.ReadAllText(index.ClassnamesPath)).Order().ToArray();

        GlobalEditorData.ContentPath = index.ContentPath;
        host.Content.RootDirectory = GlobalEditorData.ContentPath;

        GlobalEditorData.RegisteredEntityMeta = JsonConvert.DeserializeObject<Dictionary<string, EntityClassMetadata>>(File.ReadAllText(index.MetadataPath));

        for (int i = 0; i < GlobalMapData.LoadedMaterials.Length; i++)
        {
            GlobalMapData.MaterialNameToIndex.Add(GlobalMapData.LoadedMaterials[i].Name, i);
            if (!string.IsNullOrEmpty(GlobalMapData.LoadedMaterials[i].TextureName))
                GlobalMapData.LoadedMaterials[i].Texture = host.Content.Load<Texture2D>($"{GlobalMapData.LoadedMaterials[i].TextureName}");
            if (!string.IsNullOrEmpty(GlobalMapData.LoadedMaterials[i].NormalName))
                GlobalMapData.LoadedMaterials[i].Normal = host.Content.Load<Texture2D>($"{GlobalMapData.LoadedMaterials[i].NormalName}");
            if (!string.IsNullOrEmpty(GlobalMapData.LoadedMaterials[i].SpecularName))
                GlobalMapData.LoadedMaterials[i].Specular = host.Content.Load<Texture2D>($"{GlobalMapData.LoadedMaterials[i].SpecularName}");
        }
        //RokMessages.Append("{ Rockwall : Internal System }" + $" Loaded Materials OK!");
    }
    public static async void OpenEntityInspector(IEnumerable<EntityReference> entities)
    {
        await new EntityInspectorWindow(entities).ShowDialog(MainWindow.Instance);
    }
}