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

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    public void Button_Create() => svnManager.SVNBranchTag.CreateRemoteCopy();
    public void Button_SwitchBranch() => svnManager.SVNBranchTag.SwitchToSelectedBranch();
    public void Button_SwitchTag() => svnManager.SVNBranchTag.SwitchToSelectedTag();
    public void Button_DeleteBranch() => svnManager.SVNBranchTag.DeleteSelectedBranch();
    public void Button_DeleteTag() => svnManager.SVNBranchTag.DeleteSelectedTag();

}
