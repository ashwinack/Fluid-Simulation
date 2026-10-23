Shader "Hidden/Fluid/GradientSubtract"
{
    Properties
    {
        uPressure ("Pressure", 2D) = "black" {}
        uVelocity ("Velocity", 2D) = "black" {}
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
            sampler2D uVelocity;

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                float L = tex2D(uPressure, i.vL).x;
                float R = tex2D(uPressure, i.vR).x;
                float T = tex2D(uPressure, i.vT).x;
                float B = tex2D(uPressure, i.vB).x;
                float2 velocity = tex2D(uVelocity, i.vUv).xy;
                velocity -= float2(R - L, T - B);
                return fixed4(velocity, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
