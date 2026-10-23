using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FluidSim
{
    /// <summary>
    /// Runtime parameter panel for <see cref="FluidSimulation"/>, equivalent to the
    /// dat.GUI panel in the original WebGL demo.
    ///
    /// The UI is built from code into <see cref="panelCanvas"/>, which lives on the
    /// control display. It never draws over the fluid: the fluid canvas owns display 1
    /// and this one owns display 2.
    ///
    /// Layout is a fixed three-column grid authored for a 1080p control screen, so
    /// every control fits without scrolling and no row ever has to compress.
    /// </summary>
    public partial class FluidControlPanel : MonoBehaviour
    {
        public FluidSimulation sim;

        [Tooltip("Canvas the controls are built into. Should target the control display. " +
                 "If left empty one is created targeting display 2.")]
        public Canvas panelCanvas;

        [Tooltip("Display index the control panel renders on. 0 = display 1, which is " +
                 "where the controls live while the fluid spans displays 2 and up.")]
        public int controlDisplayIndex = 0;

        // The control screen is authored at 1080p and scaled to whatever it lands on.
        const float RefWidth = 1920f;
        const float RefHeight = 1080f;

        const float SidePadding = 32f;
        const float ColumnSpacing = 28f;
        const float TitleBand = 76f;

        const float LabelWidth = 175f;
        const float ValueWidth = 105f;
        const float RowSpacing = 6f;
        const float CellSpacing = 10f;
        const float RowHeight = 42f;
        const float HeaderHeight = 46f;
        const float ButtonHeight = 46f;
        // Comfortably inside the 48px row pitch so handles never touch their neighbours.
        const float HandleHeight = 20f;

        static readonly Color Accent = new Color(0.45f, 0.8f, 1f);
        static readonly Color ValueColor = new Color(0.55f, 0.85f, 1f);

        Font font;
        RectTransform col1, col2, col3;
        readonly List<Action> refreshers = new List<Action>();
        float smoothedFrameTime = 1f / 60f;

        Text statusText;
        float statusClearTime;

        static readonly int[] SimResolutions = { 32, 64, 128, 256 };
        static readonly int[] DyeResolutions = { 128, 256, 512, 1024, 2048 };

        const float WindowButtonWidth = 240f;

        static readonly int[] RevealFitValues = { 0, 1, 2, 3 };
        static readonly string[] RevealFitNames = { "Fill wall", "Fit wall", "Stretch", "Each screen" };

        void Awake()
        {
            if (sim == null) sim = FindAnyObjectByType<FluidSimulation>();

            // Extra displays do not exist until activated. Harmless with one display,
            // required for the second window in a build.
            for (int i = 1; i < Display.displays.Length; i++)
            {
                if (!Display.displays[i].active) Display.displays[i].Activate();
            }

            // The config may have moved the control screen; honour it before building.
            if (sim != null)
            {
                if (sim.ControlDisplayIndex >= 0) controlDisplayIndex = sim.ControlDisplayIndex;
                sim.ControlDisplayIndex = controlDisplayIndex;
            }

            font = LoadFont();
            BuildUI();
        }

        static Font LoadFont()
        {
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (f == null)
            {
                try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            }
            return f;
        }

        void BuildUI()
        {
            if (panelCanvas == null) panelCanvas = CreateCanvas();

            // Never key off Display.displays.Length here: it is always 1 inside the
            // editor even when a second Game view is showing display 2.
            panelCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            panelCanvas.targetDisplay = controlDisplayIndex;
            panelCanvas.sortingOrder = 100;

            var scaler = panelCanvas.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = panelCanvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefWidth, RefHeight);
            scaler.matchWidthOrHeight = 1f;          // lock to height -> always 1080p tall

            if (panelCanvas.GetComponent<GraphicRaycaster>() == null)
                panelCanvas.gameObject.AddComponent<GraphicRaycaster>();

            ClearChildren(panelCanvas.transform);

            var bg = NewRect("Panel", panelCanvas.transform);
            bg.anchorMin = Vector2.zero;
            bg.anchorMax = Vector2.one;
            bg.offsetMin = Vector2.zero;
            bg.offsetMax = Vector2.zero;
            bg.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.09f, 1f);

            var title = NewText(bg, "FLUID SIMULATION CONTROLS", 30, TextAnchor.MiddleLeft, Accent);
            var tr = title.rectTransform;
            tr.anchorMin = new Vector2(0f, 1f);
            tr.anchorMax = new Vector2(1f, 1f);
            tr.pivot = new Vector2(0.5f, 1f);
            tr.offsetMin = new Vector2(SidePadding, -TitleBand + 14f);
            tr.offsetMax = new Vector2(-SidePadding, -18f);

            statusText = NewText(bg, "", 18, TextAnchor.MiddleRight, new Color(0.6f, 0.9f, 0.6f));
            var sr = statusText.rectTransform;
            sr.anchorMin = new Vector2(0f, 1f);
            sr.anchorMax = new Vector2(1f, 1f);
            sr.pivot = new Vector2(0.5f, 1f);
            sr.offsetMin = new Vector2(SidePadding, -TitleBand + 14f);
            sr.offsetMax = new Vector2(-SidePadding - 4f * (WindowButtonWidth + 12f) - 8f, -18f);

            // Top-right corner, right to left: the window manager (displays,
            // resolution, mode), the colour manager (palette), the animation style
            // picker, whose label shows the style on the wall, and the background
            // image behind the spotlight.
            TopRightButton(bg, "WindowManagerButton", "WINDOW MANAGER", 0, OpenWindowManager);
            TopRightButton(bg, "ColorManagerButton", "COLOR MANAGER", 1, OpenColorManager);
            var styleButton = TopRightButton(bg, "StylePickerButton", "STYLE", 2, OpenStylePicker);
            TopRightButton(bg, "BackgroundButton", "BACKGROUND", 3, OpenBackgroundDialog);
            var styleLabel = styleButton.GetComponentInChildren<Text>();
            refreshers.Add(() =>
                styleLabel.text = "STYLE: " + FluidSimulation.AnimationStyleNames[(int)sim.animationStyle].ToUpperInvariant());

            var columns = NewRect("Columns", bg);
            columns.anchorMin = Vector2.zero;
            columns.anchorMax = Vector2.one;
            columns.offsetMin = new Vector2(SidePadding, 24f);
            columns.offsetMax = new Vector2(-SidePadding, -TitleBand);
            var hlg = columns.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = ColumnSpacing;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = false;
            hlg.childAlignment = TextAnchor.UpperLeft;

            col1 = NewColumn("Column1", columns);
            col2 = NewColumn("Column2", columns);
            col3 = NewColumn("Column3", columns);

            BuildColumn1();
            BuildColumn2();
            BuildColumn3();

            // Last, so they draw over the columns when open.
            BuildWindowManager(bg);
            BuildColorManager(bg);
            BuildStylePicker(bg);
            BuildBackgroundDialog(bg);
        }

        /// <summary>A button in the title band, <paramref name="slot"/> places from the right.</summary>
        Button TopRightButton(Transform parent, string name, string label, int slot, Action onClick)
        {
            var rect = NewRect(name, parent);
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(WindowButtonWidth, 44f);
            rect.anchoredPosition = new Vector2(-SidePadding - slot * (WindowButtonWidth + 12f), -16f);
            return NewButton(rect, label, onClick);
        }

        Canvas CreateCanvas()
        {
            var go = new GameObject("FluidControlCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);
            return go.GetComponent<Canvas>();
        }

        static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) Destroy(t.GetChild(i).gameObject);
        }

        RectTransform NewColumn(string name, Transform parent)
        {
            var col = NewRect(name, parent);
            var vlg = col.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = RowSpacing;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;
            // No ContentSizeFitter: it re-sized the columns about their centre pivot, so
            // shorter columns started lower than taller ones. The parent layout sizes
            // them instead. flexibleHeight 0 keeps each at its natural height (rows force
            // -expand, which would otherwise make the column soak up the spare height
            // and space its rows out), so all three start flush at the top.
            col.gameObject.AddComponent<LayoutElement>().flexibleHeight = 0f;
            return col;
        }

        // ---------- content ----------

        void BuildColumn1()
        {
            AddHeader(col1, "SIMULATION  (CLASSIC)");
            AddOptionSlider(col1, "sim resolution", null, SimResolutions,
                () => sim.simResolution, v => { sim.simResolution = v; sim.ReinitFramebuffers(); });
            AddOptionSlider(col1, "dye resolution", null, DyeResolutions,
                () => sim.dyeResolution, v => { sim.dyeResolution = v; sim.ReinitFramebuffers(); });
            AddSlider(col1, "density diffusion", 0f, 4f, () => sim.densityDissipation, v => sim.densityDissipation = v);
            AddSlider(col1, "velocity diffusion", 0f, 4f, () => sim.velocityDissipation, v => sim.velocityDissipation = v);
            AddSlider(col1, "pressure", 0f, 1f, () => sim.pressure, v => sim.pressure = v);
            AddSlider(col1, "pressure iterations", 1f, 50f, () => sim.pressureIterations,
                v => sim.pressureIterations = Mathf.RoundToInt(v), true);
            AddSlider(col1, "vorticity", 0f, 50f, () => sim.curl, v => sim.curl = v);
            AddSlider(col1, "splat radius", 0.01f, 1f, () => sim.splatRadius, v => sim.splatRadius = v);
            AddSlider(col1, "splat force", 1000f, 12000f, () => sim.splatForce, v => sim.splatForce = v, true);

            // Buttons are paired two to a row so column 1 also has room for FLUID DISPLAY,
            // which frees column 2 for the colour picker.
            AddHeader(col1, "FIELD");
            AddButtons(col1,
                ("Random splats", () => sim.MultipleSplats(sim.initialSplats)),
                ("Reset field", () => sim.ReinitFramebuffers()));

            AddHeader(col1, "SETTINGS");
            AddButtons(col1,
                ("Save settings", () =>
                {
                    if (sim.SaveSettings())
                        SetStatus("Saved to " + System.IO.Path.GetDirectoryName(sim.ActiveConfigPath));
                    else
                        SetStatus("Could not write the settings files - see the log", true);
                }),
                ("Reset to defaults", () =>
                {
                    sim.ResetToDefaults();
                    SetStatus("Reset to defaults");
                }));

            // Everything here is changed in the WINDOW MANAGER (top-right button).
            AddHeader(col1, "FLUID DISPLAY");
            AddReadout(col1, "displays in use", () =>
                $"{sim.DisplaysInUse}  (1 controls + {sim.FluidDisplayCount} fluid)");
            AddReadout(col1, "control window", () =>
                sim.ModeOf(0) == FluidSimulation.WindowMode.Windowed ? "Windowed" : "Borderless");
            AddReadout(col1, "wall size", () => sim.CurrentDisplaySize);
            AddReadout(col1, "screens", () => sim.DescribeWall());
        }

        void BuildColumn2()
        {
            AddHeader(col2, "APPEARANCE  (CLASSIC)");
            AddSlider(col2, "color update speed", 0f, 30f, () => sim.colorUpdateSpeed, v => sim.colorUpdateSpeed = v);
            AddSlider(col2, "exposure", 1f, 20f, () => sim.exposure, v => sim.exposure = v);
            AddToggle(col2, "draw on hover", () => sim.drawOnHover, v => sim.drawOnHover = v);
            AddToggle(col2, "shading", () => sim.shading, v => sim.shading = v);
            AddToggle(col2, "colorful", () => sim.colorful, v => sim.colorful = v);
            AddToggle(col2, "paused", () => sim.paused, v => sim.paused = v);

            // Colours are edited in the COLOR MANAGER (top-right button); this is the
            // quick on/off and a preview of how a stroke runs through the palette.
            AddHeader(col2, "COLOR");
            AddToggle(col2, "use color palette", () => sim.usePalette, v => sim.usePalette = v);
            AddPalettePreview(col2);
            AddReadout(col2, "palette", () =>
                $"{sim.palette.Length} color{(sim.palette.Length == 1 ? "" : "s")}, " +
                $"{sim.paletteSecondsPerColor:0.0}s each, {(sim.paletteLoop ? "loop" : "stay on last")}");

            AddHeader(col2, "ATTRACT MODE");
            AddToggle(col2, "enabled", () => sim.idleMode, v => sim.idleMode = v);
            AddToggle(col2, "during interaction", () => sim.attractDuringInteraction,
                v => sim.attractDuringInteraction = v);
            AddSlider(col2, "idle delay (s)", 0f, 120f, () => sim.idleDelay, v => sim.idleDelay = v, true);
            AddSlider(col2, "virtual fingers", 1f, 8f, () => sim.idlePointers,
                v => sim.idlePointers = Mathf.RoundToInt(v), true);
            AddSlider(col2, "wander speed", 0.01f, 1f, () => sim.idleSpeed, v => sim.idleSpeed = v);
        }

        void BuildColumn3()
        {
            AddHeader(col3, "TIMING");
            AddSlider(col3, "simulation rate", 15f, 144f, () => sim.simulationRate, v => sim.simulationRate = v, true);
            AddSlider(col3, "simulation speed", 0.1f, 4f, () => sim.simulationSpeed, v => sim.simulationSpeed = v);

            AddHeader(col3, "BLOOM  (CLASSIC)");
            AddToggle(col3, "enabled", () => sim.bloom, v => sim.bloom = v);
            AddSlider(col3, "intensity", 0f, 2f, () => sim.bloomIntensity, v => sim.bloomIntensity = v);
            AddSlider(col3, "threshold", 0f, 2f, () => sim.bloomThreshold, v => sim.bloomThreshold = v);
            AddSlider(col3, "soft knee", 0f, 1f, () => sim.bloomSoftKnee, v => sim.bloomSoftKnee = v);

            AddHeader(col3, "SUNRAYS  (CLASSIC)");
            AddToggle(col3, "enabled", () => sim.sunrays, v => sim.sunrays = v);
            AddSlider(col3, "weight", 0f, 2f, () => sim.sunraysWeight, v => sim.sunraysWeight = v);

            // The logo image hidden behind the fluid, shown only inside a spotlight that
            // follows the mouse while it hovers and every finger on the glass.
            AddHeader(col3, "LOGO SPOTLIGHT");
            AddToggle(col3, "enabled", () => sim.revealMode, v => sim.revealMode = v);
            AddOptionSlider(col3, "image fit", RevealFitNames, RevealFitValues,
                () => (int)sim.revealFit, v => sim.revealFit = (FluidSimulation.RevealFit)v);
            AddSlider(col3, "spotlight size", 0.03f, 0.6f, () => sim.spotlightSize, v => sim.spotlightSize = v);
            AddSlider(col3, "fluid over logo", 0f, 1f, () => sim.revealFluidOpacity, v => sim.revealFluidOpacity = v);
            AddSlider(col3, "logo outside", 0f, 1f, () => sim.revealBackground, v => sim.revealBackground = v);

            // One row, to leave room for the section above. Touch count is handy on
            // site: touch a fluid screen and watch it count the fingers.
            AddHeader(col3, "STATS");
            AddReadout(col3, "status", () =>
                $"{Mathf.RoundToInt(1f / Mathf.Max(smoothedFrameTime, 0.0001f))} fps, " +
                $"{sim.ActivePointerCount} touch{(sim.ActivePointerCount == 1 ? "" : "es")}, {sim.ModeDescription}");
        }

        void SetStatus(string message, bool warning = false)
        {
            if (statusText == null) return;
            statusText.text = message;
            statusText.color = warning ? new Color(1f, 0.65f, 0.4f) : new Color(0.6f, 0.9f, 0.6f);
            statusClearTime = Time.unscaledTime + 4f;
        }

        // ---------- primitives ----------

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        Text NewText(Transform parent, string value, int size, TextAnchor anchor, Color textColor)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.text = value;
            t.color = textColor;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.raycastTarget = false;
            return t;
        }

        Text NewCell(Transform parent, string value, int size, TextAnchor anchor, Color textColor, float width)
        {
            var t = NewText(parent, value, size, anchor, textColor);
            var le = t.gameObject.AddComponent<LayoutElement>();
            if (width > 0f) { le.preferredWidth = width; le.minWidth = width; le.flexibleWidth = 0f; }
            else le.flexibleWidth = 1f;
            return t;
        }

        RectTransform NewRow(Transform column, float height)
        {
            var row = NewRect("Row", column);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = CellSpacing;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            return row;
        }

        void AddHeader(Transform column, string label)
        {
            var row = NewRow(column, HeaderHeight);
            NewCell(row, label, 22, TextAnchor.LowerLeft, Accent, 0f);
        }

        void AddReadout(Transform column, string label, Func<string> get)
        {
            var row = NewRow(column, RowHeight);
            NewCell(row, label, 18, TextAnchor.MiddleLeft, Color.white, LabelWidth);
            var v = NewCell(row, get(), 18, TextAnchor.MiddleRight, ValueColor, 0f);
            refreshers.Add(() => v.text = get());
        }

        void AddSlider(Transform column, string label, float min, float max,
                       Func<float> get, Action<float> set, bool whole = false)
        {
            var row = NewRow(column, RowHeight);
            NewCell(row, label, 18, TextAnchor.MiddleLeft, Color.white, LabelWidth);
            var slider = CreateSlider(row, min, max, get(), whole);
            var value = NewCell(row, Format(get(), whole), 18, TextAnchor.MiddleRight, ValueColor, ValueWidth);

            slider.onValueChanged.AddListener(v =>
            {
                set(v);
                value.text = Format(v, whole);
            });

            refreshers.Add(() =>
            {
                float v = get();
                if (!Mathf.Approximately(slider.value, v))
                {
                    slider.SetValueWithoutNotify(v);
                    value.text = Format(v, whole);
                }
            });
        }

        /// <summary>Slider that steps through a fixed list of values, e.g. resolutions.</summary>
        void AddOptionSlider(Transform column, string label, string[] names, int[] values,
                             Func<int> get, Action<int> set)
        {
            int startIndex = Mathf.Max(0, Array.IndexOf(values, get()));

            var row = NewRow(column, RowHeight);
            NewCell(row, label, 18, TextAnchor.MiddleLeft, Color.white, LabelWidth);
            var slider = CreateSlider(row, 0, values.Length - 1, startIndex, true);
            string Name(int idx) => names != null ? names[idx] : values[idx].ToString();
            var value = NewCell(row, Name(startIndex), 18, TextAnchor.MiddleRight, ValueColor, ValueWidth);

            // onValueChanged fires continuously while the handle is dragged, including
            // for the same discrete index many times per second. These setters rebuild
            // every framebuffer, so firing on repeats churned ~20 RenderTextures per
            // frame and exhausted the D3D12 descriptor heap (CreateDescriptorHeap
            // E_OUTOFMEMORY -> unrecoverable device error). Only act on a real change.
            int appliedIndex = startIndex;
            slider.onValueChanged.AddListener(v =>
            {
                int idx = Mathf.Clamp(Mathf.RoundToInt(v), 0, values.Length - 1);
                if (idx == appliedIndex) return;
                appliedIndex = idx;
                value.text = Name(idx);
                set(values[idx]);
            });

            refreshers.Add(() =>
            {
                int idx = Mathf.Max(0, Array.IndexOf(values, get()));
                if (idx != appliedIndex)
                {
                    appliedIndex = idx;
                    slider.SetValueWithoutNotify(idx);
                }
                value.text = Name(idx);
            });
        }

        Slider CreateSlider(Transform parent, float min, float max, float value, bool whole)
        {
            var go = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minWidth = 160f;

            var bg = NewRect("Background", go.transform);
            bg.anchorMin = new Vector2(0f, 0.5f);
            bg.anchorMax = new Vector2(1f, 0.5f);
            bg.sizeDelta = new Vector2(0f, 6f);
            bg.anchoredPosition = Vector2.zero;
            bg.gameObject.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.24f);

            var fillArea = NewRect("Fill Area", go.transform);
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.sizeDelta = new Vector2(-12f, 6f);
            fillArea.anchoredPosition = Vector2.zero;

            var fill = NewRect("Fill", fillArea);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(0f, 1f);
            fill.sizeDelta = new Vector2(12f, 0f);
            fill.gameObject.AddComponent<Image>().color = new Color(0.25f, 0.65f, 0.95f);

            // Slider.UpdateVisuals() rebuilds the handle's anchors every update as
            // (0,0)-(1,1) with only the slider axis collapsed to the value, so the
            // handle is always stretched on the cross axis and its height works out as
            // slideArea.height + handle.sizeDelta.y. Setting the handle's own vertical
            // anchors is pointless - Unity overwrites them. Constrain the slide area
            // instead, exactly as Unity's own Slider prefab does.
            var handleArea = NewRect("Handle Slide Area", go.transform);
            handleArea.anchorMin = new Vector2(0f, 0.5f);
            handleArea.anchorMax = new Vector2(1f, 0.5f);
            handleArea.sizeDelta = new Vector2(-12f, HandleHeight);
            handleArea.anchoredPosition = Vector2.zero;

            var handle = NewRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(12f, 0f);   // width 12, height follows the area
            handle.gameObject.AddComponent<Image>().color = new Color(0.9f, 0.94f, 1f);

            var slider = go.GetComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.wholeNumbers = whole;
            slider.minValue = min;
            slider.maxValue = max;
            slider.SetValueWithoutNotify(value);
            return slider;
        }

        void AddToggle(Transform column, string label, Func<bool> get, Action<bool> set)
        {
            var row = NewRow(column, RowHeight);
            NewCell(row, label, 18, TextAnchor.MiddleLeft, Color.white, LabelWidth);

            var go = new GameObject("Toggle", typeof(RectTransform), typeof(Toggle));
            go.transform.SetParent(row, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 26f;
            le.minWidth = 26f;
            le.flexibleWidth = 0f;

            var bg = NewRect("Background", go.transform);
            bg.anchorMin = new Vector2(0f, 0.5f);
            bg.anchorMax = new Vector2(0f, 0.5f);
            bg.pivot = new Vector2(0f, 0.5f);
            bg.sizeDelta = new Vector2(24f, 24f);
            bg.anchoredPosition = Vector2.zero;
            var bgImg = bg.gameObject.AddComponent<Image>();
            bgImg.color = new Color(0.2f, 0.2f, 0.24f);

            var check = NewRect("Checkmark", bg);
            check.anchorMin = Vector2.zero;
            check.anchorMax = Vector2.one;
            check.offsetMin = new Vector2(5f, 5f);
            check.offsetMax = new Vector2(-5f, -5f);
            var checkImg = check.gameObject.AddComponent<Image>();
            checkImg.color = new Color(0.25f, 0.75f, 1f);

            var toggle = go.GetComponent<Toggle>();
            toggle.targetGraphic = bgImg;
            toggle.graphic = checkImg;
            toggle.SetIsOnWithoutNotify(get());
            toggle.onValueChanged.AddListener(v => set(v));

            refreshers.Add(() =>
            {
                bool v = get();
                if (toggle.isOn != v) toggle.SetIsOnWithoutNotify(v);
            });

            var spacer = NewRect("Spacer", row);
            spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        }

        /// <summary>One row of equal-width buttons.</summary>
        void AddButtons(Transform column, params (string label, Action onClick)[] buttons)
        {
            var row = NewRow(column, ButtonHeight);
            foreach (var b in buttons) NewButton(row, b.label, b.onClick);
        }

        // ---------- colour picker ----------

        const float HueWidth = 30f;

        static readonly Color[] PresetColors =
        {
            new Color(1f, 0f, 0f), new Color(1f, 0.5f, 0f), new Color(1f, 1f, 0f),
            new Color(0f, 1f, 0.2f), new Color(0f, 1f, 1f),
            new Color(0f, 0.35f, 1f), new Color(0.55f, 0f, 1f), new Color(1f, 0f, 1f),
            new Color(1f, 0.3f, 0.6f), new Color(1f, 1f, 1f),
        };

        /// <summary>
        /// Saturation/value square, hue bar, a swatch you can type a colour code into,
        /// and one-tap presets, laid out as one row of <paramref name="height"/>.
        /// Every pick is reported through <paramref name="onChange"/>. Returns a setter
        /// that loads a colour into the editor without reporting it.
        /// </summary>
        Action<Color> BuildColorEditor(Transform column, float height, float svWidth, Action<Color> onChange)
        {
            float h = 0f, s = 1f, v = 1f;
            Color applied = Color.white;

            var row = NewRow(column, height);

            // The square is three layers blended in gamma space, which is exactly HSV:
            // pure hue, then white fading out left-to-right (saturation), then black
            // fading out bottom-to-top (value). Only the base colour changes with hue,
            // so dragging needs no texture rebuild.
            var sv = NewRect("SaturationValue", row);
            FixedWidth(sv, svWidth);
            var svBase = sv.gameObject.AddComponent<Image>();
            Overlay(sv, "Saturation", MakeRamp(horizontal: true), Color.white);
            Overlay(sv, "Value", MakeRamp(horizontal: false), Color.black);

            var svCursor = NewRect("Cursor", sv);
            svCursor.sizeDelta = new Vector2(18f, 18f);
            var ring = svCursor.gameObject.AddComponent<RawImage>();
            ring.texture = MakeRing();
            ring.raycastTarget = false;

            var hue = NewRect("Hue", row);
            FixedWidth(hue, HueWidth);
            var hueImg = hue.gameObject.AddComponent<RawImage>();
            hueImg.texture = MakeHueStrip();

            // Dark bar with a white core, so it reads against every hue.
            var hueCursor = NewRect("Cursor", hue);
            hueCursor.sizeDelta = new Vector2(8f, 7f);
            var hueCursorImg = hueCursor.gameObject.AddComponent<Image>();
            hueCursorImg.color = new Color(0.05f, 0.05f, 0.06f);
            hueCursorImg.raycastTarget = false;
            var hueCore = NewRect("Core", hueCursor);
            hueCore.anchorMin = Vector2.zero;
            hueCore.anchorMax = Vector2.one;
            hueCore.offsetMin = new Vector2(2f, 2f);
            hueCore.offsetMax = new Vector2(-2f, -2f);
            var hueCoreImg = hueCore.gameObject.AddComponent<Image>();
            hueCoreImg.raycastTarget = false;

            var side = NewRect("Side", row);
            side.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var vlg = side.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            // The swatch doubles as the colour-code box: tap it and type e.g. #FF0000.
            // Presets take two rows of 38 below; the swatch gets the rest of the height.
            var swatch = NewRect("Swatch", side);
            swatch.gameObject.AddComponent<LayoutElement>().preferredHeight = height - 2f * (38f + 6f);
            var swatchImg = swatch.gameObject.AddComponent<Image>();
            var hex = NewText(swatch, "", 22, TextAnchor.MiddleCenter, Color.white);
            hex.rectTransform.anchorMin = Vector2.zero;
            hex.rectTransform.anchorMax = Vector2.one;
            hex.rectTransform.offsetMin = Vector2.zero;
            hex.rectTransform.offsetMax = Vector2.zero;
            hex.supportRichText = false;
            // Underline marks the swatch as editable.
            var underline = NewRect("Underline", swatch);
            underline.anchorMin = new Vector2(0.25f, 0f);
            underline.anchorMax = new Vector2(0.75f, 0f);
            underline.pivot = new Vector2(0.5f, 0f);
            underline.sizeDelta = new Vector2(0f, 2f);
            underline.anchoredPosition = new Vector2(0f, 10f);
            var underlineImg = underline.gameObject.AddComponent<Image>();
            underlineImg.raycastTarget = false;

            var field = swatch.gameObject.AddComponent<InputField>();
            field.textComponent = hex;
            field.targetGraphic = swatchImg;
            field.transition = Selectable.Transition.None;   // keep the swatch its true colour
            field.characterLimit = 9;
            field.lineType = InputField.LineType.SingleLine;
            field.customCaretColor = true;

            void Refresh()
            {
                svBase.color = Color.HSVToRGB(h, 1f, 1f);
                svCursor.anchorMin = svCursor.anchorMax = new Vector2(s, v);
                hueCursor.anchorMin = new Vector2(0f, h);
                hueCursor.anchorMax = new Vector2(1f, h);
                swatchImg.color = applied;
                float luma = 0.299f * applied.r + 0.587f * applied.g + 0.114f * applied.b;
                Color ink = luma > 0.55f ? Color.black : Color.white;
                hex.color = ink;
                field.caretColor = ink;
                underlineImg.color = new Color(ink.r, ink.g, ink.b, 0.45f);
                // Never rewrite the text under the operator's fingers mid-typing.
                if (!field.isFocused)
                    field.SetTextWithoutNotify("#" + ColorUtility.ToHtmlStringRGB(applied));
            }

            void Publish()
            {
                applied.a = 1f;
                Refresh();
                onChange?.Invoke(applied);
            }

            void Commit()
            {
                applied = Color.HSVToRGB(h, s, v);
                Publish();
            }

            // Set from an exact colour (typed code), so the code the operator typed is
            // what is used, with no HSV round-trip.
            void CommitExact(Color c)
            {
                Color.RGBToHSV(c, out h, out s, out v);
                applied = c;
                Publish();
            }

            // Apply as soon as a full 6-digit code is typed, so the wall changes live;
            // shorter forms like #F00 and names like "red" are applied on Enter.
            field.onValueChanged.AddListener(text =>
            {
                string digits = text.Trim().TrimStart('#');
                if ((digits.Length == 6 || digits.Length == 8) && TryParseColorCode(text, out var c))
                    CommitExact(c);
            });
            field.onEndEdit.AddListener(text =>
            {
                if (TryParseColorCode(text, out var c))
                    CommitExact(c);
                else if (text.Trim().Length > 0)
                    ColorEditorWarning("\"" + text.Trim() + "\" is not a colour code - use e.g. #FF0000");
                field.SetTextWithoutNotify("#" + ColorUtility.ToHtmlStringRGB(applied));
            });

            sv.gameObject.AddComponent<FluidColorPickerArea>().onPick = uv => { s = uv.x; v = uv.y; Commit(); };
            hue.gameObject.AddComponent<FluidColorPickerArea>().onPick = uv => { h = Mathf.Min(uv.y, 0.999f); Commit(); };

            const int PerRow = 5;
            for (int start = 0; start < PresetColors.Length; start += PerRow)
            {
                var presetRow = NewRect("Presets", side);
                presetRow.gameObject.AddComponent<LayoutElement>().preferredHeight = 38f;
                var hlg = presetRow.gameObject.AddComponent<HorizontalLayoutGroup>();
                hlg.spacing = 6f;
                hlg.childControlWidth = true;
                hlg.childControlHeight = true;
                hlg.childForceExpandWidth = true;
                hlg.childForceExpandHeight = true;

                for (int i = start; i < Mathf.Min(start + PerRow, PresetColors.Length); i++)
                {
                    Color preset = PresetColors[i];
                    var go = new GameObject("Preset", typeof(RectTransform), typeof(Image), typeof(Button));
                    go.transform.SetParent(presetRow, false);
                    go.AddComponent<LayoutElement>().flexibleWidth = 1f;
                    var img = go.GetComponent<Image>();
                    img.color = preset;
                    var button = go.GetComponent<Button>();
                    button.targetGraphic = img;
                    button.onClick.AddListener(() => CommitExact(preset));
                }
            }

            Refresh();

            return c =>
            {
                applied = new Color(c.r, c.g, c.b, 1f);
                Color.RGBToHSV(applied, out h, out s, out v);
                field.SetTextWithoutNotify("#" + ColorUtility.ToHtmlStringRGB(applied));
                Refresh();
            };
        }

        /// <summary>
        /// Accepts FF0000, #FF0000, #F00, #FF0000FF and HTML names such as "red".
        /// Alpha is ignored - dye has no transparency.
        /// </summary>
        static bool TryParseColorCode(string text, out Color color)
        {
            color = default;
            string t = text == null ? "" : text.Trim();
            if (t.Length == 0) return false;

            string digits = t.TrimStart('#');
            bool isHex = digits.Length > 0;
            foreach (char ch in digits)
                if (!Uri.IsHexDigit(ch)) { isHex = false; break; }

            if (!ColorUtility.TryParseHtmlString(isHex ? "#" + digits : t, out color)) return false;
            color.a = 1f;
            return true;
        }

        static void FixedWidth(RectTransform rect, float width)
        {
            var le = rect.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minWidth = width;
            le.flexibleWidth = 0f;
        }

        static void Overlay(RectTransform parent, string name, Texture texture, Color tint)
        {
            var r = NewRect(name, parent);
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
            var img = r.gameObject.AddComponent<RawImage>();
            img.texture = texture;
            img.color = tint;
            img.raycastTarget = false;   // the base Image underneath receives the picks
        }

        static Texture2D NewTexture(int w, int h) => new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        /// <summary>White with alpha 1 -> 0 along the axis; tinted by the RawImage colour.</summary>
        static Texture2D MakeRamp(bool horizontal)
        {
            const int n = 256;
            var t = horizontal ? NewTexture(n, 1) : NewTexture(1, n);
            var px = new Color32[n];
            for (int i = 0; i < n; i++) px[i] = new Color32(255, 255, 255, (byte)(255 - i));
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static Texture2D MakeHueStrip()
        {
            const int n = 256;
            var t = NewTexture(1, n);
            var px = new Color32[n];
            for (int i = 0; i < n; i++) px[i] = Color.HSVToRGB(i / (n - 1f), 1f, 1f);
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        /// <summary>Hollow white ring with a dark rim, visible on light and dark colours.</summary>
        static Texture2D MakeRing()
        {
            const int n = 32;
            var t = NewTexture(n, n);
            var px = new Color32[n * n];
            var centre = new Vector2(n * 0.5f, n * 0.5f);
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), centre);
                float rim = Mathf.Clamp01(15f - d) * Mathf.Clamp01(d - 7.5f);
                float core = Mathf.Clamp01(13f - d) * Mathf.Clamp01(d - 9.5f);
                byte c = (byte)(255f * core);
                px[y * n + x] = new Color32(c, c, c, (byte)(255f * Mathf.Max(rim, core)));
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        /// <summary>
        /// A button that fills its parent. Inside a layout group the group sizes it
        /// instead, sharing the row with its siblings.
        /// </summary>
        Button NewButton(Transform row, string label, Action onClick)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(row, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            go.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var img = go.GetComponent<Image>();
            img.color = new Color(0.16f, 0.32f, 0.45f);

            var t = NewText(go.transform, label, 18, TextAnchor.MiddleCenter, Color.white);
            var tr = t.rectTransform;
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;

            var button = go.GetComponent<Button>();
            button.targetGraphic = img;
            button.onClick.AddListener(() => onClick());
            return button;
        }

        static string Format(float v, bool whole)
        {
            return whole ? Mathf.RoundToInt(v).ToString() : v.ToString("0.00");
        }

        void Update()
        {
            smoothedFrameTime = Mathf.Lerp(smoothedFrameTime, Time.unscaledDeltaTime, 0.1f);
            for (int i = 0; i < refreshers.Count; i++) refreshers[i]();

            if (statusText != null && statusClearTime > 0f && Time.unscaledTime > statusClearTime)
            {
                statusText.text = "";
                statusClearTime = 0f;
            }
        }
    }
}
