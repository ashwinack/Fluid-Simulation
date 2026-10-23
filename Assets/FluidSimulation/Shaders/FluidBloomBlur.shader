Shader "Hidden/Fluid/BloomBlur"
{
    Properties
    {
        uTexture ("Source", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // Pass 0: plain 4-tap box blur, used while downsampling the chain.
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert_fluid
            #pragma fragment frag
            #include "FluidCommon.cginc"

            sampler2D uTexture;

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                float4 sum = tex2D(uTexture, i.vL)
                           + tex2D(uTexture, i.vR)
                           + tex2D(uTexture, i.vT)
                           + tex2D(uTexture, i.vB);
                return sum * 0.25;
            }
            ENDCG
        }

        // Pass 1: same blur, additively blended - used on the way back up the
        // chain so each mip accumulates onto the larger one (gl.blendFunc(ONE, ONE)
        // in the original).
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert_fluid
            #pragma fragment frag
            #include "FluidCommon.cginc"

            sampler2D uTexture;

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                float4 sum = tex2D(uTexture, i.vL)
                           + tex2D(uTexture, i.vR)
                           + tex2D(uTexture, i.vT)
                           + tex2D(uTexture, i.vB);
                return sum * 0.25;
            }
            ENDCG
        }
    }
}
