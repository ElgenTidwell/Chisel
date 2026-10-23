using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Platform.Storage;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Models;
using Rockwall2.Editor.Common;
using Rockwall2.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rockwall2.Editor.Model.Utils;
public static class ModelImporter
{
    static async Task<IStorageFile> GetFBX()
    {
        var topLevel = TopLevel.GetTopLevel(MainWindow.Instance);
        var storageProvider = topLevel.StorageProvider;
        var startPart = await storageProvider.TryGetFolderFromPathAsync(System.IO.Path.Combine(GlobalEditorData.WorkingDirectory, "Models"));

        var customType = new FilePickerFileType("FBX Files")
        {
            Patterns = new[] { "*.fbx" },
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open File",
            FileTypeFilter = new[] { customType },
            AllowMultiple = false,
            SuggestedStartLocation = startPart,
        });

        if (files == null || files.Count <= 0) return null;

        return files[0];
    }

    static async Task ShowSimpleError(string title, string message)
    {
        var box = MessageBoxManager.GetMessageBoxCustom(new MessageBoxCustomParams
        {
            ButtonDefinitions = new List<ButtonDefinition>
            {
                new ButtonDefinition { Name = "Ok" },
            },
            ContentTitle = title,
            ContentMessage = message,
            SystemDecorations = SystemDecorations.BorderOnly,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            CloseOnClickAway = true
        });
        await box.ShowAsPopupAsync(MainWindow.Instance);
    }

    public static async void ImportFromBlender()
    {
        var dialog = new BlenderImport();
        var result = await dialog.ShowDialog<bool>(MainWindow.Instance);
        if (!result) return;

        string blendPath = dialog.SelectedBlendPath;
        var importMode = dialog.ImportMode;

        List<string> collections = new();
        string scanError = null;
        await Task.Run(() =>
        {
            try
            {
                collections = BlenderConverter.ListCollections(blendPath, BlenderDetector.BlenderExePath);
            }
            catch (Exception ex)
            {
                scanError = ex.Message;
            }
        });

        string? collectionFilter = null;
        if (scanError != null)
        {
            await ShowSimpleError(
                "Collection Scan Failed",
                $"Couldn't scan collections in this file, importing everything instead.\n\n{scanError}");
        }
        else if (collections.Count > 1)
        {
            var picker = new CollectionPicker(collections);
            if (!await picker.ShowDialog<bool>(MainWindow.Instance)) return; // user cancelled
            collectionFilter = picker.SelectedCollection; // null = import everything
        }

        string meshFbx = null;
        List<(string name, string path)> animFbxes = null;
        List<(string meshName, string key, string path)> shapeKeys = null;
        string errorMsg = null;

        await Task.Run(() =>
        {
            try
            {
                (meshFbx, animFbxes, shapeKeys) =
                    BlenderConverter.Export(blendPath, BlenderDetector.BlenderExePath, collectionFilter);
            }
            catch (Exception ex) { errorMsg = ex.Message; }
        });
        if (errorMsg != null)
        {
            await ShowSimpleError("Issues Importing", $"Error: {errorMsg}");
            return;
        }
        MainWindow.Instance.modelEditor.Clear();
        try
        {
            switch (importMode)
            {
                case BlendImportMode.Full:
                    ModelEditorData.ImportFromBlendFull(meshFbx, animFbxes, shapeKeys);
                    break;

                case BlendImportMode.Mesh:
                    ModelEditorData.ImportFromBlendMesh(meshFbx, shapeKeys);
                    break;

                case BlendImportMode.Animations:
                    var fbxNames = ModelEditorData.PeekBlendAnimationNames(animFbxes);
                    var existingNames = ModelEditorData.ActiveModel?.Animations
                        .Select(s => s.Name).ToHashSet() ?? new HashSet<string>();

                    var picker = new AnimationPicker(fbxNames, existingNames);
                    var res = await picker.ShowDialog<bool>(MainWindow.Instance);
                    if (!res || picker.SelectedAnimations.Count == 0) break;

                    ModelEditorData.ImportFromBlendAnimations(
                        animFbxes, picker.SelectedAnimations);
                    break;
            }

            ModelEditorData.MorphState.Initialize(ModelEditorData.ActiveModel);

            var dangling = ModelEditorData.FindDanglingSequenceReferences();
            if (dangling.Count > 0)
            {
                string list = string.Join("\n", dangling.Select(d => $"  {d.sequenceName} → \"{d.missingAnimationName}\""));
                var box = MessageBoxManager.GetMessageBoxCustom(new MessageBoxCustomParams
                {
                    ButtonDefinitions = new List<ButtonDefinition> { new ButtonDefinition { Name = "Ok" } },
                    ContentTitle = "Sequences reference missing animations",
                    ContentMessage = $"These sequences reference animations that no longer exist after this import:\n\n{list}",
                    SystemDecorations = SystemDecorations.BorderOnly,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    CloseOnClickAway = true
                });
                await box.ShowAsPopupAsync(MainWindow.Instance);
            }

            MainWindow.Instance.modelEditor.RefreshAll();
        }
        finally
        {
            BlenderConverter.Cleanup(meshFbx);
        }
    }
    public static async void ImportFromFBX()
    {
        var fbx = await GetFBX();
        if (fbx == null) return;

        MainWindow.Instance.modelEditor.Clear();

        ModelEditorData.ImportFBXFile(fbx.Path.LocalPath);

        MainWindow.Instance.modelEditor.RefreshAll();
    }
    public static async void ReimportFBXMesh()
    {
        var fbx = await GetFBX();
        if (fbx == null) return;

        MainWindow.Instance.modelEditor.Clear();

        ModelEditorData.ReimportFBXModel(fbx.Path.LocalPath);

        MainWindow.Instance.modelEditor.RefreshAll();
    }
    public static async void ReimportFBXAnimations()
    {
        var fbx = await GetFBX();
        if (fbx == null) return;

        MainWindow.Instance.modelEditor.Clear();

        var fbxNames = ModelEditorData.PeekFBXAnimationNames(fbx.Path.LocalPath);
        if (fbxNames.Count == 0)
        {
            await ShowSimpleError("Issues Reimporting", "There were no animations found on the FBX.");
            return;
        }

        var existingNames = ModelEditorData.ActiveModel?.Animations
            .Select(s => s.Name)
            .ToHashSet() ?? new HashSet<string>();

        var picker = new AnimationPicker(fbxNames, existingNames);
        var ret = await picker.ShowDialog<bool>(MainWindow.Instance);
        if (!ret || picker.SelectedAnimations.Count == 0) return;

        ModelEditorData.ReimportFBXAnimations(fbx.Path.LocalPath, picker.SelectedAnimations);

        var dangling = ModelEditorData.FindDanglingSequenceReferences();
        if (dangling.Count > 0)
        {
            string list = string.Join("\n", dangling.Select(d => $"  {d.sequenceName} → \"{d.missingAnimationName}\""));
            var box = MessageBoxManager.GetMessageBoxCustom(new MessageBoxCustomParams
            {
                ButtonDefinitions = new List<ButtonDefinition> { new ButtonDefinition { Name = "Ok" } },
                ContentTitle = "Sequences reference missing animations",
                ContentMessage = $"These sequences reference animations that no longer exist after this import:\n\n{list}",
                SystemDecorations = SystemDecorations.BorderOnly,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                CloseOnClickAway = true
            });
            await box.ShowAsPopupAsync(MainWindow.Instance);
        }

        MainWindow.Instance.modelEditor.RefreshAll();
    }
}