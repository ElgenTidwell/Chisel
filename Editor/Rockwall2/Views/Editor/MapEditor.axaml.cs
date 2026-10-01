using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Rockwall;
using Rockwall2.Editor.Common;
using Rockwall2.Editor.Common.Toolbar;
using Rockwall2.Editor.Mapper;
using Rockwall2.Editor.Mapper.Toolbar;
using Rockwall2.Editor.Mapper.Utils;
using Rockwall2.Tools;
using Rockwall2.ViewModels.Editor;
using Rockwall2.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rockwall2;
public partial class MapEditor : UserControl
{
    public Control GameView => gameControl;

    private ToolButtonDef?[] hotbarSlots = Array.Empty<ToolButtonDef?>();
    private readonly List<Border> hotbarSlotBorders = new();
    private DispatcherTimer hotbarHideTimer = null!;

    Guid? SelectedUserGroup => (visGroupsUser.SelectedItem as TreeViewItem)?.Tag as Guid?;

    public MapEditor()
    {
        if (!Design.IsDesignMode)
            DataContext = new MapEditorViewModel();

        InitializeComponent();
        gameControl.Host = App.Host;
        GameView.PointerEntered += (a, b) => GameView.Focus();

        var atlas = new ToolIconAtlas(
            new Bitmap(AssetLoader.Open(new Uri("avares://Rockwall2/Assets/Icons/rw2icons.png"))),
            cellSize: 32);

        var leftCategories = ToolbarDefinitions.BuildLeftToolbar(atlas);
        var topCategories = ToolbarDefinitions.BuildTopToolbar(atlas);

        LeftToolbar.ItemsSource = leftCategories;
        TopToolbar.ItemsSource = topCategories;

        Hotbar.RegisterAvailable(leftCategories.Concat(topCategories));
        ToolbarDefinitions.ApplyDefaultHotbarAssignments();

        BuildHotbarVisuals();
        Hotbar.SlotAssigned += slot => Dispatcher.UIThread.Post(() => RefreshHotbarSlot(slot));
        Hotbar.SlotActivated += slot => Dispatcher.UIThread.Post(() => ShowHotbarPopout(slot));

        hotbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        hotbarHideTimer.Tick += (_, _) => { hotbarHideTimer.Stop(); HideHotbar(); };

        VisGroupManager.Changed += () => Dispatcher.UIThread.Post(RefreshVisGroups);
        RefreshVisGroups();

        RefreshGridSize();
    }

    private void BuildHotbarVisuals()
    {
        HotbarItems.Children.Clear();
        hotbarSlotBorders.Clear();
        for (int slot = 1; slot <= Hotbar.SlotCount; slot++)
        {
            var border = new Border { Classes = { "hotbar-slot" } };
            hotbarSlotBorders.Add(border);
            HotbarItems.Children.Add(border);
            RefreshHotbarSlot(slot);
        }
    }
    private void RefreshHotbarSlot(int slot)
    {
        var border = hotbarSlotBorders[slot - 1];
        var def = Hotbar.GetSlot(slot);

        border.Classes.Set("empty", def == null);
        ToolTip.SetTip(border, def != null ? $"{slot}: {def.ToolTip}" : $"{slot}: (empty)");
        border.Child = def != null
            ? new Image { Source = def.IconSource, Stretch = Stretch.Uniform }
            : null;
    }
    private void ShowHotbarPopout(int slot)
    {
        for (int i = 0; i < hotbarSlotBorders.Count; i++)
            hotbarSlotBorders[i].Classes.Set("active", i + 1 == slot);

        HotbarOverlay.Opacity = 1;
        HotbarOverlay.Margin = new Thickness(0, 56, 0, 0);
        hotbarHideTimer.Stop();
        hotbarHideTimer.Start();
    }
    private void HideHotbar()
    {
        HotbarOverlay.Opacity = 0;
    }

    private async void BrowseTextures_Click(object? sender, RoutedEventArgs e)
    {
        int mat = await MaterialPicker.PickAsync(MainWindow.Instance);
        if (mat == -1) return;

        TexturePreview.Source = GlobalEditorData.TexturesAsImages[mat].Image;
        Toolbelt.ActiveTexture = GlobalMapData.LoadedMaterials[mat].Name;
    }
    public void RefreshViews()
    {
        TexturePreview.Source = GlobalEditorData.TexturesAsImages[GlobalMapData.MaterialNameToIndex[Toolbelt.ActiveTexture]].Image;
    }
    public void RefreshGridSize()
    {
        gridSizeLabel.Content = Transformable.GridSize;
    }

