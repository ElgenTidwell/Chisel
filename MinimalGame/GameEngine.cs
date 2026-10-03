using Engine;
using Engine.Rendering;
using Engine.UI;
using Engine.Utils;
using Engine.Utils.Settings;
using Gum.Wireframe;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MinimalGame.Rendering;
using MonoGameGum.GueDeriving;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;

namespace MinimalGame;
public class GameEngine : MainEngine
{
    public static CVarBool AIDebug = new CVarBool("ai_debug", false);
    public static CVarBool AIDebugNearest = new CVarBool("ai_debug_nearest", false);
    public static CVarBool AINoTarget = new CVarBool("ai_notarget", false);
    public static CVarBool AIDisable = new CVarBool("ai_disable", false);
    private float totalSeconds;

    public static AutoExposure Exposure;
    public static Bloom Bloom;
    bool isActivelyPlaying => IsMapLoaded;

    private float autosaveTimer = 0;

    public GameEngine() : base()
    {
        GameSettings.GameName = "Game";
        Content.RootDirectory = "Content";
        IsMouseVisible = false;
        Instance = this;
    }

    protected override void Initialize()
    {
        DefaultCursorLockState = true;
        Window.AllowUserResizing = false;
        Window.Title = "Game";

        GameSettings.Settings.Add("mouse_sensitivity", 1f);

        base.Initialize();
    }

    protected override void LoadSetting(string settingName, object value)
    {
        base.LoadSetting(settingName, value);
        switch (settingName)
        {
            // settings
        }
    }
    protected override void LoadContent()
    {
        PostProcessingShader = ShaderBuilder.BuildContentShader(GraphicsDevice, "PostProcessing");

        Exposure = new AutoExposure();
        Bloom = new Bloom();
        Window.ClientSizeChanged += (s, a) =>
        {
            Exposure.Resize(RenderEngine.ScreenRenderTexture.Width, RenderEngine.ScreenRenderTexture.Height);
            Bloom.Resize(RenderEngine.ScreenRenderTexture.Width, RenderEngine.ScreenRenderTexture.Height);
        };

        base.LoadContent();

        var args = Environment.GetCommandLineArgs();
        if (args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                // regular command execution. Map is a bit different because of the way it's passed from RW2
                if (args[i].StartsWith('+'))
                {
                    var commandParts = new List<string> { args[i].Substring(1) };
                    int j = i + 1;
                    while (j < args.Length && !args[j].StartsWith('+') && !args[j].StartsWith('-'))
                    {
                        commandParts.Add(args[j]);
                        j++;
                    }

                    Console.Execute(string.Join(' ', commandParts));
                    i = j - 1;
                    continue;
                }

                if (!args[i].StartsWith('-')) continue;
                if (args[i] == "-map") { LoadMap(args[i + 1]); IsPaused = false; }
            }
        }
    }

    protected override void Update(GameTime gameTime)
    {
        DefaultCursorLockState = isActivelyPlaying;
        PauseWhenMenusOpen = isActivelyPlaying;

        if (!isActivelyPlaying)
        {
            UnlockMouse();
        }
        bool showMenu = (IsPaused || !isActivelyPlaying);
        if (showMenu && !GumUtils.IsScreenActive("Menu"))
        {
            ShowMenu();
        }
        if (!showMenu && GumUtils.IsScreenActive("Menu"))
        {
            GumUtils.RemoveScreen("Menu");
        }

        base.Update(gameTime);

        PostProcessingShader.Param("time").SetValue(totalSeconds);
    }

    public override void OnMapLoaded()
    {
        base.OnMapLoaded();
    }

    protected override void Draw(GameTime gameTime)
    {
        PrepWorldRender();
        base.Draw(gameTime);

        Exposure.Update(PreviousFrameDelta);
        Bloom.Update(RenderEngine.ScreenRenderTexture);

        PostProcessingShader.Param("ExposureTexture").SetValue(Exposure.ExposureTexture);
        PostProcessingShader.Param("KeyValue").SetValue(Exposure.KeyValue);
        PostProcessingShader.Param("MinExposure").SetValue(Exposure.MinExposure);
        PostProcessingShader.Param("MaxExposure").SetValue(Exposure.MaxExposure);
        PostProcessingShader.Param("BloomTexture").SetValue(Bloom.BloomTexture);
        PostProcessingShader.Param("BloomIntensity").SetValue(Bloom.Intensity);

        EndWorldRender();
    }

    private void ShowMenu()
    {
        var menu = GumUtils.ShowScreen("Menu");

        var saveButton = ((InteractiveGue)menu.GetChildByNameRecursively("SaveButton"));

        var text = (TextRuntime)saveButton.GetChildByName("TextInstance");

        text.Text = isActivelyPlaying ? "SAVE GAME" : "NEW GAME";

        saveButton.Click += (s, a) =>
        {
            if (!isActivelyPlaying)
            {
                // Start a new game. Here you would load a map.
                LoadMap($"{FullPath}/Maps/empty");
            }
            else
            {
                Console.Execute("save_wnd");
            }
        };
        ((InteractiveGue)menu.GetChildByNameRecursively("LoadButton")).Click += (s, a) =>
        {
            Console.Execute("load_wnd");
        };
        ((InteractiveGue)menu.GetChildByNameRecursively("OptionsButton")).Click += (s, a) =>
        {
            Console.Execute("opt_wnd");
        };
        ((InteractiveGue)menu.GetChildByNameRecursively("QuitButton")).Click += (s, a) =>
        {
            var window = CGUI.Window("Confirmation", 200, 150)
                            .Label("Are you sure?")
                            .BottomBar(("Cancel", (from) => from.Close()), ("Confirm", (from) => Console.Execute("quit")))
                            .Build();
        };
    }
}