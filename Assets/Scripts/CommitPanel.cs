using SVN.Core;
using System;
using System.Collections;
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
    private SVNAdd svnAdd;
    private SVNMissing svnMissing;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnCommit = new SVNCommit(svnUI, svnManager);
        svnStatus = new SVNStatus(svnUI, svnManager);
        svnAdd = new SVNAdd(svnUI, svnManager);
        svnMissing = new SVNMissing(svnUI, svnManager);
    }

    public async void Button_ShowModified() => svnStatus.ShowOnlyModified();
    public void Button_Commit() => svnCommit.CommitAll();
    public void Button_Add() => svnAdd.AddAll();
    //public void Button_Refresh() => svnCommit.RefreshCommitList();
    public void Button_FixMissing() => svnMissing.FixMissingFiles();
}
