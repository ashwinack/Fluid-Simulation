Shader "Hidden/Fluid/Advection"
{
    Properties
    {
        uVelocity ("Velocity", 2D) = "black" {}
        uSource ("Source", 2D) = "black" {}
        dt ("Delta Time", Float) = 0.016
        dissipation ("Dissipation", Float) = 0
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
            sampler2D uSource;
            float dt;
            float dissipation;

            // Sim and source can be different resolutions (dye vs velocity),
            // so the source is always sampled with its own filtered lookup;
            // RenderTextures use hardware bilinear filtering, matching the
            // non-MANUAL_FILTERING branch of the original WebGL shader.
            fixed4 frag (v2f_fluid i) : SV_Target
            {
                float2 coord = i.vUv - dt * tex2D(uVelocity, i.vUv).xy * _TexelSize;
                fixed4 result = tex2D(uSource, coord);
                float decay = 1.0 + dissipation * dt;
                // Only the RGB (or XY, for velocity) channels dissipate. Alpha is kept
                // pinned at 1 so the dye's UI RawImage always stays fully opaque and
                // never fades to reveal whatever is behind the canvas.
                return fixed4(result.rgb / decay, 1.0);
            }
            ENDCG
        }
    }
}
