// Рендер-апскейл, проход 1 (FSR 1.0): FidelityFX Super Resolution 1.0 EASU
// (Edge-Adaptive Spatial Upsampling). Порт скалярной версии из ffx_fsr1.h
// (AMD, MIT; см. tools/fsr/ffx_fsr1.h). Вместо gather4 — 12 явных выборок
// текселей с точечным сэмплером (интерполяция источника задаётся в C#).
//
// Компиляция (как Upscale.fx):
//   fxc FsrEasu.fx /T ps_4_0 /D D2D_FULL_SHADER /D D2D_ENTRY=main /E main
//       /Fo:FsrEasu.cso /I <SDK>\Include\<ver>\um
//
// This file contains a port of source code from the AMD FidelityFX Super
// Resolution 1.0 project. Copyright (c) 2021 Advanced Micro Devices, Inc.
// Licensed under the MIT license (see tools/fsr/LICENSE-FSR.txt).

#define D2D_INPUT_COUNT 1
#define D2D_INPUT0_COMPLEX
#include "d2d1effecthelpers.hlsli"

// Разрешение источника (кадр потока) и вывода (вписанный кадр), пиксели.
float2 srcSize;
float2 dstSize;

// Точечная выборка текселя по целым координатам (координаты клампятся).
float3 LoadTexel(int2 p)
{
    int2 maxXY = (int2)srcSize - 1;
    p = clamp(p, int2(0, 0), maxXY);
    float2 uv = (float2(p) + 0.5) / srcSize;
    return D2DSampleInput(0, uv).rgb;
}

float Luma(float3 c)
{
    // Luma*2 как в FSR (2 FMA).
    return c.b * 0.5 + (c.r * 0.5 + c.g);
}

// Накопление направления и длины края (FsrEasuSetF, скалярная версия).
// biS/biT/biU/biV — выбор квадранта билинейного веса.
void EasuSetF(inout float2 dir, inout float1 len, float2 pp,
              bool biS, bool biT, bool biU, bool biV,
              float lA, float lB, float lC, float lD, float lE)
{
    float w = 0.0;
    if (biS) w = (1.0 - pp.x) * (1.0 - pp.y);
    if (biT) w =        pp.x  * (1.0 - pp.y);
    if (biU) w = (1.0 - pp.x) *        pp.y ;
    if (biV) w =        pp.x  *        pp.y ;

    float dc = lD - lC;
    float cb = lC - lB;
    float lenX = 1.0 / max(abs(dc), abs(cb));
    float dirX = lD - lB;
    dir.x += dirX * w;
    lenX = saturate(abs(dirX) * lenX);
    lenX *= lenX;
    len += lenX * w;

    float ec = lE - lC;
    float ca = lC - lA;
    float lenY = 1.0 / max(abs(ec), abs(ca));
    float dirY = lE - lA;
    dir.y += dirY * w;
    lenY = saturate(abs(dirY) * lenY);
    lenY *= lenY;
    len += lenY * w;
}

// Один вклад 12-точечного ядра (FsrEasuTapF, скалярная версия).
void EasuTapF(inout float3 aC, inout float1 aW, float2 off,
              float2 dir, float2 len2, float lob, float clp, float3 c)
{
    // Поворот смещения на направление градиента.
    float2 v;
    v.x = (off.x *  dir.x) + (off.y * dir.y);
    v.y = (off.x * -dir.y) + (off.y * dir.x);
    v *= len2;
    float d2 = v.x * v.x + v.y * v.y;
    d2 = min(d2, clp);
    // Приближение lanczos2 без sin/rcp/sqrt:
    //  (25/16 * (2/5 * x^2 - 1)^2 - (25/16 - 1)) * (1/4 * x^2 - 1)^2
    float wB = 2.0 / 5.0 * d2 - 1.0;
    float wA = lob * d2 - 1.0;
    wB *= wB;
    wA *= wA;
    wB = 25.0 / 16.0 * wB - (25.0 / 16.0 - 1.0);
    float w = wB * wA;
    aC += c * w;
    aW += w;
}

