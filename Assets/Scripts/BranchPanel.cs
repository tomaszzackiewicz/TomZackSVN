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
    private SVNBranchTag svnBranchTag;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnBranchTag = new SVNBranchTag(svnUI, svnManager);
    }

    public void Button_Create() => svnBranchTag.CreateRemoteCopy();
    public void Button_SwitchBranch() => svnBranchTag.SwitchToSelectedBranch();
    public void Button_SwitchTag() => svnBranchTag.SwitchToSelectedTag();
    public void Button_DeleteBranch() => svnBranchTag.DeleteSelectedBranch();
    public void Button_DeleteTag() => svnBranchTag.DeleteSelectedTag();

}
