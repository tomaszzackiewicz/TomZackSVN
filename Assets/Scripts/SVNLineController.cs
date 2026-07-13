using SVN.Core;
using System;
using System.Collections;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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
    [SerializeField] private Button resolveBtn;
    [SerializeField] private Button commitBtn;

    private SvnTreeElement _element;
    private SVNStatus svnStatus;

    private float lastClickTime;
    private const float doubleClickThreshold = 0.3f;
    private Coroutine _hoverCoroutine;
    private CancellationTokenSource _diffHoverCts;

    public void Setup(SvnTreeElement element, SVNStatus manager)
    {
        _element = element;
        svnStatus = manager;

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
        if (addBtn) addBtn.gameObject.SetActive(false);
        if (revertBtn) revertBtn.gameObject.SetActive(false);
        if (logBtn) logBtn.gameObject.SetActive(false);
        if (lockBtn) lockBtn.gameObject.SetActive(false);
        if (blameBtn) blameBtn.gameObject.SetActive(false);
        if (explorerBtn) explorerBtn.gameObject.SetActive(false);
        if (resolveBtn) resolveBtn.gameObject.SetActive(false);
        if (commitBtn) commitBtn.gameObject.SetActive(false);
    }

    private void RenderIndent()
    {
        string indent = "";
        for (int i = 0; i < _element.Depth; i++)
            indent += (i == _element.Depth - 1) ? "└─ " : " |  ";
        indentText.text = indent;
    }

    private void RenderStatusAndName()
    {
        bool isRoot = _element.FullPath == ".svn-root" || _element.FullPath == "__ROOT__";
        string statusClean = (_element.Status == "DIR" || string.IsNullOrEmpty(_element.Status))
            ? ""
            : $" [{_element.Status}]";

        nameText.fontStyle = FontStyles.Normal;
        nameText.color = Color.white;

        if (_element.IsFolder)
        {
            string dirHex = "#003366";
            statusText.text = isRoot
                ? $"<b><color={dirHex}>[ROOT]</color></b>{statusClean}"
                : $"<b><color={dirHex}>[DIR]</color></b>{statusClean}";
            statusText.color = Color.black;

            nameText.text = _element.Name;
            nameText.color = new Color(0f, 0.2f, 0.4f);
            nameText.fontStyle = FontStyles.Bold;

            if (sizeText) sizeText.text = "";
        }
        else
        {
            statusText.text = "<color=#ADD8E6>[FILE]</color> " + statusClean;
            statusText.color = GetStatusColor(_element.Status);
            nameText.text = _element.Name;

            if (sizeText)
                sizeText.text = _element.IsCommitDelegate ? "" : _element.Size;
        }
    }

    private void SetupFoldButton()
    {
        if (!foldButton) return;

        CanvasGroup cg = foldButton.GetComponent<CanvasGroup>();
        if (!cg) cg = foldButton.gameObject.AddComponent<CanvasGroup>();

        if (_element.IsFolder)
        {
            cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true;

            var btnText = foldButton.GetComponentInChildren<TextMeshProUGUI>();
            if (btnText)
            {
                btnText.text = "▼";
                btnText.rectTransform.localRotation = Quaternion.Euler(0, 0, _element.IsExpanded ? 0f : 90f);
            }
            foldButton.onClick.RemoveAllListeners();
            foldButton.onClick.AddListener(OnFoldClick);
        }
        else
        {
            cg.alpha = 0f; cg.interactable = false; cg.blocksRaycasts = false;
        }
    }

    private void SetupSelectionToggle()
    {
        if (!selectionToggle) return;

        selectionToggle.onValueChanged.RemoveAllListeners();
        selectionToggle.SetIsOnWithoutNotify(_element.IsChecked);
        if (nameText) nameText.alpha = _element.IsChecked ? 1f : 0.6f;

        selectionToggle.onValueChanged.AddListener(val =>
        {
            _element.IsChecked = val;
            if (nameText) nameText.alpha = val ? 1f : 0.6f;
            if (_element.IsFolder)
                svnStatus.ToggleChildrenSelection(_element, val);
            svnStatus.NotifySelectionChanged();
        });
    }

    private void SetupFullRowButton()
    {
        if (!fullRowButton) return;

        fullRowButton.onClick.RemoveAllListeners();

        bool isRootMeta = _element.FullPath == ".svn-root" || _element.FullPath == "__ROOT__";
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
            statusText.text = "[ROOT]";
            return;
        }

        if (canDiff)
        {
            fullRowButton.interactable = true;
            fullRowButton.onClick.AddListener(OnFullRowClick);
            BindHover(fullRowButton, "Click: Preview | Double-Click: External Diff");

            var hoverHandler = fullRowButton.gameObject.GetComponent<SVNHoverHandler>();
            if (!hoverHandler) hoverHandler = fullRowButton.gameObject.AddComponent<SVNHoverHandler>();

            hoverHandler.OnHoverEnter += () => _hoverCoroutine = StartCoroutine(LoadDiffPreview(_element.FullPath));
            hoverHandler.OnHoverExit += () =>
            {
                if (_hoverCoroutine != null) { StopCoroutine(_hoverCoroutine); _hoverCoroutine = null; }
                SVNLogBridge.ClearTooltip();
            };
        }
        else if (isFolder)
        {
            fullRowButton.interactable = true;
            fullRowButton.onClick.AddListener(OnFoldClick);
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

        if (!_element.IsFolder && hasChanges)
        {
            if (isUnversioned && addBtn) ActivateButton(addBtn, () => SVNManager.Instance.GetModule<SVNAdd>()?.AddSingleItem(_element), "Add this unversioned file to SVN control.");

            if (status == "C" && resolveBtn) ActivateButton(resolveBtn, () => SVNManager.Instance?.PanelHandler?.Button_OpenResolve(), "This file has conflicts. Click to open Resolve panel.");

            if (!isUnversioned && status != "!" && status != "C" && commitBtn)
            {
                commitBtn.gameObject.SetActive(true);
                commitBtn.onClick.RemoveAllListeners();
                commitBtn.onClick.AddListener(async () =>
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
                });
                BindHover(commitBtn, "Commit only this file.");
            }

            if (!_element.IsFolder && !isUnversioned && status != "A" && !isMissingOrDeleted && lockBtn)
            {
                lockBtn.gameObject.SetActive(true);
                lockBtn.onClick.RemoveAllListeners();
                lockBtn.interactable = true;

                lockBtn.onClick.AddListener(async () =>
                {
                    var lockModule = SVNManager.Instance.GetModule<SVNLock>();
                    if (lockModule == null) return;
                    lockBtnText.text = "…";
                    lockBtn.interactable = false;
                    try
                    {
                        await lockModule.ToggleLockSingleItem(_element);
                    }
                    finally
                    {
                        lockBtn.interactable = true;
                        lockBtnText.text = _element.LockedByOther ? "<color=#FF4444>O</color>"
                                        : _element.LockedByMe ? "<color=#00FF00>K</color>"
                                        : "<color=#E6E6E6>U</color>";
                    }
                });

                if (_element.LockedByOther) { lockBtnText.text = "<color=#FF4444>O</color>"; BindHover(lockBtn, "Locked by another user."); }
                else if (_element.LockedByMe) { lockBtnText.text = "<color=#00FF00>K</color>"; BindHover(lockBtn, "Click to unlock."); }
                else { lockBtnText.text = "<color=#E6E6E6>U</color>"; BindHover(lockBtn, "Click to lock."); }
            }
            else if (lockBtn) lockBtn.gameObject.SetActive(false);

            if (!isUnversioned && revertBtn) ActivateButton(revertBtn, () => SVNManager.Instance.GetModule<SVNRevert>()?.RevertSingleItem(_element), "Discard local changes and restore to repository version.");

            if (!isUnversioned && status != "A" && logBtn) ActivateButton(logBtn, () => SVNManager.Instance.GetModule<SVNLog>()?.ShowLogForPath(_element.FullPath), "Open SVN Log history for this file.");

            if (explorerBtn)
            {
                ActivateButton(explorerBtn, () =>
                {
                    var ext = SVNManager.Instance.GetModule<SVNExternal>();
                    if (isMissingOrDeleted) ext?.OpenInExplorer();
                    else ext?.OpenInExplorerAndSelect(_element.FullPath);
                }, "Open file location in Windows Explorer.");
            }

            if (blameBtn)
            {
                blameBtn.onClick.RemoveAllListeners();
                bool canBlame = !_element.IsFolder && status != "?" && status != "A" && !string.IsNullOrEmpty(status);
                blameBtn.gameObject.SetActive(canBlame);
                if (canBlame)
                {
                    blameBtn.onClick.AddListener(() =>
                    {
                        SVNManager.Instance?.GetModule<SVNBlame>()?.ShowBlameInMainConsole(_element.FullPath);
                    });
                    BindHover(blameBtn, "See who last modified each line of this file.");
                }
            }
        }
        else
        {
            if (explorerBtn) ActivateButton(explorerBtn, () => SVNManager.Instance.GetModule<SVNExternal>()?.OpenInExplorerAndSelect(_element.FullPath), "Open location in Windows Explorer.");
        }
    }

    private void ActivateButton(Button btn, Action action, string tooltip)
    {
        btn.gameObject.SetActive(true);
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(() => action());
        BindHover(btn, tooltip);
    }

    private void ApplyRowBackground()
    {
        Color rowColor = GetRowBackgroundColor(_element.Status);
        if (TryGetComponent<Image>(out var bg))
            bg.color = rowColor;
        else
        {
            var image = gameObject.AddComponent<Image>();
            image.color = rowColor;
        }
    }

    private Color GetRowBackgroundColor(string status) => status switch
    {
        "M" => new Color(1f, 0.85f, 0f, 0.08f),
        "A" => new Color(0f, 1f, 0f, 0.06f),
        "?" => new Color(0f, 0.9f, 1f, 0.06f),
        "D" or "!" => new Color(1f, 0.2f, 0.2f, 0.08f),
        "C" => new Color(1f, 0f, 1f, 0.1f),
        "I" => new Color(0.3f, 0.3f, 0.3f, 0.05f),
        _ => new Color(0, 0, 0, 0)
    };

    private IEnumerator LoadDiffPreview(string relativePath)
    {
        _diffHoverCts?.Cancel();
        _diffHoverCts = new CancellationTokenSource();
        var token = _diffHoverCts.Token;
        yield return new WaitForSeconds(0.5f);
        if (token.IsCancellationRequested) yield break;

        if (_element.Status == "?")
        {
            SVNLogBridge.LogTooltip("File is not under version control.");
            yield break;
        }

        var task = SvnRunner.RunAsync($"diff \"{relativePath}\"", SVNManager.Instance.WorkingDir, false, token);
        yield return new WaitUntil(() => task.IsCompleted || token.IsCancellationRequested);
        if (token.IsCancellationRequested) yield break;

        if (task.IsFaulted || string.IsNullOrEmpty(task.Result))
        {
            SVNLogBridge.LogTooltip("No local changes.");
            yield break;
        }

        string diff = task.Result;
        var lines = diff.Split('\n');
        var preview = new System.Text.StringBuilder();
        int count = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("@@") || line.StartsWith("---") || line.StartsWith("+++")) continue;
            if (count >= 3) break;
            if (!string.IsNullOrWhiteSpace(line))
            {
                preview.AppendLine(line.Trim());
                count++;
            }
        }
        SVNLogBridge.LogTooltip(preview.Length > 0 ? preview.ToString() : "No changes in this file.");
    }

    public void ApplyFilter(string filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText)) { gameObject.SetActive(true); return; }
        string f = filterText.ToLower();
        bool matches = _element.Name.ToLower().Contains(f)
                    || _element.FullPath.ToLower().Contains(f)
                    || _element.Status.ToLower().Contains(f);
        gameObject.SetActive(matches);
    }

    private void OnFullRowClick()
    {
        float elapsed = Time.time - lastClickTime;
        var diffModule = SVNManager.Instance?.GetModule<SVNDiff>();
        if (diffModule == null) return;

        if (elapsed <= doubleClickThreshold)
            _ = diffModule.ShowDiff(_element.FullPath);
        else
            _ = diffModule.ShowPreviewInUnity(_element.FullPath);

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

    private Color GetStatusColor(string status) => status switch
    {
        "M" => ParseHex("#FFD700"),
        "K" => ParseHex("#00FF00"),
        "O" => ParseHex("#FF4444"),
        "A" => ParseHex("#00FF00"),
        "?" => ParseHex("#00E5FF"),
        "D" or "!" => ParseHex("#FF4444"),
        "C" => ParseHex("#FF00FF"),
        "I" => ParseHex("#444444"),
        _ => Color.white
    };

    private Color ParseHex(string hex)
    {
        ColorUtility.TryParseHtmlString(hex, out Color c);
        return c;
    }

    private void BindHover(Button btn, string tooltipText)
    {
        if (!btn) return;

        var existingHandler = btn.gameObject.GetComponent<SVNHoverHandler>();
        if (existingHandler) Destroy(existingHandler);

        var handler = btn.gameObject.AddComponent<SVNHoverHandler>();
        handler.OnHoverEnter += () => SVNLogBridge.LogTooltip(tooltipText);
        handler.OnHoverExit += () => SVNLogBridge.ClearTooltip();
    }
}