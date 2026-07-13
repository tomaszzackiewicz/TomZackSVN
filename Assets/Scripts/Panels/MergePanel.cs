using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class MergePanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        if (svnUI?.MergeBranchesDropdown != null)
        {
            svnUI.MergeBranchesDropdown.onValueChanged.RemoveAllListeners();
            svnUI.MergeBranchesDropdown.onValueChanged.AddListener(Dropdown_OnBranchSelected);
        }

        var merge = svnManager.GetModule<SVNMerge>();
        if (merge != null)
        {
            merge.OnDryRunCompleted -= HandleDryRunResult;
            merge.OnDryRunCompleted += HandleDryRunResult;
        }

        ClearMergeUI();
        Button_RefreshBranchDropdown();
    }

    private void OnDisable()
    {
        var merge = svnManager?.GetModule<SVNMerge>();
        if (merge != null)
            merge.OnDryRunCompleted -= HandleDryRunResult;
    }

    private void ClearMergeUI()
    {
        if (svnUI.MergeConsoleText != null)
            SVNLogBridge.UpdateUIField(svnUI.MergeConsoleText, "", "MERGE", append: false);

        if (svnUI.MergeFilesContainer != null)
        {
            foreach (Transform child in svnUI.MergeFilesContainer)
                Destroy(child.gameObject);
        }
    }

    public void Button_CancelMerge()
    {
        var merge = svnManager.GetModule<SVNMerge>();
        merge?.CancelMerge();
    }

    public void Button_RefreshBranchDropdown() => _ = RefreshBranchDropdown();

    public void Button_Compare() => _ = svnManager.GetModule<SVNMerge>().CompareWithTrunk();

    public async void Button_SyncWithTrunk()
    {
        try { await AutoSync(); }
        finally { Button_RefreshBranchDropdown(); }
    }

    public void Button_RepairMergeHistory() => _ = svnManager.GetModule<SVNMerge>().RepairMergeHistory();

    public async void Button_ForceMergeFromTrunk()
    {
        try { await svnManager.GetModule<SVNMerge>().ForceMergeFromTrunk(); }
        finally { Button_RefreshBranchDropdown(); }
    }

    public async void Button_DryRunMerge()
    {
        HandleDryRunResult(new SVNMerge.MergeFileResult());
        string source = GetSafeSource();
        if (string.IsNullOrEmpty(source)) return;

        try { await svnManager.GetModule<SVNMerge>().ExecuteMerge(source, true); }
        finally { Button_RefreshBranchDropdown(); }
    }

    public async void Button_ConfirmMerge()
    {
        HandleDryRunResult(new SVNMerge.MergeFileResult());
        string source = GetSafeSource();
        if (string.IsNullOrEmpty(source)) return;

        try { await svnManager.GetModule<SVNMerge>().ExecuteMerge(source, false); }
        finally { Button_RefreshBranchDropdown(); }
    }

    public void Button_CancelLocalMerge() => _ = CancelLocalMergeInternal();
    public void Button_RevertToHead() => _ = RevertToHead();
    public void Button_UndoMerge() => _ = svnManager.GetModule<SVNMerge>().UndoLastMerge();

    private async Task RefreshBranchDropdown()
    {
        try
        {
            var merge = svnManager.GetModule<SVNMerge>();
            if (merge == null) return;

            string[] branches = await merge.FetchAvailableBranches(true);
            if (svnUI?.MergeBranchesDropdown == null) return;

            var options = new List<string> { "trunk" };
            if (branches != null)
            {
                options.AddRange(branches
                    .Select(b => b.TrimEnd('/'))
                    .Where(b => b.ToLower() != "trunk" && !string.IsNullOrEmpty(b)));
            }

            svnUI.MergeBranchesDropdown.ClearOptions();
            svnUI.MergeBranchesDropdown.AddOptions(options);
            svnUI.MergeBranchesDropdown.RefreshShownValue();

            Dropdown_OnBranchSelected(0);
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"[MergePanel] Refresh failed: {ex.Message}");
        }
    }

    public void Dropdown_OnBranchSelected(int index)
    {
        if (svnUI?.MergeBranchesDropdown == null) return;
        if (svnUI.MergeBranchesDropdown.options.Count == 0) return;
        if (index < 0 || index >= svnUI.MergeBranchesDropdown.options.Count) return;

        string selectedName = svnUI.MergeBranchesDropdown.options[index].text;
        if (!string.IsNullOrWhiteSpace(selectedName) && svnUI.MergeSourceInput != null)
            svnUI.MergeSourceInput.text = selectedName;
    }

    private string GetSafeSource() => svnUI?.MergeSourceInput?.text?.Trim() ?? string.Empty;

    private async Task CancelLocalMergeInternal()
    {
        var merge = svnManager.GetModule<SVNMerge>();
        if (merge != null) await merge.CancelLocalMerge();
    }

    private async Task RevertToHead()
    {
        var merge = svnManager.GetModule<SVNMerge>();
        if (merge != null) await merge.RevertToHead();
    }

    private async Task AutoSync()
    {
        var merge = svnManager.GetModule<SVNMerge>();
        string source = GetSafeSource();
        if (merge == null || string.IsNullOrWhiteSpace(source)) return;

        string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
        SVNLogBridge.LogLine($"[AutoSync] Current: {currentUrl}");
        SVNLogBridge.LogLine($"[AutoSync] Source : {source}");

        await merge.ExecuteMerge(source, false);
    }

    private void HandleDryRunResult(SVNMerge.MergeFileResult result)
    {
        if (svnUI?.MergeFilesContainer == null || svnUI.MergeFileItemPrefab == null) return;

        foreach (Transform child in svnUI.MergeFilesContainer)
            Destroy(child.gameObject);

        foreach (SVNMerge.MergeFileInfo file in result.Files)
        {
            MergeFileItem item = Instantiate(svnUI.MergeFileItemPrefab, svnUI.MergeFilesContainer);
            item.Setup(file);
        }
    }
}