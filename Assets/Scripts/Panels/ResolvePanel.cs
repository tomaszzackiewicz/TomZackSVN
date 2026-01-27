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

        svnManager.SVNLoad.UpdateUIFromManager();
    }

    public void Button_OpenInEditor() => svnManager.SVNResolve.Button_OpenInEditor();
    public void Button_MarkAsResolved() => svnManager.SVNResolve.Button_MarkAsResolved();
    public void Button_ResolveTheirs() => svnManager.SVNResolve.Button_ResolveTheirs();
    public void Button_ResolveMine() => svnManager.SVNResolve.Button_ResolveMine();
}
