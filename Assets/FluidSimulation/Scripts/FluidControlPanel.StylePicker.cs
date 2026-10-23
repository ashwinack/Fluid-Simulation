using UnityEngine;
using UnityEngine.UI;

namespace FluidSim
{
    /// <summary>
    /// The animation style picker: a dialog over the control panel, opened from the
    /// top-right STYLE button, with one card per style. Tapping a card switches the
    /// wall immediately; every simulation parameter stays exactly where it is.
    /// "Save as default" keeps the choice for the next launch.
    /// </summary>
    public partial class FluidControlPanel
    {
        const float SpCardHeight = 230f;
        RectTransform[] spSettings;
        const int SpPreviewW = 220, SpPreviewH = 130;

        RectTransform spOverlay;
        Text spStatusText;
        Image[] spFrames;

        void BuildStylePicker(Transform parent)
        {
            spOverlay = NewRect("StylePicker", parent);
            spOverlay.anchorMin = Vector2.zero;
            spOverlay.anchorMax = Vector2.one;
            spOverlay.offsetMin = Vector2.zero;
            spOverlay.offsetMax = Vector2.zero;
            spOverlay.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.78f);

            var dialog = NewRect("Dialog", spOverlay);
            dialog.anchorMin = dialog.anchorMax = new Vector2(0.5f, 0.5f);
            dialog.sizeDelta = new Vector2(1240f, 900f);
            dialog.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.13f, 1f);
            var vlg = dialog.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(32, 32, 28, 28);
            vlg.spacing = 12f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            NewCell(NewRow(dialog, 44f), "ANIMATION STYLE", 28, TextAnchor.MiddleLeft, Accent, 0f);
            var info = NewCell(NewRow(dialog, 30f),
                "Tap a style to switch the wall immediately. Each style is its own animation with its own settings below.",
                17, TextAnchor.UpperLeft, WmMuted, 0f);
            info.horizontalOverflow = HorizontalWrapMode.Wrap;
            info.verticalOverflow = VerticalWrapMode.Overflow;

            var cards = NewRow(dialog, SpCardHeight);
            cards.GetComponent<HorizontalLayoutGroup>().spacing = 12f;
            int n = FluidSimulation.AnimationStyleNames.Length;
            spFrames = new Image[n];
            for (int i = 0; i < n; i++) spFrames[i] = BuildStyleCard(cards, i);

            // Each style's own settings; only the selected style's block is shown.
            var settingsHeader = NewCell(NewRow(dialog, 36f), "", 20, TextAnchor.LowerLeft, Accent, 0f);
            var area = NewRect("StyleSettings", dialog);
            var areaLe = area.gameObject.AddComponent<LayoutElement>();
            areaLe.preferredHeight = areaLe.minHeight = 6f * (RowHeight + RowSpacing);
            spSettings = new RectTransform[n];
            for (int i = 0; i < n; i++) spSettings[i] = BuildStyleSettings(area, (FluidSimulation.AnimationStyle)i);
            refreshers.Add(() =>
            {
                int sel = (int)sim.animationStyle;
                settingsHeader.text = FluidSimulation.AnimationStyleNames[sel].ToUpperInvariant() + " SETTINGS";
                for (int i = 0; i < spSettings.Length; i++)
                    if (spSettings[i].gameObject.activeSelf != (i == sel)) spSettings[i].gameObject.SetActive(i == sel);
            });

            var buttons = NewRow(dialog, 54f);
            NewButton(buttons, "Save as default", () =>
            {
                bool ok = sim.SaveAnimationStyleSetting();
                string name = FluidSimulation.AnimationStyleNames[(int)sim.animationStyle];
                SpStatus(ok ? $"{name} and every style's settings saved - the wall starts with them next time."
                            : "Could not write fluid-settings.json - see the log.", !ok);
            });
            NewButton(buttons, "Close", () => spOverlay.gameObject.SetActive(false));

            spStatusText = NewCell(NewRow(dialog, 30f), "", 18, TextAnchor.MiddleLeft, WmMuted, 0f);

            // The selected card follows the live style, whoever changed it.
            refreshers.Add(() =>
            {
                if (spFrames == null) return;
                int sel = (int)sim.animationStyle;
                for (int i = 0; i < spFrames.Length; i++)
                    spFrames[i].color = i == sel ? Color.white : new Color(0.2f, 0.2f, 0.24f);
            });

