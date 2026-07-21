using SVN.Core;
using UnityEngine;

public class BlamePanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
        svnManager.GetModule<SVNLoad>().UpdateUIFromManager();
    }

    public void Button_BrowseBlameFile() => svnManager.GetModule<SVNExternal>().BrowseBlameFilePath();
    public void Button_Blame() => svnManager.GetModule<SVNBlame>().ExecuteBlame();
}