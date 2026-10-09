using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
namespace Rockwall2.Editor.Model.Utils;
public static class BlenderConverter
{
    private record ShapeKeyManifestEntry(int mesh_index, string mesh_name, string key, string file);
    public static List<string> ListCollections(string blendPath, string blenderExe)
    {
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            "blend_collections_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string outFile = Path.Combine(tempDir, "collections.json");
        string scriptPath = Path.Combine(tempDir, "list_collections.py");

        try
        {
            File.WriteAllText(scriptPath, BuildListCollectionsScript(outFile), Encoding.UTF8);
            RunBlender(blenderExe, blendPath, scriptPath, showWindow: false);

            if (!File.Exists(outFile))
                return new List<string>();

            string json = File.ReadAllText(outFile);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string BuildListCollectionsScript(string outFile)
    {
        string outPath = outFile.Replace('\\', '/');
        var lines = new[]
        {
            "import bpy, json",
            "",
            "def collect_names(collection, seen):",
            "    names = []",
            "    for child in collection.children:",
            "        if child.name not in seen:",
            "            seen.add(child.name)",
            "            names.append(child.name)",
            "        names.extend(collect_names(child, seen))",
            "    return names",
            "",
            "seen = set()",
            "names = collect_names(bpy.context.scene.collection, seen)",
            "",
            $"with open(r'{outPath}', 'w', encoding='utf-8') as f:",
            "    json.dump(names, f)",
        };
        return string.Join("\n", lines);
    }


    public static (string meshFbx, List<(string name, string path)> animFbxes,
        List<(string meshName, string key, string path)> shapeKeys)
        Export(string blendPath, string blenderExe, string? collectionFilter = null)
    {
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            "blend_export_" + Path.GetFileNameWithoutExtension(blendPath));
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);
        string meshFbx = Path.Combine(tempDir, "mesh.fbx");
        string scriptPath = Path.Combine(tempDir, "export.py");
        File.WriteAllText(scriptPath, BuildScript(tempDir, meshFbx, collectionFilter), Encoding.UTF8);
        RunBlender(blenderExe, blendPath, scriptPath, showWindow: true);
        if (!File.Exists(meshFbx))
            throw new Exception(
                "Blender export failed; mesh.fbx was not produced.\n" +
                "Check the console window that appeared for the full error.");
        var animFbxes = new List<(string name, string path)>();
        foreach (var file in Directory.GetFiles(tempDir, "anim_*.fbx"))
        {
            string actionName = Path.GetFileNameWithoutExtension(file)
                .Substring("anim_".Length);
            animFbxes.Add((actionName, file));
        }

        var shapeKeys = new List<(string, string, string)>();
        string manifestPath = Path.Combine(tempDir, "shapekeys_manifest.json");
        if (File.Exists(manifestPath))
        {
            var entries = JsonSerializer.Deserialize<List<ShapeKeyManifestEntry>>(
                File.ReadAllText(manifestPath)) ?? new();
            foreach (var e in entries)
                shapeKeys.Add((e.mesh_name, e.key, Path.Combine(tempDir, e.file)));
        }

        return (meshFbx, animFbxes, shapeKeys);
    }
    private static string BuildScript(string tempDir, string meshFbx, string? collectionFilter)
    {
        string outDir = tempDir.Replace('\\', '/');
        string meshOut = meshFbx.Replace('\\', '/');
        string template = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Scripts", "export.py"));

        string filterLiteral = string.IsNullOrWhiteSpace(collectionFilter)
            ? ""
            : collectionFilter.Replace("\\", "\\\\").Replace("'", "\\'");

        return template
            .Replace("__MESH_OUT__", meshOut)
            .Replace("__OUT_DIR__", outDir)
            .Replace("__COLLECTION_FILTER__", filterLiteral);
    }

    private static void RunBlender(string blenderExe, string blendPath, string scriptPath, bool showWindow)
    {
        ProcessStartInfo psi;
        showWindow &= OperatingSystem.IsWindows();

        if (showWindow)
        {
            // cmd /c closes the window automatically after Blender exits
            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{blenderExe}\" \"{blendPath}\" --background --addons io_scene_fbx --python \"{scriptPath}\"\"",
                UseShellExecute = true,
                CreateNoWindow = false,
            };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = blenderExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(blendPath);
            psi.ArgumentList.Add("--background");
            psi.ArgumentList.Add("--addons");
            psi.ArgumentList.Add("io_scene_fbx");
            psi.ArgumentList.Add("--python");
            psi.ArgumentList.Add(scriptPath);
        }

        using var proc = Process.Start(psi)
            ?? throw new Exception("Failed to start Blender process.");

        if (!showWindow)
        {
            proc.StandardOutput.ReadToEndAsync();
            proc.StandardError.ReadToEndAsync();
        }

        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new Exception($"Blender exited with code {proc.ExitCode}.");
    }
    public static void Cleanup(string meshFbxPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(meshFbxPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }
}