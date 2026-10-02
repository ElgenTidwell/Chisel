uniform sampler2D SourceTexture;
uniform vec2 texelSize;

varying vec2 uv;

void main()
{
    vec2 halfTexel = texelSize * 0.5;

    float sum = 0.0;
    sum += texture2D(SourceTexture, uv + vec2(-halfTexel.x, -halfTexel.y)).r;
    sum += texture2D(SourceTexture, uv + vec2(halfTexel.x, -halfTexel.y)).r;
    sum += texture2D(SourceTexture, uv + vec2(-halfTexel.x, halfTexel.y)).r;
    sum += texture2D(SourceTexture, uv + vec2(halfTexel.x, halfTexel.y)).r;

    float average = sum * 0.25;
    gl_FragColor = vec4(average, average, average, 1.0);
}