using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace FluidSim
{
    /// <summary>
    /// Real-time GPU fluid simulation (Stable Fluids / semi-Lagrangian advection
    /// + Jacobi pressure projection + vorticity confinement), ported from
    /// Pavel Dobryakov's WebGL Fluid Simulation:
    /// https://github.com/PavelDoGreat/WebGL-Fluid-Simulation
    ///
    /// Each WebGL fragment-shader pass over a framebuffer becomes a Unity
    /// Shader + Graphics.Blit over a ping-ponged RenderTexture pair.
    /// </summary>
    // Runs first: it loads fluid-config.json and activates the displays in Awake, and
    // FluidDisplayWall / FluidControlPanel both build from that configuration.
    [DefaultExecutionOrder(-100)]
    public partial class FluidSimulation : MonoBehaviour
    {
        [Header("Resolution")]
        [Tooltip("Simulation grid resolution along the shorter screen axis.")]
        public int simResolution = 128;
        [Tooltip("Dye (visible color) resolution along the shorter screen axis.")]
        public int dyeResolution = 1024;

        [Header("Timing")]
        [Tooltip("Simulation steps per second. The browser original runs at 60. " +
                 "This is deliberately NOT tied to the render framerate: every advection " +
                 "step resamples the dye bilinearly, which blurs it slightly, so stepping " +
                 "at 1000fps destroys the fine filament detail instead of adding motion.")]
        public float simulationRate = 60f;
        [Tooltip("Multiplies simulated time. Above 1 the fluid evolves faster.")]
        [Range(0.1f, 4f)] public float simulationSpeed = 1f;
        [Tooltip("Upper bound on catch-up steps in one frame, so a hitch cannot spiral.")]
        [Range(1, 8)] public int maxStepsPerFrame = 3;

        [Header("Simulation")]
        public float densityDissipation = 1f;
        public float velocityDissipation = 0.2f;
        [Range(0f, 1f)] public float pressure = 0.8f;
        [Range(1, 50)] public int pressureIterations = 20;
        public float curl = 30f;
        [Range(0.01f, 1f)] public float splatRadius = 0.25f;
        public float splatForce = 6000f;

        [Header("Appearance")]
        [Tooltip("Cycle the stroke color over time. The color stays coherent per stroke - " +
                 "it is refreshed on a timer, never per splat.")]
        public bool colorful = true;
        public float colorUpdateSpeed = 10f;

        [Header("Color palette")]
        [Tooltip("Colour strokes from the palette instead of random hues. Every stroke - " +
                 "touch, mouse, attract fingers - starts on the first colour and blends " +
                 "through the rest in order, so its tail keeps the earlier colours.")]
        public bool usePalette = false;
        [Tooltip("1 to 8 colours, in stroke order: first = where a stroke starts.")]
        public Color[] palette =
        {
            new Color(0f, 0.6f, 1f), new Color(0.55f, 0f, 1f), new Color(1f, 0f, 0.6f),
        };
        [Tooltip("Seconds a stroke takes to blend from one palette colour to the next.")]
        [Range(0.1f, 10f)] public float paletteSecondsPerColor = 1.5f;
        [Tooltip("After the last colour: true = blend back to the first and keep cycling, " +
                 "false = stay on the last colour.")]
        public bool paletteLoop = true;

        public const int MaxPaletteColors = 8;

        // ---------- animation styles ----------

        /// <summary>
        /// How the wall is drawn. Every style runs the same simulation with the same
        /// parameters and the same mouse / touch behaviour - only the look changes, so
        /// switching keeps every slider where it is.
        /// </summary>
        public enum AnimationStyle { Classic = 0, Water = 1, ShootingStars = 2, Comets = 3, Neon = 4 }

        public static readonly string[] AnimationStyleNames = { "Classic", "Water", "Shooting Stars", "Comets", "Neon" };

        public static readonly string[] AnimationStyleDescriptions =
        {
            "The original look: coloured dye with shading, bloom and sunrays.",
            "Living blue water, always flowing: drifting shades, rolling waves, glittering wisps.",
            "A night sky. Each finger draws a white-hot shooting star that flies on when released.",
            "One big glowing comet per finger, with a soft coma and a long tapering tail.",
            "Glowing neon tubes tracing every swirl of the flow on black.",
        };

        [Header("Animation style")]
        public AnimationStyle animationStyle = AnimationStyle.Classic;

        // Each style's own parameters. Classic uses the original fields above (the main
        // control panel); these are edited in the STYLE picker. No style reads
        // another's set, and Classic's shading, bloom, sunrays and exposure are only
        // ever applied to Classic.
        public WaterSettings water = new WaterSettings();
        public ShootingStarSettings shootingStars = new ShootingStarSettings();
        public CometSettings comet = new CometSettings();
        public NeonSettings neon = new NeonSettings();

        [Tooltip("Water's rippling surface.")]
        public Shader rippleShader;
        [Tooltip("Neon's glow source.")]
        public Shader neonEdgeShader;
        [Tooltip("Water's own idle current and drifting wisps.")]
        public Shader waterCurrentShader;
        Material rippleMat, neonEdgeMat, waterCurrentMat;
        RenderTexture rippleRead, rippleWrite, neonRT, neonTempRT;
        readonly Vector4[] rippleDrops = new Vector4[16];

        /// <summary>The fluid solver's settings for the style on the wall.</summary>
        struct SolverSettings { public float curl, velocityDissipation, densityDissipation, force, radius; }

        SolverSettings Solver()
        {
            switch (animationStyle)
            {
                case AnimationStyle.Water:
                    return new SolverSettings { curl = water.swirl, velocityDissipation = water.viscosity,
                        densityDissipation = 1.5f + water.viscosity * 0.3f, force = water.currentStrength, radius = 0.3f };
                case AnimationStyle.Neon:
                    return new SolverSettings { curl = neon.swirl, velocityDissipation = 0.4f,
                        densityDissipation = 2f, force = 6000f, radius = 0.22f };
                case AnimationStyle.ShootingStars:
                case AnimationStyle.Comets:
                    // The flow is never drawn in these styles; it only gently nudges
                    // their sparks, so it is kept calm.
                    return new SolverSettings { curl = 0f, velocityDissipation = 1f,
                        densityDissipation = 2f, force = 3000f, radius = 0.2f };
                default:
                    return new SolverSettings { curl = curl, velocityDissipation = velocityDissipation,
                        densityDissipation = densityDissipation, force = splatForce, radius = splatRadius };
            }
        }

        /// <summary>Dye gain for the style's own pipeline; only Classic uses the Exposure slider.</summary>
        float StyleExposure() =>
            animationStyle == AnimationStyle.Classic ? exposure :
            animationStyle == AnimationStyle.Water ? 2.5f : 4f;

        [Tooltip("Moves the particles of the Water, Shooting Stars and Comets styles.")]
        public Shader particleUpdateShader;
        [Tooltip("Draws the particles of the Water, Shooting Stars and Comets styles.")]
        public Shader particleRenderShader;

        FluidParticles particles;
        readonly Vector4[] emitterData = new Vector4[FluidParticles.MaxEmitters];
        readonly Vector4[] emitterVelData = new Vector4[FluidParticles.MaxEmitters];

        /// <summary>Switch the look immediately. Parameters are untouched.</summary>
        public void SetAnimationStyle(AnimationStyle style)
        {
            style = (AnimationStyle)Mathf.Clamp((int)style, 0, AnimationStyleNames.Length - 1);
            if (style == animationStyle) return;
            animationStyle = style;
            // Old particles, trails and comets belong to the previous look.
            particles?.Reset();
            comets?.Clear();
        }

        /// <summary>
        /// How strongly the dye itself is drawn, how fast it hides what is behind it,
        /// and how much bloom it gets. Bloom spreads even faint dye into a glow over
        /// the whole wall, which is right for Classic but washes out a night sky, so
        /// the dark styles take much less of it.
        /// </summary>
        static void StyleGains(AnimationStyle style, out float fluidGain, out float coverGain, out float bloomGain)
        {
            // Bloom is Classic's effect and is only ever added to Classic.
            switch (style)
            {
                case AnimationStyle.Water:         fluidGain = 0.45f; coverGain = 0.8f; bloomGain = 0f; break; // light in deep water
                case AnimationStyle.ShootingStars:                                                              // their own renderer
                case AnimationStyle.Comets:        fluidGain = 0f;    coverGain = 0f;   bloomGain = 0f; break; // draws everything
                case AnimationStyle.Neon:          fluidGain = 1f;    coverGain = 3f;   bloomGain = 0f; break; // own glow pass
                default:                           fluidGain = 1f;    coverGain = 3f;   bloomGain = 1f; break;
            }
        }

        /// <summary>The particle layer of each style. Sizes are for a 1080-tall wall.</summary>
        ParticleLook ParticlesFor(AnimationStyle style)
        {
            var look = ParticleRecipe(style);
            // Each style's own sliders scale its particles.
            switch (style)
            {
                case AnimationStyle.Water:
                    look.count = Mathf.Max(Mathf.RoundToInt(110000 * water.glitterAmount), 1000);
                    look.intensity *= water.glitterBrightness * 0.7f;
                    look.enabled = water.glitterAmount > 0.001f;
                    break;
                case AnimationStyle.ShootingStars:
                    look.perSpeed *= shootingStars.sparkle * 1.6f;
                    look.intensity *= shootingStars.brightness / 1.3f;
                    break;
                case AnimationStyle.Comets:
                    look.perSpeed *= comet.particleCount * 2f;
                    look.intensity *= comet.glowIntensity / 1.2f;
                    break;
            }
            return look;
        }

        static ParticleLook ParticleRecipe(AnimationStyle style)
        {
            switch (style)
            {
                case AnimationStyle.Water:
                    // Dense fine glitter, like mica powder in water: suspended
                    // everywhere and carried by the current, but only really catching
                    // the light in the swirling bands of the flow, so it gathers into
                    // sparkling clouds with clear water between them. Sizes vary, so a
                    // few bigger sparkles stand out among the fine dust.
                    return new ParticleLook
                    {
                        // Short lives keep re-scattering the glitter evenly; long ones let it
                        // pile up into bright foam lines along the edges of each swirl.
                        enabled = true, count = 200000, atPointers = false, life = new Vector2(3f, 7f),
                        streak = 0.02f, minLength = 1f, maxLength = 6f, headSize = 1.6f,
                        // dyeLit < 1: brightest in the milky wisps, but never quite
                        // gone in the clear water between them.
                        intensity = 0.6f, tint = 0.2f, twinkle = 0.8f, dyeLit = 0.8f, trailFade = 0f,
                        drift = 0.003f, baseColor = new Color(0.8f, 0.9f, 1f), sizeJitter = 0.5f,
                    };
                // Shooting Stars and Comets are drawn as ONE comet per pointer by
                // FluidComets (see CometFor). Their particles are only the sparkle
                // dust shed along the path. The trail buffer is cleared every frame
                // (trailFade 0), because the comet ribbon is redrawn whole each frame.
                case AnimationStyle.ShootingStars:
                    return new ParticleLook
                    {
                        enabled = true, count = 3000, atPointers = true, life = new Vector2(0.4f, 1f),
                        streak = 0.02f, minLength = 1f, maxLength = 12f, headSize = 8f,
                        intensity = 0.6f, tint = 1f, twinkle = 0.6f, dyeLit = 0f, trailFade = 0f,
                        drift = 0f, spawnRadius = 0.006f, perSpeed = 60f, perSecond = 0f,
                        baseColor = new Color(1f, 0.97f, 0.95f), coreWhite = 0.6f,
                        ballistic = true, follow = 0.1f, speedShare = new Vector2(0.05f, 0.3f),
                        dustShare = 1f, dustSpeed = 0.03f, dustSize = 0.3f, drag = 2f,
                    };
                case AnimationStyle.Comets:
                    return new ParticleLook
                    {
                        enabled = true, count = 1500, atPointers = true, life = new Vector2(0.5f, 1.2f),
                        streak = 0.02f, minLength = 1f, maxLength = 12f, headSize = 10f,
                        intensity = 0.7f, tint = 1f, twinkle = 0.6f, dyeLit = 0f, trailFade = 0f,
                        drift = 0f, spawnRadius = 0.015f, perSpeed = 40f, perSecond = 0f,
                        baseColor = new Color(1f, 0.95f, 0.9f), coreWhite = 0.6f,
                        ballistic = true, follow = 0.1f, speedShare = new Vector2(0.05f, 0.25f),
                        dustShare = 1f, dustSpeed = 0.05f, dustSize = 0.3f, drag = 1.5f,
                    };
                default:
                    return new ParticleLook { enabled = false };
            }
        }

        /// <summary>
        /// The single comet per pointer of Shooting Stars and Comets: one big head at
        /// the finger and one tail along its recent path, tapering to nothing.
        /// Sizes are for a 1080-tall wall.
        /// </summary>
        CometLook CometFor(AnimationStyle style)
        {
            switch (style)
            {
                case AnimationStyle.ShootingStars:
                    // White-hot head, thin tail; keeps shooting on after the finger lifts.
                    return new CometLook { enabled = true, tailSeconds = shootingStars.tailLength,
                        headRadius = shootingStars.headSize, tailWidth = shootingStars.headSize * 1.1f,
                        intensity = shootingStars.brightness, coreWhite = 1.1f,
                        coast = shootingStars.speed, smoothing = 0f, coma = 0.12f };
                case AnimationStyle.Comets:
                    // Big glowing head wrapped in a soft coma, wide tail, sweeping arcs.
                    return new CometLook { enabled = true, tailSeconds = comet.tailLength,
                        headRadius = comet.headSize, tailWidth = comet.headSize * 1.7f,
                        intensity = comet.glowIntensity, coreWhite = 1.3f,
                        coast = 0.4f, smoothing = comet.trajectory, coma = 0.35f * comet.glowIntensity };
                default:
                    return new CometLook { enabled = false };
            }
        }

        [Tooltip("Draws the single comet per pointer of the Shooting Stars and Comets styles.")]
        public Shader cometShader;
        FluidComets comets;

        /// <summary>A pointer's colour at full brightness: the palette colour along its stroke, or its random hue.</summary>
        Color CometColor(FluidPointer p)
        {
            Color c = usePalette ? PaletteColorAt(Time.unscaledTime - p.strokeStart) : p.color;
            float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            c = m > 1e-4f ? c / m : Color.white;
            c.a = 1f;
            return c;
        }

        void UpdateComets()
        {
            if (comets == null || particles == null || particles.Trail == null) return;
            var look = CometFor(animationStyle);
            if (!look.enabled) return;

            var trail = particles.Trail;
            var wallPx = new Vector2(trail.width, trail.height);
            float now = Time.unscaledTime;
            comets.BeginFrame();
            for (int i = 0; i < pointers.Count; i++)
                if (pointers[i].down) comets.Record(pointers[i].id, pointers[i].uv, CometColor(pointers[i]), now, wallPx, look.smoothing);
            comets.Advance(now, look.coast, wallPx);
            comets.Prune(now, look.tailSeconds);
            comets.Draw(trail, look, now);
        }

        /// <summary>
        /// Move and draw this frame's particles. The fingers - real and attract - are
        /// the emitters: births scale with how fast each one is moving.
        /// </summary>
        void UpdateParticles(float simDt, float realDt)
        {
            if (particles == null || displayRT == null) return;
            var look = ParticlesFor(animationStyle);
            if (!look.enabled) return;

            particles.Ensure(displayRT.width, displayRT.height);

            var wall = GetDisplaySize();
            float aspect = wall.x / (float)Mathf.Max(wall.y, 1);
            int n = 0;
            for (int i = 0; i < pointers.Count && n < emitterData.Length; i++)
            {
                var p = pointers[i];
                if (!p.down) { p.emitSeen = false; p.emitVel = Vector2.zero; continue; }
                if (p.emitSeen)
                {
                    // Smoothed, so a mouse's uneven per-frame steps do not jitter the
                    // direction of the stars it throws.
                    Vector2 v = (p.uv - p.emitUv) / Mathf.Max(realDt, 1e-4f);
                    p.emitVel = Vector2.Lerp(p.emitVel, v, 0.35f);
                }
                p.emitUv = p.uv;
                p.emitSeen = true;
                Vector2 vH = new Vector2(p.emitVel.x * aspect, p.emitVel.y);   // wall heights per second
                float births = (look.perSecond + look.perSpeed * vH.magnitude) * realDt;
                emitterVelData[n] = new Vector4(p.emitVel.x, p.emitVel.y, 0f, 0f);
                emitterData[n++] = new Vector4(p.uv.x, p.uv.y, births, 0f);
            }
            for (int k = n; k < emitterData.Length; k++) { emitterData[k] = Vector4.zero; emitterVelData[k] = Vector4.zero; }

            particles.Frame(look, simDt, realDt, velocity.read, dye.read, texelSizeSim, aspect, StyleExposure(),
                            emitterData, emitterVelData, n);
            UpdateComets();     // drawn into the same buffer, on top of the dust
        }

        public enum RevealFit
        {
            /// <summary>Cover the whole wall, cropping the image as needed.</summary>
            FillWall = 0,
            /// <summary>The whole image, centred on the wall, black bars at the sides.</summary>
            FitWall = 1,
            /// <summary>Stretch the image to the wall's exact shape.</summary>
            Stretch = 2,
            /// <summary>A full copy of the image on every fluid screen, each covering it.</summary>
            EachScreen = 3,
        }

        [Header("Logo reveal")]
        [Tooltip("The fluid acts as a mask over revealTexture: wherever the fluid is bright - " +
                 "the circle under the mouse or a finger, and its trail - the image shows " +
                 "through. Elsewhere the wall stays black.")]
        public bool revealMode = true;
        [Tooltip("The image revealed. Assigned in the scene from the Assets folder, so it " +
                 "is built into the app.")]
        public Texture2D revealTexture;
        [Tooltip("Optional: load the image from a file instead, so it can be swapped without " +
                 "a rebuild. Relative paths are looked up in StreamingAssets (and, in the " +
                 "editor, the project folder, so \"Assets/logo.jpg\" works). Empty = use " +
                 "revealTexture.")]
        public string revealImagePath = "";
        public RevealFit revealFit = RevealFit.FillWall;
        [Tooltip("Radius of the spotlight circle that follows the mouse and each finger, " +
                 "as a fraction of the wall height.")]
        [Range(0.03f, 0.6f)] public float spotlightSize = 0.2f;
        [Tooltip("How gradually the spotlight's edge fades into the fluid, as a share of its " +
                 "radius. 0.05 = a crisp rim, 1 = a soft glow with no visible edge.")]
        [Range(0.05f, 1f)] public float spotlightSoftness = 0.6f;
        [Tooltip("Image brightness outside the spotlights. 0 = hidden (black) until " +
                 "someone hovers or touches; 1 = always fully visible.")]
        [Range(0f, 1f)] public float revealBackground = 0f;
        [Tooltip("Fluid opacity over the image inside a spotlight. Outside the spotlights " +
                 "the fluid is always fully opaque. 0 = the logo shows clean; 1 = the " +
                 "fluid hides it.")]
        [Range(0f, 1f)] public float revealFluidOpacity = 0.35f;
        [Tooltip("Also light a spotlight under each attract-mode finger. Off = only real " +
                 "visitors (mouse hover, touch) reveal the image.")]
        public bool spotlightFollowsAttract = false;
        [Tooltip("Seconds for a spotlight to appear, and to fade after the pointer leaves.")]
        public float spotlightFadeIn = 0.15f, spotlightFadeOut = 0.6f;

        /// <summary>
        /// One spotlight. Kept separately from the pointers so it can fade out where
        /// the finger lifted rather than vanishing the instant it does.
        /// </summary>
        class Spot
        {
            public int id;
            public Vector2 uv;
            public float intensity;
            public bool live;
        }
        readonly List<Spot> spots = new List<Spot>();
        readonly Vector4[] spotData = new Vector4[MaxDisplays];

        void UpdateSpots(float dt)
        {
            for (int i = 0; i < spots.Count; i++) spots[i].live = false;

            for (int i = 0; i < pointers.Count; i++)
            {
                var p = pointers[i];
                if (!p.down) continue;
                if (p.id <= IdlePointerIdBase && !spotlightFollowsAttract) continue;

                Spot s = null;
                for (int k = 0; k < spots.Count; k++) if (spots[k].id == p.id) { s = spots[k]; break; }
                if (s == null) { s = new Spot { id = p.id, uv = p.uv }; spots.Add(s); }
                s.uv = p.uv;
                s.live = true;
            }

            for (int i = spots.Count - 1; i >= 0; i--)
            {
                var s = spots[i];
                float rate = dt / Mathf.Max(s.live ? spotlightFadeIn : spotlightFadeOut, 0.01f);
                s.intensity = Mathf.MoveTowards(s.intensity, s.live ? 1f : 0f, rate);
                if (!s.live && s.intensity <= 0f) spots.RemoveAt(i);
            }

            // The shader takes 8. More fingers than that is rare; the dimmest go.
            if (spots.Count > spotData.Length)
            {
                spots.Sort((a, b) => b.intensity.CompareTo(a.intensity));
                spots.RemoveRange(spotData.Length, spots.Count - spotData.Length);
            }
        }

        /// <summary>Spotlights currently lit, for the panel readout.</summary>
        public int SpotlightCount => spots.Count;

        Texture2D loadedRevealTexture;
        string loadedRevealPath;

        /// <summary>
        /// The image in use: the file at revealImagePath if one is set and readable,
        /// otherwise the texture assigned in the scene. Reloaded only when the path
        /// changes.
        /// </summary>
        public Texture RevealTexture
        {
            get
            {
                string path = (revealImagePath ?? "").Trim();
                if (path.Length == 0) return revealTexture;
                if (path.StartsWith(BuiltInPrefix))
                    return FindBuiltInBackground(path.Substring(BuiltInPrefix.Length)) ?? revealTexture;
                if (path != loadedRevealPath)
                {
                    loadedRevealPath = path;
                    if (loadedRevealTexture != null) Destroy(loadedRevealTexture);
                    loadedRevealTexture = LoadImageFile(path);
                }
                return loadedRevealTexture != null ? loadedRevealTexture : revealTexture;
            }
        }

        /// <summary>Where the reveal image comes from, for the panel readout.</summary>
        public string RevealSource
        {
            get
            {
                var tex = RevealTexture;
                if (tex == null) return "no image";
                bool fromFile = tex == loadedRevealTexture && loadedRevealTexture != null;
                string name = fromFile ? Path.GetFileName(loadedRevealPath) : tex.name;
                return $"{name} ({tex.width}x{tex.height})";
            }
        }

        static Texture2D LoadImageFile(string path)
        {
            var candidates = new List<string>();
            if (Path.IsPathRooted(path)) candidates.Add(path);
            else
            {
                candidates.Add(Path.Combine(Application.streamingAssetsPath, path));
                // Editor convenience: "Assets/..." relative to the project folder.
                candidates.Add(Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", path));
            }

            foreach (string file in candidates)
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true)
                    {
                        name = Path.GetFileName(file),
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                    };
                    if (tex.LoadImage(File.ReadAllBytes(file))) return tex;
                    Destroy(tex);
                    Debug.LogWarning($"[FluidSimulation] {file} is not a PNG or JPG image.");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[FluidSimulation] Could not load reveal image {file}: {e.Message}");
                }
            }
            Debug.LogWarning($"[FluidSimulation] Reveal image \"{path}\" not found; using the built-in image.");
            return null;
        }

        /// <summary>Bumped whenever the palette changes, so previews know to redraw.</summary>
        public int PaletteVersion { get; private set; }

        /// <summary>Replace the palette. Colours are clamped to 0..1 and made opaque.</summary>
        public void SetPalette(bool use, Color[] colors, float secondsPerColor, bool loop)
        {
            usePalette = use;
            palette = CleanPalette(colors);
            paletteSecondsPerColor = Mathf.Clamp(secondsPerColor, 0.1f, 10f);
            paletteLoop = loop;
            PaletteVersion++;
        }

        Color[] CleanPalette(Color[] colors)
        {
            if (colors == null || colors.Length == 0) return (Color[])palette.Clone();
            int n = Mathf.Min(colors.Length, MaxPaletteColors);
            var clean = new Color[n];
            for (int i = 0; i < n; i++)
                clean[i] = new Color(Mathf.Clamp01(colors[i].r), Mathf.Clamp01(colors[i].g), Mathf.Clamp01(colors[i].b), 1f);
            return clean;
        }

        /// <summary>
        /// The palette colour a stroke shows <paramref name="strokeSeconds"/> after it
        /// began: colour 1 at 0 s, blending smoothly into each next colour.
        /// </summary>
        public Color PaletteColorAt(float strokeSeconds)
        {
            var pal = palette;
            if (pal == null || pal.Length == 0) return Color.white;
            if (pal.Length == 1) return pal[0];

            float x = Mathf.Max(strokeSeconds, 0f) / Mathf.Max(paletteSecondsPerColor, 0.01f);
            int n = pal.Length;
            if (paletteLoop)
            {
                x = Mathf.Repeat(x, n);
                int i = Mathf.FloorToInt(x) % n;
                return Color.Lerp(pal[i], pal[(i + 1) % n], Smooth(x - Mathf.Floor(x)));
            }
            x = Mathf.Min(x, n - 1);
            int a = Mathf.Min(Mathf.FloorToInt(x), n - 1);
            int b = Mathf.Min(a + 1, n - 1);
            return Color.Lerp(pal[a], pal[b], Smooth(x - a));
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);
        [Tooltip("Light the dye as a height field, producing the fine ridged detail.")]
        public bool shading = true;
        [Tooltip("Display gain. Dye peaks near 0.15, so it needs roughly 6x to reach " +
                 "full display range. Raise for a hotter, more blown-out look.")]
        [Range(1f, 20f)] public float exposure = 9f;
        public bool paused = false;

        [Header("Bloom")]
        public bool bloom = true;
        public float bloomIntensity = 0.8f;
        [Range(0f, 2f)] public float bloomThreshold = 0.6f;
        [Range(0f, 1f)] public float bloomSoftKnee = 0.7f;
        public int bloomResolution = 256;
        [Range(2, 10)] public int bloomIterations = 8;

        [Header("Sunrays")]
        public bool sunrays = true;
        [Range(0f, 2f)] public float sunraysWeight = 1f;
        public int sunraysResolution = 196;

        /// <summary>Resolution for one physical display.</summary>
        [System.Serializable]
        public class DisplayResolution
        {
            [Tooltip("Display index. 0 = display 1.")]
            public int display;
            [Tooltip("Width in pixels. 0 = the monitor's native width.")]
            public int width;
            [Tooltip("Height in pixels. 0 = the monitor's native height.")]
            public int height;
            [Tooltip("\"windowed\" or \"borderless\". Empty = default: display 1 follows " +
                     "windowMode, every other display is borderless.")]
            public string mode;

            public DisplayResolution Clone() =>
                new DisplayResolution { display = display, width = width, height = height, mode = mode };
        }

        /// <summary>Unity supports at most 8 displays.</summary>
        public const int MaxDisplays = 8;

        public const string ModeWindowed = "windowed";
        public const string ModeBorderless = "borderless";

        [Header("Displays")]
        [Tooltip("Resolution of each display, set independently. 0 x 0 means native. " +
                 "A display with no entry also runs at native.")]
        public DisplayResolution[] displayResolutions =
        {
            new DisplayResolution { display = 0 },
            new DisplayResolution { display = 1 },
            new DisplayResolution { display = 2 },
            new DisplayResolution { display = 3 },
        };

        /// <summary>How the fluid window presents itself. This decides whether a
        /// resolution change is actually visible.</summary>
        public enum WindowMode
        {
            /// <summary>A normal resizable window. Screen.SetResolution resizes it.</summary>
            Windowed = 0,
            /// <summary>Borderless window filling the monitor. The window stays at the
            /// monitor's native size; a requested resolution is only rendered then
            /// upscaled, so the change is not visible. This is Unity's default.</summary>
            Borderless = 1,
            /// <summary>
            /// Takes over the display and switches its video mode.
            /// NOT offered in the control panel: Windows minimises an exclusive
            /// fullscreen window the moment it loses focus, which happens on every
            /// click on the control screen. Borderless looks identical without that.
            /// </summary>
            ExclusiveFullscreen = 2,
        }

        /// <summary>Highest window mode the UI and config accept (Borderless).</summary>
        public const int MaxSelectableWindowMode = 1;

        [Tooltip("Borderless keeps the window at the monitor's native size and merely " +
                 "upscales, so resolution changes are invisible. Use Windowed or " +
                 "Exclusive Fullscreen for a resolution change to take real effect.")]
        public WindowMode windowMode = WindowMode.Windowed;

        [Tooltip("Also drive the real window/display, not just the internal render " +
                 "target. No effect inside the editor, where the Game view owns its size.")]
        public bool resizeWindowOnApply = true;

        [Tooltip("Display indices the fluid spans, left to right. 0 = display 1. " +
                 "For a control screen on display 1 and a 3-screen wall use 1,2,3.")]
        public int[] fluidDisplays = { 1, 2, 3 };

        [Tooltip("How many of fluidDisplays the fluid actually uses, counted from the left. " +
                 "0 = all of them. Screens left out go black. Switchable live from the panel.")]
        public int activeScreens = 0;

        [Header("Interaction")]
        [Tooltip("React to the mouse simply moving over the fluid screen, with no button " +
                 "held. This is how a touch panel already behaves - a finger on the glass " +
                 "is enough - so it keeps mouse and touch consistent. Turn off to require " +
                 "a held left button.")]
        public bool drawOnHover = true;

        [Header("Attract mode")]
        [Tooltip("When nobody is touching a fluid screen, virtual fingers wander across " +
                 "the wall so it never sits black. A real touch takes over instantly.")]
        public bool idleMode = true;
        [Tooltip("Seconds without a touch on a fluid screen before attract mode resumes. " +
                 "The app starts in attract mode.")]
        [Range(0f, 120f)] public float idleDelay = 8f;
        [Tooltip("How many virtual fingers stir the fluid while idle.")]
        [Range(1, 8)] public int idlePointers = 3;
        [Tooltip("How fast the virtual fingers wander.")]
        [Range(0.01f, 1f)] public float idleSpeed = 0.12f;
        [Tooltip("Keep the virtual fingers running while visitors touch the wall, so " +
                 "real strokes mix with the attract motion. Off = they step aside the " +
                 "moment someone touches, and return after idleDelay.")]
        public bool attractDuringInteraction = true;

        [Header("Startup")]
        public int initialSplats = 20;

        [Header("Shaders")]
        public Shader clearShader;
        public Shader splatShader;
        public Shader advectionShader;
        public Shader divergenceShader;
        public Shader curlShader;
        public Shader vorticityShader;
        public Shader pressureShader;
        public Shader gradientSubtractShader;
        public Shader displayShader;
        public Shader bloomPrefilterShader;
        public Shader bloomBlurShader;
        public Shader bloomFinalShader;
        public Shader sunraysMaskShader;
        public Shader sunraysShader;
        public Shader blurShader;

        [Header("Output")]
        [Tooltip("RawImage that displays the fluid. Stretch it to fill the screen.")]
        public RawImage displayImage;

        Material clearMat, splatMat, advectionMat, divergenceMat, curlMat,
                 vorticityMat, pressureMat, gradientSubtractMat, displayMat,
                 bloomPrefilterMat, bloomBlurMat, bloomFinalMat,
                 sunraysMaskMat, sunraysMat, blurMat;

        class DoubleFBO
        {
            public RenderTexture read;
            public RenderTexture write;
            public void Swap()
            {
                var t = read;
                read = write;
                write = t;
            }
        }

        DoubleFBO velocity, dye, pressureFBO;
        RenderTexture divergenceRT, curlRT, displayRT;
        RenderTexture bloomRT, sunraysRT, sunraysTempRT;
        readonly List<RenderTexture> bloomMips = new List<RenderTexture>();

        Vector2Int simSize, dyeSize;
        Vector2 texelSizeSim;

        /// <summary>
        /// One interacting finger (or the mouse). The original demo keeps an array of
        /// these so several touches each carry their own stroke colour and velocity,
        /// which is what makes multi-touch look right rather than one shared smear.
        /// </summary>
        class FluidPointer
        {
            public int id;
            public bool down;
            public bool wasDown;
            public Vector2 uv;
            public Vector2 lastSplatUv;
            public Color color;
            public float strokeStart;   // Time.unscaledTime the current stroke began
            public Vector2 rippleUv;    // last position seen by Water's ripples
            public bool rippleSeen;
            public Vector2 emitUv;      // last position seen by the particle emitter
            public Vector2 emitVel;     // smoothed velocity, wall UV per second
            public bool emitSeen;
        }

        const int MousePointerId = -1;
        readonly List<FluidPointer> pointers = new List<FluidPointer>();
        int lastScreenW, lastScreenH;
        int pendingScreenW, pendingScreenH;
        float screenSettlesAt;
        const float ScreenSettleDelay = 0.25f;

        float colorUpdateTimer;
        float stepAccumulator;

        static readonly int TexelSizeID = Shader.PropertyToID("_TexelSize");

        /// <summary>Simulation steps executed in the last rendered frame.</summary>
        public int StepsLastFrame { get; private set; }

        /// <summary>Resolution of the whole wall, e.g. "5760 x 1080".</summary>
        public string CurrentDisplaySize =>
            displayRT != null ? $"{displayRT.width} x {displayRT.height}" : "-";

        /// <summary>The composited fluid. Each wall screen draws its own slice of this.</summary>
        public RenderTexture OutputTexture => displayRT;

        // ---------- persisted settings ----------

        public const string ConfigFileName = "fluid-config.json";
        public const string PanelFileName = "fluid-settings.json";

        /// <summary>
        /// Deployment config, shipped beside the executable in StreamingAssets so it can
        /// be edited with a text editor before launch without rebuilding.
        /// </summary>
        public static string ConfigPath =>
            Path.Combine(Application.streamingAssetsPath, ConfigFileName);

        /// <summary>Used when StreamingAssets is not writable (read-only install dir).</summary>
        public static string FallbackConfigPath =>
            Path.Combine(Application.persistentDataPath, ConfigFileName);

        /// <summary>Path the settings were last read from or written to.</summary>
        public string ActiveConfigPath { get; private set; } = "";

        /// <summary>
        /// Everything the control panel tunes. Kept in its own file so that
        /// fluid-config.json stays the short, hand-edited display config.
        /// </summary>
        /// <summary>See <see cref="Settings"/> for why this is a class.</summary>
        [System.Serializable]
        public class PanelSettings
        {
            public int simResolution, dyeResolution, pressureIterations;
            public float densityDissipation, velocityDissipation, pressure, curl;
            public float splatRadius, splatForce, colorUpdateSpeed, exposure;
            public bool colorful, shading;
            public bool bloom;
            public float bloomIntensity, bloomThreshold, bloomSoftKnee;
            public bool sunrays;
            public float sunraysWeight;
            public float simulationRate, simulationSpeed;
            public bool drawOnHover;
            public bool idleMode;
            public float idleDelay;
            public int idlePointers;
            public float idleSpeed;
            public bool attractDuringInteraction;
            public bool usePalette;
            public Color[] palette;
            public float paletteSecondsPerColor;
            public bool paletteLoop;
            public int animationStyle;
            public WaterSettings water;
            public ShootingStarSettings shootingStars;
            public CometSettings comet;
            public NeonSettings neon;
            public bool revealMode;
            public string revealImagePath;
            public int revealFit;
            public float spotlightSize;
            public float spotlightSoftness;
            public bool spotlightFollowsAttract;
            public float revealBackground;
            public float revealFluidOpacity;
        }

        public PanelSettings CapturePanelSettings() => new PanelSettings
        {
            simResolution = simResolution,
            dyeResolution = dyeResolution,
            pressureIterations = pressureIterations,
            densityDissipation = densityDissipation,
            velocityDissipation = velocityDissipation,
            pressure = pressure,
            curl = curl,
            splatRadius = splatRadius,
            splatForce = splatForce,
            colorUpdateSpeed = colorUpdateSpeed,
            exposure = exposure,
            colorful = colorful,
            shading = shading,
            bloom = bloom,
            bloomIntensity = bloomIntensity,
            bloomThreshold = bloomThreshold,
            bloomSoftKnee = bloomSoftKnee,
            sunrays = sunrays,
            sunraysWeight = sunraysWeight,
            simulationRate = simulationRate,
            simulationSpeed = simulationSpeed,
            drawOnHover = drawOnHover,
            idleMode = idleMode,
            idleDelay = idleDelay,
            idlePointers = idlePointers,
            idleSpeed = idleSpeed,
            attractDuringInteraction = attractDuringInteraction,
            usePalette = usePalette,
            palette = (Color[])palette.Clone(),
            paletteSecondsPerColor = paletteSecondsPerColor,
            paletteLoop = paletteLoop,
            animationStyle = (int)animationStyle,
            water = water.Clone(),
            shootingStars = shootingStars.Clone(),
            comet = comet.Clone(),
            neon = neon.Clone(),
            revealMode = revealMode,
            revealImagePath = revealImagePath,
            revealFit = (int)revealFit,
            spotlightSize = spotlightSize,
            spotlightSoftness = spotlightSoftness,
            spotlightFollowsAttract = spotlightFollowsAttract,
            revealBackground = revealBackground,
            revealFluidOpacity = revealFluidOpacity,
        };

        public void ApplyPanelSettings(PanelSettings p)
        {
            simResolution = Mathf.Max(p.simResolution, 16);
            dyeResolution = Mathf.Max(p.dyeResolution, 32);
            pressureIterations = Mathf.Clamp(p.pressureIterations, 1, 50);
            densityDissipation = p.densityDissipation;
            velocityDissipation = p.velocityDissipation;
            pressure = Mathf.Clamp01(p.pressure);
            curl = p.curl;
            splatRadius = Mathf.Clamp(p.splatRadius, 0.01f, 1f);
            splatForce = p.splatForce;
            colorUpdateSpeed = p.colorUpdateSpeed;
            exposure = Mathf.Clamp(p.exposure, 1f, 20f);
            colorful = p.colorful;
            shading = p.shading;
            bloom = p.bloom;
            bloomIntensity = p.bloomIntensity;
            bloomThreshold = p.bloomThreshold;
            bloomSoftKnee = p.bloomSoftKnee;
            sunrays = p.sunrays;
            sunraysWeight = p.sunraysWeight;
            simulationRate = Mathf.Max(p.simulationRate, 1f);
            simulationSpeed = Mathf.Max(p.simulationSpeed, 0.01f);
            drawOnHover = p.drawOnHover;
            idleMode = p.idleMode;
            idleDelay = Mathf.Clamp(p.idleDelay, 0f, 120f);
            idlePointers = Mathf.Clamp(p.idlePointers, 1, 8);
            idleSpeed = Mathf.Clamp(p.idleSpeed, 0.01f, 1f);
            attractDuringInteraction = p.attractDuringInteraction;
            SetPalette(p.usePalette, p.palette, p.paletteSecondsPerColor, p.paletteLoop);
            SetAnimationStyle((AnimationStyle)p.animationStyle);
            if (p.water != null) { water = p.water.Clone(); water.Clamp(); }
            if (p.shootingStars != null) { shootingStars = p.shootingStars.Clone(); shootingStars.Clamp(); }
            if (p.comet != null) { comet = p.comet.Clone(); comet.Clamp(); }
            if (p.neon != null) { neon = p.neon.Clone(); neon.Clamp(); }
            revealMode = p.revealMode;
            revealImagePath = p.revealImagePath ?? "";
            revealFit = (RevealFit)Mathf.Clamp(p.revealFit, 0, 3);
            spotlightSize = Mathf.Clamp(p.spotlightSize, 0.03f, 0.6f);
            spotlightSoftness = Mathf.Clamp(p.spotlightSoftness, 0.05f, 1f);
            spotlightFollowsAttract = p.spotlightFollowsAttract;
            revealBackground = Mathf.Clamp01(p.revealBackground);
            revealFluidOpacity = Mathf.Clamp01(p.revealFluidOpacity);
        }

        /// <summary>
        /// Save just the palette into fluid-settings.json - the colour manager's Save.
        /// The file's other values are kept exactly as they are on disk, so saving
        /// colours never also commits whatever the sliders happen to be set to.
        /// </summary>
        public bool SavePaletteSettings() => SavePanelPatch(p =>
        {
            p.usePalette = usePalette;
            p.palette = (Color[])palette.Clone();
            p.paletteSecondsPerColor = paletteSecondsPerColor;
            p.paletteLoop = paletteLoop;
        });

        /// <summary>Save just the animation style into fluid-settings.json - the style picker's Save.</summary>
        public bool SaveAnimationStyleSetting() => SavePanelPatch(p =>
        {
            p.animationStyle = (int)animationStyle;
            p.water = water.Clone();
            p.shootingStars = shootingStars.Clone();
            p.comet = comet.Clone();
            p.neon = neon.Clone();
        });

        /// <summary>
        /// Change only some keys of fluid-settings.json: the file is read, patched and
        /// written back, so every other saved value stays exactly as it is on disk.
        /// </summary>
        bool SavePanelPatch(System.Action<PanelSettings> patch)
        {
            foreach (string dir in new[] { StreamingDir, FallbackDir })
            {
                string path = Path.Combine(dir, PanelFileName);
                var p = CapturePanelSettings();
                if (TryRead(path, out string existing))
                {
                    try { JsonUtility.FromJsonOverwrite(existing, p); }
                    catch (System.Exception e) { Debug.LogWarning($"[FluidSimulation] Bad {path}: {e.Message}"); }
                }
                patch(p);
                if (TryWrite(path, JsonUtility.ToJson(p, true))) return true;
            }
            return false;
        }

        PanelSettings panelDefaults;

        /// <summary>
        /// Display configuration, read from fluid-config.json at startup.
        ///
        /// Deliberately display-only: resolution has to be decided before the graphics
        /// device is up, so it is a launch setting rather than something the control
        /// panel changes live. Simulation and appearance values stay on the component
        /// where the panel can tune them freely without touching any buffers.
        /// </summary>
        // A class, not a struct, so JsonUtility.FromJsonOverwrite can be used: it
        // replaces only the keys actually present in the file and leaves everything
        // else at the value it was pre-filled with. FromJson would instead reset any
        // missing key to zero/false, so a config written by an older build would
        // silently switch off every option added since.
        [System.Serializable]
        public class Settings
        {
            public int windowMode;
            public int controlDisplay = -1;   // -1 means "leave as configured"
            public int[] fluidDisplays;
            public int activeScreens;         // 0 = all of fluidDisplays
            public DisplayResolution[] displays;
        }

        Settings defaults;
        bool defaultsCaptured;

        /// <summary>Set by the control panel so its display index round-trips to the config.</summary>
        public int ControlDisplayIndex { get; set; } = 0;

        static DisplayResolution[] CloneResolutions(DisplayResolution[] src)
        {
            if (src == null) return new DisplayResolution[0];
            var copy = new DisplayResolution[src.Length];
            for (int i = 0; i < src.Length; i++) copy[i] = src[i]?.Clone();
            return copy;
        }

        // Deep copies throughout: the captured defaults must not alias the live arrays,
        // or editing a resolution at runtime would silently rewrite "Reset to defaults".
        public Settings CaptureSettings() => new Settings
        {
            windowMode = (int)windowMode,
            controlDisplay = ControlDisplayIndex,
            fluidDisplays = (int[])fluidDisplays.Clone(),
            activeScreens = activeScreens,
            displays = CloneResolutions(displayResolutions),
        };

        public void ApplySettings(Settings s)
        {
            if (s.displays != null && s.displays.Length > 0)
            {
                var clean = new List<DisplayResolution>();
                var seen = new HashSet<int>();
                foreach (var d in s.displays)
                {
                    if (d == null || d.display < 0) continue;
                    // A display listed twice keeps its first entry, the one FindResolution
                    // would have used anyway, so a copy-pasted block cannot win silently.
                    if (!seen.Add(d.display))
                    {
                        Debug.LogWarning($"[FluidSimulation] Display {d.display} is listed more than " +
                                         "once in the config; using the first entry.");
                        continue;
                    }
                    var c = d.Clone();
                    c.width = Mathf.Max(c.width, 0);
                    c.height = Mathf.Max(c.height, 0);
                    // Width without height (or vice versa) is meaningless; treat as native.
                    if (c.width == 0 || c.height == 0) { c.width = 0; c.height = 0; }
                    c.mode = NormaliseMode(c.mode);
                    clean.Add(c);
                }
                displayResolutions = clean.ToArray();
            }

            if (s.windowMode > MaxSelectableWindowMode)
            {
                Debug.LogWarning($"[FluidSimulation] windowMode {s.windowMode} (exclusive " +
                                 "fullscreen) minimises whenever the control screen is " +
                                 "clicked. Using Borderless instead, which looks the same.");
            }
            windowMode = (WindowMode)Mathf.Clamp(s.windowMode, 0, MaxSelectableWindowMode);
            // A per-display mode on display 1 is the more specific setting.
            var main = FindResolution(0);
            if (main != null && !string.IsNullOrEmpty(main.mode))
                windowMode = main.mode == ModeWindowed ? WindowMode.Windowed : WindowMode.Borderless;

            if (s.fluidDisplays != null && s.fluidDisplays.Length > 0)
                fluidDisplays = (int[])s.fluidDisplays.Clone();
            activeScreens = Mathf.Max(s.activeScreens, 0);
            if (s.controlDisplay >= 0) ControlDisplayIndex = s.controlDisplay;
        }

        /// <summary>Write the current settings to the config file. Returns false if neither
        /// location could be written, in which case the reason is logged.</summary>
        /// <summary>
        /// Write both the display config and every control-panel value. Returns false
        /// if nothing could be written, in which case the reason is logged.
        /// </summary>
        public bool SaveSettings()
        {
            string displayJson = JsonUtility.ToJson(CaptureSettings(), true);
            string panelJson = JsonUtility.ToJson(CapturePanelSettings(), true);

            bool wroteAny = false;

            foreach (string dir in new[] { StreamingDir, FallbackDir })
            {
                bool display = TryWrite(Path.Combine(dir, ConfigFileName), displayJson);
                bool panel = TryWrite(Path.Combine(dir, PanelFileName), panelJson);

                if (display || panel)
                {
                    ActiveConfigPath = Path.Combine(dir, ConfigFileName);
                    wroteAny = true;
                    break;
                }
            }
            return wroteAny;
        }

        /// <summary>
        /// Write fluid-config.json only - the window manager's Save. Unlike SaveSettings
        /// this leaves fluid-settings.json alone, so saving a display layout never also
        /// commits whatever the sliders happen to be set to.
        /// </summary>
        public bool SaveDisplayConfig()
        {
            string json = JsonUtility.ToJson(CaptureSettings(), true);
            foreach (string dir in new[] { StreamingDir, FallbackDir })
            {
                string path = Path.Combine(dir, ConfigFileName);
                if (TryWrite(path, json))
                {
                    ActiveConfigPath = path;
                    return true;
                }
            }
            return false;
        }

        static string StreamingDir => Application.streamingAssetsPath;
        static string FallbackDir => Application.persistentDataPath;

        static bool TryWrite(string path, string json)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, json);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[FluidSimulation] Could not write {path}: {e.Message}");
                return false;
            }
        }

        /// <summary>Read both config files. Returns false if neither was found.</summary>
        public bool LoadSettings()
        {
            bool any = false;

            // Display config: StreamingAssets wins, so a deployed machine's hand-edited
            // file is not shadowed by a stale copy in the fallback location.
            foreach (string path in new[] { ConfigPath, FallbackConfigPath })
            {
                if (!TryRead(path, out string json)) continue;
                try
                {
                    // Seed with the current values so keys absent from the file keep
                    // their default rather than being zeroed.
                    var s = CaptureSettings();
                    JsonUtility.FromJsonOverwrite(json, s);
                    ApplySettings(s);
                    ActiveConfigPath = path;
                    any = true;
                    break;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[FluidSimulation] Bad {path}: {e.Message}");
                }
            }

            foreach (string dir in new[] { StreamingDir, FallbackDir })
            {
                string path = Path.Combine(dir, PanelFileName);
                if (!TryRead(path, out string json)) continue;
                try
                {
                    var p = CapturePanelSettings();
                    JsonUtility.FromJsonOverwrite(json, p);
                    ApplyPanelSettings(p);
                    any = true;
                    break;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[FluidSimulation] Bad {path}: {e.Message}");
                }
            }

            return any;
        }

        static bool TryRead(string path, out string json)
        {
            json = null;
            try
            {
                if (!File.Exists(path)) return false;
                json = File.ReadAllText(path);
                return !string.IsNullOrWhiteSpace(json);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[FluidSimulation] Could not read {path}: {e.Message}");
                return false;
            }
        }

        /// <summary>Delete both config files and go back to the values authored in the scene.</summary>
        public void ResetToDefaults()
        {
            foreach (string dir in new[] { StreamingDir, FallbackDir })
            {
                foreach (string name in new[] { ConfigFileName, PanelFileName })
                {
                    string path = Path.Combine(dir, name);
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[FluidSimulation] Could not delete {path}: {e.Message}");
                    }
                }
            }
            ActiveConfigPath = "";
            ApplySettings(defaults);
            ApplyPanelSettings(panelDefaults);
            ReinitFramebuffers();
        }

        public bool HasSavedSettings => File.Exists(ConfigPath) || File.Exists(FallbackConfigPath);

        /// <summary>
        /// Launch-time overrides, so an installation can be started from a script:
        ///   FluidSim.exe -fluid-width 1280 -fluid-height 720 -fluid-window windowed
        /// These feed the same fields the Apply button drives.
        /// </summary>
        void ApplyCommandLineOverrides()
        {
            string[] args;
            try { args = System.Environment.GetCommandLineArgs(); }
            catch { return; }

            int w = 0, h = 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                string key = args[i].ToLowerInvariant();
                string val = args[i + 1];

                if (key == "-fluid-width") int.TryParse(val, out w);
                else if (key == "-fluid-height") int.TryParse(val, out h);
                else if (key == "-fluid-window")
                {
                    switch (val.ToLowerInvariant())
                    {
                        case "windowed": windowMode = WindowMode.Windowed; break;
                        case "borderless": windowMode = WindowMode.Borderless; break;
                        case "fullscreen": windowMode = WindowMode.ExclusiveFullscreen; break;
                    }
                }
            }

            // Applies to every fluid screen, overriding their config entries.
            // Every screen that could be switched on, not only the ones in use now.
            if (w > 0 && h > 0)
            {
                for (int slot = 0; slot < AvailableFluidScreens; slot++)
                    SetResolutionEntry(fluidDisplays[slot], w, h);
            }
        }

        void Awake()
        {
            if (!defaultsCaptured)
            {
                defaults = CaptureSettings();
                panelDefaults = CapturePanelSettings();
                defaultsCaptured = true;
            }
            LoadSettings();
            ApplyCommandLineOverrides();

            // Before InitFramebuffers: native sizes of the secondary displays are only
            // known once they are active, and the wall size is built from them.
            ActivateDisplays();

            clearMat = new Material(clearShader);
            splatMat = new Material(splatShader);
            advectionMat = new Material(advectionShader);
            divergenceMat = new Material(divergenceShader);
            curlMat = new Material(curlShader);
            vorticityMat = new Material(vorticityShader);
            pressureMat = new Material(pressureShader);
            gradientSubtractMat = new Material(gradientSubtractShader);
            displayMat = new Material(displayShader);
            bloomPrefilterMat = new Material(bloomPrefilterShader);
            bloomBlurMat = new Material(bloomBlurShader);
            bloomFinalMat = new Material(bloomFinalShader);
            sunraysMaskMat = new Material(sunraysMaskShader);
            sunraysMat = new Material(sunraysShader);
            blurMat = new Material(blurShader);

            if (cometShader != null) comets = new FluidComets(cometShader);
            if (rippleShader != null) rippleMat = new Material(rippleShader);
            if (neonEdgeShader != null) neonEdgeMat = new Material(neonEdgeShader);
            if (waterCurrentShader == null) waterCurrentShader = Shader.Find("Hidden/Fluid/WaterCurrent");
            if (waterCurrentShader != null) waterCurrentMat = new Material(waterCurrentShader);
            if (particleUpdateShader != null && particleRenderShader != null)
                particles = new FluidParticles(particleUpdateShader, particleRenderShader);
            else
                Debug.LogWarning("[FluidSimulation] Particle shaders not assigned; Water, Shooting Stars " +
                                 "and Comets will draw without their particles.");

            InitFramebuffers();
        }

        void Start()
        {
            // Push the configured window mode to the real window on launch. This must
            // run even when no explicit resolution is set: otherwise the player keeps
            // whatever mode Player Settings booted it in (FullScreenWindow by default)
            // and the config's windowMode is silently ignored. A borderless-fullscreen
            // window minimises when focus moves to the control window on the second
            // display, which is what made the app keep minimising.
            // This also applies each display's own configured resolution.
            if (resizeWindowOnApply) ApplyDisplayResolutions();

            MultipleSplats(initialSplats);
        }

        void OnDestroy()
        {
            ReleaseFramebuffers();
            particles?.Release();
            if (loadedRevealTexture != null) Destroy(loadedRevealTexture);
        }

        // ---------- framebuffers ----------

        static RenderTextureFormat Pick(RenderTextureFormat preferred)
        {
            return SystemInfo.SupportsRenderTextureFormat(preferred)
                ? preferred
                : RenderTextureFormat.ARGBHalf;
        }

        RenderTexture CreateRT(int w, int h, RenderTextureFormat format, FilterMode filter = FilterMode.Bilinear)
        {
            var rt = new RenderTexture(Mathf.Max(w, 1), Mathf.Max(h, 1), 0, Pick(format), RenderTextureReadWrite.Linear)
            {
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        DoubleFBO CreateDoubleFBO(int w, int h, RenderTextureFormat format, FilterMode filter = FilterMode.Bilinear)
        {
            return new DoubleFBO { read = CreateRT(w, h, format, filter), write = CreateRT(w, h, format, filter) };
        }

        /// <summary>Screens listed in fluidDisplays - the most the fluid can span.</summary>
        public int AvailableFluidScreens => Mathf.Max(fluidDisplays != null ? fluidDisplays.Length : 1, 1);

        /// <summary>
        /// Number of screens the fluid is spread across right now (at least 1): the
        /// first <see cref="activeScreens"/> entries of fluidDisplays. Every wall
        /// calculation walks slots 0..FluidDisplayCount-1, so the unused screens simply
        /// drop out of the wall.
        /// </summary>
        public int FluidDisplayCount =>
            activeScreens <= 0 ? AvailableFluidScreens : Mathf.Clamp(activeScreens, 1, AvailableFluidScreens);

        /// <summary>
        /// Switch how many screens the fluid spans, live. The wall's aspect changes, so
        /// every buffer is rebuilt (deferred to the next Update, like any UI-driven
        /// rebuild) and FluidDisplayWall re-slices the screens on the same frame.
        /// </summary>
        public void SetActiveScreens(int count)
        {
            int n = Mathf.Clamp(count, 1, AvailableFluidScreens);
            if (n == FluidDisplayCount) return;
            activeScreens = n;
            ReinitFramebuffers();
        }

        DisplayResolution FindResolution(int display)
        {
            if (displayResolutions == null) return null;
            for (int i = 0; i < displayResolutions.Length; i++)
                if (displayResolutions[i] != null && displayResolutions[i].display == display)
                    return displayResolutions[i];
            return null;
        }

        /// <summary>A copy of one display's configured entry, or null if it has none.</summary>
        public DisplayResolution ResolutionOf(int display) => FindResolution(display)?.Clone();

        /// <summary>The monitor's own resolution, or zero if it is not connected.</summary>
        public Vector2Int NativeSizeOf(int display)
        {
            if (display < 0 || display >= Display.displays.Length) return Vector2Int.zero;
            var d = Display.displays[display];
            return new Vector2Int(d.systemWidth, d.systemHeight);
        }

        /// <summary>Pixel size of one display, honouring its own configured resolution.</summary>
        public Vector2Int SizeOfDisplay(int display)
        {
            var r = FindResolution(display);
            if (r != null && r.width > 0 && r.height > 0)
                return new Vector2Int(r.width, r.height);

            // Native. Secondary displays report their monitor size; the main window
            // (and every display inside the editor, where only one exists) falls back
            // to the current screen.
            if (display > 0 && display < Display.displays.Length)
            {
                var d = Display.displays[display];
                if (d.systemWidth > 0 && d.systemHeight > 0)
                    return new Vector2Int(d.systemWidth, d.systemHeight);
            }
            return new Vector2Int(Mathf.Max(Screen.width, 1), Mathf.Max(Screen.height, 1));
        }

        /// <summary>
        /// Pixel size of the whole wall: the fluid screens' widths added together, and
        /// the tallest of their heights. Screens may differ in resolution, so the wall
        /// is not simply N times one screen.
        /// </summary>
        Vector2Int GetDisplaySize()
        {
            int w = 0, h = 0;
            for (int slot = 0; slot < FluidDisplayCount; slot++)
            {
                var s = SizeOfDisplay(fluidDisplays[slot]);
                w += s.x;
                h = Mathf.Max(h, s.y);
            }
            return new Vector2Int(Mathf.Max(w, 1), Mathf.Max(h, 1));
        }

        /// <summary>
        /// Where a wall slot sits horizontally, as (start, width) in 0..1. Each slice is
        /// proportional to its screen's pixel width, so a 2560-wide screen between two
        /// 1920-wide ones gets a wider slice and the fluid keeps a uniform scale across
        /// the seams instead of stretching on the bigger panel.
        /// </summary>
        public Vector2 SlotSpan(int slot)
        {
            float total = GetDisplaySize().x;
            float start = 0f;
            for (int i = 0; i < slot; i++) start += SizeOfDisplay(fluidDisplays[i]).x;
            float width = SizeOfDisplay(fluidDisplays[slot]).x;
            return new Vector2(start / total, width / total);
        }

        /// <summary>The fluid screens' sizes, e.g. "1920x1080 | 2560x1440 | 1920x1080".</summary>
        public string DescribeWall()
        {
            var parts = new string[FluidDisplayCount];
            for (int slot = 0; slot < parts.Length; slot++)
            {
                var s = SizeOfDisplay(fluidDisplays[slot]);
                parts[slot] = s.x + "x" + s.y;
            }
            return string.Join(" | ", parts);
        }

        // Grid resolutions follow the WALL aspect, not one screen's. Using the screen
        // aspect on a 3-wide wall would squash the simulation horizontally.
        Vector2Int GetResolution(int resolution)
        {
            var wall = GetDisplaySize();
            float aspectRatio = (float)wall.x / Mathf.Max(wall.y, 1);
            if (aspectRatio < 1f) aspectRatio = 1f / aspectRatio;

            int min = Mathf.Max(resolution, 1);
            int max = Mathf.RoundToInt(resolution * aspectRatio);

            return wall.x > wall.y
                ? new Vector2Int(max, min)
                : new Vector2Int(min, max);
        }

        /// <summary>Rebuild just the output buffer after a render-resolution change.</summary>
        public void ApplyRenderResolution()
        {
            if (displayImage != null) displayImage.texture = null;
            DestroyRT(ref displayRT);
            var size = GetDisplaySize();
            displayRT = CreateRT(size.x, size.y, RenderTextureFormat.ARGBHalf);
        }

        /// <summary>
        /// Set an arbitrary resolution for the fluid screen. Rebuilds the render target
        /// and, in a build, resizes the window itself. In the editor only the render
        /// target changes, because the Game view owns its own size.
        /// </summary>
        int pendingWidth, pendingHeight;
        bool resolutionChangeQueued;

        /// <summary>
        /// Request a new fluid resolution.
        ///
        /// The work is deferred to the start of the next Update rather than run
        /// immediately. This is normally called from a UI button callback, which fires
        /// in the middle of the frame while the Canvas still holds and is about to draw
        /// the current render target - tearing that target down right then crashes the
        /// graphics driver.
        /// </summary>
        public void ApplyResolution(int width, int height)
        {
            pendingWidth = Mathf.Max(width, 0);
            pendingHeight = Mathf.Max(height, 0);
            resolutionChangeQueued = true;
        }

        void ApplyQueuedResolution()
        {
            resolutionChangeQueued = false;

            // Applies to every fluid screen; the control screen keeps its own entry.
            for (int slot = 0; slot < AvailableFluidScreens; slot++)
                SetResolutionEntry(fluidDisplays[slot], pendingWidth, pendingHeight);

            // The wall's aspect may have changed, and the dye/velocity grids follow it,
            // so everything is rebuilt rather than just the output buffer.
            ReinitFramebuffersNow();

            if (resizeWindowOnApply) ApplyDisplayResolutions();
        }

        void SetResolutionEntry(int display, int width, int height)
        {
            var r = FindResolution(display);
            if (r == null)
            {
                r = new DisplayResolution { display = display };
                var list = new List<DisplayResolution>(displayResolutions ?? new DisplayResolution[0]) { r };
                displayResolutions = list.ToArray();
            }
            r.width = width;
            r.height = height;
        }

        /// <summary>
        /// Push each display's configured resolution onto the real hardware.
        ///
        /// Which call does the work depends on the display:
        ///  - display 0 is the player's main window, driven by Screen.SetResolution.
        ///    It only genuinely resizes in Windowed; in Borderless the window stays at
        ///    the monitor size and the requested resolution is rendered then upscaled.
        ///  - every other display is driven by Display.SetRenderingResolution, since
        ///    Screen.SetResolution only ever addresses the main one. Unity always opens
        ///    secondary displays as a window filling their monitor, so for those this
        ///    sets the render resolution, which is then scaled to the panel.
        /// </summary>
        void ApplyDisplayResolutions()
        {
            var main = SizeOfDisplay(0);
            Screen.SetResolution(main.x, main.y, ToFullScreenMode(windowMode));

            for (int i = 1; i < Display.displays.Length && i < MaxDisplays; i++)
                ApplySecondaryDisplay(i);
        }

        // Secondary displays this session has shrunk into a window, so switching one
        // back to borderless knows it has to be re-expanded to fill its monitor.
        readonly bool[] secondaryWindowed = new bool[MaxDisplays];

        /// <summary>
        /// Size one secondary display. Unity opens these as a frameless window filling
        /// the monitor, and there is no way to give them a title bar. "Windowed" is
        /// therefore a frameless window of the requested size centred on its monitor,
        /// set with Display.SetParams; "borderless" fills the monitor and the requested
        /// size becomes the render resolution, scaled to the panel.
        /// </summary>
        void ApplySecondaryDisplay(int i)
        {
            var d = Display.displays[i];
            if (!d.active) return;

            var r = FindResolution(i);
            bool custom = r != null && r.width > 0 && r.height > 0;
            bool windowed = ModeOf(i) == WindowMode.Windowed;

            if (windowed && custom && TryGetMonitorArea(i, out RectInt area))
            {
                int w = Mathf.Min(r.width, area.width);
                int h = Mathf.Min(r.height, area.height);
                d.SetParams(w, h, area.x + (area.width - w) / 2, area.y + (area.height - h) / 2);
                secondaryWindowed[i] = true;
            }
            else if (secondaryWindowed[i] && TryGetMonitorArea(i, out RectInt full))
            {
                d.SetParams(d.systemWidth, d.systemHeight, full.x, full.y);
                secondaryWindowed[i] = false;
            }

            if (custom) d.SetRenderingResolution(r.width, r.height);
            else if (d.systemWidth > 0 && d.systemHeight > 0) d.SetRenderingResolution(d.systemWidth, d.systemHeight);
        }

        static readonly List<DisplayInfo> displayLayout = new List<DisplayInfo>();

        /// <summary>
        /// The desktop area of the monitor behind secondary display i. Unity does not
        /// link Display.displays to Screen.GetDisplayLayout, so this pairs them the way
        /// both are enumerated: the main window's monitor is display 1, and the other
        /// monitors follow in the order Windows lists them. A monitor of the same size
        /// as display i is preferred, which settles the common mixed-size cases.
        /// </summary>
        bool TryGetMonitorArea(int i, out RectInt area)
        {
            area = default;
            displayLayout.Clear();
            try { Screen.GetDisplayLayout(displayLayout); }
            catch { return false; }

            var main = Screen.mainWindowDisplayInfo;
            var others = new List<DisplayInfo>();
            foreach (var info in displayLayout)
                if (!(info.name == main.name && info.workArea.Equals(main.workArea))) others.Add(info);

            int k = i - 1;
            if (k < 0 || k >= others.Count) return false;

            var d = Display.displays[i];
            var pick = others[k];
            if (pick.width != d.systemWidth || pick.height != d.systemHeight)
            {
                foreach (var info in others)
                    if (info.width == d.systemWidth && info.height == d.systemHeight) { pick = info; break; }
            }

            area = pick.workArea;
            return area.width > 0 && area.height > 0;
        }

        /// <summary>
        /// Windowed or borderless for one display. An entry's own mode wins; otherwise
        /// display 1 follows windowMode and the rest are borderless, which is how Unity
        /// opens them.
        /// </summary>
        public WindowMode ModeOf(int display)
        {
            var r = FindResolution(display);
            if (r != null && !string.IsNullOrEmpty(r.mode))
                return r.mode == ModeWindowed ? WindowMode.Windowed : WindowMode.Borderless;
            if (display == 0) return windowMode == WindowMode.Windowed ? WindowMode.Windowed : WindowMode.Borderless;
            return WindowMode.Borderless;
        }

        static string NormaliseMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return "";
            string m = mode.Trim().ToLowerInvariant();
            if (m == ModeWindowed || m == "window" || m == "0") return ModeWindowed;
            if (m == ModeBorderless || m == "fullscreen" || m == "1") return ModeBorderless;
            Debug.LogWarning($"[FluidSimulation] Unknown display mode \"{mode}\"; use windowed or borderless.");
            return "";
        }

        /// <summary>Displays the app is using: the control screen plus the fluid screens.</summary>
        public int DisplaysInUse => Mathf.Clamp(1 + FluidDisplayCount, 2, MaxDisplays);

        /// <summary>Monitors Windows reports. Always 1 inside the editor.</summary>
        public int ConnectedDisplays => Display.displays.Length;

        /// <summary>
        /// The fluid screen list after a change to how many displays are in use:
        /// displays 1..count, the control screen excluded. Screens already listed keep
        /// their order, so a hand-tuned left-to-right order survives; missing ones are
        /// appended; screens beyond the count stay listed after them for next time.
        /// </summary>
        public int[] FluidPoolFor(int displayCount)
        {
            int n = Mathf.Clamp(displayCount, 2, MaxDisplays);
            var inUse = new List<int>();
            var spare = new List<int>();
            if (fluidDisplays != null)
            {
                foreach (int d in fluidDisplays)
                {
                    if (d < 0 || d >= MaxDisplays || d == ControlDisplayIndex) continue;
                    if (inUse.Contains(d) || spare.Contains(d)) continue;
                    (d < n ? inUse : spare).Add(d);
                }
            }
            for (int d = 0; d < n; d++)
                if (d != ControlDisplayIndex && !inUse.Contains(d)) inUse.Add(d);
            inUse.AddRange(spare);
            return inUse.ToArray();
        }

        /// <summary>
        /// Apply a layout from the window manager to the running app and persist it:
        /// how many displays are in use, and each one's resolution and mode. Displays
        /// newly brought into use are activated on the spot; the wall is rebuilt on the
        /// next Update. Returns false if fluid-config.json could not be written (the
        /// layout is still applied).
        /// </summary>
        public bool ApplyWindowLayout(int displayCount, DisplayResolution[] entries)
        {
            int n = Mathf.Clamp(displayCount, 2, MaxDisplays);

            fluidDisplays = FluidPoolFor(n);
            activeScreens = n - 1;

            if (entries != null)
            {
                foreach (var e in entries)
                {
                    if (e == null || e.display < 0 || e.display >= MaxDisplays) continue;
                    bool native = e.width <= 0 || e.height <= 0;
                    SetResolutionEntry(e.display, native ? 0 : e.width, native ? 0 : e.height);
                    FindResolution(e.display).mode = NormaliseMode(e.mode);
                }
            }
            windowMode = ModeOf(0);

            ActivateDisplays();
            if (resizeWindowOnApply) ApplyDisplayResolutions();
            ReinitFramebuffers();

            return SaveDisplayConfig();
        }

        /// <summary>
        /// Secondary displays do not exist until activated. Done here, before anything
        /// reads display sizes, so the wall and the input mapping see the real monitors.
        ///
        /// Only the displays in use are activated, so a spare monitor keeps showing the
        /// desktop. Windows cannot close a display window once opened, so reducing the
        /// count while running leaves the dropped screens black until the next launch.
        /// </summary>
        void ActivateDisplays()
        {
            for (int i = 1; i < Display.displays.Length; i++)
            {
                bool inUse = i == ControlDisplayIndex || WallSlotOf(i) >= 0;
                if (inUse && !Display.displays[i].active) Display.displays[i].Activate();
            }
        }

        static FullScreenMode ToFullScreenMode(WindowMode mode)
        {
            switch (mode)
            {
                case WindowMode.Borderless: return FullScreenMode.FullScreenWindow;
                case WindowMode.ExclusiveFullscreen: return FullScreenMode.ExclusiveFullScreen;
                default: return FullScreenMode.Windowed;
            }
        }

        /// <summary>
        /// Change how the main window (display 1) presents itself, keeping its size.
        /// Secondary displays are unaffected: Unity always gives them a window that
        /// fills their monitor.
        /// </summary>
        public void ApplyWindowMode(WindowMode mode)
        {
            windowMode = mode;
            if (!resizeWindowOnApply) return;

            var main = SizeOfDisplay(0);
            Screen.SetResolution(main.x, main.y, ToFullScreenMode(mode));
        }

        /// <summary>Drop the override and follow the display's native size again.</summary>
        public void UseNativeResolution()
        {
            ApplyResolution(0, 0);
        }

        void InitFramebuffers()
        {
            simSize = GetResolution(simResolution);
            dyeSize = GetResolution(dyeResolution);
            texelSizeSim = new Vector2(1f / simSize.x, 1f / simSize.y);

            // Channel counts follow the original: RGBA dye, RG velocity, R for the
            // scalar fields. Less bandwidth than making everything RGBA.
            dye = CreateDoubleFBO(dyeSize.x, dyeSize.y, RenderTextureFormat.ARGBHalf);
            velocity = CreateDoubleFBO(simSize.x, simSize.y, RenderTextureFormat.RGHalf);
            pressureFBO = CreateDoubleFBO(simSize.x, simSize.y, RenderTextureFormat.RHalf, FilterMode.Point);
            divergenceRT = CreateRT(simSize.x, simSize.y, RenderTextureFormat.RHalf, FilterMode.Point);
            curlRT = CreateRT(simSize.x, simSize.y, RenderTextureFormat.RHalf, FilterMode.Point);

            var displaySize = GetDisplaySize();
            displayRT = CreateRT(displaySize.x, displaySize.y, RenderTextureFormat.ARGBHalf);

            InitBloomFramebuffers();
            InitSunraysFramebuffers();
            InitStyleBuffers();

            lastScreenW = Screen.width;
            lastScreenH = Screen.height;
        }

        void InitBloomFramebuffers()
        {
            var res = GetResolution(bloomResolution);
            bloomRT = CreateRT(res.x, res.y, RenderTextureFormat.ARGBHalf);

            bloomMips.Clear();
            for (int i = 0; i < bloomIterations; i++)
            {
                int w = res.x >> (i + 1);
                int h = res.y >> (i + 1);
                if (w < 2 || h < 2) break;
                bloomMips.Add(CreateRT(w, h, RenderTextureFormat.ARGBHalf));
            }
        }

        void InitSunraysFramebuffers()
        {
            var res = GetResolution(sunraysResolution);
            sunraysRT = CreateRT(res.x, res.y, RenderTextureFormat.RHalf);
            sunraysTempRT = CreateRT(res.x, res.y, RenderTextureFormat.RHalf);
        }

        /// <summary>
        /// Release AND destroy a render target, clearing the reference.
        ///
        /// Release() alone frees the GPU surface but leaves the managed object alive
        /// and still referenced - notably by RawImage.texture, which would then draw
        /// a surface that no longer exists. It also leaked one object per rebuild.
        /// </summary>
        static void DestroyRT(ref RenderTexture rt)
        {
            if (rt == null) return;
            if (RenderTexture.active == rt) RenderTexture.active = null;
            rt.Release();
            if (Application.isPlaying) Destroy(rt);
            else DestroyImmediate(rt);
            rt = null;
        }

        void ReleaseFramebuffers()
        {
            // Stop the UI pointing at the texture before it goes away.
            if (displayImage != null) displayImage.texture = null;

            foreach (var fbo in new[] { velocity, dye, pressureFBO })
            {
                if (fbo == null) continue;
                DestroyRT(ref fbo.read);
                DestroyRT(ref fbo.write);
            }
            DestroyRT(ref divergenceRT);
            DestroyRT(ref curlRT);
            DestroyRT(ref displayRT);
            DestroyRT(ref bloomRT);
            DestroyRT(ref sunraysRT);
            DestroyRT(ref sunraysTempRT);
            DestroyRT(ref rippleRead);
            DestroyRT(ref rippleWrite);
            DestroyRT(ref neonRT);
            DestroyRT(ref neonTempRT);

            for (int i = 0; i < bloomMips.Count; i++)
            {
                var mip = bloomMips[i];
                DestroyRT(ref mip);
            }
            bloomMips.Clear();
        }

        bool reinitQueued;

        /// <summary>
        /// Queue a full rebuild of every buffer. Deferred for the same reason as
        /// ApplyResolution: UI callbacks fire mid-frame while the Canvas is still
        /// holding the current render target.
        /// </summary>
        public void ReinitFramebuffers()
        {
            reinitQueued = true;
        }

        void ReinitFramebuffersNow()
        {
            reinitQueued = false;
            ReleaseFramebuffers();
            InitFramebuffers();
            MultipleSplats(initialSplats);
        }

        // ---------- main loop ----------

        void Update()
        {
            // Deferred from UI callbacks so buffers are never swapped mid-frame.
            if (reinitQueued) ReinitFramebuffersNow();
            if (resolutionChangeQueued) ApplyQueuedResolution();

            // Debounced: while a window edge is being dragged the size changes every
            // frame. Rebuilding on each one churns ~20 render targets per frame, which
            // is what exhausts the graphics driver's descriptor pool and kills the
            // process with an unrecoverable device error. Wait for the size to settle.
            if (Screen.width != lastScreenW || Screen.height != lastScreenH)
            {
                if (Screen.width != pendingScreenW || Screen.height != pendingScreenH)
                {
                    pendingScreenW = Screen.width;
                    pendingScreenH = Screen.height;
                    screenSettlesAt = Time.unscaledTime + ScreenSettleDelay;
                }
                else if (Time.unscaledTime >= screenSettlesAt)
                {
                    ReleaseFramebuffers();
                    InitFramebuffers();
                }
            }

            float frameTime = Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            UpdateColors(frameTime);
            ReadPointers();
            UpdateSpots(frameTime);

            // Fixed-timestep simulation, decoupled from render rate.
            float step = 1f / Mathf.Max(simulationRate, 1f);
            stepAccumulator += frameTime * simulationSpeed;

            int steps = 0;
            while (stepAccumulator >= step && steps < maxStepsPerFrame)
            {
                ApplyPointerSplat();          // inputs apply every step, paused or not
                if (!paused && animationStyle == AnimationStyle.Water) StepWaterCurrent(step);
                if (!paused) Step(step);
                if (!paused && animationStyle == AnimationStyle.Water) StepRipples();
                stepAccumulator -= step;
                steps++;
            }
            StepsLastFrame = steps;

            // Drop any backlog we could not work through rather than chasing it.
            if (stepAccumulator > step * maxStepsPerFrame) stepAccumulator = 0f;

            // Particles ride the fluid in simulation time (frozen when paused), while
            // their trails fade in real time.
            UpdateParticles(paused ? 0f : frameTime * simulationSpeed, frameTime);
            Render();
        }

        // Refresh the stroke color on a timer rather than per splat, matching the
        // original's updateColors()/COLOR_UPDATE_SPEED behaviour. Generating a new
        // hue per splat makes overlapping dye sum to achromatic white.
        void UpdateColors(float dt)
        {
            if (!colorful) return;

            colorUpdateTimer += dt * colorUpdateSpeed;
            if (colorUpdateTimer >= 1f)
            {
                colorUpdateTimer = Mathf.Repeat(colorUpdateTimer, 1f);
                // Each pointer gets its own new hue, so simultaneous fingers stay
                // visually distinct instead of all drawing the same colour.
                for (int i = 0; i < pointers.Count; i++) pointers[i].color = RandomColor();
            }
        }

        void Step(float dt)
        {
            var solver = Solver();
            curlMat.SetVector(TexelSizeID, texelSizeSim);
            curlMat.SetTexture("uVelocity", velocity.read);
            Graphics.Blit(null, curlRT, curlMat);

            vorticityMat.SetVector(TexelSizeID, texelSizeSim);
            vorticityMat.SetTexture("uVelocity", velocity.read);
            vorticityMat.SetTexture("uCurl", curlRT);
            vorticityMat.SetFloat("curl", solver.curl);
            vorticityMat.SetFloat("dt", dt);
            Graphics.Blit(null, velocity.write, vorticityMat);
            velocity.Swap();

            divergenceMat.SetVector(TexelSizeID, texelSizeSim);
            divergenceMat.SetTexture("uVelocity", velocity.read);
            Graphics.Blit(null, divergenceRT, divergenceMat);

            clearMat.SetTexture("uTexture", pressureFBO.read);
            clearMat.SetFloat("value", pressure);
            Graphics.Blit(null, pressureFBO.write, clearMat);
            pressureFBO.Swap();

            pressureMat.SetVector(TexelSizeID, texelSizeSim);
            pressureMat.SetTexture("uDivergence", divergenceRT);
            for (int i = 0; i < pressureIterations; i++)
            {
                pressureMat.SetTexture("uPressure", pressureFBO.read);
                Graphics.Blit(null, pressureFBO.write, pressureMat);
                pressureFBO.Swap();
            }

            gradientSubtractMat.SetVector(TexelSizeID, texelSizeSim);
            gradientSubtractMat.SetTexture("uPressure", pressureFBO.read);
            gradientSubtractMat.SetTexture("uVelocity", velocity.read);
            Graphics.Blit(null, velocity.write, gradientSubtractMat);
            velocity.Swap();

            advectionMat.SetVector(TexelSizeID, texelSizeSim);
            advectionMat.SetTexture("uVelocity", velocity.read);
            advectionMat.SetTexture("uSource", velocity.read);
            advectionMat.SetFloat("dt", dt);
            advectionMat.SetFloat("dissipation", solver.velocityDissipation);
            Graphics.Blit(null, velocity.write, advectionMat);
            velocity.Swap();

            advectionMat.SetTexture("uVelocity", velocity.read);
            advectionMat.SetTexture("uSource", dye.read);
            advectionMat.SetFloat("dissipation", solver.densityDissipation);
            Graphics.Blit(null, dye.write, advectionMat);
            dye.Swap();
        }

        // ---------- post processing ----------

        void ApplyBloom(RenderTexture source, RenderTexture destination)
        {
            if (bloomMips.Count < 2) return;

            float knee = bloomThreshold * bloomSoftKnee + 0.0001f;
            bloomPrefilterMat.SetVector("uCurve",
                new Vector4(bloomThreshold - knee, knee * 2f, 0.25f / knee, 0f));
            bloomPrefilterMat.SetFloat("uThreshold", bloomThreshold);
            bloomPrefilterMat.SetFloat("uExposure", exposure);
            bloomPrefilterMat.SetTexture("uTexture", source);
            Graphics.Blit(null, destination, bloomPrefilterMat);

            RenderTexture last = destination;

            // Downsample chain.
            for (int i = 0; i < bloomMips.Count; i++)
            {
                var dest = bloomMips[i];
                bloomBlurMat.SetVector(TexelSizeID, new Vector2(1f / last.width, 1f / last.height));
                bloomBlurMat.SetTexture("uTexture", last);
                Graphics.Blit(null, dest, bloomBlurMat, 0);
                last = dest;
            }

            // Upsample, accumulating additively (pass 1 is Blend One One).
            for (int i = bloomMips.Count - 2; i >= 0; i--)
            {
                var baseTex = bloomMips[i];
                bloomBlurMat.SetVector(TexelSizeID, new Vector2(1f / last.width, 1f / last.height));
                bloomBlurMat.SetTexture("uTexture", last);
                Graphics.Blit(null, baseTex, bloomBlurMat, 1);
                last = baseTex;
            }

            bloomFinalMat.SetVector(TexelSizeID, new Vector2(1f / last.width, 1f / last.height));
            bloomFinalMat.SetTexture("uTexture", last);
            bloomFinalMat.SetFloat("uIntensity", bloomIntensity);
            Graphics.Blit(null, destination, bloomFinalMat);
        }

        void ApplySunrays(RenderTexture source, RenderTexture mask, RenderTexture destination)
        {
            sunraysMaskMat.SetTexture("uTexture", source);
            Graphics.Blit(null, mask, sunraysMaskMat);

            sunraysMat.SetFloat("uWeight", sunraysWeight);
            sunraysMat.SetTexture("uTexture", mask);
            Graphics.Blit(null, destination, sunraysMat);
        }

        void Blur(RenderTexture target, RenderTexture temp, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                blurMat.SetVector(TexelSizeID, new Vector2(1f / target.width, 0f));
                blurMat.SetTexture("uTexture", target);
                Graphics.Blit(null, temp, blurMat);

                blurMat.SetVector(TexelSizeID, new Vector2(0f, 1f / target.height));
                blurMat.SetTexture("uTexture", temp);
                Graphics.Blit(null, target, blurMat);
            }
        }

        void Render()
        {
            if (displayRT == null) return;

            // Classic's own effects - shading, bloom, sunrays - run for Classic only,
            // so none of them can leak into another style.
            bool classic = animationStyle == AnimationStyle.Classic;
            bool useBloom = classic && bloom, useSunrays = classic && sunrays, useShading = classic && shading;
            if (useBloom) ApplyBloom(dye.read, bloomRT);
            if (useSunrays)
            {
                // dye.write doubles as scratch for the mask, as in the original.
                ApplySunrays(dye.read, dye.write, sunraysRT);
                Blur(sunraysRT, sunraysTempRT, 1);
            }

            SetKeyword(displayMat, "FLUID_SHADING", useShading);
            SetKeyword(displayMat, "FLUID_BLOOM", useBloom);
            SetKeyword(displayMat, "FLUID_SUNRAYS", useSunrays);

            float styleExposure = StyleExposure();
            displayMat.SetVector(TexelSizeID, new Vector2(1f / displayRT.width, 1f / displayRT.height));
            displayMat.SetFloat("uExposure", styleExposure);

            // Water's surface, Neon's glow, the night sky's stars: each style's own inputs.
            displayMat.SetTexture("uRipple", animationStyle == AnimationStyle.Water && rippleRead != null
                ? rippleRead : (Texture)Texture2D.blackTexture);
            if (rippleRead != null) displayMat.SetVector("uRippleTexel", new Vector2(1f / rippleRead.width, 1f / rippleRead.height));
            displayMat.SetFloat("uWaveHeight", water.waveHeight);
            displayMat.SetFloat("uWaterTime", waterTime % 7200f);
            displayMat.SetFloat("uIdleWaves", water.idleWaves);
            displayMat.SetFloat("uColorFlow", water.colorFlow);
            displayMat.SetFloat("uGlitter", water.glitterAmount * water.glitterBrightness);
            displayMat.SetFloat("uNeonWidth", neon.lineWidth);
            displayMat.SetFloat("uNeonIntensity", neon.colorIntensity);
            displayMat.SetFloat("uStarDensity", shootingStars.starDensity);
            displayMat.SetTexture("uNeonGlow", animationStyle == AnimationStyle.Neon && neonRT != null && RenderNeonGlow(styleExposure)
                ? neonRT : (Texture)Texture2D.blackTexture);
            displayMat.SetTexture("uTexture", dye.read);
            displayMat.SetTexture("uBloom", bloomRT);
            displayMat.SetTexture("uSunrays", sunraysRT);

            // Animation style: background, how the dye is drawn, and the particle layer.
            StyleGains(animationStyle, out float fluidGain, out float coverGain, out float bloomGain);
            var wallSize = GetDisplaySize();
            displayMat.SetFloat("uStyle", (int)animationStyle);
            displayMat.SetFloat("uFluidGain", fluidGain);
            displayMat.SetFloat("uCoverGain", coverGain);
            displayMat.SetFloat("uBloomGain", bloomGain);
            displayMat.SetFloat("uTime", Time.unscaledTime % 1000f);
            displayMat.SetVector("uDyeTexel", new Vector2(1f / dye.read.width, 1f / dye.read.height));
            displayMat.SetFloat("uWallAspect", wallSize.x / (float)Mathf.Max(wallSize.y, 1));
            bool withParticles = particles != null && particles.Trail != null && ParticlesFor(animationStyle).enabled;
            displayMat.SetTexture("uParticles", withParticles ? particles.Trail : (Texture)Texture2D.blackTexture);

            var reveal = revealMode ? RevealTexture : null;
            SetKeyword(displayMat, "FLUID_REVEAL", reveal != null);
            if (reveal != null) SetRevealUniforms(reveal);
            Graphics.Blit(null, displayRT, displayMat);

            if (displayImage != null) displayImage.texture = displayRT;
        }

        /// <summary>Neon's own glow: its lines drawn small and blurred. False when there is no glow.</summary>
        bool RenderNeonGlow(float styleExposure)
        {
            if (neonEdgeMat == null || neon.glowRadius <= 0.001f) return false;
            neonEdgeMat.SetTexture("uTexture", dye.read);
            neonEdgeMat.SetVector("uDyeTexel", new Vector2(1f / dye.read.width, 1f / dye.read.height));
            neonEdgeMat.SetFloat("uNeonWidth", neon.lineWidth);
            neonEdgeMat.SetFloat("uExposure", styleExposure);
            Graphics.Blit(null, neonRT, neonEdgeMat);
            Blur(neonRT, neonTempRT, Mathf.Clamp(Mathf.RoundToInt(neon.glowRadius * 8f), 1, 8));
            return true;
        }

        /// <summary>
        /// Advance Water's surface one step: fingers drop ripples - more the faster
        /// they move - and the waves travel and die away.
        /// </summary>
        void StepRipples()
        {
            if (rippleMat == null || rippleRead == null) return;
            var wall = GetDisplaySize();
            float aspect = wall.x / (float)Mathf.Max(wall.y, 1);

            int n = 0;
            for (int i = 0; i < pointers.Count && n < rippleDrops.Length; i++)
            {
                var p = pointers[i];
                if (!p.down) { p.rippleSeen = false; continue; }
                Vector2 d = p.uv - p.rippleUv;
                d.x *= aspect;
                float moved = p.rippleSeen ? d.magnitude : 0f;
                p.rippleUv = p.uv;
                p.rippleSeen = true;
                float strength = water.rippleIntensity * Mathf.Clamp01(moved * 40f) * 0.6f;
                if (strength > 1e-4f) rippleDrops[n++] = new Vector4(p.uv.x, p.uv.y, strength, 0f);
            }
            for (int k = n; k < rippleDrops.Length; k++) rippleDrops[k] = Vector4.zero;

            rippleMat.SetVector("uTexel", new Vector2(1f / rippleRead.width, 1f / rippleRead.height));
            rippleMat.SetFloat("uAspect", aspect);
            if (n > 0)
            {
                rippleMat.SetTexture("uTexture", rippleRead);
                rippleMat.SetVectorArray("uDrops", rippleDrops);
                rippleMat.SetFloat("uDropCount", n);
                rippleMat.SetFloat("uDropRadius", 0.012f);
                Graphics.Blit(null, rippleWrite, rippleMat, 1);
                (rippleRead, rippleWrite) = (rippleWrite, rippleRead);
            }
            rippleMat.SetTexture("uTexture", rippleRead);
            rippleMat.SetFloat("uDamping", water.rippleLife);
            Graphics.Blit(null, rippleWrite, rippleMat, 0);
            (rippleRead, rippleWrite) = (rippleWrite, rippleRead);
        }

        float waterTime;

        /// <summary>
        /// Water's idle life, every simulation step: the water is steered towards its
        /// own slow swirling current and milky wisps drift into it, so it keeps
        /// moving - and the glitter keeps flowing - with nobody touching it.
        /// </summary>
        void StepWaterCurrent(float dt)
        {
            waterTime += dt;
            if (waterCurrentMat == null) return;
            var wall = GetDisplaySize();
            waterCurrentMat.SetFloat("uAspect", wall.x / (float)Mathf.Max(wall.y, 1));
            waterCurrentMat.SetFloat("uTime", waterTime % 7200f);
            waterCurrentMat.SetFloat("uDt", dt);
            waterCurrentMat.SetFloat("uSimHeight", velocity.read.height);
            waterCurrentMat.SetFloat("uCurrent", water.idleCurrent);
            waterCurrentMat.SetFloat("uMist", water.mist);

            if (water.idleCurrent > 0.001f)
            {
                waterCurrentMat.SetTexture("uTarget", velocity.read);
                Graphics.Blit(null, velocity.write, waterCurrentMat, 0);
                velocity.Swap();
            }
            if (water.mist > 0.001f)
            {
                waterCurrentMat.SetTexture("uTarget", dye.read);
                Graphics.Blit(null, dye.write, waterCurrentMat, 1);
                dye.Swap();
            }
        }

        void InitStyleBuffers()
        {
            var wall = GetDisplaySize();
            float aspect = wall.x / (float)Mathf.Max(wall.y, 1);
            int rh = 360, rw = Mathf.Max(Mathf.RoundToInt(rh * aspect), 1);
            rippleRead = CreateRT(rw, rh, RenderTextureFormat.RGHalf);
            rippleWrite = CreateRT(rw, rh, RenderTextureFormat.RGHalf);
            ClearRT(rippleRead);
            ClearRT(rippleWrite);
            int nh = Mathf.Max(displayRT.height / 4, 1), nw = Mathf.Max(displayRT.width / 4, 1);
            neonRT = CreateRT(nw, nh, RenderTextureFormat.ARGBHalf);
            neonTempRT = CreateRT(nw, nh, RenderTextureFormat.ARGBHalf);
        }

        static void ClearRT(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        readonly Vector4[] revealRanges = new Vector4[MaxDisplays];
        readonly Vector4[] revealST = new Vector4[MaxDisplays];

        /// <summary>
        /// Lay the reveal image over the wall. Each range is a horizontal stretch of
        /// the wall in wall UV (the whole wall, or one per screen for EachScreen) plus
        /// the scale/offset that maps it onto the image for the chosen fit.
        /// </summary>
        void SetRevealUniforms(Texture tex)
        {
            var wall = GetDisplaySize();
            float imageAspect = tex.width / (float)Mathf.Max(tex.height, 1);
            bool each = revealFit == RevealFit.EachScreen;
            int count = each ? FluidDisplayCount : 1;

            for (int k = 0; k < MaxDisplays; k++)
            {
                Vector2 span = k < count ? (each ? SlotSpan(k) : new Vector2(0f, 1f)) : new Vector2(2f, 1f);
                // Every screen shows the wall's full height, so a range's shape is its
                // share of the wall width against the wall height.
                float areaAspect = span.y * wall.x / Mathf.Max(wall.y, 1);
                revealRanges[k] = new Vector4(span.x, span.y, 0f, 0f);
                revealST[k] = FitScaleOffset(imageAspect, areaAspect, each ? RevealFit.FillWall : revealFit);
            }

            displayMat.SetTexture("uReveal", tex);
            displayMat.SetVectorArray("uRevealRange", revealRanges);
            displayMat.SetVectorArray("uRevealST", revealST);
            displayMat.SetFloat("uRevealCount", count);
            int n = Mathf.Min(spots.Count, spotData.Length);
            for (int k = 0; k < spotData.Length; k++)
                spotData[k] = k < n ? new Vector4(spots[k].uv.x, spots[k].uv.y, spots[k].intensity, 0f) : Vector4.zero;
            displayMat.SetVectorArray("uSpots", spotData);
            displayMat.SetFloat("uSpotCount", n);
            displayMat.SetFloat("uSpotRadius", spotlightSize);
            displayMat.SetFloat("uSpotSoftness", spotlightSoftness);
            displayMat.SetFloat("uWallAspect", wall.x / (float)Mathf.Max(wall.y, 1));
            displayMat.SetFloat("uRevealBackground", revealBackground);
            displayMat.SetFloat("uRevealFluidOpacity", revealFluidOpacity);
        }

        /// <summary>Scale (xy) and offset (zw) taking an area's 0..1 UV to image UV.</summary>
        static Vector4 FitScaleOffset(float imageAspect, float areaAspect, RevealFit fit)
        {
            if (fit == RevealFit.Stretch) return new Vector4(1f, 1f, 0f, 0f);

            bool areaWider = areaAspect > imageAspect;
            if (fit == RevealFit.FitWall)
            {
                // The image fills one axis; the other gets black bars.
                if (areaWider)
                {
                    float f = imageAspect / areaAspect;          // image's share of the width
                    return new Vector4(1f / f, 1f, -(1f - f) * 0.5f / f, 0f);
                }
                float g = areaAspect / imageAspect;              // image's share of the height
                return new Vector4(1f, 1f / g, 0f, -(1f - g) * 0.5f / g);
            }

            // Fill: the image covers the area and is cropped, keeping its centre.
            if (areaWider)
            {
                float sy = imageAspect / areaAspect;
                return new Vector4(1f, sy, 0f, (1f - sy) * 0.5f);
            }
            float sx = areaAspect / imageAspect;
            return new Vector4(sx, 1f, (1f - sx) * 0.5f, 0f);
        }

        static void SetKeyword(Material mat, string keyword, bool on)
        {
            if (on) mat.EnableKeyword(keyword);
            else mat.DisableKeyword(keyword);
        }

        // ---------- input ----------

        // Matches a real mousemove event: pixel-scale UV change, not float noise.
        const float MoveEpsilonSq = 0.0000001f;

        /// <summary>
        /// Normalise a pointer position against the size of the display it is on.
        /// Screen.width/height describe the main display only, so on a wall the
        /// secondary screens need their own dimensions or every touch lands skewed.
        /// </summary>
        Vector2 ToUV(Vector2 screenPos, int displayIndex)
        {
            int w = Screen.width, h = Screen.height;

            if (displayIndex >= 0 && displayIndex < Display.displays.Length)
            {
                var d = Display.displays[displayIndex];
                if (d.renderingWidth > 0 && d.renderingHeight > 0)
                {
                    w = d.renderingWidth;
                    h = d.renderingHeight;
                }
            }

            return new Vector2(screenPos.x / Mathf.Max(w, 1), screenPos.y / Mathf.Max(h, 1));
        }

        /// <summary>True when the position falls inside a screen.</summary>
        static bool OnScreen(Vector2 uv)
        {
            return uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
        }

        /// <summary>
        /// Which slot of the wall a display index occupies, or -1 if that display is
        /// not part of the wall (the control screen, for instance).
        /// </summary>
        public int WallSlotOf(int displayIndex)
        {
            // Only screens in use: a touch on a switched-off screen is ignored.
            if (fluidDisplays == null) return -1;
            for (int i = 0; i < FluidDisplayCount; i++)
                if (fluidDisplays[i] == displayIndex) return i;
            return -1;
        }

        /// <summary>
        /// Turn a position local to one fluid screen into a UV across the whole wall.
        /// Screen 2 covers u 0-1/3, screen 3 covers 1/3-2/3, screen 4 covers 2/3-1,
        /// so a stroke carries on across the seams instead of restarting per screen.
        /// </summary>
        bool TryWallUV(Vector2 localUV, int displayIndex, out Vector2 wallUV)
        {
            wallUV = default;

            int slot = WallSlotOf(displayIndex);
            if (slot < 0 || !OnScreen(localUV)) return false;

            // Must match the slice FluidDisplayWall shows on that screen, or a touch
            // would land offset from the finger on any screen wider or narrower than
            // its neighbours.
            var span = SlotSpan(slot);
            wallUV = new Vector2(span.x + localUV.x * span.y, localUV.y);
            return true;
        }

        FluidPointer GetPointer(int id)
        {
            for (int i = 0; i < pointers.Count; i++)
                if (pointers[i].id == id) return pointers[i];

            var p = new FluidPointer { id = id, color = RandomColor() };
            pointers.Add(p);
            return p;
        }

        void ReadPointers()
        {
            // Everything currently held is re-asserted below; anything not seen this
            // frame is treated as released.
            for (int i = 0; i < pointers.Count; i++) pointers[i].down = false;

            // Only input that lands on a FLUID screen ends the attract loop, so an
            // operator adjusting the control panel does not switch it off.
            bool realInput = false;

#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse != null)
            {
                int display = mouse.displayIndex.ReadValue();
                Vector2 screenPos = mouse.position.ReadValue();
                Vector2 local = ToUV(screenPos, display);

                // A resting cursor is not a visitor. In hover mode the mouse pointer is
                // "down" whenever it is over the wall, so only actual movement (or a
                // click) counts as someone interacting.
                // The first reading only establishes a baseline. Comparing it against the
                // placeholder would count a cursor that is merely resting over a fluid
                // screen at launch as "moved" and end attract mode immediately.
                bool mouseMoved = mouseSeen
                               && ((screenPos - lastMouseScreenPos).sqrMagnitude > 0.25f
                                   || display != lastMouseDisplay);
                mouseSeen = true;
                lastMouseScreenPos = screenPos;
                lastMouseDisplay = display;

                // TryWallUV rejects anything not on a fluid screen. That matters most
                // in hover mode: the cursor keeps reporting a position while it is over
                // the control screen, and without this the fluid would be painted from
                // whichever edge the cursor left through.
                if (TryWallUV(local, display, out Vector2 wallUV)
                    && (drawOnHover || mouse.leftButton.isPressed))
                {
                    var p = GetPointer(MousePointerId);
                    p.down = true;
                    p.uv = wallUV;
                    if (mouseMoved || mouse.leftButton.isPressed) realInput = true;
                }
            }

            // Every active finger gets its own pointer, so N fingers make N strokes.
            var touchscreen = Touchscreen.current;
            if (touchscreen != null)
            {
                var touches = touchscreen.touches;
                for (int i = 0; i < touches.Count; i++)
                {
                    var touch = touches[i];
                    var phase = touch.phase.ReadValue();
                    bool active = phase == UnityEngine.InputSystem.TouchPhase.Began
                               || phase == UnityEngine.InputSystem.TouchPhase.Moved
                               || phase == UnityEngine.InputSystem.TouchPhase.Stationary;
                    if (!active) continue;

                    int display = touch.displayIndex.ReadValue();
                    Vector2 local = ToUV(touch.position.ReadValue(), display);
                    if (!TryWallUV(local, display, out Vector2 wallUV)) continue;

                    var p = GetPointer(touch.touchId.ReadValue());
                    p.down = true;
                    p.uv = wallUV;
                    realInput = true;
                }
            }
#else
            // The legacy API carries no per-pointer display info, so everything is
            // treated as arriving on the first fluid screen.
            int legacyDisplay = fluidDisplays != null && fluidDisplays.Length > 0 ? fluidDisplays[0] : 0;
            Vector2 legacyMouse = Input.mousePosition;
            bool legacyMoved = mouseSeen && (legacyMouse - lastMouseScreenPos).sqrMagnitude > 0.25f;
            mouseSeen = true;
            lastMouseScreenPos = legacyMouse;

            Vector2 mouseLocal = ToUV(legacyMouse, legacyDisplay);
            if (TryWallUV(mouseLocal, legacyDisplay, out Vector2 mouseWall)
                && (drawOnHover || Input.GetMouseButton(0)))
            {
                var p = GetPointer(MousePointerId);
                p.down = true;
                p.uv = mouseWall;
                if (legacyMoved || Input.GetMouseButton(0)) realInput = true;
            }
            for (int i = 0; i < Input.touchCount; i++)
            {
                var touch = Input.GetTouch(i);
                if (touch.phase == UnityEngine.TouchPhase.Ended
                 || touch.phase == UnityEngine.TouchPhase.Canceled) continue;

                Vector2 touchLocal = ToUV(touch.position, legacyDisplay);
                if (!TryWallUV(touchLocal, legacyDisplay, out Vector2 touchWall)) continue;

                var p = GetPointer(touch.fingerId);
                p.down = true;
                p.uv = touchWall;
                realInput = true;
            }
#endif

            if (realInput) lastRealInputTime = Time.unscaledTime;
            UpdateIdlePointers();
        }

        // ---------- attract / idle mode ----------

        const int IdlePointerIdBase = -100;   // real ids are -1 (mouse) and >= 0 (touch)

        Vector2 lastMouseScreenPos;
        int lastMouseDisplay = -1;
        bool mouseSeen;
        // Far in the past, so the app starts in attract mode rather than black.
        float lastRealInputTime = float.NegativeInfinity;

        /// <summary>True while nobody has touched a fluid screen for idleDelay seconds.</summary>
        public bool IsIdle =>
            Time.unscaledTime - lastRealInputTime >= Mathf.Max(idleDelay, 0f);

        /// <summary>
        /// True while the virtual fingers are running: attract mode is on, and either
        /// the wall is idle or they are allowed to keep going alongside real touches.
        /// </summary>
        public bool AttractActive => idleMode && (attractDuringInteraction || IsIdle);

        /// <summary>Human-readable state for the control panel.</summary>
        public string ModeDescription
        {
            get
            {
                if (IsIdle) return idleMode ? "idle - attract running" : "idle - attract off";
                return AttractActive ? "interactive + attract" : "interactive";
            }
        }

        /// <summary>
        /// While idle, drive a few virtual fingers along smooth wandering paths across
        /// the whole wall. They go through exactly the same splat path as a real touch,
        /// so the attract loop looks like someone playing with it. The moment a real
        /// touch lands they simply stop being asserted, and ApplyPointerSplat retires
        /// them on the next step - the wall hands over with no visible switch.
        /// </summary>
        void UpdateIdlePointers()
        {
            if (!AttractActive) return;

            float t = Time.unscaledTime * Mathf.Max(idleSpeed, 0f);
            int count = Mathf.Clamp(idlePointers, 0, 8);

            for (int i = 0; i < count; i++)
            {
                // Perlin noise gives a continuous, never-repeating path. Each finger reads
                // a different region of the noise field so they wander independently.
                float su = 17.3f + i * 41.7f;
                float sv = 91.1f + i * 23.9f;
                float u = Mathf.PerlinNoise(su, t);
                float v = Mathf.PerlinNoise(sv, t + 7.7f);

                // Perlin output bunches around 0.5; stretch it so the fingers actually
                // reach the outer screens and the top and bottom of the wall.
                u = Mathf.Clamp01((u - 0.5f) * 2.2f + 0.5f);
                v = Mathf.Clamp01((v - 0.5f) * 2.2f + 0.5f);

                var p = GetPointer(IdlePointerIdBase - i);
                p.down = true;
                p.uv = new Vector2(Mathf.Lerp(0.04f, 0.96f, u), Mathf.Lerp(0.08f, 0.92f, v));
            }
        }

        // Called once per simulation step, not per rendered frame, so the splat rate
        // stays at simulationRate instead of scaling with a 1000fps render loop.
        void ApplyPointerSplat()
        {
            for (int i = pointers.Count - 1; i >= 0; i--)
            {
                var p = pointers[i];

                if (!p.down)
                {
                    // Finished touches are discarded; the mouse pointer is reused.
                    if (p.id != MousePointerId) pointers.RemoveAt(i);
                    else p.wasDown = false;
                    continue;
                }

                if (!p.wasDown)
                {
                    p.wasDown = true;
                    p.lastSplatUv = p.uv;
                    p.color = RandomColor();
                    // A new stroke starts on the first palette colour. The attract
                    // fingers never lift, so each is started a different number of
                    // colours along - otherwise they would all show the same colour.
                    int idle = IdlePointerIdBase - p.id;
                    p.strokeStart = Time.unscaledTime
                                  - (idle >= 0 ? idle * paletteSecondsPerColor : 0f);
                    Splat(p.uv, Vector2.zero, DyeFor(p));
                    continue;
                }

                Vector2 delta = p.uv - p.lastSplatUv;
                if (delta.sqrMagnitude > MoveEpsilonSq)
                {
                    Splat(p.uv, CorrectDelta(delta) * Solver().force, DyeFor(p));
                    p.lastSplatUv = p.uv;
                }
            }
        }

        /// <summary>Fingers currently touching (plus the mouse if held). For the UI readout.</summary>
        public int ActivePointerCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < pointers.Count; i++)
                    if (pointers[i].down && pointers[i].id > IdlePointerIdBase) n++;
                return n;
            }
        }

        // correctDeltaX/correctDeltaY from the original: keeps a diagonal drag pushing
        // at the same angle regardless of the canvas aspect ratio. Pointer positions
        // are in WALL UV, so the canvas here is the whole wall - using one screen's
        // aspect made vertical drags push ~3x harder than horizontal ones on a
        // three-screen wall.
        Vector2 CorrectDelta(Vector2 delta)
        {
            var wall = GetDisplaySize();
            float aspectRatio = (float)wall.x / Mathf.Max(wall.y, 1);
            if (aspectRatio < 1f) delta.x *= aspectRatio;
            else delta.y /= aspectRatio;
            return delta;
        }

        void Splat(Vector2 uv, Vector2 vel, Color splatColor)
        {
            float aspectRatio = (float)simSize.x / simSize.y;
            float radius = Solver().radius / 100f;
            if (aspectRatio > 1f) radius *= aspectRatio;

            splatMat.SetVector(TexelSizeID, texelSizeSim);
            splatMat.SetFloat("aspectRatio", aspectRatio);
            splatMat.SetVector("splatPoint", uv);
            splatMat.SetFloat("radius", radius);

            splatMat.SetTexture("uTarget", velocity.read);
            splatMat.SetVector("splatColor", new Vector4(vel.x, vel.y, 0f, 1f));
            Graphics.Blit(null, velocity.write, splatMat);
            velocity.Swap();

            // Water: a stroke leaves only as much milky white as its own slider asks
            // for - at 0 it just swirls the water (and the wisps and glitter in it).
            if (animationStyle == AnimationStyle.Water)
            {
                if (water.strokeWhite <= 0.001f) return;
                splatColor *= water.strokeWhite;
            }
            splatMat.SetTexture("uTarget", dye.read);
            splatMat.SetVector("splatColor", new Vector4(splatColor.r, splatColor.g, splatColor.b, 1f));
            Graphics.Blit(null, dye.write, splatMat);
            dye.Swap();
        }

        /// <summary>Scatter bright random splats across the field (the "Random splats" button).</summary>
        public void MultipleSplats(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Color c = (usePalette ? ToDye(palette[Random.Range(0, palette.Length)]) : RandomColor()) * 10f;
                Vector2 pt = new Vector2(Random.value, Random.value);
                Vector2 vel = new Vector2((Random.value - 0.5f) * 1000f, (Random.value - 0.5f) * 1000f);
                Splat(pt, vel, c);
            }
        }

        // Read at splat time rather than stored on the pointer: the palette colour moves
        // along with the stroke's age, and an applied palette recolours live strokes.
        Color DyeFor(FluidPointer p) =>
            usePalette ? ToDye(PaletteColorAt(Time.unscaledTime - p.strokeStart)) : p.color;

        // Same 0.15 dye scale as RandomColor, so a palette colour is exactly as bright
        // on the wall as a random one of the same hue.
        static Color ToDye(Color c) => new Color(c.r * 0.15f, c.g * 0.15f, c.b * 0.15f);

        Color RandomColor()
        {
            Color c = Color.HSVToRGB(Random.value, 1f, 1f);
            return new Color(c.r * 0.15f, c.g * 0.15f, c.b * 0.15f);
        }
    }
}
