layout(std430, binding = 2) buffer LightmapBuffer { vec4 lm[]; };

#define LM_STRIDE (lm.length() / 3)
#define LM_B1(i) lm[(i)]
#define LM_B2(i) lm[LM_STRIDE + (i)]
#define LM_B3(i) lm[2 * LM_STRIDE + (i)]