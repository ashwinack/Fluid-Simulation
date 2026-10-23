Shader "Hidden/Fluid/Splat"
{
    Properties
    {
        uTarget ("Target", 2D) = "black" {}
        aspectRatio ("Aspect Ratio", Float) = 1
        splatColor ("Splat Color", Vector) = (0,0,0,1)
        splatPoint ("Splat Point", Vector) = (0.5,0.5,0,0)
        radius ("Radius", Float) = 0.25
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_fluid
            #pragma fragment frag
            #include "FluidCommon.cginc"

            sampler2D uTarget;
            float aspectRatio;
            float3 splatColor;
            float2 splatPoint;
            float radius;

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                float2 p = i.vUv - splatPoint.xy;
                p.x *= aspectRatio;
                float3 splat = exp(-dot(p, p) / radius) * splatColor;
                float3 base = tex2D(uTarget, i.vUv).xyz;
                return fixed4(base + splat, 1.0);
            }
            ENDCG
        }
    }
}
