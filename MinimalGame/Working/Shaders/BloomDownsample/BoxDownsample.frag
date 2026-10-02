#include "BloomCommon.glsl"

varying vec2 uv;

void main()
{
    gl_FragColor = vec4(SampleBox13(uv, false), 1.0);
}