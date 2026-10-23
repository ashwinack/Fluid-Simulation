using UnityEngine;

namespace FluidSim
{
    /// <summary>
    /// How one animation style uses the particle layer. Styles without particles
    /// (Classic, Neon) leave <see cref="enabled"/> false and cost nothing.
    /// </summary>
    public struct ParticleLook
    {
        public bool enabled;
        public int count;                 // particles in use, up to 262144
        public bool atPointers;           // true = born at the fingers, false = scattered over the wall
        public Vector2 life;              // lifetime range, seconds
        public float streak;              // seconds of motion drawn as the tail
        public float minLength, maxLength;// tail length limits, pixels at 1080p
        public float headSize;            // head diameter, pixels at 1080p
        public float intensity;
        public float tint;                // 0 = baseColor, 1 = the dye colour underneath
        public float twinkle;
        public float dyeLit;              // brighter where there is dye
        public float trailFade;           // per 60 fps frame; 0 = no trails
        public float drift;               // random wander, wall heights per second
        public float spawnRadius;         // scatter round a finger, wall heights
        public float perSpeed;            // births per second per (wall height per second) of finger speed
        public float perSecond;           // births per second from a finger that is not moving
        public Color baseColor;
        public float coreWhite;           // white-hot centre of each head

        // Ballistic particles fly straight at a share of the finger's speed instead
        // of riding the fluid - one straight streak per stroke, not a fan of curls.
        public bool ballistic;
        public float follow;              // how much the fluid carries them (flow particles: 1)
        public Vector2 speedShare;        // share of the finger's speed, min..max
        public float dustShare;           // share born as sparkle dust
        public float dustSpeed;           // dust scatter speed, wall heights per second
        public float dustSize;            // dust head size, share of headSize
        public float drag;                // how fast own velocity dies away, per second
        public float sizeJitter;          // 0 = every particle the same size
    }

    /// <summary>
    /// A GPU particle layer that rides the fluid. Particle state lives in a float
    /// texture (one texel each) updated by a blit, and they are drawn as streaks
    /// into a trail buffer that the display shader adds on top of the fluid.
    ///
    /// Particles are passive: they read the velocity field but never write to it,
    /// so every animation style runs on exactly the same simulation and all its
    /// parameters (force, curl, dissipation...) keep working unchanged.
    /// </summary>
    public class FluidParticles
    {
        public const int StateSize = 512;              // 262144 particles max (Water's glitter uses most)
        public const int MaxEmitters = 16;

        readonly Material updateMat, renderMat;
        RenderTexture stateRead, stateWrite, motionRead, motionWrite, trail;
        public RenderTexture Trail => trail;

        public FluidParticles(Shader update, Shader render)
        {
            updateMat = new Material(update);
            renderMat = new Material(render);
        }

