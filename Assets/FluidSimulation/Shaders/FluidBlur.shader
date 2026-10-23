Shader "Hidden/Fluid/Blur"
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
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            // Separable blur. _TexelSize carries the axis to blur along:
            // (1/width, 0) for the horizontal pass, (0, 1/height) for vertical.
            float2 _TexelSize;

            struct appdata_blur
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f_blur
            {
                float4 pos : SV_POSITION;
                float2 vUv : TEXCOORD0;
                float2 vL : TEXCOORD1;
                float2 vR : TEXCOORD2;
            };

            v2f_blur vert (appdata_blur v)
            {
                v2f_blur o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float offset = 1.33333333;
                o.vUv = v.texcoord;
                o.vL = v.texcoord - _TexelSize * offset;
                o.vR = v.texcoord + _TexelSize * offset;
                return o;
            }

            sampler2D uTexture;

            fixed4 frag (v2f_blur i) : SV_Target
            {
                float4 sum = tex2D(uTexture, i.vUv) * 0.29411764;
                sum += tex2D(uTexture, i.vL) * 0.35294117;
                sum += tex2D(uTexture, i.vR) * 0.35294117;
                return sum;
            }
            ENDCG
        }
    }
}
