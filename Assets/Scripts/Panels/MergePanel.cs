using SVN.Core;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class MergePanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        Button_RefreshBranchDropdown();
    }

    private void Start()
    {
        svnUI.MergeBranchesDropdown.onValueChanged.RemoveAllListeners();
        svnUI.MergeBranchesDropdown.onValueChanged.AddListener(Dropdown_OnBranchSelected);
    }

    public void Button_Compare() => svnManager.GetModule<SVNMerge>().CompareWithTrunk();

    public void Button_DryRunMerge()
    {
        string source = svnUI.MergeSourceInput.text;
        svnManager.GetModule<SVNMerge>().ExecuteMerge(source, true);
    }

    public void Button_ConfirmMerge()
    {
        string source = svnUI.MergeSourceInput.text;
        svnManager.GetModule<SVNMerge>().ExecuteMerge(source, false);
    }

    public async void Button_RefreshBranchDropdown()
    {
        string[] branches = await svnManager.GetModule<SVNMerge>().FetchAvailableBranches();

        svnUI.MergeBranchesDropdown.ClearOptions();

        List<string> options = new List<string> { "trunk" };
        if (branches != null)
        {
            options.AddRange(branches.ToList());
        }

        svnUI.MergeBranchesDropdown.AddOptions(options);

        Dropdown_OnBranchSelected(0);
    }

    public void Dropdown_OnBranchSelected(int index)
    {
        if (svnUI.MergeBranchesDropdown.options.Count == 0) return;

        string selectedName = svnUI.MergeBranchesDropdown.options[index].text;

        svnUI.MergeSourceInput.text = selectedName;
    }

    public async Task Button_RevertMerge()
    {
        await svnManager.GetModule<SVNMerge>().RevertMerge();
    }
}