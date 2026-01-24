using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst.Intrinsics;
using UnityEngine;

public class LoadPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private SVNExternal svnExternal;
    private SVNLoad svnLoad;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnExternal = new SVNExternal(svnUI, svnManager);
        svnLoad = new SVNLoad(svnUI, svnManager);

        svnLoad.UpdateUIFromManager();
    }

    public void Button_Browse() => svnExternal.BrowseLocalPath();
    public void Button_LoadRepo() => svnLoad.LoadRepoPathAndRefresh();
}
