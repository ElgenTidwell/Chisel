using Engine;
using Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace MinimalGame.Rendering;

public class AutoExposure
{
    RenderTarget2D[] luminanceChain;
    RenderTarget2D luminanceReduced;
    RenderTarget2D exposureA;
    RenderTarget2D exposureB;
    bool pingPong;
    ShaderHandle luminanceInitialEffect;
    ShaderHandle downsampleEffect;
    ShaderHandle adaptEffect;
    GraphicsDevice graphicsDevice;

    public float KeyValue = 0.35f;
    public float MinExposure = 0.1f;
    public float MaxExposure = 8.0f;
    public float AdaptationSpeedUp = 1.0f;
    public float AdaptationSpeedDown = 0.8f;

    public float MeteringRadius = 0.7f;
    public float MinMeteredLuminance = 0.02f;
    public float MaxMeteredLuminance = 20.0f;
    public float MeteringVerticalBias = 0.15f;
    public float MinMeteredWeight = 0.15f;

    public int UpdateInterval = 2;
    int frameCounter;
    float accumulatedDeltaTime;

    VertexBuffer quadVB; // repeated in both files cause idgaf heh

    public AutoExposure()
    {
        graphicsDevice = MainEngine.Instance.GraphicsDevice;
        luminanceInitialEffect = ShaderBuilder.BuildContentShader(graphicsDevice, "LuminanceInitial");
        downsampleEffect = ShaderBuilder.BuildContentShader(graphicsDevice, "LuminanceDownsample");
        adaptEffect = ShaderBuilder.BuildContentShader(graphicsDevice, "AdaptExposure");
        BuildChain(RenderEngine.ScreenRenderTexture.Width, RenderEngine.ScreenRenderTexture.Height);
        luminanceReduced = new RenderTarget2D(graphicsDevice, 1, 1, false, SurfaceFormat.HalfSingle, DepthFormat.None, 0, RenderTargetUsage.PlatformContents);
        exposureA = new RenderTarget2D(graphicsDevice, 1, 1, false, SurfaceFormat.HalfSingle, DepthFormat.None, 0, RenderTargetUsage.PlatformContents);
        exposureB = new RenderTarget2D(graphicsDevice, 1, 1, false, SurfaceFormat.HalfSingle, DepthFormat.None, 0, RenderTargetUsage.PlatformContents);
        ClearExposureTargets();
    }

    void ClearExposureTargets()
    {
        graphicsDevice.SetRenderTarget(exposureA);
        graphicsDevice.Clear(new Color(0.18f, 0.18f, 0.18f, 1.0f));
        graphicsDevice.SetRenderTarget(exposureB);
        graphicsDevice.Clear(new Color(0.18f, 0.18f, 0.18f, 1.0f));
        graphicsDevice.SetRenderTarget(null);
    }

    void BuildChain(int width, int height)
    {
        var sizes = new List<Point>();
        int w = Math.Max(1, width / 8);
        int h = Math.Max(1, height / 8);
        sizes.Add(new Point(w, h));
        while (w > 8 || h > 8)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            sizes.Add(new Point(w, h));
        }
        luminanceChain = new RenderTarget2D[sizes.Count];
        for (int i = 0; i < sizes.Count; i++)
        {
            luminanceChain[i] = new RenderTarget2D(graphicsDevice, sizes[i].X, sizes[i].Y, false, SurfaceFormat.HalfSingle, DepthFormat.None, 0, RenderTargetUsage.PlatformContents);
        }