            spOverlay.gameObject.SetActive(false);
        }

        Image BuildStyleCard(Transform row, int index)
        {
            var go = new GameObject("Style" + index, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(row, false);
            go.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var frame = go.GetComponent<Image>();

            var inner = NewRect("Inner", go.transform);
            inner.anchorMin = Vector2.zero;
            inner.anchorMax = Vector2.one;
            inner.offsetMin = new Vector2(4f, 4f);
            inner.offsetMax = new Vector2(-4f, -4f);
            inner.gameObject.AddComponent<Image>().color = new Color(0.13f, 0.13f, 0.17f);
            inner.GetComponent<Image>().raycastTarget = false;

            var preview = NewRect("Preview", inner);
            preview.anchorMin = new Vector2(0f, 1f);
            preview.anchorMax = new Vector2(1f, 1f);
            preview.pivot = new Vector2(0.5f, 1f);
            preview.sizeDelta = new Vector2(0f, 110f);
            preview.anchoredPosition = Vector2.zero;
            var img = preview.gameObject.AddComponent<RawImage>();
            img.texture = MakeStylePreview((FluidSimulation.AnimationStyle)index);
            img.raycastTarget = false;

            var name = NewText(inner, FluidSimulation.AnimationStyleNames[index], 22, TextAnchor.UpperLeft, Color.white);
            var nr = name.rectTransform;
            nr.anchorMin = new Vector2(0f, 1f);
            nr.anchorMax = new Vector2(1f, 1f);
            nr.pivot = new Vector2(0.5f, 1f);
            nr.offsetMin = new Vector2(14f, -150f);
            nr.offsetMax = new Vector2(-14f, -118f);

            var desc = NewText(inner, FluidSimulation.AnimationStyleDescriptions[index], 16, TextAnchor.UpperLeft, WmMuted);
            desc.horizontalOverflow = HorizontalWrapMode.Wrap;
            desc.verticalOverflow = VerticalWrapMode.Overflow;
            var dr = desc.rectTransform;
            dr.anchorMin = new Vector2(0f, 0f);
            dr.anchorMax = new Vector2(1f, 1f);
            dr.offsetMin = new Vector2(14f, 8f);
            dr.offsetMax = new Vector2(-14f, -152f);

            var button = go.GetComponent<Button>();
            button.targetGraphic = frame;
            button.transition = Selectable.Transition.None;     // the frame shows selection
            button.onClick.AddListener(() =>
            {
                sim.SetAnimationStyle((FluidSimulation.AnimationStyle)index);
                string n = FluidSimulation.AnimationStyleNames[index];
                SpStatus($"{n} is on the wall now (not saved - use Save as default to keep it).", false);
                SetStatus("Animation style: " + n);
            });
            return frame;
        }

        /// <summary>One style's own sliders. Classic's live on the main panel.</summary>
        RectTransform BuildStyleSettings(Transform parent, FluidSimulation.AnimationStyle style)
        {
            var block = NewRect("Settings_" + style, parent);
            block.anchorMin = Vector2.zero;
            block.anchorMax = Vector2.one;
            block.offsetMin = Vector2.zero;
            block.offsetMax = Vector2.zero;
            var vlg = block.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = RowSpacing;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            switch (style)
            {
                case FluidSimulation.AnimationStyle.Water:
                {
                    // Two columns: how it reacts to hands, and its own idle life.
                    var cols = NewRow(block, 6f * RowHeight + 5f * RowSpacing);
                    cols.GetComponent<HorizontalLayoutGroup>().spacing = 40f;
                    var left = SettingsColumn(cols);
                    var right = SettingsColumn(cols);
                    AddSlider(left, "viscosity", 0f, 4f, () => sim.water.viscosity, v => sim.water.viscosity = v);
                    AddSlider(left, "wave height", 0f, 2f, () => sim.water.waveHeight, v => sim.water.waveHeight = v);
                    AddSlider(left, "ripple intensity", 0f, 2f, () => sim.water.rippleIntensity, v => sim.water.rippleIntensity = v);
                    AddSlider(left, "swirl", 0f, 50f, () => sim.water.swirl, v => sim.water.swirl = v);
                    AddSlider(left, "glitter amount", 0f, 1f, () => sim.water.glitterAmount, v => sim.water.glitterAmount = v);
                    AddSlider(left, "stroke whiteness", 0f, 1f, () => sim.water.strokeWhite, v => sim.water.strokeWhite = v);
                    AddSlider(right, "glitter brightness", 0f, 2f, () => sim.water.glitterBrightness, v => sim.water.glitterBrightness = v);
                    AddSlider(right, "idle current", 0f, 2f, () => sim.water.idleCurrent, v => sim.water.idleCurrent = v);
                    AddSlider(right, "mist", 0f, 2f, () => sim.water.mist, v => sim.water.mist = v);
                    AddSlider(right, "idle waves", 0f, 2f, () => sim.water.idleWaves, v => sim.water.idleWaves = v);
                    AddSlider(right, "colour flow", 0f, 3f, () => sim.water.colorFlow, v => sim.water.colorFlow = v);
                    break;
                }
                case FluidSimulation.AnimationStyle.ShootingStars:
                    AddSlider(block, "speed", 0f, 3f, () => sim.shootingStars.speed, v => sim.shootingStars.speed = v);
                    AddSlider(block, "brightness", 0.2f, 3f, () => sim.shootingStars.brightness, v => sim.shootingStars.brightness = v);
                    AddSlider(block, "tail length", 0.1f, 2f, () => sim.shootingStars.tailLength, v => sim.shootingStars.tailLength = v);
                    AddSlider(block, "star density", 0f, 1f, () => sim.shootingStars.starDensity, v => sim.shootingStars.starDensity = v);
                    AddSlider(block, "head size", 4f, 60f, () => sim.shootingStars.headSize, v => sim.shootingStars.headSize = v, true);
                    AddSlider(block, "sparkle", 0f, 1f, () => sim.shootingStars.sparkle, v => sim.shootingStars.sparkle = v);
                    break;
                case FluidSimulation.AnimationStyle.Comets:
                    AddSlider(block, "trajectory", 0f, 1f, () => sim.comet.trajectory, v => sim.comet.trajectory = v);
                    AddSlider(block, "glow intensity", 0.2f, 3f, () => sim.comet.glowIntensity, v => sim.comet.glowIntensity = v);
                    AddSlider(block, "particle count", 0f, 1f, () => sim.comet.particleCount, v => sim.comet.particleCount = v);
                    AddSlider(block, "head size", 10f, 120f, () => sim.comet.headSize, v => sim.comet.headSize = v, true);
                    AddSlider(block, "tail length", 0.2f, 3f, () => sim.comet.tailLength, v => sim.comet.tailLength = v);
                    break;
                case FluidSimulation.AnimationStyle.Neon:
                    AddSlider(block, "glow radius", 0f, 1f, () => sim.neon.glowRadius, v => sim.neon.glowRadius = v);
                    AddSlider(block, "colour intensity", 0.2f, 3f, () => sim.neon.colorIntensity, v => sim.neon.colorIntensity = v);
                    AddSlider(block, "line width", 0.5f, 4f, () => sim.neon.lineWidth, v => sim.neon.lineWidth = v);
                    AddSlider(block, "swirl", 0f, 50f, () => sim.neon.swirl, v => sim.neon.swirl = v);
                    break;
                default:
                    var note = NewCell(NewRow(block, 60f),
                        "Classic uses the main control panel: SIMULATION, APPEARANCE, BLOOM and SUNRAYS. " +
                        "Those settings affect Classic only.", 18, TextAnchor.UpperLeft, WmMuted, 0f);
                    note.horizontalOverflow = HorizontalWrapMode.Wrap;
                    note.verticalOverflow = VerticalWrapMode.Overflow;
                    break;
            }

            block.gameObject.SetActive(style == sim.animationStyle);
            return block;
        }

        RectTransform SettingsColumn(Transform row)
        {
            var col = NewRect("Column", row);
            col.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var vlg = col.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = RowSpacing;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            return col;
        }

        void OpenStylePicker()
        {
            if (spOverlay == null) return;
            spStatusText.text = "";
            spOverlay.SetAsLastSibling();
            spOverlay.gameObject.SetActive(true);
        }

        void SpStatus(string message, bool warning)
        {
            spStatusText.text = message;
            spStatusText.color = warning ? new Color(1f, 0.65f, 0.4f) : new Color(0.6f, 0.9f, 0.6f);
        }

        // ---------- previews ----------

        /// <summary>A small painted hint of each style for its card.</summary>
        static Texture2D MakeStylePreview(FluidSimulation.AnimationStyle style)
        {
            int w = SpPreviewW, h = SpPreviewH;
            var px = new Color[w * h];
            var rng = new System.Random(1234 + (int)style);

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)w, v = y / (float)h;
                Color c = Color.black;
                switch (style)
                {
                    case FluidSimulation.AnimationStyle.Water:
                        c = Color.Lerp(new Color(0.01f, 0.05f, 0.16f), new Color(0.12f, 0.38f, 0.72f), v * v);
                        c += new Color(0.1f, 0.2f, 0.3f) * Mathf.Max(0f, Mathf.Sin(u * 9f + Mathf.Sin(v * 6f) * 2f)) * 0.35f * (1f - v);
                        break;
                    case FluidSimulation.AnimationStyle.ShootingStars:
                        c = Color.Lerp(Color.black, new Color(0.01f, 0.02f, 0.06f), v);
                        break;
                    case FluidSimulation.AnimationStyle.Comets:
                        c = Color.Lerp(new Color(0.005f, 0.01f, 0.03f), new Color(0.02f, 0.03f, 0.09f), v);
                        break;
                    case FluidSimulation.AnimationStyle.Neon:
                        float r = Vector2.Distance(new Vector2(u * 1.7f, v), new Vector2(0.85f, 0.5f));
                        float ring = Mathf.Exp(-Mathf.Pow((Mathf.Repeat(r * 6f, 1f) - 0.5f) * 9f, 2f)) * Mathf.Exp(-r * 2f);
                        c = Color.HSVToRGB(Mathf.Repeat(r * 1.3f + 0.55f, 1f), 0.9f, 1f) * ring * 1.3f;
                        break;
                    default: // Classic
                        c = Blob(u, v, 0.28f, 0.55f, 0.18f, new Color(1f, 0.1f, 0.5f))
                          + Blob(u, v, 0.55f, 0.4f, 0.2f, new Color(0.1f, 0.6f, 1f))
                          + Blob(u, v, 0.78f, 0.62f, 0.16f, new Color(1f, 0.7f, 0.1f));
                        break;
                }
                px[y * w + x] = c;
            }

            if (style == FluidSimulation.AnimationStyle.Water)
                for (int i = 0; i < 260; i++) Dot(px, w, h, rng.Next(w), rng.Next(h), (float)rng.NextDouble() * 0.8f + 0.2f, new Color(0.8f, 0.9f, 1f));
            if (style == FluidSimulation.AnimationStyle.ShootingStars || style == FluidSimulation.AnimationStyle.Comets)
                for (int i = 0; i < 70; i++) Dot(px, w, h, rng.Next(w), rng.Next(h), (float)rng.NextDouble() * 0.6f, Color.white);
            if (style == FluidSimulation.AnimationStyle.ShootingStars)
            {
                Streak(px, w, h, 40, 30, 150, 95, 1.2f, 1.6f, new Color(1f, 0.97f, 0.9f));
                Streak(px, w, h, 90, 20, 190, 75, 1.0f, 1.3f, new Color(0.8f, 0.9f, 1f));
                Streak(px, w, h, 20, 70, 95, 118, 1.0f, 1.2f, new Color(1f, 0.9f, 0.8f));
            }
            if (style == FluidSimulation.AnimationStyle.Comets)
            {
                Streak(px, w, h, 20, 40, 150, 90, 5f, 3.2f, new Color(0.3f, 0.7f, 1f));
                Streak(px, w, h, 60, 105, 185, 60, 4f, 2.6f, new Color(1f, 0.55f, 0.2f));
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        static Color Blob(float u, float v, float cx, float cy, float r, Color col)
        {
            float d = new Vector2((u - cx) * 1.7f, v - cy).sqrMagnitude;
            return col * Mathf.Exp(-d / (r * r));
        }

        static void Dot(Color[] px, int w, int h, int x, int y, float b, Color col)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            px[y * w + x] += col * b;
        }

        /// <summary>A tail from (x0,y0) brightening towards a glowing head at (x1,y1).</summary>
        static void Streak(Color[] px, int w, int h, int x0, int y0, int x1, int y1, float head, float gain, Color col)
        {
            int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)) * 2;
            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                float fx = Mathf.Lerp(x0, x1, t), fy = Mathf.Lerp(y0, y1, t);
                float radius = Mathf.Lerp(0.6f, head * 0.5f + 0.6f, t * t);
                int r = Mathf.CeilToInt(radius + 1);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = Mathf.RoundToInt(fx) + dx, y = Mathf.RoundToInt(fy) + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    float d2 = (x - fx) * (x - fx) + (y - fy) * (y - fy);
                    px[y * w + x] += col * (t * t * gain * Mathf.Exp(-d2 / (radius * radius)) / steps * 6f);
                }
            }
        }
    }
}