        static RenderTexture NewState()
        {
            var fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)
                ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf;
            var rt = new RenderTexture(StateSize, StateSize, 0, fmt, RenderTextureReadWrite.Linear)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        /// <summary>Make sure the buffers exist and the trail matches the wall size.</summary>
        public void Ensure(int width, int height)
        {
            if (stateRead == null)
            {
                stateRead = NewState(); stateWrite = NewState();
                motionRead = NewState(); motionWrite = NewState();
                Clear(stateRead); Clear(stateWrite); Clear(motionRead); Clear(motionWrite);
            }
            if (trail != null && trail.width == width && trail.height == height) return;
            Destroy(ref trail);
            trail = new RenderTexture(Mathf.Max(width, 1), Mathf.Max(height, 1), 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            trail.Create();
            Clear(trail);
        }

        /// <summary>Kill every particle and wipe the trails - used when the style changes.</summary>
        public void Reset()
        {
            Clear(stateRead);
            Clear(stateWrite);
            Clear(motionRead);
            Clear(motionWrite);
            Clear(trail);
        }

        static void Clear(RenderTexture rt)
        {
            if (rt == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        static void Destroy(ref RenderTexture rt)
        {
            if (rt == null) return;
            if (RenderTexture.active == rt) RenderTexture.active = null;
            rt.Release();
            Object.Destroy(rt);
            rt = null;
        }

        public void Release()
        {
            Destroy(ref stateRead);
            Destroy(ref stateWrite);
            Destroy(ref motionRead);
            Destroy(ref motionWrite);
            Destroy(ref trail);
        }

        /// <summary>
        /// Advance and draw one frame. <paramref name="emitters"/> holds the fingers:
        /// xy = wall UV, z = births this frame. Converted here into the per-dead-
        /// particle chance the update shader rolls against. <paramref name="emitterVel"/>
        /// holds each finger's velocity (wall UV per second), for ballistic particles.
        /// </summary>
        public void Frame(in ParticleLook look, float simDt, float realDt, RenderTexture velocity, RenderTexture dye,
                          Vector2 simTexel, float wallAspect, float exposure, Vector4[] emitters, Vector4[] emitterVel,
                          int emitterCount)
        {
            if (trail == null) return;
            int count = Mathf.Clamp(look.count, 0, StateSize * StateSize);

            // Births are asked for per emitter; the shader instead gives every dead
            // particle a chance to be born at one emitter picked at random. Scale
            // the ask by how many particles are likely dead to get that chance.
            float avgLife = (look.life.x + look.life.y) * 0.5f;
            float wanted = 0f;
            for (int k = 0; k < emitterCount; k++) wanted += emitters[k].z;
            float aliveEstimate = Mathf.Min(wanted / Mathf.Max(realDt, 1e-4f) * avgLife, count * 0.9f);
            float dead = Mathf.Max(count - aliveEstimate, 1f);
            for (int k = 0; k < emitterCount; k++)
                emitters[k].z = Mathf.Clamp01(emitters[k].z * emitterCount / dead);

            updateMat.SetTexture("uState", stateRead);
            updateMat.SetTexture("uMotion", motionRead);
            updateMat.SetTexture("uVelocity", velocity);
            updateMat.SetFloat("uFollow", look.ballistic ? look.follow : 1f);
            updateMat.SetFloat("uBallistic", look.ballistic ? 1f : 0f);
            updateMat.SetVector("uSpeedShare", look.speedShare);
            updateMat.SetFloat("uDustShare", look.ballistic ? look.dustShare : 0f);
            updateMat.SetFloat("uDustSpeed", look.dustSpeed);
            updateMat.SetFloat("uDrag", look.drag);
            updateMat.SetVectorArray("uEmitterVel", emitterVel);
            updateMat.SetVector("uVelScale", simTexel);
            updateMat.SetFloat("uDt", simDt);
            updateMat.SetFloat("uTime", Time.unscaledTime % 1000f);
            updateMat.SetFloat("uStateSize", StateSize);
            updateMat.SetFloat("uCount", count);
            updateMat.SetFloat("uSpawnMode", look.atPointers ? 1f : 0f);
            updateMat.SetVector("uLife", look.life);
            updateMat.SetFloat("uSpawnRadius", look.spawnRadius);
            updateMat.SetFloat("uDrift", look.drift);
            updateMat.SetFloat("uAspect", wallAspect);
            updateMat.SetVectorArray("uEmitters", emitters);
            updateMat.SetFloat("uEmitterCount", emitterCount);
            // Both halves read the same old state and make the same birth decisions,
            // then both buffers swap together.
            Graphics.Blit(null, motionWrite, updateMat, 1);
            Graphics.Blit(null, stateWrite, updateMat, 0);
            (stateRead, stateWrite) = (stateWrite, stateRead);
            (motionRead, motionWrite) = (motionWrite, motionRead);

            // Trails: fade what is there (frame-rate independent), or wipe it.
            if (look.trailFade > 0f)
            {
                renderMat.SetFloat("uFade", Mathf.Pow(look.trailFade, realDt * 60f));
                Graphics.Blit(null, trail, renderMat, 1);
            }
            else Clear(trail);

            // Sizes are authored for a 1080-tall wall and scaled to the real one.
            float px = trail.height / 1080f;
            renderMat.SetTexture("uState", stateRead);
            renderMat.SetTexture("uMotion", motionRead);
            renderMat.SetFloat("uFollow", look.ballistic ? look.follow : 1f);
            renderMat.SetFloat("uBallistic", look.ballistic ? 1f : 0f);
            renderMat.SetFloat("uDustSize", look.dustSize);
            renderMat.SetFloat("uCoreWhite", look.coreWhite);
            renderMat.SetFloat("uSizeJitter", look.sizeJitter);
            renderMat.SetTexture("uVelocity", velocity);
            renderMat.SetTexture("uDye", dye);
            renderMat.SetFloat("uStateSize", StateSize);
            renderMat.SetVector("uVelScale", simTexel);
            renderMat.SetVector("uTargetSize", new Vector2(trail.width, trail.height));
            renderMat.SetFloat("uStreak", look.streak);
            renderMat.SetFloat("uMinLength", look.minLength * px);
            renderMat.SetFloat("uMaxLength", look.maxLength * px);
            renderMat.SetFloat("uHeadSize", Mathf.Max(look.headSize * px, 1f));
            renderMat.SetFloat("uIntensity", look.intensity);
            renderMat.SetFloat("uTint", look.tint);
            renderMat.SetFloat("uTwinkle", look.twinkle);
            renderMat.SetFloat("uDyeLit", look.dyeLit);
            renderMat.SetFloat("uExposure", exposure);
            renderMat.SetFloat("uTime", Time.unscaledTime % 1000f);
            renderMat.SetVector("uBaseColor", look.baseColor);

            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(trail);
            renderMat.SetPass(0);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, count * 6, 1);
            RenderTexture.active = prev;
        }
    }
}
