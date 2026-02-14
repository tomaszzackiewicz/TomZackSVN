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

        svnManager.GetModule<SVNLoad>().UpdateUIFromManager();
    }

    public void Button_BrowseDestFolder() => svnManager.GetModule<SVNExternal>().BrowseDestinationFolderPathLoad();
    public void Button_BrowsePrivateKey() => svnManager.GetModule<SVNExternal>().BrowsePrivateKeyPathLoad();
    public void Button_LoadRepo() => svnManager.GetModule<SVNLoad>().LoadRepoPathAndRefresh();
}
