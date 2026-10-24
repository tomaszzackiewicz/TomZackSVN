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
        svnManager ??= SVNManager.Instance;
        if (svnManager == null)
            return;

        var statusModule = svnManager.GetModule<SVNStatus>();
        if (statusModule == null)
        {
            SVNLogBridge.LogErrorToOutput("[CommitPanel] SVNStatus module not available.");
            return;
        }

        try
        {
            await statusModule.ExecuteRefreshWithAutoExpand();
            statusModule.RenderCommitTreeIfNeeded();
            statusModule.UpdateSelectedSizeDisplay();
        }
        catch (System.Exception ex)
        {
            SVNLogBridge.LogErrorToOutput($"[CommitPanel] Refresh failed: {ex.Message}");
        }
    }

    public void Button_ShowModified() => svnManager.GetModule<SVNStatus>().ShowOnlyModified();
    public void Button_Commit() => svnManager.GetModule<SVNCommit>().CommitAll();
    public void Button_CommitSelected() => svnManager.GetModule<SVNCommit>().CommitSelected();
    public void Button_CancelCommit() => svnManager.GetModule<SVNCommit>().CancelOperation();
}