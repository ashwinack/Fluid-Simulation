Shader "Hidden/Fluid/ParticleRender"
{
    // Pass 0 draws every particle as a streak: a round glowing head with a tail laid
    // back along the direction the fluid is carrying it, longer the faster it moves.
    // One quad per particle, built in the vertex shader from SV_VertexID, so there is
    // no mesh - Graphics.DrawProceduralNow(Triangles, count * 6).
    //
    // Pass 1 multiplies the trail buffer by uFade, which is how streaks of earlier
    // frames linger and die away into long tails.
    Properties
    {
        uState ("State", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            sampler2D uState;
            sampler2D uMotion;      // own velocity (xy) and kind (z: 1 = dust)
            sampler2D uVelocity;
            sampler2D uDye;
            float uFollow;          // how much the fluid carries a particle
            float uBallistic;       // 1 = particles keep their own velocity
            float uDustSize;        // dust head size, as a share of uHeadSize
            float uCoreWhite;       // white-hot centre of each head
            float uSizeJitter;      // 0 = all the same size, 1 = down to nothing
            float uStateSize;
            float2 uVelScale;       // velocity texture units -> UV per second
            float2 uTargetSize;     // trail buffer size in pixels
            float uStreak;          // seconds of motion drawn as the tail
            float uMinLength;       // tail length limits, pixels
            float uMaxLength;
            float uHeadSize;        // head diameter, pixels
            float uIntensity;
            float uTint;            // 0 = uBaseColor, 1 = the colour of the dye underneath
            float uTwinkle;         // 0 = steady, 1 = full twinkle
            float uDyeLit;          // 0 = even, 1 = brighter where there is dye
            float uExposure;
            float uTime;
            float3 uBaseColor;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 q : TEXCOORD0;       // pixels from the head centre: x along, y across
                float4 col : TEXCOORD1;
                float2 len : TEXCOORD2;     // x = tail length, y = head radius, pixels
            };

            v2f vert (uint vid : SV_VertexID)
            {
                v2f o = (v2f)0;
                uint size = (uint)uStateSize;
                uint pid = vid / 6;
                uint c = vid % 6;
                float2 corner = c == 0 ? float2(0, 0) : c == 1 ? float2(1, 0) : c == 2 ? float2(0, 1)
                              : c == 3 ? float2(1, 0) : c == 4 ? float2(1, 1) : float2(0, 1);

                float2 tuv = (float2(pid % size, pid / size) + 0.5) / uStateSize;
                float4 s = tex2Dlod(uState, float4(tuv, 0.0, 0.0));
                if (!(s.w > 0.0 && s.z < s.w))
                {
                    o.pos = float4(-10.0, -10.0, 0.0, 1.0);     // off screen: clipped
                    return o;
                }

                float4 m = tex2Dlod(uMotion, float4(tuv, 0.0, 0.0));
                float2 flow = tex2Dlod(uVelocity, float4(s.xy, 0.0, 0.0)).xy * uVelScale;
                float2 vel = m.xy * uBallistic + flow * uFollow;
                bool dust = m.z > 0.5;
                float2 velPx = vel * uTargetSize;
                float speed = length(velPx);
                float2 dir = speed > 1e-4 ? velPx / speed : float2(1.0, 0.0);
                float2 perp = float2(-dir.y, dir.x);
                // Size varies per particle, so a burst of comets is a mix of big and
                // small ones rather than identical copies.
                float h0 = frac(sin(dot(tuv, float2(39.346, 11.135))) * 43758.5453);
                float scale = lerp(1.0 - uSizeJitter, 1.0, h0);
                // Dust: tiny sparks with barely any tail.
                float head = dust ? max(uHeadSize * uDustSize, 1.0) : uHeadSize * scale;
                float len = dust ? min(speed * uStreak * 0.25, head * 2.0)
                                 : clamp(speed * uStreak * scale, uMinLength * scale, uMaxLength * scale);
                float w = head * 3.0;                           // room for the head's glow and the tail's halo

                float along = lerp(-len, w * 0.5, corner.x);
                float across = (corner.y - 0.5) * w;
                float2 px = s.xy * uTargetSize + dir * along + perp * across;
                float2 clip = px / uTargetSize * 2.0 - 1.0;
            #if UNITY_UV_STARTS_AT_TOP
                clip.y = -clip.y;                               // drawing into a render texture
            #endif
                o.pos = float4(clip, 0.0, 1.0);
                o.q = float2(along, across);
                o.len = float2(len, head * 0.5);

                // Fade in quickly, hold, fade out over the last part of the life.
                float t = s.z / s.w;
                float fade = smoothstep(0.0, 0.06, t) * (1.0 - smoothstep(0.55, 1.0, t));

                // Colour from the dye the particle is flying through, so palette and
                // random colours carry over to every style.
                float3 dye = tex2Dlod(uDye, float4(s.xy, 0.0, 0.0)).rgb * uExposure;
                float dl = max(dye.r, max(dye.g, dye.b));
                float3 hue = dl > 1e-4 ? dye / dl : float3(1.0, 1.0, 1.0);
                float3 col = lerp(uBaseColor, hue, uTint * saturate(dl * 3.0));

                float h = frac(sin(dot(tuv, float2(12.9898, 78.233))) * 43758.5453);
                float twinkle = dust ? 1.0 : uTwinkle;          // dust always sparkles
                float tw = lerp(1.0, 0.25 + 0.75 * (0.5 + 0.5 * sin(uTime * (3.0 + h * 11.0) + h * 40.0)), twinkle);
                // Glitter is faint in clear water and catches the light in the bright
                // swirls, which is what makes the current visible.
                float lit = lerp(1.0, 0.06 + saturate(dl * 1.8) * 1.2, uDyeLit);

                float bright = fade * tw * lit * uIntensity;
                o.col = float4(col * bright, bright);           // a = brightness, for the white core
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float r = i.len.y;
                // Head: a soft round glow.
                float2 hd = float2(max(i.q.x, 0.0) + min(i.q.x, 0.0) * 0.35, i.q.y);
                float head = exp(-dot(hd, hd) / max(r * r, 0.25));
                // Tail: brightest at the head, thinning and dimming to nothing.
                float t = saturate(1.0 + i.q.x / max(i.len.x, 1.0));
                float thin = r * (0.25 + 0.75 * t);
                float tail = t * t * exp(-(i.q.y * i.q.y) / max(thin * thin, 0.25)) * step(i.q.x, 0.0);
                // A soft, wider halo round the tail, so it reads as a glowing cone
                // rather than a hard line.
                float halo = pow(t, 1.5) * exp(-(i.q.y * i.q.y) / max(thin * thin * 5.0, 0.25)) * step(i.q.x, 0.0);
                tail += halo * 0.35;
                // White-hot core in the middle of the head, coloured glow round it.
                float core = head * head * head * uCoreWhite * i.col.a;
                // Fade out before the side edges of the quad (half-width 3 x radius), so
                // the halo never ends in a hard rectangular edge.
                float edge = 1.0 - smoothstep(0.55, 1.0, abs(i.q.y) / max(r * 3.0, 0.5));
                return float4((i.col.rgb * max(head, tail * 0.8) + core) * edge, 0.0);
            }
            ENDCG
        }

        Pass
        {
            // trail *= uFade
            Blend DstColor Zero

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            float uFade;

            float4 frag (v2f_img i) : SV_Target
            {
                return float4(uFade, uFade, uFade, uFade);
            }
            ENDCG
        }
    }
}
