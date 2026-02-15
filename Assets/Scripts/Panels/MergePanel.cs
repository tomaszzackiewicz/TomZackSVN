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

    // W klasie MergePanel.cs
    public async void Button_RefreshBranchDropdown()
    {
        // Wywołujemy nową, bezpieczniejszą metodę z modułu SVNMerge
        string[] branches = await svnManager.GetModule<SVNMerge>().FetchAvailableBranches();

        svnUI.MergeBranchesDropdown.ClearOptions();

        List<string> options = new List<string> { "trunk" };

        if (branches != null && branches.Length > 0)
        {
            // Dodajemy tylko nazwy folderów (usuwamy ewentualne slashe na końcu)
            options.AddRange(branches.Select(b => b.TrimEnd('/')).ToList());
        }

        svnUI.MergeBranchesDropdown.AddOptions(options);

        // Automatycznie ustawiamy tekst w polu Input na pierwszą opcję
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