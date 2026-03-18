using UnityEngine;
using TMPro;
using SVN.Core;
using UnityEngine.UI;
using System;

public class SvnLineController : MonoBehaviour
{
    public event Action OnHoverEnter;
    public event Action OnHoverExit;

    [SerializeField] private bool IsCommitDelegate;
    [SerializeField] private TextMeshProUGUI indentText;
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI nameText;
    [SerializeField] private TextMeshProUGUI sizeText;
    [SerializeField] private Button foldButton;
    [SerializeField] private Toggle selectionToggle;
    [SerializeField] private Button fullRowButton;
    [SerializeField] private Button revertBtn;
    [SerializeField] private Button logBtn;
    [SerializeField] private Button explorerBtn;
    [SerializeField] private Button addBtn;
    [SerializeField] private Button lockBtn;
    [SerializeField] private TextMeshProUGUI lockBtnText;
    [SerializeField] private Button blameBtn;

    private SvnTreeElement _element;
    private SVNStatus svnStatus;

    private float lastClickTime;
    private const float doubleClickThreshold = 0.3f;

    public void Setup(SvnTreeElement element, SVNStatus manager)
    {
        _element = element;
        svnStatus = manager;

        string indent = "";
        for (int i = 0; i < element.Depth; i++)
        {
            indent += (i == element.Depth - 1) ? "└─ " : " |  ";
        }
        indentText.text = indent;

        string statusClean = (element.Status == "DIR" || string.IsNullOrEmpty(element.Status))
                             ? ""
                             : $" [{element.Status}]";

        nameText.fontStyle = FontStyles.Normal;
        nameText.color = Color.white;

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
                    svnStatus.ToggleChildrenSelection(_element, val);
                }

