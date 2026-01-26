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

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    public async void Button_ShowModified() => svnManager.SVNStatus.ShowOnlyModified();
    public void Button_Commit() => svnManager.SVNCommit.CommitAll();
    public void Button_Add() => svnManager.SVNAdd.AddAll();
    public void Button_FixMissing() => svnManager.SVNMissing.FixMissingFiles();
}
