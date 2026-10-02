uniform sampler2D SourceTexture;
uniform vec2 texelSize;
uniform float filterRadius;

varying vec2 uv;

void main()
{
    vec2 offset = texelSize * filterRadius;

    vec3 a = texture2D(SourceTexture, uv + vec2(-offset.x, offset.y)).rgb;
    vec3 b = texture2D(SourceTexture, uv + vec2(0.0, offset.y)).rgb;
    vec3 c = texture2D(SourceTexture, uv + vec2(offset.x, offset.y)).rgb;

    vec3 d = texture2D(SourceTexture, uv + vec2(-offset.x, 0.0)).rgb;
    vec3 e = texture2D(SourceTexture, uv).rgb;
    vec3 f = texture2D(SourceTexture, uv + vec2(offset.x, 0.0)).rgb;

    vec3 g = texture2D(SourceTexture, uv + vec2(-offset.x, -offset.y)).rgb;
    vec3 h = texture2D(SourceTexture, uv + vec2(0.0, -offset.y)).rgb;
    vec3 i = texture2D(SourceTexture, uv + vec2(offset.x, -offset.y)).rgb;

    vec3 result = e * 4.0;
    result += (b + d + f + h) * 2.0;
    result += (a + c + g + i) * 1.0;
    result *= (1.0 / 16.0);

    gl_FragColor = vec4(result, 1.0);
}