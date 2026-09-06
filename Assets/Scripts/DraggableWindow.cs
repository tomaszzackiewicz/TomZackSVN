using UnityEngine;
using UnityEngine.EventSystems;

public class DraggableWindow : MonoBehaviour, IBeginDragHandler, IDragHandler
{
    private RectTransform _window;
    private Vector2 _offset;

    private void Awake()
    {
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