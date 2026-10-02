uniform sampler2D CurrentLuminanceTexture;
uniform sampler2D PreviousExposureTexture;

uniform float deltaTime;
uniform float adaptationSpeedUp;
uniform float adaptationSpeedDown;

varying vec2 uv;

void main()
{
    float previousLuminance = texture2D(PreviousExposureTexture, vec2(0.5, 0.5)).r;
    float currentLogLuminance = texture2D(CurrentLuminanceTexture, vec2(0.5, 0.5)).r;
    float currentLuminance = exp(currentLogLuminance);
    float speed = currentLuminance > previousLuminance ? adaptationSpeedUp : adaptationSpeedDown;
    float adapted = previousLuminance + (currentLuminance - previousLuminance) * (1.0 - exp(-deltaTime * speed));
    gl_FragColor = vec4(adapted, adapted, adapted, 1.0);
}