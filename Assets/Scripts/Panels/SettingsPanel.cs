using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst.Intrinsics;
using UnityEngine;

public class SettingsPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnManager.SVNSettings.UpdateUIFromManager();
    }

    private void Start()
    {
       

        //svnManager.SVNSettings.UpdateUIFromManager();
    }

    public void Button_SaveWorkingDir() => svnManager.SVNSettings.SaveWorkingDir();
    public void Button_SaveRepoUrl() => svnManager.SVNSettings.SaveRepoUrl();
    public void Button_SaveSSHKeyPath() => svnManager.SVNSettings.SaveSSHKeyPath();
    public void Button_SaveMergeEditorPath() => svnManager.SVNSettings.SaveMergeEditorPath();
}
