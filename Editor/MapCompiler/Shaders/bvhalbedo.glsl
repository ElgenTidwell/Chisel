vec3 GetTriAlbedo(uint triIdx)
{
    return unpackUnorm4x8(surfaces[triIdx].albedo).rgb;
}