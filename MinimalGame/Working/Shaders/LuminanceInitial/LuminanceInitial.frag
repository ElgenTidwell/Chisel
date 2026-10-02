uniform sampler2D SceneTexture;

uniform float meteringRadius;
uniform float meteringVerticalBias;
uniform float minMeteredWeight;
uniform float minMeteredLuminance;
uniform float maxMeteredLuminance;

varying vec2 uv;

void main()
{
    vec3 color = texture2D(SceneTexture, uv).rgb;
    float luminance = dot(color, vec3(0.2126, 0.7152, 0.0722));
    luminance = clamp(luminance, minMeteredLuminance, maxMeteredLuminance);

    vec2 offset = uv - vec2(0.5, 0.5);
    offset.y -= meteringVerticalBias;
    float distFromCenter = length(offset) * 2.0;

    float falloff = clamp(1.0 - smoothstep(meteringRadius, 1.42, distFromCenter), 0.0, 1.0);
    float weight = mix(minMeteredWeight, 1.0, falloff);

    luminance = mix(1.0, luminance, weight);
    float logLuminance = log(luminance);
    gl_FragColor = vec4(logLuminance, logLuminance, logLuminance, 1.0);
}