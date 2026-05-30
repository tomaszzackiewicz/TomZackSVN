using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class MergePanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private bool _isRefreshing;

    void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        if (svnUI?.MergeBranchesDropdown != null)
        {
            svnUI.MergeBranchesDropdown.onValueChanged.RemoveAllListeners();
            svnUI.MergeBranchesDropdown.onValueChanged.AddListener(Dropdown_OnBranchSelected);
        }

        Button_RefreshBranchDropdown();
    }

    // =====================================================
    // 🧠 CORE ACTIONS
    // =====================================================

    public void Button_RefreshBranchDropdown()
    {
        _ = RefreshBranchDropdown();
    }

    public void Button_Compare()
    {
        _ = svnManager.GetModule<SVNMerge>().CompareWithTrunk();
    }

    public void Button_SyncWithTrunk()
    {
        //_ = svnManager.GetModule<SVNMerge>().ExecuteMerge("trunk", false);
        _ = AutoSync();
    }

    public void Button_DryRunMerge()
    {
        string source = GetSafeSource();
        _ = svnManager.GetModule<SVNMerge>().ExecuteMerge(source, true);
    }

    public void Button_ConfirmMerge()
    {
        string source = GetSafeSource();
        _ = svnManager.GetModule<SVNMerge>().ExecuteMerge(source, false);
    }

    public void Button_CancelLocalMerge()
    {
        _ = CancelLocalMergeInternal();
    }

    public void Button_UndoMerge()
    {
        _ = svnManager.GetModule<SVNMerge>().UndoLastMerge();
    }

    private async Task RefreshBranchDropdown()
    {
        // if (_isRefreshing) return;
        // _isRefreshing = true;

        try
        {
            var merge = svnManager.GetModule<SVNMerge>();
            if (merge == null)
            {
                Debug.LogError("[MergePanel] SVNMerge module not found!");
                return;
            }

            string[] branches = await merge.FetchAvailableBranches(true);

            if (svnUI?.MergeBranchesDropdown == null) return;

            // 1. Zabezpieczenie: usuwamy trunk z listy pobranej, aby nie było duplikatów
            var options = new List<string> { "trunk" };
            if (branches != null)
            {
                var cleanBranches = branches
                    .Select(b => b.TrimEnd('/'))
                    .Where(b => b.ToLower() != "trunk" && !string.IsNullOrEmpty(b));

                options.AddRange(cleanBranches);
            }

            // 2. Wymuszenie aktualizacji UI
            svnUI.MergeBranchesDropdown.ClearOptions();
            svnUI.MergeBranchesDropdown.AddOptions(options);
            svnUI.MergeBranchesDropdown.RefreshShownValue(); // To często naprawia problem wizualny w Unity

            Debug.Log($"[MergePanel] Dropdown refreshed. Found {options.Count} options.");

            // 3. Bezpieczne ustawienie domyślnej wartości
            Dropdown_OnBranchSelected(0);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MergePanel] Refresh failed: {ex.Message}");
        }
        finally
        {
            //_isRefreshing = false;
        }
    }

    // =====================================================
    // 🎯 DROPDOWN HANDLER (SAFE)
    // =====================================================

    public void Dropdown_OnBranchSelected(int index)
    {
        if (svnUI?.MergeBranchesDropdown == null) return;
        if (svnUI.MergeBranchesDropdown.options.Count == 0) return;

        if (index < 0 || index >= svnUI.MergeBranchesDropdown.options.Count)
            return;

        string selectedName = svnUI.MergeBranchesDropdown.options[index].text;

        if (string.IsNullOrWhiteSpace(selectedName))
            return;

        if (svnUI.MergeSourceInput != null)
            svnUI.MergeSourceInput.text = selectedName;
    }

    // =====================================================
    // 🔒 SAFE INPUT RESOLUTION
    // =====================================================

    private string GetSafeSource()
    {
        if (svnUI?.MergeSourceInput == null)
            return string.Empty;

        string source = svnUI.MergeSourceInput.text;

        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        return source.Trim();
    }

    private async Task CancelLocalMergeInternal()
    {
        var merge = svnManager.GetModule<SVNMerge>();
        if (merge == null) return;

        await merge.CancelLocalMerge();
    }

    public async Task AutoSync()
    {
        var merge = svnManager.GetModule<SVNMerge>();
        if (merge == null)
            return;

        string source = GetSafeSource();

        if (string.IsNullOrWhiteSpace(source))
            return;

        string currentUrl =
            await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);

        Debug.Log($"[AutoSync] Current: {currentUrl}");
        Debug.Log($"[AutoSync] Source : {source}");

        await merge.ExecuteMerge(source, false);
    }

    private string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        return input
            .Replace("\\", "/")
            .TrimEnd('/')
            .ToLowerInvariant()
            .Replace("svn+ssh://", "")
            .Replace("https://", "")
            .Replace("http://", "");
    }

}