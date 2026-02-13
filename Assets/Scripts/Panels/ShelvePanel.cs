using SVN.Core;
using UnityEngine;

public class ShalvePanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    public void Button_Stash() => svnManager.SVNShelve.ExecuteShelve();
}
