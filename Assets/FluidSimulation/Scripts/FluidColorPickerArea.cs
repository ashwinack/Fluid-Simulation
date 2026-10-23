using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FluidSim
{
    /// <summary>
    /// Turns a UI rect into a 2D pick surface for the control panel's colour picker.
    /// Reports the press / drag position normalised to 0..1 inside the rect, clamped
    /// so dragging past the edge pins to the edge instead of stopping.
    /// </summary>
    public class FluidColorPickerArea : MonoBehaviour, IPointerDownHandler, IDragHandler,
                                        IInitializePotentialDragHandler
    {
        public Action<Vector2> onPick;

        public void OnPointerDown(PointerEventData e) => Report(e);
        public void OnDrag(PointerEventData e) => Report(e);

        // Picking should follow the finger from the first pixel, not after the
        // EventSystem's drag threshold.
        public void OnInitializePotentialDrag(PointerEventData e) => e.useDragThreshold = false;

        void Report(PointerEventData e)
        {
            var rect = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    rect, e.position, e.pressEventCamera, out var local))
                return;

            var r = rect.rect;
            var uv = new Vector2(
                Mathf.Clamp01((local.x - r.xMin) / Mathf.Max(r.width, 1f)),
                Mathf.Clamp01((local.y - r.yMin) / Mathf.Max(r.height, 1f)));
            onPick?.Invoke(uv);
        }
    }
}
