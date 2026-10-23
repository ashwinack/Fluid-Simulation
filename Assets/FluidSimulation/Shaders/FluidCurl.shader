Shader "Hidden/Fluid/Curl"
{
    Properties
    {
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

            sampler2D uVelocity;

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                float L = tex2D(uVelocity, i.vL).y;
                float R = tex2D(uVelocity, i.vR).y;
                float T = tex2D(uVelocity, i.vT).x;
                float B = tex2D(uVelocity, i.vB).x;
                float vorticity = R - L - T + B;
                return fixed4(0.5 * vorticity, 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
