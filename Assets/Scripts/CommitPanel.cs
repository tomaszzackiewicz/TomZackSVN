using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst.Intrinsics;
using UnityEngine;

public class CommitPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private SVNCommit svnCommit;
    private SVNStatus svnStatus;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnCommit = new SVNCommit(svnUI, svnManager);
        svnStatus = new SVNStatus(svnUI, svnManager);
    }

    public void Button_ShowModified() => svnStatus.ShowOnlyModified();
    public void Button_Commit() => svnCommit.CommitAll();
    //public void Button_Refresh() => svnCommit.RefreshCommitList();
    public void Button_Refresh() => svnStatus.RefreshView();
}