                svnStatus.NotifySelectionChanged();
            });
        }

        if (fullRowButton != null)
        {
            fullRowButton.onClick.RemoveAllListeners();

            bool isFile = !_element.IsFolder;
            bool canDiff = isFile && !string.IsNullOrEmpty(_element.Status);

            if (canDiff)
            {
                fullRowButton.interactable = true;
                fullRowButton.onClick.AddListener(OnFullRowClick);

                statusText.text = statusClean;
                BindHover(fullRowButton, "Click: Preview | Double-Click: External Diff");
            }
            else if (_element.IsFolder)
            {
                fullRowButton.interactable = true;
                fullRowButton.onClick.AddListener(OnFoldClick);
                BindHover(fullRowButton, _element.IsExpanded ? "Click to collapse" : "Click to expand");
            }
            else
            {
                fullRowButton.interactable = false;
                BindHover(fullRowButton, "File is up to date.");
            }
        }

        string status = _element.Status;
        bool isUnversioned = status == "?";
        bool isMissingOrDeleted = status == "!" || status == "D";
        bool hasChanges = !string.IsNullOrEmpty(status) && status != " ";

        if (addBtn != null) addBtn.gameObject.SetActive(false);
        if (revertBtn != null) revertBtn.gameObject.SetActive(false);
        if (logBtn != null) logBtn.gameObject.SetActive(false);
        if (lockBtn != null) lockBtn.gameObject.SetActive(false);
        if (blameBtn != null) blameBtn.gameObject.SetActive(false);
        if (explorerBtn != null) explorerBtn.gameObject.SetActive(false);

        if (!_element.IsFolder && hasChanges)
        {
            if (isUnversioned && addBtn != null)
            {
                addBtn.gameObject.SetActive(true);
                addBtn.onClick.RemoveAllListeners();
                addBtn.onClick.AddListener(() =>
                {
                    SVNManager.Instance.GetModule<SVNAdd>()?.AddSingleItem(_element);
                });
                BindHover(addBtn, "Add this unversioned file to SVN control.");
            }

            bool isLockedByMe = status.Contains("K");
            bool isLockedByOthers = status.Contains("O");

            if (!_element.IsFolder && !isUnversioned && !isMissingOrDeleted && lockBtn != null)
            {
                lockBtn.gameObject.SetActive(true);
                lockBtn.onClick.RemoveAllListeners();

                if (isLockedByOthers)
                {
                    lockBtn.interactable = false;
                    lockBtnText.text = "<color=#FF4444>O</color>";
                    lockBtnText.fontStyle = FontStyles.Bold;
                    BindHover(lockBtn, "File is LOCKED by another user.");
                }
                else if (isLockedByMe)
                {
                    lockBtn.interactable = true;
                    lockBtn.onClick.AddListener(() => SVNManager.Instance.GetModule<SVNLock>()?.ToggleLockSingleItem(_element));
                    lockBtnText.text = "<color=#00FF00>K</color>";
                    lockBtnText.fontStyle = FontStyles.Bold;
                    BindHover(lockBtn, "You have the lock. Click to release it.");
                }
                else
                {
                    lockBtn.interactable = true;
                    lockBtn.onClick.AddListener(() => SVNManager.Instance.GetModule<SVNLock>()?.ToggleLockSingleItem(_element));
                    lockBtnText.text = "<color=#E6E6E6>U</color>";
                    lockBtnText.fontStyle = FontStyles.Bold;
                    BindHover(lockBtn, "File is unlocked. Click to lock it for exclusive editing.");
                }
            }
            else if (lockBtn != null)
            {
                lockBtn.gameObject.SetActive(false);
            }

            if (!isUnversioned && revertBtn != null)
            {
                revertBtn.gameObject.SetActive(true);
                revertBtn.onClick.RemoveAllListeners();
                revertBtn.onClick.AddListener(() =>
                {
                    SVNManager.Instance.GetModule<SVNRevert>()?.RevertSingleItem(_element);
                });
                BindHover(revertBtn, "Discard local changes and restore to repository version.");
            }

            if (!isUnversioned && logBtn != null)
            {
                logBtn.gameObject.SetActive(true);
                logBtn.onClick.RemoveAllListeners();
                logBtn.onClick.AddListener(() =>
                {
                    SVNManager.Instance.GetModule<SVNLog>()?.ShowLogForPath(_element.FullPath);
                });
                BindHover(logBtn, "Open SVN Log history for this file.");
            }

            if (explorerBtn != null)
            {
                explorerBtn.gameObject.SetActive(true);
                explorerBtn.onClick.RemoveAllListeners();
                explorerBtn.onClick.AddListener(() =>
                {
                    var external = SVNManager.Instance.GetModule<SVNExternal>();
                    if (isMissingOrDeleted)
                        external?.OpenInExplorer();
                    else
                        external?.OpenInExplorerAndSelect(_element.FullPath);
                });
                BindHover(explorerBtn, "Open file location in Windows Explorer.");
            }

            if (blameBtn != null)
            {
                blameBtn.onClick.RemoveAllListeners();

                bool isFile = !_element.IsFolder;
                bool hasHistory = _element.Status != "Added" && _element.Status != "?" && !string.IsNullOrEmpty(_element.Status);

                bool canBlame = isFile && hasHistory;

                blameBtn.gameObject.SetActive(canBlame);

                if (canBlame)
                {
                    blameBtn.onClick.AddListener(() =>
                    {
                        var blameModule = SVNManager.Instance?.GetModule<SVNBlame>();
                        if (blameModule != null)
                        {
                            _ = blameModule?.ShowBlameInMainConsole(_element.FullPath);

                        }
                    });

                    BindHover(blameBtn, "See who last modified each line of this file.");
                }
            }
        }
        else
        {
            if (explorerBtn != null)
            {
                explorerBtn.gameObject.SetActive(true);
                explorerBtn.onClick.RemoveAllListeners();
                explorerBtn.onClick.AddListener(() =>
                {
                    SVNManager.Instance.GetModule<SVNExternal>()?.OpenInExplorerAndSelect(_element.FullPath);
                });
                BindHover(explorerBtn, "Open location in Windows Explorer.");
            }
        }
    }

    private void OnFullRowClick()
    {
        float timeSinceLastClick = Time.time - lastClickTime;
        var diffModule = SVNManager.Instance?.GetModule<SVNDiff>();
        if (diffModule == null) return;

        if (timeSinceLastClick <= doubleClickThreshold)
        {
            _ = diffModule.ShowDiff(_element.FullPath);
        }
        else
        {
            _ = diffModule.ShowPreviewInUnity(_element.FullPath);
        }

        lastClickTime = Time.time;
    }

    private void OnFoldClick()
    {
        if (svnStatus != null && _element != null)
        {
            _element.IsExpanded = !_element.IsExpanded;
            svnStatus.ToggleFolderVisibility(_element);
        }
    }

    private Color GetStatusColor(string status)
    {
        switch (status)
        {
            case "M": return ParseHex("#FFD700"); // Mod (M)
            case "K": return ParseHex("#00FF00"); // Locked by ME
            case "O": return ParseHex("#FF4444"); // Locked by OTHERS
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

    private void BindHover(Button btn, string tooltipText)
    {
        if (btn == null) return;

        var handler = btn.gameObject.GetComponent<SVNHoverHandler>();
        if (handler == null) handler = btn.gameObject.AddComponent<SVNHoverHandler>();

        handler.OnHoverEnter += () =>
        {
            SVN.Core.SVNLogBridge.LogTooltip(tooltipText);
        };

        handler.OnHoverExit += () =>
        {
            SVN.Core.SVNLogBridge.ClearTooltip();
        };
    }
}