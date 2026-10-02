uniform sampler2D ScreenTexture;
uniform sampler2D DepthTexture;
uniform sampler2D ExposureTexture;
uniform sampler2D BloomTexture;
uniform float BloomIntensity;

uniform float KeyValue;
uniform float MinExposure;
uniform float MaxExposure;

uniform vec2 screenSize;
uniform float time;
uniform float UnderwaterAmount;

const vec3 UnderwaterTintColor = vec3(0.55, 0.72, 0.66);
const vec3 UnderwaterDeepColor = vec3(0.03, 0.07, 0.05);
const vec3 AbsorptionCoeff = vec3(0.9, 0.55, 0.35);

const float DistortAmountLarge = 0.006;
const float DistortAmountFine = 0.002;
const float DistortSpeedLarge = 1.2;
const float DistortSpeedFine = 2.4;

varying vec2 uv;

vec2 UnderwaterDistortion(vec2 texCoord, float amount)
{
    vec2 offset;
    offset.x = sin(texCoord.y * 30.0 + time * DistortSpeedLarge) * DistortAmountLarge
             + sin(texCoord.y * 74.0 - time * DistortSpeedFine) * DistortAmountFine;
    offset.y = cos(texCoord.x * 24.0 - time * DistortSpeedLarge * 0.9) * DistortAmountLarge
             + cos(texCoord.x * 66.0 + time * DistortSpeedFine * 1.1) * DistortAmountFine;
    return offset * amount;
}

vec4 applyVignette(vec4 color, vec2 pos, float extraDarken)
{
    vec2 position = pos - 0.5;
    float dist = length(position);

    float vignette = smoothstep(1.0, 1.0 - 0.6, dist);
    float strength = mix(0.6, 0.85, extraDarken);
    float floorAmt = mix(0.4, 0.2, extraDarken);
    color.rgb = color.rgb * (vignette * strength + floorAmt);
    return color;
}

const float LottesA = 0.9;
const float LottesD = 0.977;
const float LottesHdrMax = 10.0;
const float LottesMidIn = 0.16;
const float LottesMidOut = 0.3;

vec3 Tonemap_Lottes(vec3 x)
{
    float a = LottesA;
    float d = LottesD;
    float hdrMax = LottesHdrMax;
    float midIn = LottesMidIn;
    float midOut = LottesMidOut;

    float b =
        (-pow(midIn, a) + pow(hdrMax, a) * midOut) /
        ((pow(hdrMax, a * d) - pow(midIn, a * d)) * midOut);
    float c =
        (pow(hdrMax, a * d) * pow(midIn, a) - pow(hdrMax, a) * pow(midIn, a * d) * midOut) /
        ((pow(hdrMax, a * d) - pow(midIn, a * d)) * midOut);

    return pow(x, vec3(a)) / (pow(x, vec3(a * d)) * b + c);
}

const float HableA = 0.15;
const float HableB = 0.50;
const float HableC = 0.20;
const float HableD = 0.20;
const float HableE = 0.02;
const float HableF = 0.30;
const float HableW = 9.2;

vec3 Tonemap_HablePartial(vec3 x)
{
    return ((x * (HableA * x + HableC * HableB) + HableD * HableE) /
            (x * (HableA * x + HableB) + HableD * HableF)) - HableE / HableF;
}

vec3 Tonemap_Hable(vec3 x)
{
    float exposureBias = 3.0;
    vec3 curr = Tonemap_HablePartial(x * exposureBias);
    vec3 whiteScale = 1.0 / Tonemap_HablePartial(vec3(HableW, HableW, HableW));
    return curr * whiteScale;
}

vec3 ApplyExposureTonemap(vec3 hdrColor)
{
    float averageLuminance = texture2D(ExposureTexture, vec2(0.5, 0.5)).r;
    float exposure = clamp(KeyValue / max(averageLuminance, 0.0001), MinExposure, MaxExposure);
    vec3 exposed = hdrColor * exposure;
    vec3 mapped = Tonemap_Hable(exposed);
    return clamp(mapped, 0.0, 1.0);
}

void main()
{
    vec4 color = texture2D(ScreenTexture, uv);

    if (UnderwaterAmount > 0.001)
    {
        vec2 distortOffset = UnderwaterDistortion(uv, UnderwaterAmount);
        vec2 sampleUV = clamp(uv + distortOffset, 0.001, 0.999);

        vec3 baseColor = texture2D(ScreenTexture, sampleUV).rgb;
        float sceneDepth = texture2D(DepthTexture, sampleUV).r;

        vec3 transmittance = exp(-sceneDepth * AbsorptionCoeff);
        vec3 fogged = baseColor * UnderwaterTintColor * transmittance + UnderwaterDeepColor * (1.0 - transmittance);

        color.rgb = mix(color.rgb, fogged, UnderwaterAmount);
    }

    vec3 bloomColor = texture2D(BloomTexture, uv).rgb;
    color.rgb += bloomColor * BloomIntensity;

    color.rgb = ApplyExposureTonemap(color.rgb);
    color = applyVignette(color, uv, UnderwaterAmount);

    gl_FragColor = color;
}