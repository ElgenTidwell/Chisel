uniform sampler2D SourceTexture;
uniform vec2 texelSize;

varying vec2 uv;

void main()
{
    float sum = 0.0;
    for (int y = 0; y < 4; y++)
    {
        for (int x = 0; x < 4; x++)
        {
            vec2 offset = texelSize * (vec2(float(x), float(y)) - 1.5);
            sum += texture2D(SourceTexture, uv + offset).r;
        }
    }
    float average = sum / 16.0;
    gl_FragColor = vec4(average, average, average, 1.0);
}