using Engine;
using Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace MinimalGame.Rendering;

public class Bloom
{
    RenderTarget2D[] bloomChain;
    ShaderHandle downsampleEffect;
    ShaderHandle upsampleEffect;
    GraphicsDevice graphicsDevice;

    public float Intensity = 0.1f;
    public float FilterRadius = 2f;
    public const int MipCount = 4;
    public float Threshold = 3f;
    public float SoftKnee = 0.5f;

    VertexBuffer quadVB; // repeated in both files cause idgaf heh

    public Bloom()
    {
        graphicsDevice = MainEngine.Instance.GraphicsDevice;
        downsampleEffect = ShaderBuilder.BuildContentShader(graphicsDevice, "BloomDownsample");
        upsampleEffect = ShaderBuilder.BuildContentShader(graphicsDevice, "BloomUpsample");
        BuildChain(RenderEngine.ScreenRenderTexture.Width, RenderEngine.ScreenRenderTexture.Height);
    }

    void BuildChain(int width, int height)
    {
        var sizes = new List<Point>();
        int w = Math.Max(1, width / 2);
        int h = Math.Max(1, height / 2);
        sizes.Add(new Point(w, h));
        for (int i = 1; i < MipCount; i++)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            sizes.Add(new Point(w, h));
            if (w <= 8 || h <= 8)
            {
                break;
            }
        }
        bloomChain = new RenderTarget2D[sizes.Count];
        for (int i = 0; i < sizes.Count; i++)
        {
            bloomChain[i] = new RenderTarget2D(graphicsDevice, sizes[i].X, sizes[i].Y, false, SurfaceFormat.HdrBlendable, DepthFormat.None, 0, RenderTargetUsage.PlatformContents);
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
        for (int i = 0; i < bloomChain.Length; i++)
        {
            bloomChain[i].Dispose();
        }
        BuildChain(width, height);
    }

    public Texture2D BloomTexture
    {
        get { return bloomChain[0]; }
    }
    public void Update(Texture2D resolvedHdrScene)
    {
        downsampleEffect.SetTechnique("BloomPrefilter");
        downsampleEffect.Param("texelSize").SetValue(new Vector2(1.0f / resolvedHdrScene.Width, 1.0f / resolvedHdrScene.Height));
        downsampleEffect.Param("threshold").SetValue(Threshold);
        downsampleEffect.Param("softKnee").SetValue(SoftKnee);
        downsampleEffect.Param("ExposureTexture").SetValue(GameEngine.Exposure.ExposureTexture);
        downsampleEffect.Param("KeyValue").SetValue(GameEngine.Exposure.KeyValue);
        downsampleEffect.Param("MinExposure").SetValue(GameEngine.Exposure.MinExposure);
        downsampleEffect.Param("MaxExposure").SetValue(GameEngine.Exposure.MaxExposure);

        graphicsDevice.SetRenderTarget(bloomChain[0]);
        graphicsDevice.SetVertexBuffer(quadVB);
        graphicsDevice.RasterizerState = RasterizerState.CullNone;
        graphicsDevice.BlendState = BlendState.Opaque;
        graphicsDevice.DepthStencilState = DepthStencilState.None;
        graphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;

        graphicsDevice.Viewport = new Viewport(0, 0, bloomChain[0].Width, bloomChain[0].Height);

        downsampleEffect.Param("SourceTexture").SetValue(resolvedHdrScene);

        downsampleEffect.ApplyPass(0);
        graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, 2);

        downsampleEffect.SetTechnique("BoxDownsample");
        for (int i = 1; i < bloomChain.Length; i++)
        {
            RenderTarget2D source = bloomChain[i - 1];
            RenderTarget2D target = bloomChain[i];
            downsampleEffect.Param("texelSize").SetValue(new Vector2(1.0f / source.Width, 1.0f / source.Height));
            graphicsDevice.SetRenderTarget(target);

            graphicsDevice.Viewport = new Viewport(0, 0, bloomChain[i].Width, bloomChain[i].Height);
            downsampleEffect.Param("SourceTexture").SetValue(source);

            downsampleEffect.ApplyPass(0);
            graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, 2);
        }

        for (int i = bloomChain.Length - 2; i >= 0; i--)
        {
            RenderTarget2D source = bloomChain[i + 1];
            RenderTarget2D target = bloomChain[i];
            upsampleEffect.Param("texelSize").SetValue(new Vector2(1.0f / source.Width, 1.0f / source.Height));
            upsampleEffect.Param("filterRadius").SetValue(FilterRadius);
            graphicsDevice.SetRenderTarget(target);

            graphicsDevice.Viewport = new Viewport(0, 0, bloomChain[i].Width, bloomChain[i].Height);
            upsampleEffect.Param("SourceTexture").SetValue(source);

            upsampleEffect.ApplyPass(0);
            graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, 2);
        }

        graphicsDevice.SetRenderTarget(null);
        graphicsDevice.SetVertexBuffer(null);
    }
}