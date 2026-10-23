using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Rockwall2.Editor.Common;
using Rockwall2.Views;

namespace Rockwall2;

public enum BlendImportMode { Full, Mesh, Animations }

public partial class BlenderImport : Window
{
    public string SelectedBlendPath { get; private set; }
    public BlendImportMode ImportMode { get; private set; }

    private bool isWindowDragInEffect = false;
    private Point cursorDragStart = new(0, 0);
    public BlenderImport()
    {
        InitializeComponent();
    }
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

    private void Ok_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ImportMode = rdoFull.IsChecked == true ? BlendImportMode.Full
                   : rdoMesh.IsChecked == true ? BlendImportMode.Mesh
                   : BlendImportMode.Animations;
        Close(true);
    }
    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(false);
    }
    private async void Browse_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(MainWindow.Instance);
        var storageProvider = topLevel.StorageProvider;
        var startPart = await storageProvider.TryGetFolderFromPathAsync(System.IO.Path.Combine(GlobalEditorData.WorkingDirectory, "Models"));

        var customType = new FilePickerFileType("Blender Files")
        {
            Patterns = new[] { "*.blend" },
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open File",
            FileTypeFilter = new[] { customType },
            AllowMultiple = false,
            SuggestedStartLocation = startPart,
        });

        if (files == null || files.Count <= 0) return;

        SelectedBlendPath = files[0].Path.LocalPath;
        blendPathBox.Text = SelectedBlendPath;
        btnOK.IsEnabled = true;
    }
}