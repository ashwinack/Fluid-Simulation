using UnityEngine;
using UnityEngine.UI;

namespace FluidSim
{
    /// <summary>
    /// Spreads one fluid simulation across several screens as a video wall.
    ///
    /// The simulation renders a single field N screens wide. This builds one canvas
    /// per screen and points each at the same texture through a different
    /// <see cref="RawImage.uvRect"/>, so screen 1 shows the left third, screen 2 the
    /// middle and so on. Because it is one field rather than N simulations, a swirl
    /// crosses the seams continuously instead of stopping at a screen edge.
    ///
    /// The canvases are created at runtime, so adding or removing a screen is a config
    /// change rather than scene surgery.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class FluidDisplayWall : MonoBehaviour
    {
        public FluidSimulation sim;

        [Tooltip("Sort order for the fluid canvases. Keep below the control panel's.")]
        public int sortingOrder = 0;

        readonly System.Collections.Generic.List<RawImage> screens =
            new System.Collections.Generic.List<RawImage>();

        void Awake()
        {
            if (sim == null) sim = FindAnyObjectByType<FluidSimulation>();
            if (sim == null)
            {
                Debug.LogError("[FluidDisplayWall] No FluidSimulation in the scene.");
                enabled = false;
                return;
            }

            // FluidSimulation (execution order -100) has already loaded the config and
            // activated the displays, so the wall is built from the real layout.
            Build();
        }

        // Display index shown by each built screen, to notice when the layout changes.
        int[] builtDisplays = new int[0];

        bool LayoutChanged()
        {
            int count = sim.FluidDisplayCount;
            if (builtDisplays.Length != count) return true;
            for (int slot = 0; slot < count; slot++)
                if (builtDisplays[slot] != sim.fluidDisplays[slot]) return true;
            return false;
        }

        void Build()
        {
            // DestroyImmediate, not Destroy: the old canvases must not draw one more
            // frame on top of the new ones, or a switched-off screen would flash the
            // previous slice before going black.
            for (int i = transform.childCount - 1; i >= 0; i--)
                DestroyImmediate(transform.GetChild(i).gameObject);
            screens.Clear();

            int count = sim.FluidDisplayCount;
            builtDisplays = new int[count];
            for (int slot = 0; slot < count; slot++) builtDisplays[slot] = sim.fluidDisplays[slot];

            for (int slot = 0; slot < count; slot++)
            {
                int displayIndex = sim.fluidDisplays[slot];

                var canvasGO = new GameObject($"FluidScreen{slot + 1}_Display{displayIndex + 1}",
                    typeof(Canvas), typeof(CanvasScaler));
                canvasGO.transform.SetParent(transform, false);

                var canvas = canvasGO.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.targetDisplay = displayIndex;
                canvas.sortingOrder = sortingOrder;

                // No GraphicRaycaster: these screens are pure output. Pointer input is
                // read directly by FluidSimulation, which needs raw positions rather
                // than UI events, and a raycaster here would only add overhead.

                var imageGO = new GameObject("FluidImage", typeof(RawImage));
                imageGO.transform.SetParent(canvasGO.transform, false);

                var rect = imageGO.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;

                var image = imageGO.GetComponent<RawImage>();
                image.raycastTarget = false;
                image.uvRect = SliceFor(slot);

                screens.Add(image);
            }
        }

        /// <summary>
        /// This screen's slice of the wide field. Proportional to the screen's pixel
        /// width, so mixed-resolution screens keep the fluid at one scale across seams.
        /// Full height on every screen: a shorter panel shows the same vertical range,
        /// just at fewer pixels.
        /// </summary>
        Rect SliceFor(int slot)
        {
            var span = sim.SlotSpan(slot);
            return new Rect(span.x, 0f, span.y, 1f);
        }

        void Update()
        {
            // The panel can switch between 1, 2 and 3 screens while running. The sim
            // (execution order -100) has already rebuilt its buffers for the new wall
            // this frame, so re-slicing here keeps both in step.
            if (LayoutChanged()) Build();

            // The texture is recreated whenever resolution or buffers change, and the
            // slices move if a display's resolution changes, so both are refreshed
            // every frame rather than cached.
            var tex = sim.OutputTexture;
            for (int i = 0; i < screens.Count; i++)
            {
                if (screens[i] == null) continue;
                screens[i].texture = tex;
                screens[i].uvRect = SliceFor(i);
            }
        }

        /// <summary>Number of screens currently built, for the control panel readout.</summary>
        public int ScreenCount => screens.Count;
    }
}
