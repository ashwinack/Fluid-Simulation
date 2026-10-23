Shader "Hidden/Fluid/Display"
{
    Properties
    {
        uTexture ("Dye", 2D) = "black" {}
        uBloom ("Bloom", 2D) = "black" {}
        uSunrays ("Sunrays", 2D) = "white" {}
        uReveal ("Reveal image", 2D) = "black" {}
        uParticles ("Particles", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_fluid
            #pragma fragment frag
            #pragma multi_compile _ FLUID_SHADING
            #pragma multi_compile _ FLUID_BLOOM
            #pragma multi_compile _ FLUID_SUNRAYS
            #pragma multi_compile _ FLUID_REVEAL
            #include "FluidCommon.cginc"
            #include "FluidWaterNoise.cginc"

            sampler2D uTexture;
            sampler2D uBloom;
            sampler2D uSunrays;
            float uExposure;

            // Logo reveal. The image is laid over one or more horizontal ranges of the
            // wall (the whole wall, or one range per screen). Each range carries a
            // scale/offset from its local 0..1 to image UV, which is how Fill / Fit /
            // Stretch are expressed without any branching on the mode here.
            sampler2D uReveal;
            float4 uRevealRange[8];   // x = start, y = width, in wall UV
            float4 uRevealST[8];      // xy = scale, zw = offset: local UV -> image UV
            float uRevealCount;
            float uRevealBackground;    // image brightness outside the spotlight (0 = hidden)
            float uRevealFluidOpacity;  // fluid opacity over the image inside the spotlight

            // Spotlights: one soft circle per pointer (mouse hover or finger).
            float4 uSpots[8];           // xy = wall UV, z = intensity 0..1 (fades in/out)
            float uSpotCount;
            float uSpotRadius;          // in units of wall height
            float uSpotSoftness;        // share of the radius the edge fades over
            float uWallAspect;          // wall width / height, so circles stay round

            // 0..1 with zero slope AND zero curvature at both ends, so the fade has
            // no visible start or end line (smoothstep's curvature jump can read as
            // a faint ring on a dark image).
            float Smootherstep(float a, float b, float x)
            {
                float t = saturate((x - a) / max(b - a, 1e-5));
                return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
            }

            float SpotMask(float2 wallUv)
            {
                float m = 0.0;
                // At least ~1.5 pixels of fade even at the crispest setting, so the
                // rim is always anti-aliased.
                float aa = 1.5 / max(_ScreenParams.y, 1.0);
                float inner = uSpotRadius * (1.0 - uSpotSoftness);
                float outer = max(uSpotRadius, inner + aa);
                [unroll] for (int k = 0; k < 8; k++)
                {
                    if (k < uSpotCount)
                    {
                        float2 d = (wallUv - uSpots[k].xy) * float2(uWallAspect, 1.0);
                        float edge = 1.0 - Smootherstep(inner, outer, length(d));
                        // Overlapping spotlights merge softly instead of showing a crease.
                        m = 1.0 - (1.0 - m) * (1.0 - edge * uSpots[k].z);
                    }
                }
                return m;
            }

            // The dye is far coarser than the screen (often 128 texels tall), and plain
            // bilinear upscaling leaves stair-stepped, diamond-shaped contours. A
            // B-spline (bicubic) filter from 4 bilinear taps gives smooth contours, so
            // the fluid's edges - and the spotlight seen through them - stay clean.
            float4 CubicWeights(float v)
            {
                float4 n = float4(1.0, 2.0, 3.0, 4.0) - v;
                float4 s = n * n * n;
                float x = s.x;
                float y = s.y - 4.0 * s.x;
                float z = s.z - 4.0 * s.y + 6.0 * s.x;
                float w = 6.0 - x - y - z;
                return float4(x, y, z, w) * (1.0 / 6.0);
            }

            float3 SampleBicubic(sampler2D tex, float2 uv, float2 texel)
            {
                float2 st = uv / texel - 0.5;
                float2 f = frac(st);
                st -= f;
                float4 xc = CubicWeights(f.x);
                float4 yc = CubicWeights(f.y);
                float4 c = st.xxyy + float2(-0.5, 1.5).xyxy;
                float4 s = float4(xc.xz + xc.yw, yc.xz + yc.yw);
                float4 o = (c + float4(xc.yw, yc.yw) / s) * texel.xxyy;
                float3 s0 = tex2D(tex, o.xz).rgb, s1 = tex2D(tex, o.yz).rgb;
                float3 s2 = tex2D(tex, o.xw).rgb, s3 = tex2D(tex, o.yw).rgb;
                float sx = s.x / (s.x + s.y), sy = s.z / (s.z + s.w);
                return lerp(lerp(s3, s2, sx), lerp(s1, s0, sx), sy);
            }

            float3 RevealImage(float2 wallUv)
            {
                float4 r = uRevealRange[0];
                float4 st = uRevealST[0];
                [unroll] for (int k = 1; k < 8; k++)
                {
                    if (k < uRevealCount && wallUv.x >= uRevealRange[k].x)
                    {
                        r = uRevealRange[k];
                        st = uRevealST[k];
                    }
                }
                float2 local = float2((wallUv.x - r.x) / max(r.y, 1e-5), wallUv.y);
                float2 iuv = local * st.xy + st.zw;
                // Fit leaves bars outside the image: those stay black.
                float inside = step(0.0, iuv.x) * step(iuv.x, 1.0) * step(0.0, iuv.y) * step(iuv.y, 1.0);
                return tex2D(uReveal, saturate(iuv)).rgb * inside;
            }

            // ---- animation styles ----
            // 0 Classic, 1 Water, 2 Shooting Stars, 3 Comets, 4 Neon.
            float uStyle;
            float uFluidGain;       // how strongly the dye itself is drawn in this style
            float uCoverGain;       // how quickly the dye hides what is behind it
            float uBloomGain;       // share of bloom this style takes
            float uTime;
            float2 uDyeTexel;       // dye texel size, for Neon's edge detection
            sampler2D uParticles;   // stars / comets / glitter, additive

            // Water's own surface: a rippling height field (R = height).
            sampler2D uRipple;
            float2 uRippleTexel;
            float uWaveHeight;
            // Water's idle life.
            float uWaterTime;       // the water's own clock (stops when paused)
            float uIdleWaves;       // rolling surface waves, 0..2
            float uColorFlow;       // how fast the shades of blue drift
            float uGlitter;         // amount x brightness of the glitter

            // Neon's own lines and glow.
            sampler2D uNeonGlow;    // the lines, blurred by Neon's own glow pass
            float uNeonWidth;       // line thickness, in dye texels
            float uNeonIntensity;

            // Shooting Stars: density of the fixed stars in the sky.
            float uStarDensity;

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

            // A field of fixed stars: at most one per grid cell, a random share of
            // cells lit, each twinkling at its own rate. scale = cells per wall height.
            float StarLayer(float2 uv, float scale, float density)
            {
                float2 p = uv * float2(uWallAspect, 1.0) * scale;
                float2 cell = floor(p);
                float h = hash12(cell);
                if (h > density) return 0.0;
                float2 at = hash22(cell + 7.31) * 0.7 + 0.15;
                float d = length(frac(p) - at);
                float twinkle = 0.55 + 0.45 * sin(uTime * (0.7 + h * 9.0) + h * 91.0);
                return smoothstep(0.14, 0.0, d) * twinkle * (0.35 + 0.65 * hash12(cell + 3.7));
            }

            // ---- Water's idle life ----
            // The water's body: shades of blue drifting into one another, as layers
            // of warped noise. warp = how this spot has drifted (the glitter rides
            // it too), shade = how light the water is here, 0..1.
            void WaterBody(float2 uv, out float2 warp, out float shade)
            {
                float2 p = uv * float2(uWallAspect, 1.0);
                float t = uWaterTime * 0.05 * uColorFlow;
                warp = float2(WaterFbm(p * 1.1 + float2(0.0, t), 3),
                              WaterFbm(p * 1.1 + float2(5.2, 1.3) - t, 3));
                shade = WaterFbm(p * float2(1.2, 2.0) + warp * 1.8 + float2(t * 0.7, -t * 0.3), 4);
            }

            // 0..1 along one contour of the shade: a thin curling filament.
            float WaterRidge(float shade)
            {
                float r = saturate(1.0 - abs(shade - 0.56) * 10.0);
                return r * r;
            }

            float3 WaterColour(float2 uv, float2 warp, float shade)
            {
                float3 deep  = float3(0.0, 0.03, 0.12);
                float3 mid   = float3(0.01, 0.15, 0.42);
                float3 light = float3(0.1, 0.42, 0.8);
                float3 col = lerp(deep, mid, smoothstep(0.3, 0.64, shade));
                col = lerp(col, light, smoothstep(0.62, 0.9, shade) * 0.5);
                // Soft light filaments curling along the edges of the shades, like
                // the veils of glitter swirling in the reference water.
                float ridge = WaterRidge(shade);
                col += float3(0.25, 0.5, 0.85) * ridge * 0.22;
                // Lighter towards the surface, darkest in the depths below.
                col *= lerp(0.5, 1.15, smoothstep(0.0, 1.0, uv.y));
                col += light * smoothstep(0.72, 1.05, uv.y) * (0.18 + 0.2 * warp.x);
                return col;
            }

            // Rolling surface waves: a few long swells crossing each other, returned
            // as the surface slope.
            float2 WaterWaves(float2 uv)
            {
                float2 p = uv * float2(uWallAspect, 1.0);
                float t = uWaterTime;
                float2 d1 = float2(0.96, 0.28), d2 = float2(-0.37, 0.93), d3 = float2(0.8, -0.6);
                float2 s = d1 * 5.0 * 0.012 * cos(dot(p, d1) * 5.0 + t * 0.9)
                         + d2 * 9.0 * 0.006 * cos(dot(p, d2) * 9.0 - t * 1.3)
                         + d3 * 15.0 * 0.003 * cos(dot(p, d3) * 15.0 + t * 1.7);
                return s * uIdleWaves;
            }

            // Glitter suspended in the water: countless fine points, each flashing
            // now and then as it turns to the light, drifting with the water.
            float GlitterLayer(float2 p, float scale, float density, float seed)
            {
                float2 g = p * scale;
                float2 cell = floor(g);
                float h = hash12(cell + seed);
                if (h > density) return 0.0;
                float2 at = hash22(cell + seed + 7.31) * 0.7 + 0.15;
                float d = length(frac(g) - at);
                float flash = 0.5 + 0.5 * sin(uWaterTime * (1.2 + h * 6.0 / max(density, 1e-3)) + h * 97.0);
                flash = 0.15 + 0.85 * flash * flash * flash * flash;
                return smoothstep(0.22, 0.0, d) * flash * (0.4 + 0.6 * hash12(cell + seed + 3.7));
            }

            float3 WaterGlitter(float2 uv, float2 warp, float shade, float wisp)
            {
                float2 p = uv * float2(uWallAspect, 1.0) + warp * 0.22 + float2(uWaterTime * 0.012, 0.0);
                float g = GlitterLayer(p, 230.0, 0.12, 0.0)
                        + GlitterLayer(p * 1.07 + 3.1, 120.0, 0.03, 11.0) * 1.3;
                // Densest in the lighter clouds and wisps, sparse in the clear water.
                float cloud = 0.1 + 0.6 * smoothstep(0.5, 0.85, shade) + 1.4 * WaterRidge(shade) + 0.9 * saturate(wisp);
                return float3(0.75, 0.88, 1.0) * g * cloud * uGlitter * 0.7;
            }

            // Water's background is drawn in frag (WaterBody / WaterColour), which
            // shares its drift with the glitter.
            float3 StyleBackground(float2 uv)
            {
                float3 back = 0.0;              // Classic and Neon: black
                if (uStyle > 1.5 && uStyle < 2.5)
                {
                    // Shooting stars: a night sky.
                    float stars = StarLayer(uv, 70.0, 0.2 * uStarDensity) + 0.6 * StarLayer(uv, 140.0, 0.16 * uStarDensity);
                    float3 sky = lerp(float3(0.0, 0.0, 0.008), float3(0.004, 0.01, 0.035), uv.y);
                    back = sky + float3(0.8, 0.86, 1.0) * stars;
                }
                if (uStyle > 2.5 && uStyle < 3.5)
                {
                    // Comets: deep space navy with sparse far stars.
                    float stars = 0.5 * StarLayer(uv, 110.0, 0.05);
                    float3 space = lerp(float3(0.002, 0.004, 0.02), float3(0.01, 0.018, 0.06), uv.y);
                    back = space + float3(0.7, 0.8, 1.0) * stars;
                }
                return back;
            }

            float Luma(float3 c) { return max(c.r, max(c.g, c.b)); }

            float3 StyleFluid(float3 c, float2 uv)
            {
                float3 result = c;
                if (uStyle > 0.5 && uStyle < 1.5)
                {
                    // Water: the flow's matter is soft, milky, whitish-blue light in
                    // the water, never coloured paint.
                    result = min(Luma(c), 1.1) * float3(0.42, 0.66, 1.0);
                }
                if (uStyle > 1.5 && uStyle < 3.5)
                {
                    // Shooting stars and comets draw no fluid at all - their own
                    // renderer draws everything. (uFluidGain is 0 for them too.)
                    result = 0.0;
                }
                if (uStyle > 3.5)
                {
                    // Neon: glowing tubes of light along contour lines of the flow -
                    // like the lines on a map - so every swirl is traced by crisp
                    // lines of a fixed on-screen width, however coarse the flow's
                    // resolution. Levels are spaced 0.6 apart in brightness.
                    float v = Luma(c);
                    float f = frac(v / 0.6);
                    float dist = min(f, 1.0 - f) * 0.6 / max(fwidth(v), 1e-4);   // pixels to the nearest line
                    float tube = saturate(1.0 - dist / uNeonWidth) * step(0.06, v);
                    float3 hue = c / max(v, 1e-4);
                    result = (hue * tube * 1.3 + tex2D(uNeonGlow, uv).rgb * 0.8) * uNeonIntensity;
                }
                return result;
            }

            // Mirrors displayShaderSource from the original WebGL simulation.
            // SHADING treats dye brightness as a height field and lights it head-on,
            // which produces the fine ridged detail along the vortex filaments.
            fixed4 frag (v2f_fluid i) : SV_Target
            {
                // Water: the rippling surface bends everything seen through it and
                // throws moving highlights. Every other style: flat, untouched.
                float2 uv = i.vUv;
                float3 rippleLight = 0.0;
                if (uStyle > 0.5 && uStyle < 1.5)
                {
                    float hl = tex2D(uRipple, uv - float2(uRippleTexel.x, 0)).r;
                    float hr = tex2D(uRipple, uv + float2(uRippleTexel.x, 0)).r;
                    float ht = tex2D(uRipple, uv + float2(0, uRippleTexel.y)).r;
                    float hb = tex2D(uRipple, uv - float2(0, uRippleTexel.y)).r;
                    float2 slope = float2(hr - hl, ht - hb) * uWaveHeight;
                    float2 swell = WaterWaves(uv) * max(uWaveHeight, 0.25);
                    uv += slope * 0.02 + swell * 0.035;                  // refraction
                    float3 n = normalize(float3(-slope * 6.0, 1.0));
                    float spec = pow(saturate(dot(n, normalize(float3(-0.35, 0.5, 1.0)))), 60.0);
                    rippleLight = float3(0.7, 0.88, 1.0) * (spec * 0.9 + length(slope) * 0.6);
                    // The swells gather the light into soft moving bands, strongest
                    // near the surface.
                    float3 sn = normalize(float3(-swell * 3.0, 1.0));
                    float band = pow(saturate(dot(sn, normalize(float3(0.0, 0.45, 1.0)))), 24.0);
                    rippleLight += float3(0.35, 0.6, 0.95) * band * 0.12 * lerp(0.4, 1.0, uv.y) * uIdleWaves;
                }

                float3 c = SampleBicubic(uTexture, uv, uDyeTexel);

            #ifdef FLUID_SHADING
                float3 lc = tex2D(uTexture, i.vL).rgb;
                float3 rc = tex2D(uTexture, i.vR).rgb;
                float3 tc = tex2D(uTexture, i.vT).rgb;
                float3 bc = tex2D(uTexture, i.vB).rgb;

                float dx = length(rc) - length(lc);
                float dy = length(tc) - length(bc);

                float3 nrm = normalize(float3(dx, dy, length(_TexelSize)));
                float3 lightDir = float3(0.0, 0.0, 1.0);

                float diffuse = clamp(dot(nrm, lightDir) + 0.7, 0.7, 1.0);
                c *= diffuse;
            #endif

                // Dye peaks around 0.15, so it needs a gain to reach display range.
                // The original gets this implicitly from its un-normalised sunrays
                // multiply; here it is an explicit, tunable control instead.
                c *= uExposure;

            #ifdef FLUID_BLOOM
                float3 bloom = tex2D(uBloom, i.vUv).rgb;
            #endif

            #ifdef FLUID_SUNRAYS
                float sunrays = tex2D(uSunrays, i.vUv).r;
                c *= sunrays;
                #ifdef FLUID_BLOOM
                    bloom *= sunrays;
                #endif
            #endif

                // The original runs a linear->gamma curve on bloom because its buffers
                // are linear-light and the canvas is sRGB. This project is in Gamma
                // color space, so the dye is already display-referred and that curve
                // would be a second conversion - it lifts near-black bloom (0.05 ->
                // 0.25) into a grey haze over the whole frame. So bloom is added as is.
            #ifndef FLUID_BLOOM
                float3 bloom = 0.0;
            #endif

                // ---- animation style ----
                // Every style draws the SAME simulation; only the look differs:
                //   back  - what is behind the fluid (black, water, night sky...)
                //   fluid - how the dye itself is drawn over it
                // The fluid is effectively premultiplied (black = empty), so its
                // brightness is its coverage of the background.
                float3 back;
                float3 glitter = 0.0;
                if (uStyle > 0.5 && uStyle < 1.5)
                {
                    float2 warp; float shade;
                    WaterBody(uv, warp, shade);
                    back = WaterColour(uv, warp, shade) + rippleLight;
                    glitter = WaterGlitter(uv, warp, shade, Luma(c) * 1.5);
                }
                else back = StyleBackground(uv) + rippleLight;
                float3 fluid = StyleFluid(c, uv) * uFluidGain;
                bloom *= uBloomGain;
                float opacity = 1.0;

            #ifdef FLUID_REVEAL
                // Logo spotlight. The logo replaces the background inside the soft
                // circles that follow the mouse while it hovers and every finger on
                // the glass (and everywhere at uRevealBackground). Inside a circle the
                // fluid thins to uRevealFluidOpacity so the logo shows through it.
                float spot = SpotMask(i.vUv);
                back = lerp(back, RevealImage(i.vUv), max(uRevealBackground, spot));
                opacity = lerp(1.0, uRevealFluidOpacity, spot);
            #endif

                float lum = max(fluid.r, max(fluid.g, fluid.b));
                // How much the fluid hides what is behind it. A smooth curve rather
                // than saturate(): saturate's hard corner turned every place the dye
                // crossed one brightness into a sharp, ragged line, which is what made
                // the edge of the image showing through the fluid look rough.
                float cover = (1.0 - exp(-1.6 * lum * uCoverGain)) * opacity;
                c = fluid * opacity + back * (1.0 - cover) + bloom * opacity;
                c += glitter * opacity;     // Water's glitter sparkles through its wisps

                // Particles (stars, comets, glitter) ride on top of everything - thinned
                // inside a spotlight like the fluid, so they fade with the same soft
                // edge instead of crowding into a ring around it.
                c += tex2D(uParticles, i.vUv).rgb * opacity;

                // Half-a-level of noise: soft gradients (the spotlight's fade over a
                // dark image above all) would otherwise band into visible rings on
                // 8-bit displays.
                c += (hash12(i.vUv * _ScreenParams.xy + frac(uTime) * 61.0) - 0.5) / 255.0;

                return fixed4(c, 1.0);
            }
            ENDCG
        }
    }
}
