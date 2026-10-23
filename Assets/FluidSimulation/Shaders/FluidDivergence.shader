Shader "Hidden/Fluid/Divergence"
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
                float L = tex2D(uVelocity, i.vL).x;
                float R = tex2D(uVelocity, i.vR).x;
                float T = tex2D(uVelocity, i.vT).y;
                float B = tex2D(uVelocity, i.vB).y;
                float2 C = tex2D(uVelocity, i.vUv).xy;

                if (i.vL.x < 0.0) { L = -C.x; }
                if (i.vR.x > 1.0) { R = -C.x; }
                if (i.vT.y > 1.0) { T = -C.y; }
                if (i.vB.y < 0.0) { B = -C.y; }

                float div = 0.5 * (R - L + T - B);
                return fixed4(div, 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