        var verts = new VertexPositionTexture[]
        {
            new VertexPositionTexture(new (-1f, -1f, 0f), new (0f, 1f)),
            new VertexPositionTexture(new ( 1f, -1f, 0f), new (1f, 1f)),
            new VertexPositionTexture(new (-1f,  1f, 0f), new (0f, 0f)),
            new VertexPositionTexture(new (-1f,  1f, 0f), new (0f, 0f)),
            new VertexPositionTexture(new ( 1f, -1f, 0f), new (1f, 1f)),
            new VertexPositionTexture(new ( 1f,  1f, 0f), new (1f, 0f)),
        };
        quadVB = new VertexBuffer(graphicsDevice, typeof(VertexPositionTexture), 6, BufferUsage.None);
        quadVB.SetData(verts);
    }

    public void Resize(int width, int height)
    {
        for (int i = 0; i < luminanceChain.Length; i++)
        {
            luminanceChain[i].Dispose();
        }
        BuildChain(width, height);
    }

    public Texture2D ExposureTexture
    {
        get { return pingPong ? exposureB : exposureA; }
    }

    public void Update(float deltaTime)
    {
        accumulatedDeltaTime += deltaTime;
        frameCounter++;
        if (frameCounter < UpdateInterval)
        {
            return;
        }
        frameCounter = 0;
        float dt = accumulatedDeltaTime;
        accumulatedDeltaTime = 0f;

        var resolvedHdrScene = RenderEngine.ScreenRenderTexture;

        graphicsDevice.SetRenderTarget(luminanceChain[0]);
        luminanceInitialEffect.Param("meteringRadius").SetValue(MeteringRadius);
        luminanceInitialEffect.Param("meteringVerticalBias").SetValue(MeteringVerticalBias);
        luminanceInitialEffect.Param("minMeteredWeight").SetValue(MinMeteredWeight);
        luminanceInitialEffect.Param("minMeteredLuminance").SetValue(MinMeteredLuminance);
        luminanceInitialEffect.Param("maxMeteredLuminance").SetValue(MaxMeteredLuminance);

        graphicsDevice.SetVertexBuffer(quadVB);
        graphicsDevice.RasterizerState = RasterizerState.CullNone;
        graphicsDevice.BlendState = BlendState.Opaque;
        graphicsDevice.DepthStencilState = DepthStencilState.None;
        graphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;

        graphicsDevice.Viewport = new Viewport(0, 0, luminanceChain[0].Width, luminanceChain[0].Height);
        luminanceInitialEffect.Param("SceneTexture").SetValue(resolvedHdrScene);

        luminanceInitialEffect.ApplyPass(0);

        graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, 2);

        downsampleEffect.SetTechnique("LuminanceDownsample");
        for (int i = 1; i < luminanceChain.Length; i++)
        {
            RenderTarget2D source = luminanceChain[i - 1];
            RenderTarget2D target = luminanceChain[i];
            downsampleEffect.Param("texelSize").SetValue(new Vector2(1.0f / source.Width, 1.0f / source.Height));
            graphicsDevice.SetRenderTarget(target);

            graphicsDevice.Viewport = new Viewport(0, 0, luminanceChain[i].Width, luminanceChain[i].Height);
            downsampleEffect.Param("SourceTexture").SetValue(source);

            downsampleEffect.ApplyPass(0);
            graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, 2);
        }

        RenderTarget2D lastMip = luminanceChain[^1];
        downsampleEffect.SetTechnique("LuminanceFinalReduce");
        downsampleEffect.Param("texelSize").SetValue(new Vector2(1.0f / lastMip.Width, 1.0f / lastMip.Height));
        downsampleEffect.Param("SourceTexture").SetValue(lastMip);
        graphicsDevice.SetRenderTarget(luminanceReduced);
        graphicsDevice.Viewport = new Viewport(0, 0, 1, 1);
        downsampleEffect.ApplyPass(0);
        graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, 2);

        RenderTarget2D previousExposure = pingPong ? exposureB : exposureA;
        RenderTarget2D nextExposure = pingPong ? exposureA : exposureB;
        adaptEffect.Param("PreviousExposureTexture").SetValue(previousExposure);
        adaptEffect.Param("deltaTime").SetValue(dt);
        adaptEffect.Param("adaptationSpeedUp").SetValue(AdaptationSpeedUp);
        adaptEffect.Param("adaptationSpeedDown").SetValue(AdaptationSpeedDown);

        graphicsDevice.SetRenderTarget(nextExposure);

        graphicsDevice.Viewport = new Viewport(0, 0, nextExposure.Width, nextExposure.Height);
        adaptEffect.Param("CurrentLuminanceTexture").SetValue(luminanceReduced);

        adaptEffect.ApplyPass(0);
        graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, 2);

        pingPong = !pingPong;
        graphicsDevice.SetRenderTarget(null);
        graphicsDevice.SetVertexBuffer(null);
    }
}