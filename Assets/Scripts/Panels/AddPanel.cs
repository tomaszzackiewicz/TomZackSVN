using SVN.Core;
using UnityEngine;

[DisallowMultipleComponent]
public class AddPanel : MonoBehaviour
{
    private SVNUI _svnUI;
    private SVNManager _svnManager;
    private SVNLoad _svnLoad;
    private SVNExternal _svnExternal;

    private void Start()
    {
        _svnUI = SVNUI.Instance;
        _svnManager = SVNManager.Instance;

        if (_svnManager == null)
        {
            Debug.LogError("[LoadPanel] SVNManager.Instance is null! Panel will not function.");
            enabled = false;
            return;
        }

        _svnLoad = _svnManager.GetModule<SVNLoad>();
        _svnExternal = _svnManager.GetModule<SVNExternal>();

        _svnLoad?.UpdateUIFromManager();
    }

    public void Button_BrowseDestFolder() => _svnExternal?.BrowseDestinationFolderPathLoad();
    public void Button_BrowsePrivateKey() => _svnExternal?.BrowsePrivateKeyPathLoad();
    public void Button_LoadRepo() => _svnLoad?.LoadRepoPathAndRefresh();
}