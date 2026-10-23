Shader "Hidden/Fluid/ParticleUpdate"
{
    // Moves every particle and respawns the dead.
    //
    // Two float textures, one texel per particle:
    //   state    xy = position (wall UV), z = age (s), w = lifetime (s, 0 = dead)
    //   motion   xy = own velocity (wall UV / s), z = kind (0 = streak, 1 = dust), w = spare
    //
    // Pass 0 writes the state, pass 1 the motion. Both make exactly the same birth
    // decision (same inputs, same hashes), so a particle's two halves always agree.
    //
    // Particles are passive: they read the fluid's velocity but never write it, so
    // every animation style runs on exactly the same simulation.
    //  - Flow particles (Water, Comets) ride the fluid: uFollow = 1, no own velocity.
    //  - Ballistic particles (Shooting Stars) fly straight at a share of the finger's
    //    speed, only nudged by the fluid, which is what gives one straight streak
    //    instead of a fan of curled hairs.
    Properties
    {
        uState ("State", 2D) = "black" {}
        uMotion ("Motion", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D uState;
    sampler2D uMotion;
    sampler2D uVelocity;
    float2 uVelScale;       // sim texel size: velocity texture units -> UV per second
    float uDt;
    float uTime;
    float uStateSize;       // texels per side
    float uCount;           // particles in use; texels beyond this stay dead
    float uSpawnMode;       // 0 = anywhere on the wall, 1 = at the emitters
    float2 uLife;           // lifetime range, seconds
    float uSpawnRadius;     // scatter round an emitter, in wall heights
    float uDrift;           // random wander, wall heights per second
    float uAspect;          // wall width / height
    float uFollow;          // how much the fluid carries a particle (1 = fully)
    float uBallistic;       // 1 = particles keep their own velocity
    float2 uSpeedShare;     // ballistic: share of the finger's speed, min..max
    float uDustShare;       // ballistic: share of particles born as sparkle dust
    float uDustSpeed;       // ballistic: dust scatter speed, wall heights per second
    float uDrag;            // ballistic: how fast own velocity dies away, per second
    float4 uEmitters[16];   // xy = wall UV, z = spawn chance per dead particle
    float4 uEmitterVel[16]; // xy = finger velocity, wall UV per second
    float uEmitterCount;

    float hash12(float2 p)
    {
        float3 p3 = frac(float3(p.xyx) * 0.1031);
        p3 += dot(p3, p3.yzx + 33.33);
        return frac((p3.x + p3.y) * p3.z);
    }

    float2 hash22(float2 p)
    {
        float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
        p3 += dot(p3, p3.yzx + 33.33);
        return frac((p3.xx + p3.yz) * p3.zy);
    }

    // Everything about one particle's step, shared by both passes.
    void Simulate(float2 uv, out float4 state, out float4 motion)
    {
        float2 cell = floor(uv * uStateSize);
        float id = cell.x + cell.y * uStateSize;
        state = float4(-1.0, -1.0, 0.0, 0.0);
        motion = float4(0.0, 0.0, 0.0, 0.0);
        if (id >= uCount) return;

        float4 s = tex2D(uState, uv);
        float4 m = tex2D(uMotion, uv);
        bool inside = s.x > -0.05 && s.x < 1.05 && s.y > -0.05 && s.y < 1.05;
        bool alive = s.w > 0.0 && s.z < s.w && inside;

        if (alive)
        {
            float2 flow = tex2Dlod(uVelocity, float4(s.xy, 0.0, 0.0)).xy * uVelScale;
            // A fixed random direction per particle, so a burst fans out a little
            // instead of travelling as one tight bead.
            float2 wander = (hash22(cell + 0.37) - 0.5) * 2.0 * uDrift * float2(1.0 / uAspect, 1.0);
            float2 own = m.xy * uBallistic;
            s.xy += (own + flow * uFollow + wander) * uDt;
            s.z += uDt;
            state = s;
            // Drag: a particle shoots off along the stroke and then slows, so the
            // tail settles along the path the finger actually took instead of
            // carrying on straight past every turn.
            m.xy *= exp(-uDrag * uDt);
            motion = m;
            return;
        }

        float2 seed = cell * 1.618 + float2(uTime * 37.1, uTime * 11.3);
        float2 r = hash22(seed);
        float r2 = hash12(seed + 17.13);
        float life = lerp(uLife.x, uLife.y, hash12(seed * 1.7 + 3.31));

        if (uSpawnMode < 0.5)
        {
            // Scattered over the whole wall. The very first spawn starts part way
            // through its life, so the field does not all expire at once.
            float age = s.w <= 0.0 ? r2 * life : 0.0;
            state = float4(r, age, life);
            return;
        }

        if (uEmitterCount < 0.5) return;
        int k = min((int)(r.x * uEmitterCount), 15);
        float4 e = uEmitters[k];
        if (r2 > e.z) return;

        float2 ev = uEmitterVel[k].xy;
        float2 evH = ev * float2(uAspect, 1.0);                 // in wall heights, round on screen
        float evLen = length(evH);
        float2 dir = evLen > 1e-5 ? evH / evLen : float2(1.0, 0.0);
        float2 perp = float2(-dir.y, dir.x);

        float g1 = hash12(seed + 5.51), g2 = hash12(seed + 9.17);
        float gauss = (g1 + g2 - 1.0) * 1.7;                    // roughly normal, -1.7..1.7
        float along = (hash12(seed + 2.9) - 0.5) * 0.6;
        float2 offH = (perp * gauss + dir * along) * uSpawnRadius;
        state = float4(e.xy + offH * float2(1.0 / uAspect, 1.0), 0.0, life);

        float kind = hash12(seed + 13.7) < uDustShare ? 1.0 : 0.0;
        float share = lerp(uSpeedShare.x, uSpeedShare.y, hash12(seed + 21.3));
        float2 velH;
        if (kind < 0.5)
            velH = evH * share;                                 // streak: along the stroke
        else
        {
            float a = hash12(seed + 31.1) * 6.2831853;
            velH = evH * share * 0.35 + float2(cos(a), sin(a)) * uDustSpeed * hash12(seed + 4.4);
        }
        motion = float4(velH * float2(1.0 / uAspect, 1.0), kind, 0.0);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            float4 frag (v2f_img i) : SV_Target
            {
                float4 state, motion;
                Simulate(i.uv, state, motion);
                return state;
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            float4 frag (v2f_img i) : SV_Target
            {
                float4 state, motion;
                Simulate(i.uv, state, motion);
                return motion;
            }
            ENDCG
        }
    }
}
