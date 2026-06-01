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

    public void Button_OpenInEditor() => svnManager.GetModule<SVNResolve>().OpenInEditor();
    public void Button_MarkAsResolved() => svnManager.GetModule<SVNResolve>().MarkAsResolved();
    public void Button_ResolveTheirs() => svnManager.GetModule<SVNResolve>().ResolveTheirs();
    public void Button_ResolveMine() => svnManager.GetModule<SVNResolve>().ResolveMine();
    public void Button_ResolveFilePath() => svnManager.GetModule<SVNExternal>().BrowseResolveFilePath();
}
