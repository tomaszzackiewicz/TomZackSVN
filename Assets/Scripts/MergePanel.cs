using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class MergePanel : MonoBehaviour
{
    private SVNMerge merge;
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        merge = new SVNMerge(svnUI, svnManager);
    }

    public void Button_Compare() => merge.CompareWithTrunk();

    public void Button_DryRunMerge()
    {
        string url = svnUI.MergeSourceInput.text;
        merge.ExecuteMerge(url, true);
    }

    public void Button_ConfirmMerge()
    {
        string url = svnUI.MergeSourceInput.text;
        merge.ExecuteMerge(url, false);
    }

    public async void Button_RefreshBranchDropdown()
    {
        string[] branches = await merge.FetchAvailableBranches();

        svnUI.MergeBranchesDropdown.ClearOptions();
        svnUI.MergeBranchesDropdown.AddOptions(branches.ToList());

        // Dodaj opcjê "trunk" rêcznie, jeœli jej nie ma
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

        // Automatycznie wpisz URL do pola tekstowego
        svnUI.MergeSourceInput.text = fullUrl;
    }

    
}
