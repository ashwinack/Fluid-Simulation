Shader "Hidden/Fluid/SunraysMask"
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

            // Alpha becomes an occlusion mask: bright dye occludes, empty space
            // lets light through, which is what the radial march then smears.
            fixed4 frag (v2f_fluid i) : SV_Target
            {
                float4 c = tex2D(uTexture, i.vUv);
                float br = max(c.r, max(c.g, c.b));
                c.a = 1.0 - min(max(br * 20.0, 0.0), 0.8);
                return c;
            }
            ENDCG
        }
    }
}
