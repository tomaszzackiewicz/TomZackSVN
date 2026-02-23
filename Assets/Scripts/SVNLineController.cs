using UnityEngine;
using TMPro;
using SVN.Core;
using UnityEngine.UI;

public class SvnLineController : MonoBehaviour
{
    public bool IsCommitDelegate;
    public TextMeshProUGUI indentText;
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI nameText;
    public TextMeshProUGUI sizeText;
    public Button foldButton;
    public Toggle selectionToggle;

    private SvnTreeElement _element;
    private SVNStatus _manager;

    public void Setup(SvnTreeElement element, SVNStatus manager)
    {
        _element = element;
        _manager = manager;

        string indent = "";
        for (int i = 0; i < element.Depth; i++)
        {
            indent += (i == element.Depth - 1) ? "└─ " : " |  ";
        }
        indentText.text = indent;

        string statusClean = (element.Status == "DIR" || string.IsNullOrEmpty(element.Status))
                             ? ""
                             : $" [{element.Status}]";

        if (element.IsFolder)
        {
            string dirHex = "#003366";
            statusText.text = $"<b><color={dirHex}>[DIR]</color></b>{statusClean}";
            statusText.color = Color.black;

            nameText.text = element.Name;
            nameText.color = new Color(0f, 0.2f, 0.4f);
            nameText.fontStyle = FontStyles.Bold;

            if (sizeText != null) sizeText.text = "";
        }
        else
        {
            statusText.text = "<color=#ADD8E6>[FILE]</color> " + statusClean;
            statusText.color = GetStatusColor(element.Status);
            nameText.text = element.Name;
            sizeText.text = element.Size;

            if (element.IsCommitDelegate)
            {
                sizeText.text = "";
            }
            else
            {
                sizeText.text = element.Size;
            }
        }

        if (foldButton != null)
        {
            CanvasGroup cg = foldButton.GetComponent<CanvasGroup>();
            if (cg == null)
            {
                cg = foldButton.gameObject.AddComponent<CanvasGroup>();
            }

            if (element.IsFolder)
            {
                cg.alpha = 1f;
                cg.interactable = true;
                cg.blocksRaycasts = true;

                var btnText = foldButton.GetComponentInChildren<TextMeshProUGUI>();
                if (btnText != null)
                {
                    btnText.text = "▼";
                    btnText.rectTransform.localRotation = Quaternion.Euler(0, 0, element.IsExpanded ? 0f : 90f);
                }

                foldButton.onClick.RemoveAllListeners();
                foldButton.onClick.AddListener(OnFoldClick);
            }
            else
            {
                cg.alpha = 0f;
                cg.interactable = false;
                cg.blocksRaycasts = false;
            }
        }

        if (selectionToggle != null)
        {
            selectionToggle.onValueChanged.RemoveAllListeners();

            selectionToggle.SetIsOnWithoutNotify(element.IsChecked);

            if (nameText != null) nameText.alpha = element.IsChecked ? 1.0f : 0.6f;

            selectionToggle.onValueChanged.AddListener((val) =>
            {
                _element.IsChecked = val;
                if (nameText != null) nameText.alpha = val ? 1.0f : 0.6f;

                if (_element.IsFolder)
                {
                    _manager.ToggleChildrenSelection(_element, val);
                }

                _manager.NotifySelectionChanged();
            });
        }
    }

    private void OnFoldClick()
    {
        if (_manager != null && _element != null)
        {
            _element.IsExpanded = !_element.IsExpanded;
            _manager.ToggleFolderVisibility(_element);
        }
    }

    private Color GetStatusColor(string status)
    {
        switch (status)
        {
            case "M": return ParseHex("#FFD700"); // Mod (M)
            case "A": return ParseHex("#00FF00"); // Add (A)
            case "?": return ParseHex("#00E5FF"); // New (?)
            case "D":
            case "!": return ParseHex("#FF4444"); // Del (D/!)
            case "C": return ParseHex("#FF00FF"); // Conf (C)
            case "I": return ParseHex("#444444"); // Ignored
            default: return Color.white;
        }
    }

    private Color ParseHex(string hex)
    {
        if (ColorUtility.TryParseHtmlString(hex, out Color color))
            return color;
        return Color.white;
    }
}