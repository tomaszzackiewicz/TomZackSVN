using SVN.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst.Intrinsics;
using UnityEngine;
using UnityEngine.UIElements;

public class CommitPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    public async void Button_ShowModified() => svnManager.GetModule<SVNStatus>().ShowOnlyModified();
    public void Button_Revert() => svnManager.GetModule<SVNRevert>().RevertAll();
    public void Button_Commit() => svnManager.GetModule<SVNCommit>().CommitAll();
    public void Button_Add() => svnManager.GetModule<SVNAdd>().AddAll();
    public void Button_FixMissing() => svnManager.GetModule<SVNMissing>().FixMissingFiles();

    public void Button_CancelCommit() => svnManager.GetModule<SVNCommit>().CancelOperation();
}
