Shader "Hidden/Fluid/Sunrays"
{
    Properties
    {
        uTexture ("Mask", 2D) = "white" {}
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
            float uWeight;

            #define SUNRAY_ITERATIONS 16

            fixed4 frag (v2f_fluid i) : SV_Target
            {
                const float density = 0.3;
                const float decay = 0.95;
                const float exposure = 0.7;

                float2 coord = i.vUv;
                float2 dir = i.vUv - 0.5;
                dir *= 1.0 / float(SUNRAY_ITERATIONS) * density;

                float illuminationDecay = 1.0;
                float acc = tex2D(uTexture, i.vUv).a;

                // Normalising by the same weights the samples carry keeps the result
                // in [0,1]: 1 where nothing occludes, lower in the shadow of dense dye.
                // The original leaves this un-normalised, where it acts as a ~8x gain
                // that only stays in range because its dye never exceeds ~0.15. Ours
                // accumulates brighter than that, so an un-normalised gain blows the
                // whole frame to white.
                float norm = 1.0;

                [unroll]
                for (int s = 0; s < SUNRAY_ITERATIONS; s++)
                {
                    coord -= dir;
                    float col = tex2D(uTexture, coord).a;
                    acc += col * illuminationDecay * uWeight;
                    norm += illuminationDecay * uWeight;
                    illuminationDecay *= decay;
                }

                float shade = acc / max(norm, 0.0001);
                return fixed4(shade, 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
