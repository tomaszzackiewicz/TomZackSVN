using SVN.Core;
using System;
using System.Collections.Generic;
using System.IO;
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
    [SerializeField] private Button restoreFromRevBtn;
    [SerializeField] private Button extractToRevBtn;

    private SvnTreeElement _element;
    private SVNStatus _svnStatus;
    private Image _rowBackground;
    private float _lastClickTime = -10f;
    private const float DoubleClickThreshold = 0.3f;
    private CancellationTokenSource _destroyCts;
    private int _commitBusy;

    private CanvasGroup _canvasGroup;

    private UnityAction _onFoldClickDelegate;
    private UnityAction<bool> _onToggleChangedDelegate;
    private UnityAction _onFullRowClickDelegate;
    private UnityAction _onCommitClickDelegate;
    private UnityAction _onLockClickDelegate;
    private Dictionary<Button, SVNHoverHandler> _hoverHandlersCache;
    private LayoutElement _layoutElement;
    private static readonly Dictionary<int, string> IndentCache = new();

    public SvnTreeElement Element => _element;
    public bool IsCommitDelegate => isCommitDelegate;

    private void Awake()
    {
        _destroyCts = new CancellationTokenSource();
        _onFoldClickDelegate = OnFoldClick;
        _onToggleChangedDelegate = OnToggleChanged;
        _onFullRowClickDelegate = OnFullRowClick;
        _onCommitClickDelegate = () => SafeFireAndForget(OnCommitClickAsync);
        _onLockClickDelegate = () => SafeFireAndForget(OnLockClickAsync);

        if (!TryGetComponent(out _rowBackground))
            _rowBackground = gameObject.AddComponent<Image>();

        if (!TryGetComponent(out _layoutElement))
            _layoutElement = gameObject.AddComponent<LayoutElement>();

        _canvasGroup = GetComponent<CanvasGroup>();

        _hoverHandlersCache = new Dictionary<Button, SVNHoverHandler>();
        Button[] allButtons = GetComponentsInChildren<Button>(true);
        foreach (var btn in allButtons)
        {
            var handler = btn.GetComponent<SVNHoverHandler>();
            if (handler == null)
                handler = btn.gameObject.AddComponent<SVNHoverHandler>();

            _hoverHandlersCache[btn] = handler;
        }
    }

    private void OnDestroy()
    {
        _destroyCts?.Cancel();
        var cts = _destroyCts;
        _destroyCts = null;
        if (cts != null)
            _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });

        RemoveAllButtonListeners();
    }

    private void SafeFireAndForget(Func<Task> operation)
    {
        _ = FireAndForget(operation);
    }

    private async Task FireAndForget(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) { SVNLogBridge.LogError($"[SvnLine] {ex.Message}"); }
    }

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
        if (commitBtn != null)
        {
            commitBtn.gameObject.SetActive(false);
            commitBtn.onClick.RemoveListener(_onCommitClickDelegate);
        }
        SetButtonActive(restoreFromRevBtn, false);
        SetButtonActive(extractToRevBtn, false);
    }

    private void RenderIndent()
    {
        if (indentText == null) return;

        int depth = _element.Depth;
        float targetWidth = depth <= 0 ? 0f : depth * 30f;
        indentText.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);

        if (depth <= 0)
        {
            indentText.text = string.Empty;
            return;
        }

        if (!IndentCache.TryGetValue(depth, out string cached))
        {
            var sb = new StringBuilder(depth * 4);
            for (int i = 0; i < depth; i++)
                sb.Append(i == depth - 1 ? "└─ " : " |  ");
            cached = sb.ToString();
            IndentCache[depth] = cached;
        }

        indentText.text = cached;
    }

    private void RenderStatusAndName()
    {
        bool isRoot = IsRootElement(_element.FullPath);

        // === FIX (spójność marków): status mark ([M], [A], [D], ...) zawsze w kolorze
        // SVNColors.GetStatusHex(status). Marki typu ([DIR]/[ROOT]/[FILE]) zostają na
        // DirHex/FileHex. Nazwy: folder → DirNameColor, plik → FileNameColor (= FileHex).
        string statusHex = SVNColors.GetStatusHex(_element.Status);
        bool hasStatus = !string.IsNullOrEmpty(_element.Status)
                         && _element.Status != " "
                         && _element.Status != "DIR";

        string statusMark = hasStatus
            ? $" <color={statusHex}><b>[{_element.Status}]</b></color>"
            : "";

        string protTag =
            _element.IsProtectionRemovalPending ? $" <color={SVNColors.Warning}><b>[P-]</b></color>" :
            _element.IsProtected ? $" <color={SVNColors.Warning}><b>[P]</b></color>" : "";

        if (nameText != null)
        {
            nameText.fontStyle = FontStyles.Normal;
            nameText.color = Color.white;
        }

        if (_element.IsFolder)
        {
            string typeMark = isRoot
                ? $"<color={SVNColors.DirHex}><b>[ROOT]</b></color>"
                : $"<color={SVNColors.DirHex}><b>[DIR]</b></color>";

            if (statusText != null)
            {
                statusText.text = $"{typeMark}{statusMark}{protTag}";
                statusText.color = Color.white;
            }

            if (nameText != null)
            {
                nameText.text = _element.Name;
                nameText.color = SVNColors.DirNameColor;
                nameText.fontStyle = FontStyles.Bold;
            }

            if (sizeText != null)
                sizeText.text = "";
        }
        else
        {
            if (statusText != null)
            {
                string typeMark = $"<color={SVNColors.FileHex}>[FILE]</color>";
                statusText.text = $"{typeMark}{statusMark}{protTag}";
                statusText.color = Color.white;
            }

            if (nameText != null)
            {
                nameText.text = _element.Name;
                // === FIX: nazwa pliku w kolorze [FILE] (#ADD8E6). Wcześniej
                // brak ustawienia → dziedziczyła `Color.white` z Resetu wyżej.
                nameText.color = SVNColors.FileNameColor;
            }

            if (sizeText != null)
                sizeText.text = _element.IsCommitDelegate ? "" : _element.Size;
        }
    }

    private void SetupFoldButton()
    {
        if (foldButton == null || _onFoldClickDelegate == null) return;

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
        if (selectionToggle == null || _onToggleChangedDelegate == null) return;

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
        if (fullRowButton == null || _onFullRowClickDelegate == null) return;

        fullRowButton.onClick.RemoveListener(_onFoldClickDelegate);
        fullRowButton.onClick.RemoveListener(_onFullRowClickDelegate);

        bool isRootMeta = IsRootElement(_element.FullPath);
        bool isFolder = _element.IsFolder;
        bool isFile = !isFolder;
        bool hasStatus = !string.IsNullOrEmpty(_element.Status) && _element.Status != " " && _element.Status != "?";
        bool canDiff = !isRootMeta && isFile && hasStatus;

        if (isRootMeta)
        {
            fullRowButton.interactable = true;
            fullRowButton.onClick.AddListener(_onFullRowClickDelegate);
            BindHover(fullRowButton, "Repository root change (M .)");
            if (statusText != null) statusText.text = "[ROOT]";
            return;
        }

        if (canDiff)
        {
            fullRowButton.interactable = true;
            fullRowButton.onClick.AddListener(_onFullRowClickDelegate);

            var tooltip = new StringBuilder();
            tooltip.Append($"Path: {_element.FullPath} | Status: {_element.Status}");
            if (!string.IsNullOrEmpty(_element.Size)) tooltip.Append($" | Size: {_element.Size}");
            if (_element.LockedByMe) tooltip.Append(" | Locked by you");
            else if (_element.LockedByOther) tooltip.Append(" | Locked by another user");
            if (_element.IsProtected) tooltip.Append(" | Protected (svn:needs-lock)");
            if (_element.DiffStatsLoaded) tooltip.Append($" | Diff: +{_element.AddedLines} -{_element.RemovedLines}");
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

        if (isUnversioned && addBtn != null)
            ActivateButton(addBtn, () => SVNManager.Instance?.GetModule<SVNAdd>()?.AddSingleItem(_element), "Add this unversioned file to SVN control.");

        if (status == "C" && resolveBtn != null)
            ActivateButton(resolveBtn, () => SVNManager.Instance?.PanelHandler?.Button_OpenResolve(), "This file has conflicts. Click to open Resolve panel.");

        if (!isUnversioned && status != "!" && status != "C" && commitBtn != null)
        {
            commitBtn.gameObject.SetActive(true);
            commitBtn.onClick.RemoveListener(_onCommitClickDelegate);
            commitBtn.onClick.AddListener(_onCommitClickDelegate);
            BindHover(commitBtn, "Commit only this file.");
        }

        SetupLockButton(status, isUnversioned, isMissingOrDeleted);

        if (!isUnversioned && revertBtn != null)
            ActivateButton(revertBtn, () => SVNManager.Instance?.GetModule<SVNRevert>()?.RevertSingleItem(_element), "Discard local changes and restore to repository version.");

        if (!isUnversioned && status != "A" && logBtn != null)
            ActivateButton(logBtn, () => SVNManager.Instance?.GetModule<SVNLog>()?.ShowLogForPath(_element.FullPath), "Open SVN Log history for this file.");

        if (explorerBtn != null)
        {
            ActivateButton(explorerBtn, () =>
            {
                var ext = SVNManager.Instance?.GetModule<SVNExternal>();
                if (isMissingOrDeleted) ext?.OpenInExplorer();
                else ext?.OpenInExplorerAndSelect(_element.FullPath);
            }, "Open file location in Windows Explorer.");
        }

        SetupBlameButton(status);

        SetupHistoryRevisionButtons(status);
    }

    private static void ActivateButton(Button btn, Action action, string tooltip)
    {
        if (btn == null || action == null) return;

        btn.gameObject.SetActive(true);

        btn.onClick.RemoveAllListeners();

        btn.onClick.AddListener(() => action());
        BindHoverStatic(btn, tooltip);
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
        if (lockBtnText == null || lockBtn == null) return;

        if (_element.LockedByOther)
        {
            lockBtnText.text = $"<color={SVNColors.StatusLockedByOther}>O</color>";
            lockBtn.interactable = false;
            BindHover(lockBtn, "Locked by another user. Use the dedicated Lock Panel to force steal.");
        }
        else if (_element.LockedByMe)
        {
            lockBtnText.text = $"<color={SVNColors.StatusLockedByMe}>K</color>";
            lockBtn.interactable = true;
            BindHover(lockBtn, "Click to unlock.");
        }
        else
        {
            lockBtnText.text = $"<color={SVNColors.Info}>U</color>";
            lockBtn.interactable = true;

            BindHover(lockBtn, _element.IsProtected
                ? "Click to lock. File is protected (svn:needs-lock) — read-only until you hold the lock."
                : "Click to lock.");
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

    private void SetupHistoryRevisionButtons(string status)
    {
        bool hasHistory = !_element.IsFolder && status != "?" && status != "A" && !string.IsNullOrEmpty(status);

        if (restoreFromRevBtn != null)
        {
            restoreFromRevBtn.gameObject.SetActive(hasHistory);
            if (hasHistory)
            {
                restoreFromRevBtn.onClick.RemoveAllListeners();
                restoreFromRevBtn.onClick.AddListener(OnSendToRevisionPanelClick);
                BindHover(restoreFromRevBtn, "Send to Revision Panel to OVERWRITE this file with an older version from history.");
            }
        }

        if (extractToRevBtn != null)
        {
            extractToRevBtn.gameObject.SetActive(hasHistory);
            if (hasHistory)
            {
                extractToRevBtn.onClick.RemoveAllListeners();
                extractToRevBtn.onClick.AddListener(OnSendToRevisionPanelClick);
                BindHover(extractToRevBtn, "Send to Revision Panel to SAVE A COPY of this file from history to a chosen location.");
            }
        }
    }

    private void OnSendToRevisionPanelClick()
    {
        var ui = SVNUI.Instance;
        if (ui == null) return;

        if (ui.RevisionFilePathInput == null || ui.UpdateRevisionInput == null)
        {
            SVNLogBridge.LogError("<color=red>[Setup Error]</color> RevisionFilePathInput or UpdateRevisionInput is not assigned in SVNUI Inspector!");
            return;
        }

        SVNManager.Instance?.PanelHandler?.Button_OpenRevision();

        ui.RevisionFilePathInput.text = _element.FullPath;

        ui.UpdateRevisionInput.text = "";
        ui.UpdateRevisionInput.ActivateInputField();

        SVNLogBridge.LogLine($"<color={SVNColors.Header}><b>[Revision Tool]</b></color> File path sent to the Revision Panel successfully.");
        SVNLogBridge.LogLine($"<color={SVNColors.Warning}>-> Now, type the revision number in the highlighted field (e.g., <b>150</b>).</color>");
        SVNLogBridge.LogLine($"<color={SVNColors.Warning}>-> Then click <b>'Restore'</b> to overwrite the file, or <b>'Extract'</b> to save a copy elsewhere.</color>");
    }

    private async Task OnCommitClickAsync()
    {
        var cts = _destroyCts;
        if (cts == null || cts.IsCancellationRequested) return;
        var token = cts.Token;

        if (Interlocked.Exchange(ref _commitBusy, 1) == 1) return;

        string msgFile = null;
        try
        {
            var manager = SVNManager.Instance;
            if (manager == null) return;

            string safeName = (_element.Name ?? "file").Replace("\"", "'");
            var sb = new StringBuilder(safeName.Length);
            foreach (char c in $"Commit {safeName}")
                if (!char.IsControl(c)) sb.Append(c);
            string message = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(message)) message = "Commit single file";

            msgFile = Path.Combine(Path.GetTempPath(), $"svn_line_msg_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(msgFile, message, new UTF8Encoding(false), token);

            string result = await SvnRunner.RunAsync(
                BuildSingleCommitCommand(msgFile),
                manager.WorkingDir,
                false,
                token);

            if (this == null) return;

            if (result != null && result.Contains("Committed revision"))
            {
                SVNLogBridge.LogLine($"<color={SVNColors.Success}>Committed:</color> {_element.Name}");

                SVNStatus.ClearLockCache();
                manager.DiskChangesDetected = true;

                await manager.RefreshStatus(force: true);
            }
            else
            {
                SVNLogBridge.LogLine($"<color={SVNColors.Warning}>Commit result:</color> {result}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (this != null)
                SVNLogBridge.LogError($"Commit failed: {ex.Message}");
        }
        finally
        {
            if (msgFile != null) { try { if (File.Exists(msgFile)) File.Delete(msgFile); } catch { } }
            Interlocked.Exchange(ref _commitBusy, 0);
        }
    }

    private string BuildSingleCommitCommand(string msgFilePath)
    {
        string escapedPath = _element.FullPath.Replace("\"", "\\\"");
        return $"commit -F \"{msgFilePath}\" \"{escapedPath}\"";
    }

    private async Task OnLockClickAsync()
    {
        var cts = _destroyCts;
        if (cts == null || cts.IsCancellationRequested) return;

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
        catch (OperationCanceledException) { }
        finally
        {
            if (this != null)
            {
                if (lockBtn != null)
                    lockBtn.interactable = true;
                UpdateLockButtonVisuals();
            }
        }
    }

    private void OnFullRowClick()
    {
        float elapsed = Time.time - _lastClickTime;
        var diffModule = SVNManager.Instance?.GetModule<SVNDiff>();
        if (diffModule == null) return;

        if (elapsed <= DoubleClickThreshold)
            SafeFireAndForget(() => diffModule.ShowDiff(_element.FullPath));
        else
            SafeFireAndForget(() => diffModule.ShowPreviewInUnity(_element.FullPath));

        _lastClickTime = Time.time;
    }

    private void OnFoldClick()
    {
        if (_svnStatus == null || _element == null) return;

        _svnStatus.ToggleFolderVisibility(_element);
    }

    private void BindHover(Button btn, string tooltipText)
    {
        if (btn == null || !_hoverHandlersCache.TryGetValue(btn, out var handler)) return;
        handler.TooltipText = tooltipText;
    }

    private static void BindHoverStatic(Button btn, string tooltipText)
    {
        if (btn == null) return;
        var handler = btn.GetComponent<SVNHoverHandler>();
        if (handler != null)
            handler.TooltipText = tooltipText;
    }

    private void ApplyRowBackground()
    {
        if (_rowBackground != null)
            _rowBackground.color = SVNColors.GetRowBackgroundColor(_element.Status);
    }

    public void ApplyFilter(string filterText)
    {
        if (_layoutElement == null) return;

        if (string.IsNullOrWhiteSpace(filterText))
        {
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            _layoutElement.ignoreLayout = false;

            if (_canvasGroup != null) { _canvasGroup.alpha = 1f; _canvasGroup.blocksRaycasts = true; }
            return;
        }

        string f = filterText.Trim();
        bool matches =
            _element.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
            _element.FullPath.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
            _element.Status.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;

        if (matches)
        {
            _layoutElement.ignoreLayout = false;
            if (_canvasGroup != null) { _canvasGroup.alpha = 1f; _canvasGroup.blocksRaycasts = true; }
        }
        else
        {
            _layoutElement.ignoreLayout = true;

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 0f;
                _canvasGroup.blocksRaycasts = false;
            }
            else
            {
                gameObject.SetActive(false);
            }
        }
    }

    private static bool IsRootElement(string fullPath) =>
        fullPath == ".svn-root" || fullPath == "__ROOT__";

    private static void SetButtonActive(Button btn, bool active)
    {
        if (btn != null)
            btn.gameObject.SetActive(active);
    }

    private void RemoveAllButtonListeners()
    {
        foldButton?.onClick.RemoveListener(_onFoldClickDelegate);
        selectionToggle?.onValueChanged.RemoveListener(_onToggleChangedDelegate);
        fullRowButton?.onClick.RemoveListener(_onFullRowClickDelegate);
        fullRowButton?.onClick.RemoveListener(_onFoldClickDelegate);
        commitBtn?.onClick.RemoveListener(_onCommitClickDelegate);
        lockBtn?.onClick.RemoveListener(_onLockClickDelegate);
    }
}