Shader "Hidden/Fluid/BloomPrefilter"
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
            CGPROGRAM
            #pragma vertex vert_fluid
            #pragma fragment frag
            #include "FluidCommon.cginc"

            sampler2D uTexture;
            float3 uCurve;
            float uThreshold;
            float uExposure;

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                // Same exposure the display applies, so the threshold is measured
                // against the brightness that actually reaches the screen.
                float3 c = tex2D(uTexture, i.vUv).rgb * uExposure;
                float br = max(c.r, max(c.g, c.b));
                float rq = clamp(br - uCurve.x, 0.0, uCurve.y);
                rq = uCurve.z * rq * rq;
                c *= max(rq, br - uThreshold) / max(br, 0.0001);
                return fixed4(c, 0.0);
            }
            ENDCG
        }
    }
}
