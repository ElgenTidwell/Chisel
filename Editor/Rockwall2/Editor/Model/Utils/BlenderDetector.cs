using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Rockwall2.Editor.Model.Utils;

public static class BlenderDetector
{
    public static string BlenderExePath { get; private set; }
    public static bool IsAvailable => BlenderExePath != null;

    public static void Detect()
    {
        BlenderExePath =
            TryRegistry()
            ?? TryKnownPaths()
            ?? TryPath()
            ?? TrySteam()
            ?? null;
    }

    private static string TryRegistry()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\BlenderFoundation");
            if (key != null)
            {
                var dir = key.GetValue("Install_Dir") as string;
                if (!string.IsNullOrEmpty(dir))
                {
                    var exe = Path.Combine(dir, "blender.exe");
                    if (File.Exists(exe)) return exe;
                }
            }

            using var versioned = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\BlenderFoundation");
            if (versioned != null)
            {
                var subkeys = versioned.GetSubKeyNames()
                    .OrderByDescending(s => s)   // highest version first
                    .ToArray();

                foreach (var sub in subkeys)
                {
                    using var subKey = versioned.OpenSubKey(sub);
                    var dir = subKey?.GetValue("Install_Dir") as string;
                    if (string.IsNullOrEmpty(dir)) continue;
                    var exe = Path.Combine(dir, "blender.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch { }
        return null;
    }

    private static string TryKnownPaths()
    {
        var programFiles = new[]
        {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };

        foreach (var root in programFiles)
        {
            var blenderRoot = Path.Combine(root, "Blender Foundation");
            if (!Directory.Exists(blenderRoot)) continue;

            var dirs = Directory.GetDirectories(blenderRoot, "Blender *")
                .OrderByDescending(d => d)
                .ToArray();

            foreach (var dir in dirs)
            {
                var exe = Path.Combine(dir, "blender.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }
    private static string TrySteam()
    {
        try
        {
            //TODO: linuxize
            using var steamKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam")
                              ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam");

            var steamPath = steamKey?.GetValue("InstallPath") as string;
            if (string.IsNullOrEmpty(steamPath)) return null;

            var libraryRoots = new List<string> { steamPath };

            var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                foreach (var line in File.ReadAllLines(vdfPath))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("\"path\"")) continue;

                    var parts = trimmed.Split('"');

                    if (parts.Length >= 4)
                        libraryRoots.Add(parts[3].Replace("\\\\", "\\"));
                }
            }

            foreach (var root in libraryRoots)
            {
                var exe = Path.Combine(root, "steamapps", "common", "Blender", "blender.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return null;
    }
    private static string TryPath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        string exeName = OperatingSystem.IsWindows() ? "blender.exe" : "blender";

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = Path.Combine(dir, exeName);
            if (File.Exists(exe)) return exe;
        }
        return null;
    }
}