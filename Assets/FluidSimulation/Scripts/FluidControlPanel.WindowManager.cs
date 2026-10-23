using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FluidSim
{
    /// <summary>
    /// The window manager: a dialog over the control panel, opened from its top-right
    /// button, that sets how many displays are in use and each display's resolution
    /// and window mode. "Save &amp; apply" changes the running app on the spot and
    /// writes fluid-config.json, so the JSON never has to be edited by hand.
    ///
    /// Edits are a draft until saved: closing the dialog throws them away.
    /// </summary>
    public partial class FluidControlPanel
    {
        const int WmRowCount = FluidSimulation.MaxDisplays;
        const float WmRowHeight = 46f;

        static readonly Color WmSelected = new Color(0.25f, 0.62f, 0.95f);
        static readonly Color WmUnselected = new Color(0.2f, 0.2f, 0.24f);
        static readonly Color WmMuted = new Color(0.62f, 0.64f, 0.7f);

        class WmRow
        {
            public CanvasGroup group;
            public Text role;
            public InputField width, height;
            public Image windowedImg, borderlessImg;
            public bool windowed;
        }

        RectTransform wmOverlay;
        Text wmCountText, wmCountNote, wmStatusText;
        readonly WmRow[] wmRows = new WmRow[WmRowCount];
        int wmCount;

        void BuildWindowManager(Transform parent)
        {
            // Full-screen dimmer. Also swallows clicks so the panel behind cannot be
            // changed while the dialog is open.
            wmOverlay = NewRect("WindowManager", parent);
            wmOverlay.anchorMin = Vector2.zero;
            wmOverlay.anchorMax = Vector2.one;
            wmOverlay.offsetMin = Vector2.zero;
            wmOverlay.offsetMax = Vector2.zero;
            wmOverlay.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.78f);

            var dialog = NewRect("Dialog", wmOverlay);
            dialog.anchorMin = dialog.anchorMax = new Vector2(0.5f, 0.5f);
            dialog.sizeDelta = new Vector2(1240f, 900f);
            dialog.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.13f, 1f);
            var vlg = dialog.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(32, 32, 28, 28);
            vlg.spacing = 10f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            NewCell(NewRow(dialog, 44f), "WINDOW MANAGER", 28, TextAnchor.MiddleLeft, Accent, 0f);

            var info = NewCell(NewRow(dialog, 78f), InfoText(), 17, TextAnchor.UpperLeft, WmMuted, 0f);
            info.horizontalOverflow = HorizontalWrapMode.Wrap;
            info.verticalOverflow = VerticalWrapMode.Overflow;

            var stepper = NewRow(dialog, 50f);
            NewCell(stepper, "displays in use", 20, TextAnchor.MiddleLeft, Color.white, 200f);
            FixedButton(stepper, "-", 56f, () => SetWmCount(wmCount - 1));
            wmCountText = NewCell(stepper, "", 26, TextAnchor.MiddleCenter, ValueColor, 64f);
            FixedButton(stepper, "+", 56f, () => SetWmCount(wmCount + 1));
            wmCountNote = NewCell(stepper, "", 18, TextAnchor.MiddleLeft, WmMuted, 0f);

            var head = NewRow(dialog, 30f);
            WmHeadCell(head, "display", 130f);
            WmHeadCell(head, "shows", 250f);
            WmHeadCell(head, "width", 140f);
            WmHeadCell(head, "height", 140f);
            WmHeadCell(head, "mode", 280f);
            WmHeadCell(head, "", 0f);

            for (int i = 0; i < WmRowCount; i++) wmRows[i] = BuildWmRow(dialog, i);

            var buttons = NewRow(dialog, 54f);
            NewButton(buttons, "Save & apply", SaveWindowManager);
            NewButton(buttons, "Close", CloseWindowManager);

            wmStatusText = NewCell(NewRow(dialog, 30f), "", 18, TextAnchor.MiddleLeft, WmMuted, 0f);
            wmStatusText.horizontalOverflow = HorizontalWrapMode.Wrap;

            wmOverlay.gameObject.SetActive(false);
        }

        static string InfoText()
        {
            string where = Application.isEditor
                ? "In the editor only one display exists, so displays 2+ are previewed in Game views; the layout is still saved for the build."
                : $"Windows reports {Display.displays.Length} monitor(s) connected.";
            return "Display 1 always shows these controls. Displays 2 and up show one continuous fluid, left to right. " +
                   "Leave width and height empty for the monitor's native resolution. " +
                   "Displays 2+ never get a title bar in Unity: \"Windowed\" there is a frameless window of that size " +
                   "centred on its monitor.  " + where;
        }

        void WmHeadCell(Transform row, string label, float width)
        {
            NewCell(row, label, 16, TextAnchor.MiddleLeft, WmMuted, width);
        }

        WmRow BuildWmRow(Transform dialog, int display)
        {
            var row = NewRow(dialog, WmRowHeight);
            var r = new WmRow { group = row.gameObject.AddComponent<CanvasGroup>() };

            NewCell(row, "Display " + (display + 1), 19, TextAnchor.MiddleLeft, Color.white, 130f);
            r.role = NewCell(row, "", 17, TextAnchor.MiddleLeft, ValueColor, 250f);
            r.width = NewIntField(row, 140f);
            r.height = NewIntField(row, 140f);

            r.windowedImg = FixedButton(row, "Windowed", 135f, () => { r.windowed = true; PaintMode(r); }).image;
            r.borderlessImg = FixedButton(row, "Borderless", 135f, () => { r.windowed = false; PaintMode(r); }).image;

            // Neutral grey, so it does not read as a third selected mode.
            FixedButton(row, "Native", 110f, () =>
            {
                r.width.text = "";
                r.height.text = "";
            }).image.color = new Color(0.26f, 0.27f, 0.31f);
            return r;
        }

        static void PaintMode(WmRow r)
        {
            r.windowedImg.color = r.windowed ? WmSelected : WmUnselected;
            r.borderlessImg.color = r.windowed ? WmUnselected : WmSelected;
        }

        Button FixedButton(Transform row, string label, float width, System.Action onClick)
        {
            var b = NewButton(row, label, onClick);
            var le = b.GetComponent<LayoutElement>();
            le.flexibleWidth = 0f;
            le.preferredWidth = width;
            le.minWidth = width;
            return b;
        }

        InputField NewIntField(Transform row, float width)
        {
            var go = new GameObject("Field", typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(row, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minWidth = width;
            le.flexibleWidth = 0f;
            var img = go.GetComponent<Image>();
            img.color = new Color(0.17f, 0.17f, 0.21f);

            var text = NewText(go.transform, "", 19, TextAnchor.MiddleCenter, Color.white);
            Stretch(text.rectTransform, 6f);
            text.supportRichText = false;

            var hint = NewText(go.transform, "", 16, TextAnchor.MiddleCenter, new Color(0.5f, 0.52f, 0.58f));
            Stretch(hint.rectTransform, 6f);
            hint.fontStyle = FontStyle.Italic;

            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = hint;
            field.targetGraphic = img;
            field.contentType = InputField.ContentType.IntegerNumber;
            field.characterLimit = 5;
            field.lineType = InputField.LineType.SingleLine;
            return field;
        }

        static void Stretch(RectTransform rt, float inset)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, 0f);
            rt.offsetMax = new Vector2(-inset, 0f);
        }

        // ---------- open / close ----------

        void OpenWindowManager()
        {
            if (wmOverlay == null || sim == null) return;

            // Fill the draft from what is running now, so Close really discards edits.
            for (int i = 0; i < WmRowCount; i++)
            {
                var row = wmRows[i];
                var entry = sim.ResolutionOf(i);
                bool custom = entry != null && entry.width > 0 && entry.height > 0;
                row.width.SetTextWithoutNotify(custom ? entry.width.ToString() : "");
                row.height.SetTextWithoutNotify(custom ? entry.height.ToString() : "");

                var native = sim.NativeSizeOf(i);
                ((Text)row.width.placeholder).text = native.x > 0 ? $"native {native.x}" : "native";
                ((Text)row.height.placeholder).text = native.y > 0 ? $"native {native.y}" : "native";

                row.windowed = sim.ModeOf(i) == FluidSimulation.WindowMode.Windowed;
                PaintMode(row);
            }

            SetWmCount(sim.DisplaysInUse);
            wmStatusText.text = "";
            wmOverlay.SetAsLastSibling();
            wmOverlay.gameObject.SetActive(true);
        }

        void CloseWindowManager()
        {
            if (wmOverlay != null) wmOverlay.gameObject.SetActive(false);
        }

        void SetWmCount(int count)
        {
            wmCount = Mathf.Clamp(count, 2, FluidSimulation.MaxDisplays);
            int fluid = wmCount - 1;
            wmCountText.text = wmCount.ToString();
            wmCountNote.text = $"= 1 controls + {fluid} fluid screen{(fluid == 1 ? "" : "s")}";

            // Preview which display will show which part of the wall.
            var pool = sim.FluidPoolFor(wmCount);
            for (int i = 0; i < WmRowCount; i++)
            {
                var row = wmRows[i];
                bool inUse = i < wmCount;

                string role;
                if (i == sim.ControlDisplayIndex) role = "controls";
                else
                {
                    int slot = System.Array.IndexOf(pool, i);
                    role = inUse && slot >= 0 && slot < fluid ? $"fluid {slot + 1} of {fluid}" : "not used";
                }
                if (inUse && !Application.isEditor && i >= sim.ConnectedDisplays)
                    role += "  (not connected)";

                row.role.text = role;
                row.role.color = !inUse ? WmMuted
                               : role.Contains("not connected") ? new Color(1f, 0.65f, 0.4f)
                               : ValueColor;
                row.group.alpha = inUse ? 1f : 0.35f;
                row.group.interactable = inUse;
            }
        }

        void SaveWindowManager()
        {
            var entries = new List<FluidSimulation.DisplayResolution>();
            for (int i = 0; i < wmCount; i++)
            {
                var row = wmRows[i];
                int w = ParseSize(row.width.text);
                int h = ParseSize(row.height.text);

                if ((w > 0) != (h > 0))
                {
                    WmStatus($"Display {i + 1}: set both width and height, or leave both empty for native.", true);
                    return;
                }
                if (w > 0 && (w < 320 || h < 240 || w > 16384 || h > 16384))
                {
                    WmStatus($"Display {i + 1}: {w} x {h} is out of range (320 x 240 up to 16384 x 16384).", true);
                    return;
                }

                entries.Add(new FluidSimulation.DisplayResolution
                {
                    display = i,
                    width = w,
                    height = h,
                    mode = row.windowed ? FluidSimulation.ModeWindowed : FluidSimulation.ModeBorderless,
                });
            }

            int before = sim.DisplaysInUse;
            bool saved = sim.ApplyWindowLayout(wmCount, entries.ToArray());

            string msg = saved
                ? "Applied. Saved to " + sim.ActiveConfigPath
                : "Applied, but fluid-config.json could not be written - see the log.";
            if (wmCount < before && !Application.isEditor)
                msg += "  Displays taken out of use stay black until the app restarts (Windows cannot close them).";
            WmStatus(msg, !saved);
            SetStatus(saved ? "Window layout applied and saved" : "Window layout applied, NOT saved", !saved);
        }

        static int ParseSize(string text)
        {
            return int.TryParse(text, out int v) && v > 0 ? v : 0;
        }

        void WmStatus(string message, bool warning)
        {
            wmStatusText.text = message;
            wmStatusText.color = warning ? new Color(1f, 0.65f, 0.4f) : new Color(0.6f, 0.9f, 0.6f);
        }
    }
}
