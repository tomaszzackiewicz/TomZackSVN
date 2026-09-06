using SVN.Core;
using UnityEngine;

public class BranchPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        // === FIX D7: null-safe (panel może włączyć się przed inicjalizacją managera).
        if (svnUI != null && svnUI.BranchTagConsoleText != null)
            SVNLogBridge.UpdateUIField(svnUI.BranchTagConsoleText, "", "BRANCH_TAG", append: false);

        var branchTag = svnManager?.GetModule<SVNBranchTag>();
        if (branchTag != null)
            _ = branchTag.RefreshIfEmpty();
    }

    // === FIX D7: wspólny null-safe akcesor — kończy się NRE na kliknięciu,
    // gdy moduł nie istnieje (błąd inicjalizacji) lub manager jeszcze nie gotowy.
    private SVNBranchTag GetModule()
    {
        if (svnManager == null) svnManager = SVNManager.Instance;
        var module = svnManager?.GetModule<SVNBranchTag>();
        if (module == null)
            SVNLogBridge.LogError("[BranchPanel] SVNBranchTag module is not available.");
        return module;
    }

    public void Button_CreateBranchFromTrunk() { var m = GetModule(); if (m != null) _ = m.CreateBranchFromTrunk(); }
    public void Button_CreateBranchFromSelected() { var m = GetModule(); if (m != null) _ = m.CreateBranchFromSelected(); }
    public void Button_ShowDetails() { var m = GetModule(); if (m != null) _ = m.ShowDetailsForSelected(); }
    public void Button_SwitchBranch() { var m = GetModule(); if (m != null) _ = m.SwitchToSelectedBranch(); }
    public void Button_SwitchTag() { var m = GetModule(); if (m != null) _ = m.SwitchToSelectedTag(); }
    public void Button_DiffWithCurrentBranch() { var m = GetModule(); if (m != null) _ = m.DiffWithCurrent(false); }
    public void Button_DiffWithCurrentTag() { var m = GetModule(); if (m != null) _ = m.DiffWithCurrent(true); }
    public void Button_DeleteBranch() { var m = GetModule(); if (m != null) _ = m.DeleteSelectedBranch(); }
    public void Button_DeleteTag() { var m = GetModule(); if (m != null) _ = m.DeleteSelectedTag(); }
}