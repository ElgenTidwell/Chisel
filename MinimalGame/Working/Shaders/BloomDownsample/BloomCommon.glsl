uniform sampler2D SourceTexture;
uniform vec2 texelSize;

uniform sampler2D ExposureTexture;
uniform float threshold;
uniform float softKnee;
uniform float KeyValue;
uniform float MinExposure;
uniform float MaxExposure;

float GetExposure()
{
    float averageLuminance = texture2D(ExposureTexture, vec2(0.5, 0.5)).r;
    return clamp(KeyValue / max(averageLuminance, 0.0001), MinExposure, MaxExposure);
}

vec3 ApplyThreshold(vec3 c)
{
    float exposure = GetExposure();
    vec3 exposedColor = c * exposure;

    float brightness = max(exposedColor.r, max(exposedColor.g, exposedColor.b));
    float knee = threshold * softKnee + 0.0001;
    float soft = brightness - threshold + knee;
    soft = clamp(soft, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee);
    float contribution = max(soft, brightness - threshold);
    contribution /= max(brightness, 0.0001);

    return c * contribution;
}

vec3 SampleBox13(vec2 uv, bool applyThreshold)
{
    vec3 a = texture2D(SourceTexture, uv + texelSize * vec2(-1.0, -1.0)).rgb;
    vec3 b = texture2D(SourceTexture, uv + texelSize * vec2(0.0, -1.0)).rgb;
    vec3 c = texture2D(SourceTexture, uv + texelSize * vec2(1.0, -1.0)).rgb;
    vec3 d = texture2D(SourceTexture, uv + texelSize * vec2(-0.5, -0.5)).rgb;
    vec3 e = texture2D(SourceTexture, uv + texelSize * vec2(0.5, -0.5)).rgb;
    vec3 f = texture2D(SourceTexture, uv + texelSize * vec2(-1.0, 0.0)).rgb;
    vec3 g = texture2D(SourceTexture, uv).rgb;
    vec3 h = texture2D(SourceTexture, uv + texelSize * vec2(1.0, 0.0)).rgb;
    vec3 i = texture2D(SourceTexture, uv + texelSize * vec2(-0.5, 0.5)).rgb;
    vec3 j = texture2D(SourceTexture, uv + texelSize * vec2(0.5, 0.5)).rgb;
    vec3 k = texture2D(SourceTexture, uv + texelSize * vec2(-1.0, 1.0)).rgb;
    vec3 l = texture2D(SourceTexture, uv + texelSize * vec2(0.0, 1.0)).rgb;
    vec3 m = texture2D(SourceTexture, uv + texelSize * vec2(1.0, 1.0)).rgb;

    if (applyThreshold)
    {
        a = ApplyThreshold(a); b = ApplyThreshold(b); c = ApplyThreshold(c);
        d = ApplyThreshold(d); e = ApplyThreshold(e); f = ApplyThreshold(f);
        g = ApplyThreshold(g); h = ApplyThreshold(h); i = ApplyThreshold(i);
        j = ApplyThreshold(j); k = ApplyThreshold(k); l = ApplyThreshold(l);
        m = ApplyThreshold(m);
    }

    vec3 result = (d + e + i + j) * 0.125;
    result += (a + b + g + f) * 0.03125;
    result += (b + c + h + g) * 0.03125;
    result += (f + g + l + k) * 0.03125;
    result += (g + h + m + l) * 0.03125;
    return result;
}