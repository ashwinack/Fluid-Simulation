using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FluidSim
{
    /// <summary>
    /// The colour manager: a dialog over the control panel, opened from its top-right
    /// button, for building the stroke palette. Every stroke starts on colour 1 and
    /// blends through the palette in order, so the head of a stroke shows the newest
    /// colour and its tail keeps the earlier ones.
    ///
    /// Like the window manager, edits are a draft: "Apply" pushes them to the running
    /// wall, "Save &amp; apply" also writes them to fluid-settings.json, and "Close"
    /// discards anything not applied.
    /// </summary>
    public partial class FluidControlPanel
    {
        const float CmSwatchSize = 64f;

        class CmSwatch
        {
            public GameObject root;
            public Image frame, fill;
            public Text number;
        }

        RectTransform cmOverlay;
        Text cmStatusText, cmTimeText;
        Slider cmTimeSlider;
        Image cmRandomImg, cmPaletteImg, cmLoopImg, cmStayImg;
        Texture2D cmPreviewTex;
        Action<Color> cmLoadEditor;
        readonly CmSwatch[] cmSwatches = new CmSwatch[FluidSimulation.MaxPaletteColors];

        // Draft state.
        readonly List<Color> cmPalette = new List<Color>();
        int cmSelected;
        bool cmUsePalette, cmLoop;
        float cmSeconds;

        void BuildColorManager(Transform parent)
        {
            cmOverlay = NewRect("ColorManager", parent);
            cmOverlay.anchorMin = Vector2.zero;
            cmOverlay.anchorMax = Vector2.one;
            cmOverlay.offsetMin = Vector2.zero;
            cmOverlay.offsetMax = Vector2.zero;
            cmOverlay.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.78f);

            var dialog = NewRect("Dialog", cmOverlay);
            dialog.anchorMin = dialog.anchorMax = new Vector2(0.5f, 0.5f);
            dialog.sizeDelta = new Vector2(1240f, 830f);
            dialog.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.13f, 1f);
            var vlg = dialog.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(32, 32, 28, 28);
            vlg.spacing = 10f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            NewCell(NewRow(dialog, 44f), "COLOR MANAGER", 28, TextAnchor.MiddleLeft, Accent, 0f);

            var info = NewCell(NewRow(dialog, 48f),
                "Every stroke starts on color 1 and blends through the palette in order - the head of a stroke " +
                "shows the newest color, its tail keeps the earlier ones. Tap a color to edit it, add up to " +
                FluidSimulation.MaxPaletteColors + ". Random = rainbow colors like the original demo.",
                17, TextAnchor.UpperLeft, WmMuted, 0f);
            info.horizontalOverflow = HorizontalWrapMode.Wrap;
            info.verticalOverflow = VerticalWrapMode.Overflow;

            // Random / Palette.
            var mode = NewRow(dialog, 50f);
            NewCell(mode, "stroke colors", 20, TextAnchor.MiddleLeft, Color.white, 160f);
            cmRandomImg = FixedButton(mode, "Random", 170f, () => { cmUsePalette = false; CmRefresh(); }).image;
            cmPaletteImg = FixedButton(mode, "Palette", 170f, () => { cmUsePalette = true; CmRefresh(); }).image;
            NewCell(mode, "", 16, TextAnchor.MiddleLeft, WmMuted, 0f);

            // The palette itself: one swatch per colour, then the list controls.
            var pal = NewRow(dialog, CmSwatchSize);
            NewCell(pal, "palette", 20, TextAnchor.MiddleLeft, Color.white, 160f);
            for (int i = 0; i < cmSwatches.Length; i++) cmSwatches[i] = BuildSwatch(pal, i);
            var spacer = NewRect("Spacer", pal);
            spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            FixedButton(pal, "<", 50f, () => CmMove(-1));
            FixedButton(pal, ">", 50f, () => CmMove(+1));
            FixedButton(pal, "+ Add", 100f, CmAdd);
            FixedButton(pal, "Remove", 110f, CmRemove);

            // How one stroke runs through the palette, start to end.
            var preview = NewRow(dialog, 40f);
            NewCell(preview, "stroke:  start", 18, TextAnchor.MiddleLeft, WmMuted, 160f);
            var bar = NewRect("Gradient", preview);
            bar.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var barImg = bar.gameObject.AddComponent<RawImage>();
            barImg.raycastTarget = false;
            cmPreviewTex = NewGradientTexture();
            barImg.texture = cmPreviewTex;
            NewCell(preview, "end", 18, TextAnchor.MiddleLeft, WmMuted, 60f);

            // Editor for the selected colour.
            cmLoadEditor = BuildColorEditor(dialog, 220f, 330f, c =>
            {
                if (cmSelected < 0 || cmSelected >= cmPalette.Count) return;
                cmPalette[cmSelected] = c;
                cmUsePalette = true;    // editing a colour means you want the palette
                CmRefresh();
            });

            // Timing.
            var timing = NewRow(dialog, 46f);
            NewCell(timing, "time per color (s)", 18, TextAnchor.MiddleLeft, Color.white, 190f);
            cmTimeSlider = CreateSlider(timing, 0.1f, 10f, 1.5f, false);
            cmTimeText = NewCell(timing, "", 18, TextAnchor.MiddleRight, ValueColor, 70f);
            cmTimeSlider.onValueChanged.AddListener(v =>
            {
                cmSeconds = v;
                cmTimeText.text = v.ToString("0.0");
            });
            NewCell(timing, "after last color", 18, TextAnchor.MiddleRight, Color.white, 170f);
            cmLoopImg = FixedButton(timing, "Loop", 110f, () => { cmLoop = true; CmRefresh(); }).image;
            cmStayImg = FixedButton(timing, "Stay", 110f, () => { cmLoop = false; CmRefresh(); }).image;

            var buttons = NewRow(dialog, 54f);
            NewButton(buttons, "Apply", () => CmApply(false));
            NewButton(buttons, "Save & apply", () => CmApply(true));
            NewButton(buttons, "Close", () => cmOverlay.gameObject.SetActive(false));

            cmStatusText = NewCell(NewRow(dialog, 30f), "", 18, TextAnchor.MiddleLeft, WmMuted, 0f);

            cmOverlay.gameObject.SetActive(false);
        }

        CmSwatch BuildSwatch(Transform row, int index)
        {
            var go = new GameObject("Swatch" + (index + 1), typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(row, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = CmSwatchSize;
            le.flexibleWidth = 0f;

            var sw = new CmSwatch { root = go, frame = go.GetComponent<Image>() };

            var inner = NewRect("Fill", go.transform);
            inner.anchorMin = Vector2.zero;
            inner.anchorMax = Vector2.one;
            inner.offsetMin = new Vector2(4f, 4f);
            inner.offsetMax = new Vector2(-4f, -4f);
            sw.fill = inner.gameObject.AddComponent<Image>();
            sw.fill.raycastTarget = false;

            sw.number = NewText(inner, (index + 1).ToString(), 18, TextAnchor.MiddleCenter, Color.white);
            sw.number.rectTransform.anchorMin = Vector2.zero;
            sw.number.rectTransform.anchorMax = Vector2.one;
            sw.number.rectTransform.offsetMin = Vector2.zero;
            sw.number.rectTransform.offsetMax = Vector2.zero;

            var button = go.GetComponent<Button>();
            button.targetGraphic = sw.frame;
            button.transition = Selectable.Transition.None;   // the frame shows selection
            button.onClick.AddListener(() => CmSelect(index));
            return sw;
        }

        // ---------- open / edit ----------

        void OpenColorManager()
        {
            if (cmOverlay == null || sim == null) return;

            cmPalette.Clear();
            cmPalette.AddRange(sim.palette);
            if (cmPalette.Count == 0) cmPalette.Add(Color.white);
            cmUsePalette = sim.usePalette;
            cmLoop = sim.paletteLoop;
            cmSeconds = sim.paletteSecondsPerColor;
            cmTimeSlider.SetValueWithoutNotify(cmSeconds);
            cmTimeText.text = cmSeconds.ToString("0.0");
            cmStatusText.text = "";

            CmSelect(0);
            cmOverlay.SetAsLastSibling();
            cmOverlay.gameObject.SetActive(true);
        }

        void CmSelect(int index)
        {
            cmSelected = Mathf.Clamp(index, 0, cmPalette.Count - 1);
            cmLoadEditor(cmPalette[cmSelected]);
            CmRefresh();
        }

        void CmAdd()
        {
            if (cmPalette.Count >= FluidSimulation.MaxPaletteColors)
            {
                CmStatus($"The palette holds at most {FluidSimulation.MaxPaletteColors} colors.", true);
                return;
            }
            // Start the new colour a step round the hue wheel from the selected one, so
            // it is visibly a new entry rather than a duplicate.
            Color.RGBToHSV(cmPalette[cmSelected], out float h, out float s, out float v);
            var next = Color.HSVToRGB(Mathf.Repeat(h + 0.12f, 1f), Mathf.Max(s, 0.6f), Mathf.Max(v, 0.8f));
            cmPalette.Insert(cmSelected + 1, next);
            cmUsePalette = true;
            CmSelect(cmSelected + 1);
        }

        void CmRemove()
        {
            if (cmPalette.Count <= 1)
            {
                CmStatus("The palette needs at least one color - use Random to turn it off.", true);
                return;
            }
            cmPalette.RemoveAt(cmSelected);
            CmSelect(Mathf.Min(cmSelected, cmPalette.Count - 1));
        }

        void CmMove(int direction)
        {
            int to = cmSelected + direction;
            if (to < 0 || to >= cmPalette.Count) return;
            (cmPalette[cmSelected], cmPalette[to]) = (cmPalette[to], cmPalette[cmSelected]);
            cmSelected = to;
            CmRefresh();
        }

        void CmRefresh()
        {
            for (int i = 0; i < cmSwatches.Length; i++)
            {
                var sw = cmSwatches[i];
                bool shown = i < cmPalette.Count;
                sw.root.SetActive(shown);
                if (!shown) continue;
                Color c = cmPalette[i];
                sw.fill.color = c;
                sw.frame.color = i == cmSelected ? Color.white : new Color(0.2f, 0.2f, 0.24f);
                float luma = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                sw.number.color = luma > 0.55f ? Color.black : Color.white;
            }

            cmRandomImg.color = cmUsePalette ? WmUnselected : WmSelected;
            cmPaletteImg.color = cmUsePalette ? WmSelected : WmUnselected;
            cmLoopImg.color = cmLoop ? WmSelected : WmUnselected;
            cmStayImg.color = cmLoop ? WmUnselected : WmSelected;

            PaintGradient(cmPreviewTex, cmPalette.ToArray(), cmLoop);
        }

        void CmApply(bool save)
        {
            sim.SetPalette(cmUsePalette, cmPalette.ToArray(), cmSeconds, cmLoop);
            string what = cmUsePalette ? $"Palette of {cmPalette.Count} applied" : "Random colors applied";

            if (!save)
            {
                CmStatus(what + " (not saved - use Save & apply to keep it).", false);
                SetStatus(what);
                return;
            }
            bool saved = sim.SavePaletteSettings();
            CmStatus(saved ? what + " and saved to fluid-settings.json."
                           : what + ", but fluid-settings.json could not be written - see the log.", !saved);
            SetStatus(saved ? what + " and saved" : what + ", NOT saved", !saved);
        }

        void CmStatus(string message, bool warning)
        {
            cmStatusText.text = message;
            cmStatusText.color = warning ? new Color(1f, 0.65f, 0.4f) : new Color(0.6f, 0.9f, 0.6f);
        }

        /// <summary>Where the colour editor reports a bad typed code.</summary>
        void ColorEditorWarning(string message)
        {
            if (cmOverlay != null && cmOverlay.gameObject.activeSelf) CmStatus(message, true);
            else SetStatus(message, true);
        }

        // ---------- palette preview ----------

        /// <summary>Main-panel strip showing how a stroke runs through the live palette.</summary>
        void AddPalettePreview(Transform column)
        {
            var row = NewRow(column, RowHeight);
            NewCell(row, "stroke", 18, TextAnchor.MiddleLeft, Color.white, LabelWidth);
            var bar = NewRect("Gradient", row);
            bar.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var img = bar.gameObject.AddComponent<RawImage>();
            img.raycastTarget = false;
            var tex = NewGradientTexture();
            img.texture = tex;

            int painted = -1;
            bool paintedUse = false;
            refreshers.Add(() =>
            {
                if (painted == sim.PaletteVersion && paintedUse == sim.usePalette) return;
                painted = sim.PaletteVersion;
                paintedUse = sim.usePalette;
                PaintGradient(tex, sim.palette, sim.paletteLoop);
                // Dimmed while the palette is off, so it does not look like it is in use.
                img.color = sim.usePalette ? Color.white : new Color(1f, 1f, 1f, 0.3f);
            });
        }

        static Texture2D NewGradientTexture()
        {
            var t = new Texture2D(256, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            return t;
        }

        /// <summary>
        /// Draw one stroke's colour run into a strip: colour 1 on the left, then each
        /// next colour, with the same smooth blend the simulation uses. A looping
        /// palette is drawn through to its return to colour 1.
        /// </summary>
        static void PaintGradient(Texture2D tex, Color[] pal, bool loop)
        {
            int w = tex.width;
            var px = new Color32[w];
            int n = pal != null ? pal.Length : 0;
            for (int x = 0; x < w; x++)
            {
                Color c;
                if (n == 0) c = Color.black;
                else if (n == 1) c = pal[0];
                else
                {
                    int segments = loop ? n : n - 1;
                    float f = x / (w - 1f) * segments;
                    int i = Mathf.Min(Mathf.FloorToInt(f), segments - 1);
                    float t = f - i;
                    t = t * t * (3f - 2f * t);
                    c = Color.Lerp(pal[i % n], pal[(i + 1) % n], t);
                }
                c.a = 1f;
                px[x] = c;
            }
            tex.SetPixels32(px);
            tex.Apply(false);
        }
    }
}
