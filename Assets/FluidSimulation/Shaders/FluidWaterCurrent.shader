Shader "Hidden/Fluid/WaterCurrent"
{
    // The Water style's own idle life, applied every simulation step so the water
    // keeps moving with nobody touching it.
    //   Pass 0: steers the velocity towards a slow, swirling current (the curl of
    //           drifting noise, so it has no sources or sinks).
    //   Pass 1: breathes milky wisps into the dye in drifting bands; the current
    //           then stretches them into streaks, and they light up the glitter.
    // Hands on the glass still push the water: the pull towards the current is
    // gentle, so a stroke's swirl lives on and slowly rejoins the flow.
    Properties
    {
        uTarget ("Target", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    #include "FluidWaterNoise.cginc"
    sampler2D uTarget;
    float uAspect;        // wall width / height
    float uTime;          // seconds
    float uDt;
    float uSimHeight;     // velocity is in sim texels per second
    float uCurrent;       // 0..2
    float uMist;          // 0..2

    float Stream(float2 p, float t)
    {
        return WaterFbm(p * 0.9 + float2(t * 0.05, -t * 0.03), 3)
             + 0.35 * WaterFbm(p * 2.1 + float2(-t * 0.04, t * 0.06) + 9.3, 2);
    }

    // The idle current at p (wall heights), in wall heights per second.
    float2 Current(float2 p, float t)
    {
        const float e = 0.03;
        float dx = Stream(p + float2(e, 0), t) - Stream(p - float2(e, 0), t);
        float dy = Stream(p + float2(0, e), t) - Stream(p - float2(0, e), t);
        float2 swirl = float2(dy, -dx) / (2.0 * e);
        // A slow drift that wanders in direction, so the whole body of water
        // sways one way, then another.
        float2 drift = float2(cos(t * 0.031), 0.45 * sin(t * 0.047 + 1.3)) * 0.035;
        return swirl * 0.11 + drift;
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag (v2f_img i) : SV_Target
            {
                float4 v = tex2D(uTarget, i.uv);
                float2 p = i.uv * float2(uAspect, 1.0);
                float2 target = Current(p, uTime) * uSimHeight * uCurrent;
                v.xy += (target - v.xy) * saturate(0.6 * uDt);
                return v;
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag (v2f_img i) : SV_Target
            {
                float4 d = tex2D(uTarget, i.uv);
                float2 p = i.uv * float2(uAspect, 1.0) * float2(1.0, 1.8);   // wide, flat bands
                float t = uTime;
                float2 warp = float2(WaterFbm(p * 0.7 + t * 0.02, 2), WaterFbm(p * 0.7 - t * 0.017 + 4.1, 2));
                float m = WaterFbm(p * 1.3 + warp * 1.6 + float2(t * 0.035, t * 0.012), 4);
                float src = smoothstep(0.58, 0.85, m) * uMist;
                d.rgb += float3(0.55, 0.75, 1.0) * src * 0.16 * uDt;
                return d;
            }
            ENDCG
        }
    }
}
