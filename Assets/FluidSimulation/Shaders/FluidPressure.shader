Shader "Hidden/Fluid/Pressure"
{
    Properties
    {
        uPressure ("Pressure", 2D) = "black" {}
        uDivergence ("Divergence", 2D) = "black" {}
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

            sampler2D uPressure;
            sampler2D uDivergence;

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                float L = tex2D(uPressure, i.vL).x;
                float R = tex2D(uPressure, i.vR).x;
                float T = tex2D(uPressure, i.vT).x;
                float B = tex2D(uPressure, i.vB).x;
                float divergence = tex2D(uDivergence, i.vUv).x;
                float pressure = (L + R + B + T - divergence) * 0.25;
                return fixed4(pressure, 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
