vert = "../PostFXQuad.vert"

samplers
{
    SourceTexture = "LinearClamp"
}

LuminanceDownsample { frag = "LuminanceDownsample.frag" }
LuminanceFinalReduce { frag = "LuminanceFinalReduce.frag" }