using System.Linq;
using System.Threading.Tasks;
using SVN.Core;
using UnityEngine;

public class CommitPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Awake()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    private void OnEnable()
    {
        _ = RefreshInBackground();
    }

    private async Task RefreshInBackground()
    {
        if (svnManager == null)
            return;

        var statusModule = svnManager.GetModule<SVNStatus>();

        await statusModule.ExecuteRefreshWithAutoExpand();

        statusModule.UpdateSelectedSizeDisplay();
    }

    public void Button_ShowModified() => svnManager.GetModule<SVNStatus>().ShowOnlyModified();
    public void Button_Commit() => svnManager.GetModule<SVNCommit>().CommitAll();
    public void Button_CommitSelected() => svnManager.GetModule<SVNCommit>().CommitSelected();
    public void Button_CancelCommit() => svnManager.GetModule<SVNCommit>().CancelOperation();
}
