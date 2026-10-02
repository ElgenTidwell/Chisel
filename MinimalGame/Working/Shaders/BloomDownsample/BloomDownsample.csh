vert = "../PostFXQuad.vert"

samplers
{
    SourceTexture = "LinearClamp"
    ExposureTexture = "LinearClamp"
}

BloomPrefilter { frag = "BloomPrefilter.frag" }
BoxDownsample { frag = "BoxDownsample.frag" }