vert = "../PostFXQuad.vert"

samplers
{
    ScreenTexture = "LinearClamp"
    DepthTexture = "LinearClamp"
    ExposureTexture = "LinearClamp"
    BloomTexture = "LinearClamp"
}

FinalImage { frag = "PostProcessing.frag" }