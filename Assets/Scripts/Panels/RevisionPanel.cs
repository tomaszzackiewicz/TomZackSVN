using SVN.Core;
using UnityEngine;

public class RevisionPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    public void Button_UpdateToRevision() => svnManager.GetModule<SVNRevision>().UpdateToRevisionButton();
    public void Button_ExportRevision() => svnManager.GetModule<SVNRevision>().ExportRevisionButton();
    public void Button_RevertRevision() => svnManager.GetModule<SVNRevision>().RevertCommitsButton();
}
