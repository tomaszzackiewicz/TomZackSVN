using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class MergePanel : MonoBehaviour
{
    private SVNManager _svnManager;
    private SVNUI _svnUI;
    private SVNMerge _mergeModule;

    private void Awake() => ResolveReferences();

    private async void OnEnable()
    {
        if (_svnManager == null || _mergeModule == null)
            ResolveReferences();

        SubscribeEvents();
        ClearMergeUI();
        if (_svnManager != null && string.IsNullOrEmpty(_svnManager.WorkingDir) && _svnManager.IsProcessing)
        {
            while (_svnManager.IsProcessing && gameObject.activeInHierarchy)
            {
                await Task.Yield();
            }
        }

        if (!string.IsNullOrEmpty(_svnManager?.WorkingDir) && _mergeModule != null)
        {
            SafeFireAndForget(() => RefreshBranchDropdownAsync(false));
        }
    }

    private void SubscribeEvents()
    {
        if (_mergeModule == null) return;

        _mergeModule.OnDryRunCompleted -= HandleDryRunResult;
        _mergeModule.OnDryRunCompleted += HandleDryRunResult;

        if (_svnManager != null)
        {
            _svnManager.OnProjectChanged -= OnProjectChanged;
            _svnManager.OnProjectChanged += OnProjectChanged;
        }

        if (_svnUI?.MergeBranchesDropdown != null)
        {
            _svnUI.MergeBranchesDropdown.onValueChanged.RemoveListener(OnBranchSelected);
            _svnUI.MergeBranchesDropdown.onValueChanged.AddListener(OnBranchSelected);
        }

        if (_svnUI?.MergeSourceInput != null)
        {
            _svnUI.MergeSourceInput.onEndEdit.RemoveListener(OnManualSourceInput);
            _svnUI.MergeSourceInput.onEndEdit.AddListener(OnManualSourceInput);
        }
    }

    private void OnDisable() => UnsubscribeEvents();

    private void OnDestroy() => UnsubscribeEvents();

    private void ResolveReferences()
    {
        _svnUI = SVNUI.Instance;
        _svnManager = SVNManager.Instance;

        if (_svnManager == null)
        {
            Debug.LogError("[MergePanel] SVNManager.Instance is null. Merge functionality will not work.", this);
            return;
        }

        _mergeModule = _svnManager.GetModule<SVNMerge>();
        if (_mergeModule == null)
        {
            Debug.LogError("[MergePanel] SVNMerge module not found.", this);
        }
    }

    private bool EnsureReady()
    {
        if (_mergeModule != null && _svnManager != null) return true;
        ResolveReferences();
        return _mergeModule != null;
    }

    public void OnBranchSelected(int index)
    {
        if (_svnUI?.MergeBranchesDropdown == null) return;
        if (_svnUI.MergeBranchesDropdown.options.Count == 0) return;
        if (index < 0 || index >= _svnUI.MergeBranchesDropdown.options.Count) return;

        string selectedName = _svnUI.MergeBranchesDropdown.options[index].text;
        if (!string.IsNullOrWhiteSpace(selectedName) && _svnUI.MergeSourceInput != null)
            _svnUI.MergeSourceInput.text = selectedName;
    }

    private void OnManualSourceInput(string input)
    {
        if (_svnUI?.MergeBranchesDropdown == null || string.IsNullOrWhiteSpace(input)) return;

        string trimmedInput = input.Trim();

        int index = _svnUI.MergeBranchesDropdown.options.FindIndex(
            o => string.Equals(o.text, trimmedInput, StringComparison.OrdinalIgnoreCase));

        if (index >= 0 && _svnUI.MergeBranchesDropdown.value != index)
        {
            _svnUI.MergeBranchesDropdown.value = index;
        }
    }

    private void OnProjectChanged(SVNProject project)
    {
        if (project == null) return;
        SafeFireAndForget(() => RefreshBranchDropdownAsync(true));
    }

    private void UnsubscribeEvents()
    {
        if (_mergeModule != null)
            _mergeModule.OnDryRunCompleted -= HandleDryRunResult;

        if (_svnManager != null)
            _svnManager.OnProjectChanged -= OnProjectChanged;

        if (_svnUI?.MergeBranchesDropdown != null)
            _svnUI.MergeBranchesDropdown.onValueChanged.RemoveAllListeners();

        if (_svnUI?.MergeSourceInput != null)
            _svnUI.MergeSourceInput.onEndEdit.RemoveAllListeners();
    }

    #region UI Button Methods

    public void Button_CancelMerge() => SafeFireAndForget(() => _mergeModule.CancelMerge());
    public void Button_RefreshBranchDropdown() => SafeFireAndForget(() => RefreshBranchDropdownAsync(true));
    public void Button_Compare() => SafeFireAndForget(() => _mergeModule.CompareWithTrunk());
    public void Button_SyncWithTrunk() => SafeFireAndForget(AutoSyncAsync);
    public void Button_RepairMergeHistory() => SafeFireAndForget(() => _mergeModule.RepairMergeHistory());
    public void Button_ForceMergeFromTrunk() => SafeFireAndForget(ForceMergeFromTrunkAsync);
    public void Button_DryRunMerge() => SafeFireAndForget(DryRunMergeAsync);
    public void Button_ConfirmMerge() => SafeFireAndForget(ConfirmMergeAsync);
    public void Button_CancelLocalMerge() => SafeFireAndForget(() => _mergeModule.CancelLocalMerge());
    public void Button_RevertToHead() => SafeFireAndForget(() => _mergeModule.RevertToHead());
    public void Button_UndoMerge() => SafeFireAndForget(() => _mergeModule.UndoLastMerge());
    public void Button_CherryPickConfirm() => SafeFireAndForget(CherryPickAsync);
    public void Button_CherryPickDryRun() => SafeFireAndForget(CherryPickDryRunAsync);

    #endregion

    private async void SafeFireAndForget(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"[MergePanel] Unhandled exception: {ex.Message}");
            Debug.LogException(ex, this);
        }
    }

    private async Task RefreshBranchDropdownAsync(bool force = true)
    {
        if (!EnsureReady()) return;
        if (_svnUI?.MergeBranchesDropdown == null) return;

        string currentSelection = null;
        if (_svnUI.MergeBranchesDropdown.options.Count > 0 &&
            _svnUI.MergeBranchesDropdown.value >= 0 &&
            _svnUI.MergeBranchesDropdown.value < _svnUI.MergeBranchesDropdown.options.Count)
        {
            currentSelection = _svnUI.MergeBranchesDropdown.options[_svnUI.MergeBranchesDropdown.value].text;
        }

        try
        {
            string[] branches = await _mergeModule.FetchAvailableBranches(force).ConfigureAwait(false);

            var options = new List<string> { "trunk" };
            if (branches != null)
            {
                foreach (string b in branches)
                {
                    string clean = b?.TrimEnd('/');
                    if (!string.IsNullOrEmpty(clean) && !clean.Equals("trunk", StringComparison.OrdinalIgnoreCase))
                        options.Add(clean);
                }
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                try
                {
                    if (this == null || _svnUI?.MergeBranchesDropdown == null)
                    {
                        tcs.SetResult(false);
                        return;
                    }

                    _svnUI.MergeBranchesDropdown.ClearOptions();
                    _svnUI.MergeBranchesDropdown.AddOptions(options);

                    int indexToSelect = 0;


                    if (force && !string.IsNullOrEmpty(currentSelection))
                    {
                        int foundIndex = options.FindIndex(o => string.Equals(o, currentSelection, StringComparison.OrdinalIgnoreCase));
                        if (foundIndex >= 0) indexToSelect = foundIndex;
                    }

                    _svnUI.MergeBranchesDropdown.value = indexToSelect;
                    _svnUI.MergeBranchesDropdown.RefreshShownValue();

                    if (_svnUI.MergeSourceInput != null)
                    {
                        _svnUI.MergeSourceInput.text = options[indexToSelect];
                    }

                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MergePanel] UI Update Error: {ex.Message}");
                    tcs.SetResult(false);
                }
            });

            await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"[MergePanel] Refresh failed: {ex.Message}");
        }
    }

    private async Task DryRunMergeAsync()
    {
        if (!EnsureReady()) return;

        HandleDryRunResult(new SVNMerge.MergeFileResult());
        string source = GetSafeSource();
        if (string.IsNullOrEmpty(source)) return;

        try
        {
            await _mergeModule.ExecuteMerge(source, true).ConfigureAwait(false);
        }
        finally
        {
            await RefreshBranchDropdownAsync().ConfigureAwait(false);
        }
    }

    private async Task ConfirmMergeAsync()
    {
        if (!EnsureReady()) return;

        HandleDryRunResult(new SVNMerge.MergeFileResult());
        string source = GetSafeSource();
        if (string.IsNullOrEmpty(source)) return;

        try
        {
            await _mergeModule.ExecuteMerge(source, false).ConfigureAwait(false);
        }
        finally
        {
            await RefreshBranchDropdownAsync().ConfigureAwait(false);
        }
    }

    private async Task ForceMergeFromTrunkAsync()
    {
        if (!EnsureReady()) return;

        try
        {
            await _mergeModule.ForceMergeFromTrunk().ConfigureAwait(false);
        }
        finally
        {
            await RefreshBranchDropdownAsync().ConfigureAwait(false);
        }
    }

    private async Task AutoSyncAsync()
    {
        if (!EnsureReady()) return;

        HandleDryRunResult(new SVNMerge.MergeFileResult());

        string source = GetSafeSource();
        if (string.IsNullOrWhiteSpace(source)) return;

        string currentUrl = await SvnRunner.GetRepoUrlAsync(_svnManager.WorkingDir).ConfigureAwait(false);
        SVNLogBridge.LogLine($"[AutoSync] Current: {currentUrl}");
        SVNLogBridge.LogLine($"[AutoSync] Source : {source}");

        await _mergeModule.ExecuteMerge(source, false).ConfigureAwait(false);
    }

    #region Cherry-Pick Methods

    private async Task CherryPickAsync()
    {
        if (!EnsureReady()) return;

        string source = GetSafeSourceWithFallback();
        string revision = GetCherryPickRevision();

        if (string.IsNullOrEmpty(source))
        {
            _mergeModule.LogErrorLocal("[Cherry-Pick] Select source (e.g. trunk or branch name).");
            return;
        }

        if (string.IsNullOrEmpty(revision))
        {
            _mergeModule.LogErrorLocal("[Cherry-Pick] Wpisz numer rewizji (np. 150) lub zakres (np. 140:150).");
            return;
        }

        HandleDryRunResult(new SVNMerge.MergeFileResult());

        try
        {
            await SvnMergeOperations.CherryPickMergeAsync(_mergeModule, source, revision, isDryRun: false).ConfigureAwait(false);
        }
        finally
        {
            await RefreshBranchDropdownAsync().ConfigureAwait(false);
        }
    }

    private async Task CherryPickDryRunAsync()
    {
        if (!EnsureReady()) return;

        string source = GetSafeSourceWithFallback();
        string revision = GetCherryPickRevision();

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(revision))
        {
            if (string.IsNullOrEmpty(source)) _mergeModule.LogErrorLocal("[Cherry-Pick Dry-Run] Select source.");
            if (string.IsNullOrEmpty(revision)) _mergeModule.LogErrorLocal("[Cherry-Pick Dry-Run] Enter revision.");
            return;
        }

        HandleDryRunResult(new SVNMerge.MergeFileResult());

        try
        {
            await SvnMergeOperations.CherryPickMergeAsync(_mergeModule, source, revision, isDryRun: true).ConfigureAwait(false);
        }
        finally
        {
            await RefreshBranchDropdownAsync().ConfigureAwait(false);
        }
    }

    private string GetCherryPickRevision() => _svnUI?.MergeCherryPickRevisionInput?.text?.Trim() ?? string.Empty;

    private string GetSafeSourceWithFallback()
    {
        string source = _svnUI?.MergeSourceInput?.text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(source) && _svnUI?.MergeBranchesDropdown != null)
        {
            int idx = _svnUI.MergeBranchesDropdown.value;
            if (idx >= 0 && idx < _svnUI.MergeBranchesDropdown.options.Count)
            {
                source = _svnUI.MergeBranchesDropdown.options[idx].text;
            }
        }

        return source;
    }

    #endregion

    private void HandleDryRunResult(SVNMerge.MergeFileResult result)
    {
        if (this == null) return;
        if (_svnUI?.MergeFilesContainer == null || _svnUI.MergeFileItemPrefab == null) return;

        for (int i = _svnUI.MergeFilesContainer.childCount - 1; i >= 0; i--)
        {
            Transform child = _svnUI.MergeFilesContainer.GetChild(i);
            if (child != null) Destroy(child.gameObject);
        }

        if (result == null) return;

        if (result.RealChanges > 0 || result.Conflicts > 0)
        {
            var headerObj = new GameObject("MergeSummaryHeader", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            headerObj.transform.SetParent(_svnUI.MergeFilesContainer, false);

            var rectTransform = headerObj.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0, 1);
            rectTransform.anchorMax = new Vector2(1, 1);
            rectTransform.pivot = new Vector2(0.5f, 1);
            rectTransform.sizeDelta = new Vector2(0, 25);

            var headerText = headerObj.GetComponent<TMPro.TextMeshProUGUI>();
            headerText.richText = true;
            headerText.fontSize = 13;
            headerText.textWrappingMode = TMPro.TextWrappingModes.NoWrap;

            string summary = $"<b>Files to change: {result.RealChanges}</b>  |  " +
                             $"<color=#55FF55>Added: {result.Added}</color>  |  " +
                             $"<color=#FFFF55>Updated: {result.Updated}</color>  |  " +
                             $"<color=#FF9900>Deleted: {result.Deleted}</color>";

            if (result.Conflicts > 0)
                summary += $"  |  <color=#FF0000><b>CONFLICTS: {result.Conflicts}</b></color>";

            headerText.text = summary;
        }

        if (result.Files != null)
        {
            foreach (SVNMerge.MergeFileInfo file in result.Files)
            {
                if (file == null) continue;

                MergeFileItem item = Instantiate(_svnUI.MergeFileItemPrefab, _svnUI.MergeFilesContainer);
                item.Setup(file);
            }
        }
    }

    private void ClearMergeUI()
    {
        if (_svnUI?.MergeConsoleText != null)
            SVNLogBridge.UpdateUIField(_svnUI.MergeConsoleText, "", "MERGE", append: false);

        if (_svnUI?.MergeFilesContainer != null)
        {
            for (int i = _svnUI.MergeFilesContainer.childCount - 1; i >= 0; i--)
            {
                Transform child = _svnUI.MergeFilesContainer.GetChild(i);
                if (child != null) Destroy(child.gameObject);
            }
        }
    }

    private string GetSafeSource() => _svnUI?.MergeSourceInput?.text?.Trim() ?? string.Empty;
}