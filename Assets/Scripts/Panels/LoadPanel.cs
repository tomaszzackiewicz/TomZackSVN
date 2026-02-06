using SVN.Core;
using UnityEngine;

public class LoadPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnManager.SVNLoad.UpdateUIFromManager();
    }

    public void Button_BrowseDestFolder() => svnManager.SVNExternal.BrowseDestinationFolderPathLoad();
    public void Button_BrowsePrivateKey() => svnManager.SVNExternal.BrowsePrivateKeyPathLoad();
    public void Button_LoadRepo() => svnManager.SVNLoad.LoadRepoPathAndRefresh();
}
