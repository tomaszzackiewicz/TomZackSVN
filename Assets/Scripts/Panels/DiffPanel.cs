using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst.Intrinsics;
using UnityEngine;

public class DiffPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnManager.GetModule<SVNLoad>().UpdateUIFromManager();
    }

    public void Button_BrowseDiffFile() => svnManager.GetModule<SVNExternal>().BrowseDiffFilePath();
    public void Button_Diff() => svnManager.GetModule<SVNDiff>().ExecuteDiff();
}
