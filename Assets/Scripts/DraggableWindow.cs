using UnityEngine;
using UnityEngine.EventSystems;

[RequireComponent(typeof(RectTransform))]
public class DraggableWindow : MonoBehaviour, IBeginDragHandler, IDragHandler
{
    private RectTransform _window;
    private Vector2 _offset;

    private void Awake()
    {
        // === FIX (crash bez RequireComponent): wcześniej rzutowanie
        // (RectTransform)transform wybuchało InvalidCastException w Awake,
        // gdy GameObject nie ma RectTransform (np. przestawiony na UI → 3D).
        // [RequireComponent] daje Unity-error w edytorze zanim odpalisz grę.
        _window = (RectTransform)transform;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            (RectTransform)_window.parent,
            eventData.position,
            eventData.pressEventCamera,
            out Vector2 localPoint);

        _offset = _window.anchoredPosition - localPoint;
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)_window.parent,
                eventData.position,
                eventData.pressEventCamera,
                out Vector2 localPoint))
        {
            _window.anchoredPosition = localPoint + _offset;
        }
    }
}