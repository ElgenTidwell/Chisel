vert = "../PostFXQuad.vert"

samplers
{
    CurrentLuminanceTexture = "LinearClamp"
    PreviousExposureTexture = "LinearClamp"
}

AdaptExposure { frag = "AdaptExposure.frag" }