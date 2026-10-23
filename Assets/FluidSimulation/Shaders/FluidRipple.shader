Shader "Hidden/Fluid/Ripple"
{
    // The Water style's surface: a classic two-buffer wave equation.
    //   R = height now, G = height one step ago.
    // Pass 0 advances the waves one step; pass 1 drops disturbances at the fingers.
    Properties
    {
        uTexture ("Ripple", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D uTexture;
    float2 uTexel;
    float uDamping;          // how much of a wave survives each step
    float4 uDrops[16];       // xy = wall UV, z = strength
    float uDropCount;
    float uDropRadius;       // in wall heights
    float uAspect;           // wall width / height
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
                float2 s = tex2D(uTexture, i.uv).rg;
                float l = tex2D(uTexture, i.uv - float2(uTexel.x, 0)).r;
                float r = tex2D(uTexture, i.uv + float2(uTexel.x, 0)).r;
                float t = tex2D(uTexture, i.uv + float2(0, uTexel.y)).r;
                float b = tex2D(uTexture, i.uv - float2(0, uTexel.y)).r;
                float next = ((l + r + t + b) * 0.5 - s.g) * uDamping;
                return float4(next, s.r, 0, 0);
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            float4 frag (v2f_img i) : SV_Target
            {
                float2 s = tex2D(uTexture, i.uv).rg;
                float add = 0;
                [unroll] for (int k = 0; k < 16; k++)
                {
                    if (k < uDropCount)
                    {
                        float2 d = (i.uv - uDrops[k].xy) * float2(uAspect, 1.0);
                        add += uDrops[k].z * exp(-dot(d, d) / (uDropRadius * uDropRadius));
                    }
                }
                return float4(s.r + add, s.g, 0, 0);
            }
            ENDCG
        }
    }
}
