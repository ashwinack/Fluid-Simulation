Shader "Hidden/Fluid/Vorticity"
{
    Properties
    {
        uVelocity ("Velocity", 2D) = "black" {}
        uCurl ("Curl", 2D) = "black" {}
        curl ("Curl Amount", Float) = 30
        dt ("Delta Time", Float) = 0.016
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
            sampler2D uCurl;
            float curl;
            float dt;

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                float L = tex2D(uCurl, i.vL).x;
                float R = tex2D(uCurl, i.vR).x;
                float T = tex2D(uCurl, i.vT).x;
                float B = tex2D(uCurl, i.vB).x;
                float C = tex2D(uCurl, i.vUv).x;

                float2 force = 0.5 * float2(abs(T) - abs(B), abs(R) - abs(L));
                force /= length(force) + 0.0001;
                force *= curl * C;
                force.y *= -1.0;

                float2 vel = tex2D(uVelocity, i.vUv).xy;
                return fixed4(vel + force * dt, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
