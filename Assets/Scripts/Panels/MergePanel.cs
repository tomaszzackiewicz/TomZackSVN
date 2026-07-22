using SVN.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class MergePanel : MonoBehaviour
{
    private SVNManager _svnManager;
    private SVNUI _svnUI;
    private SVNMerge _mergeModule;

    private void Awake() => ResolveReferences();

    private void OnEnable()
    {
        if (_svnManager == null || _mergeModule == null)
            ResolveReferences();

        SubscribeEvents();
        ClearMergeUI();
        RefreshBranchDropdownAsync().Forget();
    }

    private void OnDisable()
    {
        UnsubscribeEvents();
    }

    private void OnDestroy()
    {
        UnsubscribeEvents();
    }

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

    private void SubscribeEvents()
    {
        if (_mergeModule == null) return;

        _mergeModule.OnDryRunCompleted -= HandleDryRunResult;
        _mergeModule.OnDryRunCompleted += HandleDryRunResult;

        if (_svnUI?.MergeBranchesDropdown != null)
        {
            _svnUI.MergeBranchesDropdown.onValueChanged.RemoveAllListeners();
            _svnUI.MergeBranchesDropdown.onValueChanged.AddListener(OnBranchSelected);
        }
    }

    private void UnsubscribeEvents()
    {
        if (_mergeModule != null)
            _mergeModule.OnDryRunCompleted -= HandleDryRunResult;

        if (_svnUI?.MergeBranchesDropdown != null)
            _svnUI.MergeBranchesDropdown.onValueChanged.RemoveAllListeners();
    }

    public void Button_CancelMerge()
    {
        if (EnsureReady()) _mergeModule.CancelMerge();
    }

    public void Button_RefreshBranchDropdown() => SafeFireAndForget(RefreshBranchDropdownAsync);

    public void Button_Compare() => SafeFireAndForget(() => _mergeModule.CompareWithTrunk());

    public void Button_SyncWithTrunk() => SafeFireAndForget(AutoSyncAsync);

    public void Button_RepairMergeHistory() => SafeFireAndForget(() => _mergeModule.RepairMergeHistory());

    public void Button_ForceMergeFromTrunk() => SafeFireAndForget(ForceMergeFromTrunkAsync);

    public void Button_DryRunMerge() => SafeFireAndForget(DryRunMergeAsync);

    public void Button_ConfirmMerge() => SafeFireAndForget(ConfirmMergeAsync);

    public void Button_CancelLocalMerge() => SafeFireAndForget(() => _mergeModule.CancelLocalMerge());

    public void Button_RevertToHead() => SafeFireAndForget(() => _mergeModule.RevertToHead());

    public void Button_UndoMerge() => SafeFireAndForget(() => _mergeModule.UndoLastMerge());

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

    private async Task RefreshBranchDropdownAsync()
    {
        if (!EnsureReady()) return;
        if (_svnUI?.MergeBranchesDropdown == null) return;

        try
        {
            string[] branches = await _mergeModule.FetchAvailableBranches(true).ConfigureAwait(false);

            await Task.Yield();

            if (this == null) return;

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

            _svnUI.MergeBranchesDropdown.ClearOptions();
            _svnUI.MergeBranchesDropdown.AddOptions(options);
            _svnUI.MergeBranchesDropdown.RefreshShownValue();

            OnBranchSelected(0);
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

        string source = GetSafeSource();
        if (string.IsNullOrWhiteSpace(source)) return;

        string currentUrl = await SvnRunner.GetRepoUrlAsync(_svnManager.WorkingDir).ConfigureAwait(false);
        SVNLogBridge.LogLine($"[AutoSync] Current: {currentUrl}");
        SVNLogBridge.LogLine($"[AutoSync] Source : {source}");

        await _mergeModule.ExecuteMerge(source, false).ConfigureAwait(false);
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

    private void HandleDryRunResult(SVNMerge.MergeFileResult result)
    {
        if (this == null) return;
        if (_svnUI?.MergeFilesContainer == null || _svnUI.MergeFileItemPrefab == null) return;

        for (int i = _svnUI.MergeFilesContainer.childCount - 1; i >= 0; i--)
        {
            Transform child = _svnUI.MergeFilesContainer.GetChild(i);
            if (child != null) Destroy(child.gameObject);
        }

        if (result?.Files == null) return;

        foreach (SVNMerge.MergeFileInfo file in result.Files)
        {
            if (file == null) continue;

            MergeFileItem item = Instantiate(_svnUI.MergeFileItemPrefab, _svnUI.MergeFilesContainer);
            item.Setup(file);
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

internal static class TaskExtensions
{
    public static async void Forget(this Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { Debug.LogException(ex); }
    }
}