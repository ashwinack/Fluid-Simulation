using UnityEngine;

namespace FluidSim
{
    // Each animation style owns its parameters. Classic keeps the original fields on
    // FluidSimulation (the main control panel); the others live here, are edited in
    // the STYLE picker, and are saved in fluid-settings.json under their own key.
    // Nothing from one style's set is read by another style's pipeline.

    /// <summary>Water: a flowing current with a rippling surface and suspended glitter.</summary>
    [System.Serializable]
    public class WaterSettings
    {
        [Tooltip("How thick the water is: higher = the current slows and settles faster.")]
        [Range(0f, 4f)] public float viscosity = 0.6f;
        [Tooltip("How tall the surface waves look: the strength of the light and refraction they cause.")]
        [Range(0f, 2f)] public float waveHeight = 0.8f;
        [Tooltip("How strongly a finger sets off ripples on the surface.")]
        [Range(0f, 2f)] public float rippleIntensity = 1f;
        [Tooltip("How long ripples keep travelling before they die away.")]
        [Range(0.9f, 0.999f)] public float rippleLife = 0.985f;
        [Tooltip("Swirling eddies in the current, which pull the glitter into bands.")]
        [Range(0f, 50f)] public float swirl = 14f;
        [Tooltip("How hard a finger pushes the current.")]
        [Range(500f, 12000f)] public float currentStrength = 4500f;
        [Tooltip("How much glitter is suspended in the water.")]
        [Range(0f, 1f)] public float glitterAmount = 0.8f;
        [Tooltip("How brightly the glitter sparkles.")]
        [Range(0f, 2f)] public float glitterBrightness = 1f;
        [Tooltip("The water's own current, always flowing even when nobody touches it (0 = still water).")]
        [Range(0f, 2f)] public float idleCurrent = 1f;
        [Tooltip("Milky, glittering wisps that keep drifting through the water.")]
        [Range(0f, 2f)] public float mist = 1f;
        [Tooltip("Gentle surface waves that keep rolling across the water.")]
        [Range(0f, 2f)] public float idleWaves = 1f;
        [Tooltip("How fast the shades of blue drift and change.")]
        [Range(0f, 3f)] public float colorFlow = 1f;
        [Tooltip("The milky white a hand leaves in the water where it strokes (0 = none: the stroke only swirls the water).")]
        [Range(0f, 1f)] public float strokeWhite = 0f;

        public WaterSettings Clone() => (WaterSettings)MemberwiseClone();
        public void Clamp()
        {
            viscosity = Mathf.Clamp(viscosity, 0f, 4f);
            waveHeight = Mathf.Clamp(waveHeight, 0f, 2f);
            rippleIntensity = Mathf.Clamp(rippleIntensity, 0f, 2f);
            rippleLife = Mathf.Clamp(rippleLife, 0.9f, 0.999f);
            swirl = Mathf.Clamp(swirl, 0f, 50f);
            currentStrength = Mathf.Clamp(currentStrength, 500f, 12000f);
            glitterAmount = Mathf.Clamp01(glitterAmount);
            glitterBrightness = Mathf.Clamp(glitterBrightness, 0f, 2f);
            idleCurrent = Mathf.Clamp(idleCurrent, 0f, 2f);
            mist = Mathf.Clamp(mist, 0f, 2f);
            idleWaves = Mathf.Clamp(idleWaves, 0f, 2f);
            colorFlow = Mathf.Clamp(colorFlow, 0f, 3f);
            strokeWhite = Mathf.Clamp01(strokeWhite);
        }
    }

    /// <summary>Shooting Stars: one white-hot star per finger over a night sky.</summary>
    [System.Serializable]
    public class ShootingStarSettings
    {
        [Tooltip("How far a star keeps flying after the finger lifts (0 = it stops with the finger).")]
        [Range(0f, 3f)] public float speed = 1.2f;
        [Tooltip("How bright the star and its tail are.")]
        [Range(0.2f, 3f)] public float brightness = 1.3f;
        [Tooltip("Length of the tail, in seconds of path.")]
        [Range(0.1f, 2f)] public float tailLength = 0.8f;
        [Tooltip("How many fixed stars fill the night sky.")]
        [Range(0f, 1f)] public float starDensity = 0.5f;
        [Tooltip("Size of the star's head, in pixels at 1080p.")]
        [Range(4f, 60f)] public float headSize = 28f;
        [Tooltip("How much sparkle dust the star sheds.")]
        [Range(0f, 1f)] public float sparkle = 0.6f;

        public ShootingStarSettings Clone() => (ShootingStarSettings)MemberwiseClone();
        public void Clamp()
        {
            speed = Mathf.Clamp(speed, 0f, 3f);
            brightness = Mathf.Clamp(brightness, 0.2f, 3f);
            tailLength = Mathf.Clamp(tailLength, 0.1f, 2f);
            starDensity = Mathf.Clamp01(starDensity);
            headSize = Mathf.Clamp(headSize, 4f, 60f);
            sparkle = Mathf.Clamp01(sparkle);
        }
    }

    /// <summary>Comets: one big glowing comet per finger.</summary>
    [System.Serializable]
    public class CometSettings
    {
        [Tooltip("How the comet follows the finger: 0 = right on it, 1 = sweeping wide, smooth arcs behind it.")]
        [Range(0f, 1f)] public float trajectory = 0.4f;
        [Tooltip("How strongly the head and its surrounding coma glow.")]
        [Range(0.2f, 3f)] public float glowIntensity = 1.2f;
        [Tooltip("How many sparks the comet sheds.")]
        [Range(0f, 1f)] public float particleCount = 0.5f;
        [Tooltip("Size of the comet's head, in pixels at 1080p.")]
        [Range(10f, 120f)] public float headSize = 65f;
        [Tooltip("Length of the tail, in seconds of path.")]
        [Range(0.2f, 3f)] public float tailLength = 1.2f;

        public CometSettings Clone() => (CometSettings)MemberwiseClone();
        public void Clamp()
        {
            trajectory = Mathf.Clamp01(trajectory);
            glowIntensity = Mathf.Clamp(glowIntensity, 0.2f, 3f);
            particleCount = Mathf.Clamp01(particleCount);
            headSize = Mathf.Clamp(headSize, 10f, 120f);
            tailLength = Mathf.Clamp(tailLength, 0.2f, 3f);
        }
    }

    /// <summary>Neon: glowing light tubes tracing the flow.</summary>
    [System.Serializable]
    public class NeonSettings
    {
        [Tooltip("How far the neon glow spreads round each line.")]
        [Range(0f, 1f)] public float glowRadius = 0.5f;
        [Tooltip("How vivid and bright the neon colours are.")]
        [Range(0.2f, 3f)] public float colorIntensity = 1.4f;
        [Tooltip("Thickness of the neon lines.")]
        [Range(0.5f, 4f)] public float lineWidth = 1.5f;
        [Tooltip("Swirling in the flow, which curls the lines.")]
        [Range(0f, 50f)] public float swirl = 20f;

        public NeonSettings Clone() => (NeonSettings)MemberwiseClone();
        public void Clamp()
        {
            glowRadius = Mathf.Clamp01(glowRadius);
            colorIntensity = Mathf.Clamp(colorIntensity, 0.2f, 3f);
            lineWidth = Mathf.Clamp(lineWidth, 0.5f, 4f);
            swirl = Mathf.Clamp(swirl, 0f, 50f);
        }
    }
}
