Shader "Hidden/Fluid/Comet"
{
    // One comet per pointer: a tapered ribbon along the path the finger just
    // travelled, plus a big glowing head at the finger. The mesh is built on the
    // CPU every frame with its positions already in clip space (x, y in -1..1),
    // so no matrices are involved.
    //
    // Per vertex: colour.rgb = colour, colour.a = brightness; uv = shape coords;
    // uv2.x = 0 for the tail ribbon, 1 for the head.
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float uCoreWhite;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 kind : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float kind : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                float2 c = v.vertex.xy;
            #if UNITY_UV_STARTS_AT_TOP
                c.y = -c.y;                         // drawing into a render texture
            #endif
                o.pos = float4(c, 0.0, 1.0);
                o.color = v.color;
                o.uv = v.uv;
                o.kind = v.kind.x;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float3 col = i.color.rgb;
                float a = i.color.a;
                if (i.kind > 1.5)
                {
                    // Coma: a wide, soft coloured glow round the head, no core.
                    float d2 = dot(i.uv, i.uv);
                    float fall = saturate(1.0 - d2);
                    return float4(col * exp(-d2 * 3.0) * fall * fall * a, 0.0);
                }
                if (i.kind > 0.5)
                {
                    // Head: uv in -1..1 across the quad. A coloured glow round a
                    // small white-hot centre.
                    float d2 = dot(i.uv, i.uv);
                    // Reaches exactly zero at the quad's inscribed circle, so no
                    // square edge ever shows round the glow.
                    float fall = saturate(1.0 - d2);
                    float glow = exp(-d2 * 4.5) * fall * fall;
                    float core = exp(-d2 * 30.0);
                    return float4((col * glow + core * uCoreWhite) * a, 0.0);
                }
                // Tail: uv.y runs -1..1 across the ribbon. A soft body with a
                // brighter line down the middle.
                float y2 = i.uv.y * i.uv.y;
                float body = exp(-y2 * 3.0);
                float centre = exp(-y2 * 28.0);     // ("line" is a reserved word in HLSL)
                return float4((col * body + centre * uCoreWhite * 0.35) * a, 0.0);
            }
            ENDCG
        }
    }
}