    public void ChangePrimitiveRes(object? sender, NumericUpDownValueChangedEventArgs args)
    {
        (Toolbelt.BrushTool as BrushTool).SetPrimitiveParams((int)(args.NewValue??0));
    }


    void VisNew_Click(object? s, RoutedEventArgs e)
    {
        if (VisGroupManager.Groups == null) return;
        VisGroupManager.Create($"Group {VisGroupManager.Groups.Count + 1}", SelectedUserGroup);
    }
    void VisDelete_Click(object? s, RoutedEventArgs e) { if (SelectedUserGroup is { } id) VisGroupManager.Delete(id); }
    void VisAddSel_Click(object? s, RoutedEventArgs e) { if (SelectedUserGroup is { } id) VisGroupManager.AddSelection(id); }
    void VisRemoveSel_Click(object? s, RoutedEventArgs e) { if (SelectedUserGroup is { } id) VisGroupManager.RemoveSelection(id); }
    void VisShowAll_Click(object? s, RoutedEventArgs e) { if (VisGroupManager.Groups != null) VisGroupManager.ShowAll(); }

    public void RefreshVisGroups()
    {
        visGroupsUser.Items.Clear();
        visGroupsAuto.Items.Clear();

        // Auto
        TreeViewItem Auto(string name, AutoGroupFlags f, params TreeViewItem[] kids)
        {
            var item = new TreeViewItem
            {
                IsExpanded = true,
                Tag = f,
                Header = MakeVisHeader(name, (VisGroupManager.HiddenAuto & f) == 0,
                                       v => VisGroupManager.SetAutoVisible(f, v))
            };
            foreach (var k in kids) item.Items.Add(k);
            return item;
        }
        visGroupsAuto.Items.Add(Auto("World", AutoGroupFlags.World,
            Auto("Solid", AutoGroupFlags.Solid), Auto("Sky", AutoGroupFlags.Sky), Auto("Terrain", AutoGroupFlags.Terrain)));
        visGroupsAuto.Items.Add(Auto("Entities", AutoGroupFlags.Entities,
            Auto("Point Entities", AutoGroupFlags.PointEntities), Auto("Brush Entities", AutoGroupFlags.BrushEntities),
            Auto("Lights", AutoGroupFlags.Lights), Auto("Triggers", AutoGroupFlags.Triggers)));

        // User
        if (VisGroupManager.Groups == null) return;
        foreach (var g in VisGroupManager.ChildrenOf(null)) visGroupsUser.Items.Add(BuildUserNode(g));
    }

    TreeViewItem BuildUserNode(UserVisGroup g)
    {
        var item = new TreeViewItem
        {
            Tag = g.ID,
            IsExpanded = true,
            Header = MakeVisHeader(g.Name, g.Visible,
                                   v => VisGroupManager.SetVisible(g.ID, v),
                                   n => VisGroupManager.Rename(g.ID, n))
        };
        foreach (var c in VisGroupManager.ChildrenOf(g.ID)) item.Items.Add(BuildUserNode(c));
        return item;
    }

    static Control MakeVisHeader(string text, bool visible, Action<bool> onToggle, Action<string>? onRename = null)
    {
        var cb = new CheckBox
        {
            Classes = { "vis-check" },
            IsChecked = visible
        };
        cb.IsCheckedChanged += (_, _) => onToggle(cb.IsChecked == true);

        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(cb);
        panel.Children.Add(label);

        if (onRename != null)
        {
            label.DoubleTapped += (_, _) =>
            {
                var box = new TextBox { Text = label.Text, MinHeight = 0, MinWidth = 60, FontSize = 10, Padding = new Thickness(2, 0) };
                panel.Children[1] = box;
                box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };
                bool done = false;
                void Commit()
                {
                    if (done) return; done = true;
                    var n = box.Text?.Trim();
                    if (!string.IsNullOrEmpty(n)) onRename(n); else VisGroupManager.RaiseChanged();
                }
                box.KeyDown += (_, k) => { if (k.Key == Key.Enter) Commit(); };
                box.LostFocus += (_, _) => Commit();
            };
        }
        return panel;
    }
}