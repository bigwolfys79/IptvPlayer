// Рендер-апскейл, проход 2: адаптивная резкость (упрощённый FidelityFX CAS).
// Усиливает контраст на краях сильнее там, где картинка «плоская», не
// перешарпляя детализированные области. Работает уже в разрешении окна.
//
// Компиляция — как Upscale.fx (см. README.md).

#define D2D_INPUT_COUNT 1
#define D2D_INPUT0_COMPLEX
#include "d2d1effecthelpers.hlsli"

// Разрешение входа прохода 2 (= размер окна), пиксели.
float2 dstSize;
// Сила резкости 0..1.
float sharpening;

D2D_PS_ENTRY(main)
{
    float2 uv = D2DGetInputCoordinate(0).xy;
    float2 texel = 1.0 / dstSize;

    float3 c = D2DSampleInput(0, uv).rgb;
    float3 n1 = D2DSampleInput(0, uv + float2(texel.x, 0)).rgb;
    float3 n2 = D2DSampleInput(0, uv - float2(texel.x, 0)).rgb;
    float3 n3 = D2DSampleInput(0, uv + float2(0, texel.y)).rgb;
    float3 n4 = D2DSampleInput(0, uv - float2(0, texel.y)).rgb;
    float3 blur = (c + n1 + n2 + n3 + n4) * 0.2;

    float3 mn = min(c, min(min(n1, n2), min(n3, n4)));
    float3 mx = max(c, max(max(n1, n2), max(n3, n4)));

    // Чем «плоское» окружение (мал разброс), тем сильнее усиление.
    float3 amp = sqrt(saturate(min(mn, 2.0 - mx) / max(mx, 0.0001)));
    float3 detail = c - blur;

    return float4(saturate(c + detail * amp * sharpening * 2.0), 1.0);
}
