using SVN.Core;
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

    public void Button_DeleteAllObstructions() => svnManager.GetModule<SVNResolve>().DeleteAllObstructions();
    public void Button_ResolveAllTheirs() => svnManager.GetModule<SVNResolve>().ResolveAllTheirs();
    public void Button_ResolveAllMine() => svnManager.GetModule<SVNResolve>().ResolveAllMine();
}
