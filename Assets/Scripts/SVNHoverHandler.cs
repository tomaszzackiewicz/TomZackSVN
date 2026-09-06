using SVN.Core;
using UnityEngine;
using UnityEngine.EventSystems;

public class SVNHoverHandler : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [SerializeField]
    private string _tooltipText = "";

    public string TooltipText
    {
        get => _tooltipText;
        set => _tooltipText = value;
    }

    public void SetTooltip(string tooltip)
    {
        _tooltipText = tooltip;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (!string.IsNullOrEmpty(_tooltipText))
            SVNLogBridge.LogTooltip(_tooltipText);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        SVNLogBridge.ClearTooltip();
    }

    private void OnDisable()
    {
        SVNLogBridge.ClearTooltip();
    }
}