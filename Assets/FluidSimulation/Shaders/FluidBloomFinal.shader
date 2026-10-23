Shader "Hidden/Fluid/BloomFinal"
{
    Properties
    {
        uTexture ("Source", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert_fluid
            #pragma fragment frag
            #include "FluidCommon.cginc"

            sampler2D uTexture;
            float uIntensity;

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                float4 sum = tex2D(uTexture, i.vL)
                           + tex2D(uTexture, i.vR)
                           + tex2D(uTexture, i.vT)
                           + tex2D(uTexture, i.vB);
                return sum * 0.25 * uIntensity;
            }
            ENDCG
        }
    }
}
