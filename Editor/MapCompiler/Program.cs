using Chisel;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Rockwall;
using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace MapCompiler
{
    internal class Program
    {
        public const int VersionMajor = 8;
        public const int VersionMinor = 0;
        public const int VersionPatch = 0;

        public static string WorkingDir;
        static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            PrintBanner();

            if (args == null || args.Length < 1) { CompilerConsole.Error("No map file specified."); return -100; }
            if (args.Length < 2) { CompilerConsole.Error("No materials file specified."); return -100; }
            if (args.Length < 3) { CompilerConsole.Error("No texture path specified."); return -100; }

            // Environment-variable overrides let build scripts tune quality without recompiling
            int lightmapUnitSize = int.Parse(Environment.GetEnvironmentVariable("lightmapUnitSize") ?? "4");
            bool fastVis = bool.Parse(Environment.GetEnvironmentVariable("fastVis") ?? "false");

#if !DEBUG
            try
            {
#endif
                CompilerConsole.Header("Loading Assets");

                CompilerConsole.Step("Loading map file...");
                var (brushes, entities, terrains) = LoadMap(File.ReadAllText(args[0][1..]));

                CompilerConsole.Step("Loading EDF...");
                LoadEDF(args[1][1..]);

                CompilerConsole.Step("Loading textures...");
                WorkingDir = args[2][1..];
                var (textures, matColors) = TextureLoader.Load(WorkingDir, brushes);

                CompilerConsole.Stat("Lightmap unit size", lightmapUnitSize);
                CompilerConsole.Stat("Fast vis", fastVis);

                var mapPath = args[0][1..];
                if (File.Exists(Path.ChangeExtension(mapPath, "leak"))) File.Delete(Path.ChangeExtension(mapPath, "leak"));

                MapCompileOrchestrator.Compile(
                    brushes, entities, terrains, textures, matColors,
                    mapPath, lightmapUnitSize, fastVis);

                return 0;
#if !DEBUG
            }
            catch (Exception e)
            {
                CompilerConsole.Error($"Compilation failed: {e}");
                Console.ReadKey();

                return -100;
            }
#endif
        }

        static void PrintBanner()
        {
            Console.WriteLine();

            (string text, ConsoleColor color)[] lines =
            {
                (@"  ██████╗██╗  ██╗██╗███████╗███████╗██╗      ", ConsoleColor.DarkRed),
                (@" ██╔════╝██║  ██║██║██╔════╝██╔════╝██║      ", ConsoleColor.DarkRed),
                (@" ██║     ███████║██║███████╗█████╗  ██║      ", ConsoleColor.Red    ),
                (@" ██║     ██╔══██║██║╚════██║██╔══╝  ██║      ", ConsoleColor.Red    ),
                (@" ╚██████╗██║  ██║██║███████║███████╗███████╗ ", ConsoleColor.White   ),
                (@"  ╚═════╝╚═╝  ╚═╝╚═╝╚══════╝╚══════╝╚══════╝ ", ConsoleColor.White  ),
            };

            foreach (var (text, color) in lines)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(text);
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(@"           M A P  C O M P I L E R   v"+$"{VersionMajor}.{VersionMinor}.{VersionPatch}");
            Console.ResetColor();
            Console.WriteLine();
        }

        static (Brush[] brushes, EntityReference[] entities, Terrain[] terrains) LoadMap(string json)
        {
            var map = Chisel.Formatter.MapMigration.LoadAndMigrate(json);
            return (map.Brushes, map.EntityReferences, map.Terrains);
        }

        static void LoadEDF(string edsPath)
        {
            MaterialLoader.MountMaterials(EntityDataIndex.Read(edsPath).MaterialsPath);
        }
    }
}