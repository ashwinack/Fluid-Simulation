using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace FluidSim
{
    /// <summary>
    /// The background image dialog, opened from the top-right BACKGROUND button: the
    /// image hidden behind the fluid and revealed inside the spotlight. Pick one of
    /// the images on offer, or upload your own PNG / JPG (it is copied into the
    /// Backgrounds folder). The spotlight's look is tuned here too. Picks show on
    /// the wall at once; "Save as default" keeps them for the next launch.
    /// </summary>
    public partial class FluidControlPanel
    {
        const int BgColumns = 5, BgPerPage = 10;
        const float BgTileW = 220f, BgTileH = 172f, BgThumbH = 124f;

        RectTransform bgOverlay;
        Text bgStatusText, bgPageText;
        InputField bgPathField;
        readonly List<BgTile> bgTiles = new List<BgTile>();
        List<FluidSimulation.BackgroundOption> bgOptions = new List<FluidSimulation.BackgroundOption>();
        int bgPage;
        float bgRemoveArmedUntil;

        class BgTile
        {
            public GameObject root;
            public Image frame;
            public RawImage thumb;
            public Text name, tag;
            public FluidSimulation.BackgroundOption option;
        }

        // Small copies of uploaded images, so the gallery never holds full-size ones.
        readonly Dictionary<string, Texture2D> bgThumbs = new Dictionary<string, Texture2D>();

        void BuildBackgroundDialog(Transform parent)
        {
            bgOverlay = NewRect("BackgroundDialog", parent);
            bgOverlay.anchorMin = Vector2.zero;
            bgOverlay.anchorMax = Vector2.one;
            bgOverlay.offsetMin = Vector2.zero;
            bgOverlay.offsetMax = Vector2.zero;
            bgOverlay.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.78f);

            var dialog = NewRect("Dialog", bgOverlay);
            dialog.anchorMin = dialog.anchorMax = new Vector2(0.5f, 0.5f);
            dialog.sizeDelta = new Vector2(1240f, 960f);
            dialog.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.13f, 1f);
            var vlg = dialog.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(32, 32, 28, 28);
            vlg.spacing = 10f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            NewCell(NewRow(dialog, 44f), "BACKGROUND IMAGE", 28, TextAnchor.MiddleLeft, Accent, 0f);
            var info = NewCell(NewRow(dialog, 48f),
                "The image behind the fluid, revealed inside the spotlight under the mouse or a finger. " +
                "Tap an image to put it on the wall, or upload your own PNG or JPG - it is copied into the Backgrounds folder.",
                17, TextAnchor.UpperLeft, WmMuted, 0f);
            info.horizontalOverflow = HorizontalWrapMode.Wrap;
            info.verticalOverflow = VerticalWrapMode.Overflow;

            // Gallery: one page of tiles.
            var gallery = NewRect("Gallery", dialog);
            var gle = gallery.gameObject.AddComponent<LayoutElement>();
            gle.preferredHeight = gle.minHeight = 2f * BgTileH + 12f;
            var grid = gallery.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(BgTileW, BgTileH);
            grid.spacing = new Vector2(12f, 12f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = BgColumns;
            grid.childAlignment = TextAnchor.UpperLeft;
            for (int i = 0; i < BgPerPage; i++) bgTiles.Add(BuildBgTile(gallery));

            var actions = NewRow(dialog, 50f);
            NewButton(actions, "Upload image...", UploadBackground).interactable = NativeFileDialog.Supported;
            NewButton(actions, "Remove", RemoveSelectedBackground);
            NewButton(actions, "Open folder", () =>
                Application.OpenURL("file:///" + sim.BackgroundsFolder.Replace('\\', '/')));
            FixedButton(actions, "<", 56f, () => { bgPage--; RefreshBackgroundGallery(false); });
            bgPageText = NewCell(actions, "", 18, TextAnchor.MiddleCenter, ValueColor, 110f);
            FixedButton(actions, ">", 56f, () => { bgPage++; RefreshBackgroundGallery(false); });

            var pathRow = NewRow(dialog, 46f);
            NewCell(pathRow, "or a file path", 18, TextAnchor.MiddleLeft, Color.white, LabelWidth);
            bgPathField = NewTextField(pathRow, @"e.g. D:\Images\wall.jpg");
            FixedButton(pathRow, "Use", 110f, () => ImportBackgroundFrom(bgPathField.text));

            NewCell(NewRow(dialog, 36f), "SPOTLIGHT", 20, TextAnchor.LowerLeft, Accent, 0f);
            var cols = NewRow(dialog, 3f * RowHeight + 2f * RowSpacing);
            cols.GetComponent<HorizontalLayoutGroup>().spacing = 40f;
            var left = SettingsColumn(cols);
            var right = SettingsColumn(cols);
            AddToggle(left, "enabled", () => sim.revealMode, v => sim.revealMode = v);
            AddOptionSlider(left, "image fit", RevealFitNames, RevealFitValues,
                () => (int)sim.revealFit, v => sim.revealFit = (FluidSimulation.RevealFit)v);
            AddSlider(left, "spotlight size", 0.03f, 0.6f, () => sim.spotlightSize, v => sim.spotlightSize = v);
            AddSlider(right, "edge softness", 0.05f, 1f, () => sim.spotlightSoftness, v => sim.spotlightSoftness = v);
            AddSlider(right, "fluid over image", 0f, 1f, () => sim.revealFluidOpacity, v => sim.revealFluidOpacity = v);
            AddSlider(right, "image outside", 0f, 1f, () => sim.revealBackground, v => sim.revealBackground = v);

            var buttons = NewRow(dialog, 54f);
            NewButton(buttons, "Save as default", () =>
            {
                bool ok = sim.SaveBackgroundSettings();
                BgStatus(ok ? "Background image and spotlight saved - the wall starts with them next time."
                            : "Could not write fluid-settings.json - see the log.", !ok);
            });
            NewButton(buttons, "Close", () => bgOverlay.gameObject.SetActive(false));

            bgStatusText = NewCell(NewRow(dialog, 30f), "", 18, TextAnchor.MiddleLeft, WmMuted, 0f);
            bgStatusText.horizontalOverflow = HorizontalWrapMode.Wrap;

            // The Open dialog runs on its own thread; its answer arrives here.
            refreshers.Add(NativeFileDialog.Poll);
            // The selected tile follows the live image, whoever changed it.
            refreshers.Add(() =>
            {
                if (!bgOverlay.gameObject.activeSelf) return;
                foreach (var t in bgTiles)
                    if (t.option != null) t.frame.color = sim.IsCurrentBackground(t.option) ? Color.white : WmUnselected;
            });

            bgOverlay.gameObject.SetActive(false);
        }

        BgTile BuildBgTile(Transform parent)
        {
            var go = new GameObject("Tile", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var t = new BgTile { root = go, frame = go.GetComponent<Image>() };

            var inner = NewRect("Inner", go.transform);
            inner.anchorMin = Vector2.zero;
            inner.anchorMax = Vector2.one;
            inner.offsetMin = new Vector2(4f, 4f);
            inner.offsetMax = new Vector2(-4f, -4f);
            var innerImg = inner.gameObject.AddComponent<Image>();
            innerImg.color = new Color(0.13f, 0.13f, 0.17f);
            innerImg.raycastTarget = false;

            var thumb = NewRect("Thumb", inner);
            thumb.anchorMin = new Vector2(0f, 1f);
            thumb.anchorMax = new Vector2(1f, 1f);
            thumb.pivot = new Vector2(0.5f, 1f);
            thumb.sizeDelta = new Vector2(0f, BgThumbH);
            thumb.anchoredPosition = Vector2.zero;
            t.thumb = thumb.gameObject.AddComponent<RawImage>();
            t.thumb.raycastTarget = false;

            t.name = NewText(inner, "", 16, TextAnchor.MiddleLeft, Color.white);
            var nr = t.name.rectTransform;
            nr.anchorMin = new Vector2(0f, 0f);
            nr.anchorMax = new Vector2(1f, 0f);
            nr.pivot = new Vector2(0.5f, 0f);
            nr.offsetMin = new Vector2(10f, 4f);
            nr.offsetMax = new Vector2(-10f, 36f);
            t.name.horizontalOverflow = HorizontalWrapMode.Wrap;

            t.tag = NewText(thumb, "BUILT-IN", 13, TextAnchor.UpperRight, Color.white);
            var tr = t.tag.rectTransform;
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(6f, 4f);
            tr.offsetMax = new Vector2(-6f, -4f);
            var shadow = t.tag.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.9f);

            var button = go.GetComponent<Button>();
            button.targetGraphic = t.frame;
            button.transition = Selectable.Transition.None;   // the frame shows selection
            button.onClick.AddListener(() =>
            {
                if (t.option == null) return;
                sim.SelectBackground(t.option);
                bgRemoveArmedUntil = 0f;
                BgStatus($"{t.option.name} is on the wall now (not saved - use Save as default to keep it).", false);
                SetStatus("Background: " + t.option.name);
            });
            return t;
        }

        InputField NewTextField(Transform row, string placeholder)
        {
            var go = new GameObject("PathField", typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(row, false);
            go.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var img = go.GetComponent<Image>();
            img.color = new Color(0.17f, 0.17f, 0.21f);

            var text = NewText(go.transform, "", 18, TextAnchor.MiddleLeft, Color.white);
            Stretch(text.rectTransform, 10f);
            text.supportRichText = false;
            var hint = NewText(go.transform, placeholder, 16, TextAnchor.MiddleLeft, new Color(0.5f, 0.52f, 0.58f));
            Stretch(hint.rectTransform, 10f);
            hint.fontStyle = FontStyle.Italic;

            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = hint;
            field.targetGraphic = img;
            field.lineType = InputField.LineType.SingleLine;
            field.onEndEdit.AddListener(v =>
            {
                // Enter in the field uses the path, like the Use button.
                if (Input_EnterPressed()) ImportBackgroundFrom(v);
            });
            return field;
        }

        static bool Input_EnterPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame);
#else
            return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
#endif
        }

        // ---------- open / actions ----------

        void OpenBackgroundDialog()
        {
            if (bgOverlay == null || sim == null) return;
            bgStatusText.text = "";
            bgRemoveArmedUntil = 0f;
            bgOverlay.SetAsLastSibling();
            bgOverlay.gameObject.SetActive(true);
            RefreshBackgroundGallery(true);
        }

        /// <summary>Re-read the images on offer and draw the current page.</summary>
        void RefreshBackgroundGallery(bool showCurrent)
        {
            bgOptions = sim.ListBackgrounds();
            int pages = Mathf.Max(1, Mathf.CeilToInt(bgOptions.Count / (float)BgPerPage));
            if (showCurrent)
            {
                int cur = bgOptions.FindIndex(sim.IsCurrentBackground);
                if (cur >= 0) bgPage = cur / BgPerPage;
            }
            bgPage = Mathf.Clamp(bgPage, 0, pages - 1);
            bgPageText.text = $"page {bgPage + 1}/{pages}";

            for (int i = 0; i < bgTiles.Count; i++)
            {
                var t = bgTiles[i];
                int idx = bgPage * BgPerPage + i;
                t.option = idx < bgOptions.Count ? bgOptions[idx] : null;
                t.root.SetActive(t.option != null);
                if (t.option == null) continue;
                t.name.text = t.option.name;
                t.tag.gameObject.SetActive(t.option.builtIn);
                Texture tex = t.option.builtIn ? t.option.texture : ThumbnailFor(t.option.file);
                t.thumb.texture = tex;
                t.thumb.color = tex != null ? Color.white : new Color(0.2f, 0.2f, 0.24f);
                t.thumb.uvRect = FillRect(tex, (BgTileW - 8f) / BgThumbH);
            }
        }

        /// <summary>UV rect that crops a texture to fill an area of the given aspect.</summary>
        static Rect FillRect(Texture tex, float areaAspect)
        {
            if (tex == null || tex.height == 0) return new Rect(0f, 0f, 1f, 1f);
            float imageAspect = tex.width / (float)tex.height;
            if (imageAspect > areaAspect)
            {
                float w = areaAspect / imageAspect;
                return new Rect((1f - w) * 0.5f, 0f, w, 1f);
            }
            float h = imageAspect / areaAspect;
            return new Rect(0f, (1f - h) * 0.5f, 1f, h);
        }

        Texture2D ThumbnailFor(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            string key;
            try { key = file + "|" + File.GetLastWriteTimeUtc(file).Ticks; }
            catch (System.Exception) { return null; }
            if (bgThumbs.TryGetValue(key, out var cached) && cached != null) return cached;

            Texture2D full = null;
            RenderTexture rt = null;
            var prev = RenderTexture.active;
            try
            {
                full = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!full.LoadImage(File.ReadAllBytes(file), false)) return null;
                int w = 320, h = Mathf.Clamp(Mathf.RoundToInt(320f * full.height / Mathf.Max(full.width, 1)), 16, 320);
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(full, rt);
                RenderTexture.active = rt;
                var thumb = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                thumb.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                thumb.Apply(false, true);
                bgThumbs[key] = thumb;
                return thumb;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[FluidControlPanel] No thumbnail for {file}: {e.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (full != null) Destroy(full);
            }
        }

        void UploadBackground()
        {
            if (NativeFileDialog.IsOpen) { BgStatus("The file dialog is already open.", true); return; }
            BgStatus("Choose a PNG or JPG image in the file dialog...", false);
            string start = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures);
            NativeFileDialog.OpenImage("Choose a background image", start, path =>
            {
                if (path == null) { BgStatus("No image chosen.", false); return; }
                ImportBackgroundFrom(path);
            });
        }

        void ImportBackgroundFrom(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) { BgStatus("Type the full path of a PNG or JPG file first.", true); return; }
            bool ok = sim.ImportBackground(path, out string message);
            BgStatus(ok ? message + " (not saved - use Save as default to keep it)" : message, !ok);
            if (ok)
            {
                bgPathField.SetTextWithoutNotify("");
                SetStatus("Background: " + Path.GetFileName(path));
                RefreshBackgroundGallery(true);
            }
        }

        void RemoveSelectedBackground()
        {
            var current = bgOptions.Find(sim.IsCurrentBackground);
            if (current == null) { BgStatus("Tap the image to remove first.", true); return; }
            if (current.builtIn) { BgStatus("Built-in images cannot be removed.", true); return; }
            // Deleting is permanent, so it takes a second tap.
            if (Time.unscaledTime > bgRemoveArmedUntil)
            {
                bgRemoveArmedUntil = Time.unscaledTime + 4f;
                BgStatus($"Tap Remove again to delete {Path.GetFileName(current.file)} from the Backgrounds folder.", true);
                return;
            }
            bgRemoveArmedUntil = 0f;
            bool ok = sim.RemoveBackground(current, out string message);
            BgStatus(message, !ok);
            RefreshBackgroundGallery(true);
        }

        void BgStatus(string message, bool warning)
        {
            bgStatusText.text = message;
            bgStatusText.color = warning ? new Color(1f, 0.65f, 0.4f) : new Color(0.6f, 0.9f, 0.6f);
        }
    }
}
