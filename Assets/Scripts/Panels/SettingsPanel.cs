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

        svnManager.GetModule<SVNSettings>().UpdateUIFromManager();
    }

    public void Button_SaveWorkingDir() => svnManager.GetModule<SVNSettings>().SaveWorkingDir();
    public void Button_SaveRepoUrl() => svnManager.GetModule<SVNSettings>().SaveRepoUrl();
    public void Button_SaveSSHKeyPath() => svnManager.GetModule<SVNSettings>().SaveSSHKeyPath();
    public void Button_SaveMergeEditorPath() => svnManager.GetModule<SVNSettings>().SaveMergeEditorPath();
}
