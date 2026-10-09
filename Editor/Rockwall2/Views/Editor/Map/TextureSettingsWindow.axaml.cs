using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DefaultUnDo;
using MsBox.Avalonia;
using Rockwall;
using Rockwall2.Editor.Common;
using Rockwall2.Editor.Mapper;
using Rockwall2.Editor.Mapper.Utils;
using Rockwall2.Views;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Rockwall2;

public partial class TextureSettingsWindow : Window
{
    public static TextureSettingsWindow Instance;
    public bool AutoSewEnabled => tAutoSew?.IsChecked == true;
    public bool IsTerrainMode;

    public float Strength, Radius;
    public enum TerrainEditMode
    {
        Shape,
        Alpha,
        Smooth
    }
    public enum TerainMoveMode
    {
        Normal,
        X,Y,Z
    }
    public TerainMoveMode MoveMode;
    public TerrainEditMode EditMode;

    private bool isWindowDragInEffect = false;
    private Point cursorDragStart = new(0, 0);

    private bool _suppress;
    private ToggleButton[] smoothingButtons;

    public TextureSettingsWindow()
    {
        Instance = this;
        InitializeComponent();
        if(!Design.IsDesignMode)
        {
            smoothingButtons = new[] { sg1, sg2, sg3, sg4, sg5, sg6, sg7, sg8,
                                    sg9, sg10, sg11, sg12, sg13, sg14, sg15, sg16 };

            UpdateValues();
            SyncFromClipboard();
            Closed += Window_Closed;

            tRadius.ValueChanged += TRadius_ValueChanged;
            tStrength.ValueChanged += TStrength_ValueChanged;
        }
        tModeDrop.SelectedIndex = 0;
        tMoveDrop.SelectedIndex = 0;
    }

    private void SewSeams_Click(object? sender, RoutedEventArgs e)
    {
        Rockwall2.Tools.TextureApplicationTool.Instance?.SewAllSeams();
    }

