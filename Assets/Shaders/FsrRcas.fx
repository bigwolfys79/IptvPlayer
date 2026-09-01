// Рендер-апскейл, проход 2 (FSR 1.0): FidelityFX Super Resolution 1.0 RCAS
// (Robust Contrast-Adaptive Sharpening). Порт скалярной версии из
// ffx_fsr1.h (AMD, MIT; см. tools/fsr/ffx_fsr1.h). Работает в разрешении
// вывода, 3x3 окрестность, лимитеры не дают «звона» на контрастных краях.
//
// Компиляция — как Upscale.fx:
//   fxc FsrRcas.fx /T ps_4_0 /D D2D_FULL_SHADER /D D2D_ENTRY=main /E main
//       /Fo:FsrRcas.cso /I <SDK>\Include\<ver>\um
//
// This file contains a port of source code from the AMD FidelityFX Super
// Resolution 1.0 project. Copyright (c) 2021 Advanced Micro Devices, Inc.
// Licensed under the MIT license (see tools/fsr/LICENSE-FSR.txt).

#define D2D_INPUT_COUNT 1
#define D2D_INPUT0_COMPLEX
#include "d2d1effecthelpers.hlsli"

// Разрешение входа прохода (= размер вписанного кадра), пиксели.
float2 dstSize;
// Сила резкости 0..1 (маппинг как в сэмпле AMD: 8.0 стопов при 0 —
// минимальная резкость, 0 стопов при 1 — максимум).
float sharpness;

// Предел лимитеров (FSR_RCAS_LIMIT по умолчанию).
#define RCAS_LIMIT 0.25

float Luma(float3 c)
{
    return c.b * 0.5 + (c.r * 0.5 + c.g);
}

D2D_PS_ENTRY(main)
{
    float2 uv = D2DGetInputCoordinate(0).xy;
    float2 texel = 1.0 / dstSize;

    //    b
    //  d e f
    //    h
    float3 b = D2DSampleInput(0, uv + float2(0, -texel.y)).rgb;
    float3 d = D2DSampleInput(0, uv + float2(-texel.x, 0)).rgb;
    float3 e = D2DSampleInput(0, uv).rgb;
    float3 f = D2DSampleInput(0, uv + float2(texel.x, 0)).rgb;
    float3 h = D2DSampleInput(0, uv + float2(0, texel.y)).rgb;

    float bL = Luma(b), dL = Luma(d), eL = Luma(e), fL = Luma(f), hL = Luma(h);

    // Мин/макс кольца.
    float3 mn4 = min(min(b, min(d, f)), h);
    float3 mx4 = max(max(b, max(d, f)), h);

    // Лимитеры (нужен точный rcp).
    float2 peakC = float2(1.0, -4.0);
    float3 hitMin = min(mn4, e) / (4.0 * mx4);
    float3 hitMax = (peakC.x - max(mx4, e)) / (4.0 * mn4 + peakC.y);
    float3 lobe3 = max(-hitMin, hitMax);
    float lobe = max(-RCAS_LIMIT, min(max(max(lobe3.r, lobe3.g), lobe3.b), 0.0));

    // FsrRcasCon: константа = exp2(-стопов резкости).
    float att = exp2(-lerp(8.0, 0.0, saturate(sharpness)));
    lobe *= att;

    // Разрешение: точный rcp, чтобы не менять тональность.
    float rcpL = 1.0 / (4.0 * lobe + 1.0);
    float3 pix = (lobe * (b + d + h + f) + e) * rcpL;
    return float4(saturate(pix), 1.0);
}
