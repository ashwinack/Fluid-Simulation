Shader "Hidden/Fluid/Clear"
{
    Properties
    {
        uTexture ("Texture", 2D) = "black" {}
        value ("Value", Float) = 1
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

            sampler2D uTexture;
            float value;

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                return value * tex2D(uTexture, i.vUv);
            }
            ENDCG
        }
    }
}