    private void TStrength_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        tStrengthLabel.Text = tStrength.Value.ToString("0.0");
        Strength = (float)tStrength.Value;
    }

    private void TRadius_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        tRadiusLabel.Text = tRadius.Value.ToString("0.0");
        Radius = (float)tRadius.Value;
    }

    private System.Collections.Generic.IEnumerable<FaceMoveable> SelectedFaces =>
        Toolbelt.SelectedObjects.OfType<FaceMoveable>();
    private System.Collections.Generic.IEnumerable<BrushMoveable> SelectedBrushes =>
        Toolbelt.SelectedObjects.OfType<BrushMoveable>();
    private System.Collections.Generic.IEnumerable<TerrainMoveable> SelectedTerrains =>
        Toolbelt.SelectedObjects.OfType<TerrainMoveable>();

    private bool HasFaceSelection =>
        SelectedFaces.Any(f => f.brush != -1 && f.face != -1) || SelectedBrushes.Any(b => b.brush != -1);
    private bool HasTerrainSelection =>
        SelectedTerrains.Any(f => f.terrain != -1);

    private void TitleBar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (isWindowDragInEffect)
        {
            Point currentCursorPosition = e.GetPosition(this);
            Point delta = currentCursorPosition - cursorDragStart;
            Position = this.PointToScreen(delta);
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Maximized || WindowState == WindowState.FullScreen) return;
        isWindowDragInEffect = true;
        cursorDragStart = e.GetPosition(this);
    }

    private void TitleBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => isWindowDragInEffect = false;

    public void SyncFromClipboard()
    {
        if (!TextureClipboard.HasSource) return;

        _suppress = true;
        tOffX.Value = (decimal)TextureClipboard.TOffX;
        tOffY.Value = (decimal)TextureClipboard.TOffY;
        tScaleX.Value = (decimal)MathF.Abs(TextureClipboard.TScaleX == 0f ? 1f : TextureClipboard.TScaleX);
        tScaleY.Value = (decimal)MathF.Abs(TextureClipboard.TScaleY == 0f ? 1f : TextureClipboard.TScaleY);
        tRotation.Value = (decimal)TextureClipboard.UvRotation;
        luxelScale.Value = (decimal)TextureClipboard.LuxelScale;
        flipH.IsChecked = TextureClipboard.TScaleX < 0f;
        flipV.IsChecked = TextureClipboard.TScaleY < 0f;
        projWorld.IsChecked = TextureClipboard.UvProjectionMode != UVProjectionMode.Face;
        projFace.IsChecked = TextureClipboard.UvProjectionMode == UVProjectionMode.Face;
        _suppress = false;
    }

    private void PushToClipboard()
    {
        float scaleX = MathF.Max(0.001f, (float)(tScaleX?.Value ?? 1m));
        float scaleY = MathF.Max(0.001f, (float)(tScaleY?.Value ?? 1m));
        if (flipH?.IsChecked == true) scaleX = -scaleX;
        if (flipV?.IsChecked == true) scaleY = -scaleY;

        TextureClipboard.SyncFromWindow(
            offX: (float)(tOffX?.Value ?? 0),
            offY: (float)(tOffY?.Value ?? 0),
            scaleX: scaleX,
            scaleY: scaleY,
            rotation: (float)(tRotation?.Value ?? 0m),
            luxelScale: (float)(luxelScale?.Value ?? 1m),
            projMode: projFace?.IsChecked == true ? UVProjectionMode.Face : UVProjectionMode.World);
    }

    public void UpdateValues()
    {
        UpdateTerrainSelection();

        if (!HasFaceSelection) return;
        var first = SelectedFaces.FirstOrDefault(f => f.brush != -1 && f.face != -1);
        if (first != null)
            TextureClipboard.LiftFromFace(first.brush, first.face);
        SyncSmoothingGroupsFromFirstFace();
    }
    private void ValuesChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        PushToClipboard();
        ApplyToAllSelectedFaces();
    }

    private void Rotate_Neg90(object? sender, RoutedEventArgs e) => AddRotation(-90f);
    private void Rotate_Pos90(object? sender, RoutedEventArgs e) => AddRotation(+90f);
    private void Rotate_180(object? sender, RoutedEventArgs e) => AddRotation(180f);

    private void AddRotation(float delta)
    {
        float cur = (float)(tRotation.Value ?? 0m);
        float next = ((cur + delta) % 360f + 360f) % 360f;
        tRotation.Value = (decimal)next;
    }

    private void Justify_Top(object? sender, RoutedEventArgs e) => Justify(UvCalculator.JustifyMode.Top);
    private void Justify_Bottom(object? sender, RoutedEventArgs e) => Justify(UvCalculator.JustifyMode.Bottom);
    private void Justify_Left(object? sender, RoutedEventArgs e) => Justify(UvCalculator.JustifyMode.Left);
    private void Justify_Right(object? sender, RoutedEventArgs e) => Justify(UvCalculator.JustifyMode.Right);
    private void Justify_Center(object? sender, RoutedEventArgs e) => Justify(UvCalculator.JustifyMode.Center);
    private void Justify_Fit(object? sender, RoutedEventArgs e) => Justify(UvCalculator.JustifyMode.Fit);

    private void Justify(UvCalculator.JustifyMode mode)
    {
        if (!HasFaceSelection) return;
        foreach (var fm in SelectedFaces.Where(f => f.brush != -1 && f.face != -1))
        {
            ref var face = ref MapTools.ActiveMap.Brushes[fm.brush].Faces[fm.face];
            UvCalculator.Justify(ref face, MapTools.ActiveMap.Brushes[fm.brush], mode);
            BrushOperations.RebuildBrush(ref MapTools.ActiveMap.Brushes[fm.brush]);
        }
        UpdateValues();
    }

    private void Reset_All(object? sender, RoutedEventArgs e)
    {
        _suppress = true;
        tOffX.Value = tOffY.Value = 0;
        tScaleX.Value = tScaleY.Value = 1m;
        tRotation.Value = 0m;
        flipH.IsChecked = flipV.IsChecked = false;
        _suppress = false;
        PushToClipboard();
        ApplyToAllSelectedFaces();
    }

    private void Reset_Scale(object? sender, RoutedEventArgs e)
    {
        _suppress = true;
        tScaleX.Value = tScaleY.Value = 1m;
        flipH.IsChecked = flipV.IsChecked = false;
        _suppress = false;
        PushToClipboard();
        ApplyToAllSelectedFaces();
    }

    private void Reset_Offset(object? sender, RoutedEventArgs e)
    {
        _suppress = true;
        tOffX.Value = tOffY.Value = 0;
        _suppress = false;
        PushToClipboard();
        ApplyToAllSelectedFaces();
    }

    private void ApplyToAllSelectedFaces()
    {
        if (!HasFaceSelection) return;
        var faces = SelectedFaces.Where(f => f.brush != -1 && f.face != -1).Select(fm => (fm.brush, fm.face));
        foreach (var bm in SelectedBrushes)
            for (int i = 0; i < MapTools.Brushes[bm.brush].Faces.Length; i++)
                faces = faces.Append((bm.brush, i));

        foreach (var fm in faces)
        {
            TextureClipboard.StampOntoFace(fm.brush, fm.face, includeMaterial: false);
            BrushOperations.RebuildBrush(ref MapTools.ActiveMap.Brushes[fm.brush]);
        }
    }

    private void UpdateTerrainSelection()
    {
        IsTerrainMode = HasTerrainSelection;

        tCreate.IsEnabled = !HasTerrainSelection && HasFaceSelection;
        tPMat.IsEnabled = HasTerrainSelection;
        tSMat.IsEnabled = HasTerrainSelection;
    }

    private void CreateTerrains()
    {
        var oldTerrains = MapTools.Terrains;
        var oldBrushes = MapTools.Brushes;

        foreach (var face in SelectedFaces)
        {
            var t = BrushOperations.CreateTerrainFromFace(face.brush, face.face, (int)(tPower.Value ?? 2));
            if(!t.HasValue)
            {
                MessageBoxManager.GetMessageBoxStandard("Terrain could not be created.", "The terrain could not be created, the faces might have the wrong number of unique corners (4).");
                continue;
            }

            MapTools.AddTerrain(t.Value);
            MapTools.Brushes[face.brush].isUsedForTerrain = true;
        }

        Toolbelt.UndoManager.DoOnUndo(() =>
        {
            MapTools.ActiveMap.Terrains = oldTerrains;
            MapTools.ActiveMap.Brushes = oldBrushes;
            MapTools.RecomputeAllBrushBounds();
        });
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Toolbelt.SwitchTool(Toolbelt.SelectionTool);
        Instance = null;
    }

    private void tCreate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CreateTerrains();
    }
    private async void tPMat_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        int mat = await MaterialPicker.PickAsync(MainWindow.Instance);
        if (mat == -1) return;

        foreach (var t in SelectedTerrains)
        {
            MapTools.Terrains[t.terrain].Surface = mat;
            MapTools.Terrains[t.terrain].SurfaceName = GlobalMapData.LoadedMaterials[mat].Name;
        }
    }
    private async void tSMat_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        int mat = await MaterialPicker.PickAsync(MainWindow.Instance);
        if (mat == -1) return;

        foreach (var t in SelectedTerrains)
        {
            MapTools.Terrains[t.terrain].BlendedSurface = mat;
            MapTools.Terrains[t.terrain].BlendedSurfaceName = GlobalMapData.LoadedMaterials[mat].Name;
        }
    }

    private void ComboBox_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (tModeDrop.SelectedIndex < 0) return;

        EditMode = (TerrainEditMode)tModeDrop.SelectedIndex;
    }

    private void MoveMode_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (tMoveDrop.SelectedIndex < 0) return;

        MoveMode = (TerainMoveMode)tMoveDrop.SelectedIndex;
    }

    private void SmoothingGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not ToggleButton tb || tb.Tag is not string tagStr || !int.TryParse(tagStr, out int bitNumber))
            return;

        int mask = 1 << (bitNumber - 1);
        bool on = tb.IsChecked == true;
        ApplySmoothingGroupToSelectedFaces(mask, on);
    }

    private void ApplySmoothingGroupToSelectedFaces(int mask, bool on)
    {
        if (!HasFaceSelection) return;

        var faces = SelectedFaces.Where(f => f.brush != -1 && f.face != -1).Select(fm => (fm.brush, fm.face));
        foreach (var bm in SelectedBrushes)
        {
            for (int i = 0; i < MapTools.Brushes[bm.brush].Faces.Length; i++)
            {
                faces = faces.Append((bm.brush, i));
            }
        }
        foreach (var (brush, face) in faces)
        {
            ref var f = ref MapTools.ActiveMap.Brushes[brush].Faces[face];
            if (on) f.smoothGroup |= mask;
            else f.smoothGroup &= ~mask;
        }
    }

    private void SyncSmoothingGroupsFromFirstFace()
    {
        if (!HasFaceSelection) { return; }

        var first = SelectedFaces.FirstOrDefault(f => f.brush != -1 && f.face != -1);
        int group = first != null
            ? MapTools.ActiveMap.Brushes[first.brush].Faces[first.face].smoothGroup
            : 0;

        _suppress = true;
        for (int i = 0; i < smoothingButtons.Length; i++)
            smoothingButtons[i].IsChecked = (group & (1 << i)) != 0;
        _suppress = false;
    }
}