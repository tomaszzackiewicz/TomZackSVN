using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst.Intrinsics;
using UnityEngine;

public class ResolvePanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnManager.GetModule<SVNLoad>().UpdateUIFromManager();
    }

    public void Button_OpenInEditor() => svnManager.GetModule<SVNResolve>().Button_OpenInEditor();
    public void Button_MarkAsResolved() => svnManager.GetModule<SVNResolve>().Button_MarkAsResolved();
    public void Button_ResolveTheirs() => svnManager.GetModule<SVNResolve>().Button_ResolveTheirs();
    public void Button_ResolveMine() => svnManager.GetModule<SVNResolve>().Button_ResolveMine();
    public void Button_ResolveFilePath() => svnManager.GetModule<SVNExternal>().BrowseResolveFilePath();
}
