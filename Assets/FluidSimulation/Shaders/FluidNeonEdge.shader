Shader "Hidden/Fluid/NeonEdge"
{
    // Neon's own glow source: the flow's edges in their colour, drawn into a small
    // texture that is then blurred for the halo round every line. Independent of
    // Classic's bloom.
    Properties
    {
        uTexture ("Dye", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D uTexture;
            float2 uDyeTexel;
            float uNeonWidth;
            float uExposure;

            float Luma(float3 c) { return max(c.r, max(c.g, c.b)); }

            float4 frag (v2f_img i) : SV_Target
            {
                // The same contour lines as the display pass (drawn here small,
                // then blurred into the glow).
                float3 c = tex2D(uTexture, i.uv).rgb * uExposure;
                float v = Luma(c);
                float f = frac(v / 0.6);
                float dist = min(f, 1.0 - f) * 0.6 / max(fwidth(v), 1e-4);
                float tube = saturate(1.0 - dist / max(uNeonWidth * 0.5, 0.75)) * step(0.06, v);
                float3 hue = c / max(v, 1e-4);
                return float4(hue * tube, 1.0);
            }
            ENDCG
        }
    }
}
