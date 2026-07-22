using SVN.Core;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class SvnLineController : MonoBehaviour
{
    public event Action OnHoverEnter;
    public event Action OnHoverExit;

    #region Serialized Fields
    [SerializeField] private bool isCommitDelegate;
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
    [SerializeField] private Button resolveBtn;
    [SerializeField] private Button commitBtn;
    #endregion

    #region Cached Colors
    private static readonly Color RowBgModified = new(1f, 0.85f, 0f, 0.08f);
    private static readonly Color RowBgAdded = new(0f, 1f, 0f, 0.06f);
    private static readonly Color RowBgUnversioned = new(0f, 0.9f, 1f, 0.06f);
    private static readonly Color RowBgDeleted = new(1f, 0.2f, 0.2f, 0.08f);
    private static readonly Color RowBgConflict = new(1f, 0f, 1f, 0.1f);
    private static readonly Color RowBgIgnored = new(0.3f, 0.3f, 0.3f, 0.05f);
    private static readonly Color RowBgDefault = new(0, 0, 0, 0);

    private static readonly Color StatusModified = new(1f, 0.843f, 0f);
    private static readonly Color StatusLocked = new(0f, 1f, 0f);
    private static readonly Color StatusOtherLocked = new(1f, 0.267f, 0.267f);
    private static readonly Color StatusAdded = new(0f, 1f, 0f);
    private static readonly Color StatusUnversioned = new(0f, 0.898f, 1f);
    private static readonly Color StatusDeleted = new(1f, 0.267f, 0.267f);
    private static readonly Color StatusConflict = new(1f, 0f, 1f);
    private static readonly Color StatusIgnored = new(0.267f, 0.267f, 0.267f);
    private static readonly Color StatusDefault = Color.white;

    private static readonly Color DirNameColor = new(0f, 0.2f, 0.4f);
    private static readonly string DirHex = "#003366";
    #endregion

    #region Private Fields
    private SvnTreeElement _element;
    private SVNStatus _svnStatus;
    private Image _rowBackground;
    private float _lastClickTime;
    private const float DoubleClickThreshold = 0.3f;
    private CancellationTokenSource _diffHoverCts;

    // Cached delegates to avoid closure allocations
    private UnityAction _onFoldClickDelegate;
    private UnityAction<bool> _onToggleChangedDelegate;
    private UnityAction _onFullRowClickDelegate;
    private UnityAction _onCommitClickDelegate;
    private UnityAction _onLockClickDelegate;
    #endregion

    #region Properties
    public SvnTreeElement Element => _element;
    public bool IsCommitDelegate => isCommitDelegate;
    #endregion

    #region Lifecycle
    private void Awake()
    {
        _onFoldClickDelegate = OnFoldClick;
        _onToggleChangedDelegate = OnToggleChanged;
        _onFullRowClickDelegate = OnFullRowClick;
        _onCommitClickDelegate = OnCommitClick;
        _onLockClickDelegate = OnLockClick;

        if (!TryGetComponent(out _rowBackground))
            _rowBackground = gameObject.AddComponent<Image>();
    }

    private void OnDestroy()
    {
        _diffHoverCts?.Cancel();
        _diffHoverCts?.Dispose();

        RemoveAllButtonListeners();
    }
    #endregion

    #region Setup
    public void Setup(SvnTreeElement element, SVNStatus manager)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _svnStatus = manager;

        ResetAllButtons();
        RenderIndent();
        RenderStatusAndName();
        SetupFoldButton();
        SetupSelectionToggle();
        SetupFullRowButton();
        SetupActionButtons();
        ApplyRowBackground();
    }

    private void ResetAllButtons()
    {
        SetButtonActive(addBtn, false);
        SetButtonActive(revertBtn, false);
        SetButtonActive(logBtn, false);
        SetButtonActive(lockBtn, false);
        SetButtonActive(blameBtn, false);
        SetButtonActive(explorerBtn, false);
        SetButtonActive(resolveBtn, false);
        SetButtonActive(commitBtn, false);
    }
    #endregion

    #region Rendering
    private void RenderIndent()
    {
        if (indentText == null) return;

        int depth = _element.Depth;
        if (depth <= 0)
        {
            indentText.text = string.Empty;
            return;
        }

        var sb = new StringBuilder(depth * 4);
        for (int i = 0; i < depth; i++)
            sb.Append(i == depth - 1 ? "└─ " : " |  ");

        indentText.text = sb.ToString();
    }

    private void RenderStatusAndName()
    {
        bool isRoot = IsRootElement(_element.FullPath);
        string statusClean = (_element.Status == "DIR" || string.IsNullOrEmpty(_element.Status))
            ? ""
            : $" [{_element.Status}]";

        if (nameText != null)
        {
            nameText.fontStyle = FontStyles.Normal;
            nameText.color = Color.white;
        }

        if (_element.IsFolder)
        {
            if (statusText != null)
            {
                statusText.text = isRoot
                    ? $"<b><color={DirHex}>[ROOT]</color></b>{statusClean}"
                    : $"<b><color={DirHex}>[DIR]</color></b>{statusClean}";
                statusText.color = Color.black;
            }

            if (nameText != null)
            {
                nameText.text = _element.Name;
                nameText.color = DirNameColor;
                nameText.fontStyle = FontStyles.Bold;
            }

            if (sizeText != null)
                sizeText.text = "";
        }
        else
        {
            if (statusText != null)
            {
                statusText.text = "<color=#ADD8E6>[FILE]</color> " + statusClean;
                statusText.color = GetStatusColor(_element.Status);
            }

            if (nameText != null)
                nameText.text = _element.Name;

            if (sizeText != null)
                sizeText.text = _element.IsCommitDelegate ? "" : _element.Size;
        }
    }
    #endregion

    #region Button Setup
    private void SetupFoldButton()
    {
        if (foldButton == null) return;

        if (!foldButton.TryGetComponent(out CanvasGroup cg))
            cg = foldButton.gameObject.AddComponent<CanvasGroup>();

        if (_element.IsFolder)
        {
            cg.alpha = 1f;
            cg.interactable = true;
            cg.blocksRaycasts = true;

            var btnText = foldButton.GetComponentInChildren<TextMeshProUGUI>();
            if (btnText != null)
            {
                btnText.text = "▼";
                btnText.rectTransform.localRotation = Quaternion.Euler(0, 0, _element.IsExpanded ? 0f : 90f);
            }

            foldButton.onClick.RemoveListener(_onFoldClickDelegate);
            foldButton.onClick.AddListener(_onFoldClickDelegate);
        }
        else
        {
            cg.alpha = 0f;
            cg.interactable = false;
            cg.blocksRaycasts = false;
        }
    }

    private void SetupSelectionToggle()
    {
        if (selectionToggle == null) return;

        selectionToggle.onValueChanged.RemoveListener(_onToggleChangedDelegate);
        selectionToggle.SetIsOnWithoutNotify(_element.IsChecked);

        if (nameText != null)
            nameText.alpha = _element.IsChecked ? 1f : 0.6f;

        selectionToggle.onValueChanged.AddListener(_onToggleChangedDelegate);
    }

    private void OnToggleChanged(bool val)
    {
        _element.IsChecked = val;

        if (nameText != null)
            nameText.alpha = val ? 1f : 0.6f;

        if (_element.IsFolder && _svnStatus != null)
            _svnStatus.ToggleChildrenSelection(_element, val);

        _svnStatus?.NotifySelectionChanged();
    }

    private void SetupFullRowButton()
    {
        if (fullRowButton == null) return;

        fullRowButton.onClick.RemoveListener(_onFullRowClickDelegate);

        bool isRootMeta = IsRootElement(_element.FullPath);
        bool isFolder = _element.IsFolder;
        bool isFile = !isFolder;
        bool hasStatus = !string.IsNullOrEmpty(_element.Status) && _element.Status != " " && _element.Status != "?";
        bool canDiff = !isRootMeta && isFile && hasStatus;

        if (isRootMeta)
        {
            fullRowButton.interactable = true;
            fullRowButton.onClick.AddListener(() =>
            {
                SVNManager.Instance?.GetModule<SVNLog>()?.ShowLogForPath(".");
            });
            BindHover(fullRowButton, "Repository root change (M .)");
            if (statusText != null)
                statusText.text = "[ROOT]";
            return;
        }

        if (canDiff)
        {
            fullRowButton.interactable = true;
            fullRowButton.onClick.AddListener(_onFullRowClickDelegate);

            var tooltip = new StringBuilder();
            tooltip.Append($"Path: {_element.FullPath} | Status: {_element.Status}");

            if (!string.IsNullOrEmpty(_element.Size))
                tooltip.Append($" | Size: {_element.Size}");

            if (_element.LockedByMe)
                tooltip.Append(" | Locked by you");
            else if (_element.LockedByOther)
                tooltip.Append(" | Locked by another user");

            if (_element.DiffStatsLoaded)
                tooltip.Append($" | Diff: +{_element.AddedLines} -{_element.RemovedLines}");

            tooltip.Append(" | Click: Preview | Double-Click: External Diff");

            BindHover(fullRowButton, tooltip.ToString());
        }
        else if (isFolder)
        {
            fullRowButton.interactable = true;
            fullRowButton.onClick.AddListener(_onFoldClickDelegate);
            BindHover(fullRowButton, _element.IsExpanded ? "Click to collapse" : "Click to expand");
        }
        else
        {
            fullRowButton.interactable = false;
            BindHover(fullRowButton, "No actionable change.");
        }
    }

    private void SetupActionButtons()
    {
        string status = _element.Status;
        bool isUnversioned = status == "?";
        bool isMissingOrDeleted = status == "!" || status == "D";
        bool hasChanges = !string.IsNullOrEmpty(status) && status != " ";

        if (_element.IsFolder || !hasChanges)
        {
            if (explorerBtn != null)
            {
                ActivateButton(explorerBtn, () =>
                {
                    SVNManager.Instance?.GetModule<SVNExternal>()?.OpenInExplorerAndSelect(_element.FullPath);
                }, "Open location in Windows Explorer.");
            }
            return;
        }

        // File with changes
        if (isUnversioned && addBtn != null)
        {
            ActivateButton(addBtn, () =>
            {
                SVNManager.Instance?.GetModule<SVNAdd>()?.AddSingleItem(_element);
            }, "Add this unversioned file to SVN control.");
        }

        if (status == "C" && resolveBtn != null)
        {
            ActivateButton(resolveBtn, () =>
            {
                SVNManager.Instance?.PanelHandler?.Button_OpenResolve();
            }, "This file has conflicts. Click to open Resolve panel.");
        }

        if (!isUnversioned && status != "!" && status != "C" && commitBtn != null)
        {
            commitBtn.gameObject.SetActive(true);
            commitBtn.onClick.RemoveListener(_onCommitClickDelegate);
            commitBtn.onClick.AddListener(_onCommitClickDelegate);
            BindHover(commitBtn, "Commit only this file.");
        }

        SetupLockButton(status, isUnversioned, isMissingOrDeleted);

        if (!isUnversioned && revertBtn != null)
        {
            ActivateButton(revertBtn, () =>
            {
                SVNManager.Instance?.GetModule<SVNRevert>()?.RevertSingleItem(_element);
            }, "Discard local changes and restore to repository version.");
        }

        if (!isUnversioned && status != "A" && logBtn != null)
        {
            ActivateButton(logBtn, () =>
            {
                SVNManager.Instance?.GetModule<SVNLog>()?.ShowLogForPath(_element.FullPath);
            }, "Open SVN Log history for this file.");
        }

        if (explorerBtn != null)
        {
            ActivateButton(explorerBtn, () =>
            {
                var ext = SVNManager.Instance?.GetModule<SVNExternal>();
                if (isMissingOrDeleted)
                    ext?.OpenInExplorer();
                else
                    ext?.OpenInExplorerAndSelect(_element.FullPath);
            }, "Open file location in Windows Explorer.");
        }

        SetupBlameButton(status);
    }

    private void SetupLockButton(string status, bool isUnversioned, bool isMissingOrDeleted)
    {
        if (lockBtn == null) return;

        bool canLock = !isUnversioned && status != "A" && !isMissingOrDeleted;
        if (!canLock)
        {
            lockBtn.gameObject.SetActive(false);
            return;
        }

        lockBtn.gameObject.SetActive(true);
        lockBtn.onClick.RemoveListener(_onLockClickDelegate);
        lockBtn.onClick.AddListener(_onLockClickDelegate);
        lockBtn.interactable = true;

        UpdateLockButtonVisuals();
    }

    private void UpdateLockButtonVisuals()
    {
        if (lockBtnText == null) return;

        if (_element.LockedByOther)
        {
            lockBtnText.text = "<color=#FF4444>O</color>";
            BindHover(lockBtn, "Locked by another user.");
        }
        else if (_element.LockedByMe)
        {
            lockBtnText.text = "<color=#00FF00>K</color>";
            BindHover(lockBtn, "Click to unlock.");
        }
        else
        {
            lockBtnText.text = "<color=#E6E6E6>U</color>";
            BindHover(lockBtn, "Click to lock.");
        }
    }

    private void SetupBlameButton(string status)
    {
        if (blameBtn == null) return;

        bool canBlame = !_element.IsFolder && status != "?" && status != "A" && !string.IsNullOrEmpty(status);
        blameBtn.gameObject.SetActive(canBlame);

        if (!canBlame) return;

        blameBtn.onClick.RemoveAllListeners();
        blameBtn.onClick.AddListener(() =>
        {
            SVNManager.Instance?.GetModule<SVNBlame>()?.ShowBlameInMainConsole(_element.FullPath);
        });
        BindHover(blameBtn, "See who last modified each line of this file.");
    }
    #endregion

    #region Button Actions
    private async void OnCommitClick()
    {
        string msg = $"Commit {_element.Name}";
        try
        {
            string result = await SvnRunner.RunAsync(
                $"commit -m \"{msg}\" \"{_element.FullPath}\"",
                SVNManager.Instance.WorkingDir);

            if (result.Contains("Committed revision"))
            {
                SVNLogBridge.LogLine($"<color=green>Committed:</color> {_element.Name}");
                await SVNManager.Instance.RefreshStatus();
            }
            else
            {
                SVNLogBridge.LogLine($"<color=yellow>Commit result:</color> {result}");
            }
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"Commit failed: {ex.Message}");
        }
    }

    private async void OnLockClick()
    {
        var lockModule = SVNManager.Instance?.GetModule<SVNLock>();
        if (lockModule == null) return;

        if (lockBtnText != null)
        {
            lockBtnText.text = "…";
            lockBtn.interactable = false;
        }

        try
        {
            await lockModule.ToggleLockSingleItem(_element);
        }
        finally
        {
            if (lockBtn != null)
                lockBtn.interactable = true;

            UpdateLockButtonVisuals();
        }
    }

    private void OnFullRowClick()
    {
        float elapsed = Time.time - _lastClickTime;
        var diffModule = SVNManager.Instance?.GetModule<SVNDiff>();
        if (diffModule == null) return;

        if (elapsed <= DoubleClickThreshold)
            _ = diffModule.ShowDiff(_element.FullPath);
        else
            _ = diffModule.ShowPreviewInUnity(_element.FullPath);

        _lastClickTime = Time.time;
    }

    private void OnFoldClick()
    {
        if (_svnStatus == null || _element == null) return;

        _element.IsExpanded = !_element.IsExpanded;
        _svnStatus.ToggleFolderVisibility(_element);
    }
    #endregion

    #region Hover & Tooltips
    private void BindHover(Button btn, string tooltipText)
    {
        if (btn == null) return;

        // Remove old handler and add new one — events can't be cleared from outside
        var existingHandler = btn.GetComponent<SVNHoverHandler>();
        if (existingHandler != null)
            Destroy(existingHandler);

        var handler = btn.gameObject.AddComponent<SVNHoverHandler>();
        handler.OnHoverEnter += () => SVNLogBridge.LogTooltip(tooltipText);
        handler.OnHoverExit += () => SVNLogBridge.ClearTooltip();
    }
    #endregion

    #region Background
    private void ApplyRowBackground()
    {
        if (_rowBackground != null)
            _rowBackground.color = GetRowBackgroundColor(_element.Status);
    }

    private static Color GetRowBackgroundColor(string status) => status switch
    {
        "M" => RowBgModified,
        "A" => RowBgAdded,
        "?" => RowBgUnversioned,
        "D" or "!" => RowBgDeleted,
        "C" => RowBgConflict,
        "I" => RowBgIgnored,
        _ => RowBgDefault
    };
    #endregion

    #region Filter
    public void ApplyFilter(string filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            gameObject.SetActive(true);
            return;
        }

        string f = filterText.Trim();
        bool matches =
            _element.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
            _element.FullPath.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
            _element.Status.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;

        gameObject.SetActive(matches);
    }
    #endregion

    #region Helpers
    private static bool IsRootElement(string fullPath) =>
        fullPath == ".svn-root" || fullPath == "__ROOT__";

    private static Color GetStatusColor(string status) => status switch
    {
        "M" => StatusModified,
        "K" => StatusLocked,
        "O" => StatusOtherLocked,
        "A" => StatusAdded,
        "?" => StatusUnversioned,
        "D" or "!" => StatusDeleted,
        "C" => StatusConflict,
        "I" => StatusIgnored,
        _ => StatusDefault
    };

    private static void SetButtonActive(Button btn, bool active)
    {
        if (btn != null)
            btn.gameObject.SetActive(active);
    }

    private static void ActivateButton(Button btn, Action action, string tooltip)
    {
        if (btn == null || action == null) return;

        btn.gameObject.SetActive(true);
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(() => action());
        BindHoverStatic(btn, tooltip);
    }

    private static void BindHoverStatic(Button btn, string tooltipText)
    {
        if (btn == null) return;

        var existingHandler = btn.GetComponent<SVNHoverHandler>();
        if (existingHandler != null)
            Destroy(existingHandler);

        var handler = btn.gameObject.AddComponent<SVNHoverHandler>();
        handler.OnHoverEnter += () => SVNLogBridge.LogTooltip(tooltipText);
        handler.OnHoverExit += () => SVNLogBridge.ClearTooltip();
    }

    private void RemoveAllButtonListeners()
    {
        foldButton?.onClick.RemoveListener(_onFoldClickDelegate);
        selectionToggle?.onValueChanged.RemoveListener(_onToggleChangedDelegate);
        fullRowButton?.onClick.RemoveListener(_onFullRowClickDelegate);
        commitBtn?.onClick.RemoveListener(_onCommitClickDelegate);
        lockBtn?.onClick.RemoveListener(_onLockClickDelegate);
    }
    #endregion
}