#ifndef FLUID_WATER_NOISE_INCLUDED
#define FLUID_WATER_NOISE_INCLUDED

// Smooth value noise and fractal sums for the Water style's idle motion: its
// current, its drifting wisps and its shifting shades of blue.

float WaterHash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float WaterNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = WaterHash(i);
    float b = WaterHash(i + float2(1, 0));
    float c = WaterHash(i + float2(0, 1));
    float d = WaterHash(i + float2(1, 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// 0..1, rotated between octaves so no grid shows through.
float WaterFbm(float2 p, int octaves)
{
    const float2x2 rot = float2x2(0.8, -0.6, 0.6, 0.8);
    float sum = 0.0, amp = 0.5, norm = 0.0;
    for (int k = 0; k < 5; k++)
    {
        if (k < octaves)
        {
            sum += amp * WaterNoise(p);
            norm += amp;
            p = mul(rot, p) * 2.03 + 17.1;
            amp *= 0.5;
        }
    }
    return sum / norm;
}

#endif
