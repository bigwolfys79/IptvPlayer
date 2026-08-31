// Рендер-апскейл, проход 1: Catmull-Rom бикубический апскейл кадра
// потока под размер окна (фаза 2, FrameServerRenderer).
// Компиляция (см. Assets/Shaders/README.md):
//   fxc Upscale.fx /T ps_4_0 /D D2D_FULL_SHADER /D D2D_ENTRY=main /E main
//       /Fo:Upscale.cso /I <SDK>\Include\<ver>\um

#define D2D_INPUT_COUNT 1
#define D2D_INPUT0_COMPLEX
#include "d2d1effecthelpers.hlsli"

// Разрешение источника (кадр потока) и вывода (окно), пиксели.
float2 srcSize;
float2 dstSize;

float4 CubicWeights(float x)
{
    float x2 = x * x;
    float x3 = x2 * x;
    return float4(
        (-x3 + 2.0 * x2 - x) * 0.5,
        (3.0 * x3 - 5.0 * x2 + 2.0) * 0.5,
        (-3.0 * x3 + 4.0 * x2 + x) * 0.5,
        (x3 - x2) * 0.5);
}

D2D_PS_ENTRY(main)
{
    float2 uv = D2DGetInputCoordinate(0).xy;
    float2 texel = 1.0 / srcSize;

    // Позиция в текселях источника (центры текселей = +0.5).
    float2 p = uv * srcSize - 0.5;
    float2 i = floor(p);
    float2 f = p - i;

    float4 wx = CubicWeights(f.x);
    float4 wy = CubicWeights(f.y);

    float4 c = 0;
    [unroll]
    for (int y = 0; y < 4; y++)
    {
        [unroll]
        for (int x = 0; x < 4; x++)
        {
            float2 suv = (i + float2(x, y) - 1.0 + 0.5) * texel;
            c += D2DSampleInput(0, suv) * wx[x] * wy[y];
        }
    }
    return c;
}