D2D_PS_ENTRY(main)
{
    // uv покрывает 0..1 по ВЫВОДУ (OneToOne + Transform2D в C#).
    float2 uv = D2DGetInputCoordinate(0).xy;
    float2 ip = uv * dstSize;

    // Позиция в текселях источника: pp = ip*(src/dst) + 0.5*(src/dst) - 0.5
    // (эквивалент con0 из FsrEasuCon без упаковки в биты).
    float2 scale = srcSize / dstSize;
    float2 pp = ip * scale + (0.5 * scale - 0.5);
    float2 fp = floor(pp);
    pp -= fp;
    int2 fc = (int2)fp;

    // 12 текселей вокруг угла F:
    //    b c
    //  e f g h
    //  i j k l
    //    n o
    float3 b = LoadTexel(fc + int2( 0, -1));
    float3 c = LoadTexel(fc + int2( 1, -1));
    float3 e = LoadTexel(fc + int2(-1,  0));
    float3 f = LoadTexel(fc + int2( 0,  0));
    float3 g = LoadTexel(fc + int2( 1,  0));
    float3 h = LoadTexel(fc + int2( 2,  0));
    float3 i = LoadTexel(fc + int2(-1,  1));
    float3 j = LoadTexel(fc + int2( 0,  1));
    float3 k = LoadTexel(fc + int2( 1,  1));
    float3 l = LoadTexel(fc + int2( 2,  1));
    float3 n = LoadTexel(fc + int2( 0,  2));
    float3 o = LoadTexel(fc + int2( 1,  2));

    float bL = Luma(b), cL = Luma(c), eL = Luma(e), fL = Luma(f);
    float gL = Luma(g), hL = Luma(h), iL = Luma(i), jL = Luma(j);
    float kL = Luma(k), lL = Luma(l), nL = Luma(n), oL = Luma(o);

    float2 dir = float2(0.0, 0.0);
    float len = 0.0;
    EasuSetF(dir, len, pp, true,  false, false, false, bL, eL, fL, gL, jL);
    EasuSetF(dir, len, pp, false, true,  false, false, cL, fL, gL, hL, kL);
    EasuSetF(dir, len, pp, false, false, true,  false, fL, iL, jL, kL, nL);
    EasuSetF(dir, len, pp, false, false, false, true,  gL, jL, kL, lL, oL);

    // Нормализация с очисткой близких к нулю направлений.
    float2 dir2 = dir * dir;
    float dirR = dir2.x + dir2.y;
    bool zro = dirR < (1.0 / 32768.0);
    dirR = zro ? 1.0 : 1.0 / sqrt(max(dirR, 1e-20));
    dir.x = zro ? 1.0 : dir.x;
    dir *= dirR;

    // len: {0..2} -> {0..1}, форма — квадрат.
    len *= 0.5;
    len *= len;

    // Растяжение ядра {1.0 верш, до sqrt(2) на диагонали}.
    float stretch = (dir.x * dir.x + dir.y * dir.y)
        * (1.0 / max(abs(dir.x), abs(dir.y)));
    float2 len2 = float2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
    // Отрицательный лепесток: 0.5 на плоских краях до ~0.21 на сильных.
    float lob = 0.5 + (0.25 - 0.04 - 0.5) * len;
    float clp = 1.0 / lob;

    // Аккумуляция + min/max четырёх ближайших (деринг).
    float3 min4 = min(min(f, min(g, j)), k);
    float3 max4 = max(max(f, max(g, j)), k);
    float3 aC = float3(0.0, 0.0, 0.0);
    float aW = 0.0;
    EasuTapF(aC, aW, float2( 0.0, -1.0) - pp, dir, len2, lob, clp, b);
    EasuTapF(aC, aW, float2( 1.0, -1.0) - pp, dir, len2, lob, clp, c);
    EasuTapF(aC, aW, float2(-1.0,  1.0) - pp, dir, len2, lob, clp, i);
    EasuTapF(aC, aW, float2( 0.0,  1.0) - pp, dir, len2, lob, clp, j);
    EasuTapF(aC, aW, float2( 0.0,  0.0) - pp, dir, len2, lob, clp, f);
    EasuTapF(aC, aW, float2(-1.0,  0.0) - pp, dir, len2, lob, clp, e);
    EasuTapF(aC, aW, float2( 1.0,  1.0) - pp, dir, len2, lob, clp, k);
    EasuTapF(aC, aW, float2( 2.0,  1.0) - pp, dir, len2, lob, clp, l);
    EasuTapF(aC, aW, float2( 2.0,  0.0) - pp, dir, len2, lob, clp, h);
    EasuTapF(aC, aW, float2( 1.0,  0.0) - pp, dir, len2, lob, clp, g);
    EasuTapF(aC, aW, float2( 1.0,  2.0) - pp, dir, len2, lob, clp, o);
    EasuTapF(aC, aW, float2( 0.0,  2.0) - pp, dir, len2, lob, clp, n);

    // Нормализация и деринг по min4/max4.
    float3 pix = min(max4, max(min4, aC * (1.0 / max(aW, 1e-20))));
    return float4(saturate(pix), 1.0);
}
