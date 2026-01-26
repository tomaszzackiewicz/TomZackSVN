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

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    public void Button_Compare() => svnManager.SVNMerge.CompareWithTrunk();

    public void Button_DryRunMerge()
    {
        string url = svnUI.MergeSourceInput.text;
        svnManager.SVNMerge.ExecuteMerge(url, true);
    }

    public void Button_ConfirmMerge()
    {
        string url = svnUI.MergeSourceInput.text;
        svnManager.SVNMerge.ExecuteMerge(url, false);
    }

    public async void Button_RefreshBranchDropdown()
    {
        string[] branches = await svnManager.SVNMerge.FetchAvailableBranches();

        svnUI.MergeBranchesDropdown.ClearOptions();
        svnUI.MergeBranchesDropdown.AddOptions(branches.ToList());

        svnUI.MergeBranchesDropdown.AddOptions(new List<string> { "trunk" });
    }

    public void Dropdown_OnBranchSelected(int index)
    {
        string selectedBranch = svnUI.MergeBranchesDropdown.options[index].text;
        string fullUrl;

        if (selectedBranch == "trunk")
            fullUrl = svnUI.SettingsWorkingDirInput.text + "/trunk";
        else
            fullUrl = svnUI.SettingsWorkingDirInput.text + "/branches/" + selectedBranch;

        svnUI.MergeSourceInput.text = fullUrl;
    }

    
}
