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

    private SVNSettings svnSettings;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnSettings = new SVNSettings(svnUI, svnManager);
        svnSettings.UpdateUIFromManager();
    }
}
