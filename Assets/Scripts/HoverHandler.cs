using UnityEngine;
using UnityEngine.EventSystems;

public class SVNHoverHandler : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public event System.Action OnHoverEnter;
    public event System.Action OnHoverExit;

    public void OnPointerEnter(PointerEventData eventData) => OnHoverEnter?.Invoke();
    public void OnPointerExit(PointerEventData eventData) => OnHoverExit?.Invoke();

    private void OnDestroy()
    {
        OnHoverEnter = null;
        OnHoverExit = null;
    }
}