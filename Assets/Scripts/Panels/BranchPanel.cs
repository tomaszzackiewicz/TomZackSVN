using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class BranchPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnManager.GetModule<SVNBranchTag>().RefreshUnifiedList();
    }


    public void Button_Create() => svnManager.GetModule<SVNBranchTag>().CreateRemoteCopy();
    public void Button_SwitchBranch() => svnManager.GetModule<SVNBranchTag>().SwitchToSelectedBranch();
    public void Button_SwitchTag() => svnManager.GetModule<SVNBranchTag>().SwitchToSelectedTag();
    public void Button_DeleteBranch() => svnManager.GetModule<SVNBranchTag>().DeleteSelectedBranch();
    public void Button_DeleteTag() => svnManager.GetModule<SVNBranchTag>().DeleteSelectedTag();

}
