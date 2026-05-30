using SVN.Core;
using System;
using UnityEngine;

public class BranchPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        _ = svnManager.GetModule<SVNBranchTag>().RefreshUnifiedList();
    }

    public void Button_Create()
        => _ = svnManager.GetModule<SVNBranchTag>().CreateRemoteCopy();

    public void Button_SwitchBranch()
        => _ = svnManager.GetModule<SVNBranchTag>().SwitchToSelectedBranch();

    public void Button_SwitchTag()
        => _ = svnManager.GetModule<SVNBranchTag>().SwitchToSelectedTag();

    public void Button_DeleteBranch()
        => _ = svnManager.GetModule<SVNBranchTag>().DeleteSelectedBranch();

    public void Button_DeleteTag()
        => _ = svnManager.GetModule<SVNBranchTag>().DeleteSelectedTag();
}