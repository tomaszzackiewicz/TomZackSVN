using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst.Intrinsics;
using UnityEngine;

public class IgnoredPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnManager.SVNStatus.RefreshIgnoredPanel();
    }

    public void Button_RefreshRules() => svnManager.SVNStatus.RefreshIgnoredPanel();
    public void Button_ReloadIgnoreRules() => svnManager.SVNStatus.ReloadIgnoreRules();
    public void Button_PushLocalRulesToSvn() => svnManager.SVNStatus.PushLocalRulesToSvn();
}
