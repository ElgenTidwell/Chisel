#include <PosFixup.glsl>

attribute vec4 in_Position0;
attribute vec2 in_TextureCoordinate0;

varying vec2 uv;

void main()
{
    gl_Position = in_Position0;
    uv = in_TextureCoordinate0;

    ApplyPosFixup();
}