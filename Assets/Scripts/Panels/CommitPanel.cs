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

    private async void OnEnable()
    {
        _ = RefreshInBackground();
    }

    private async Task RefreshInBackground()
    {
        if (svnManager != null)
        {
            await svnManager.GetModule<SVNStatus>().ExecuteRefreshWithAutoExpand();
        }
    }

    public async void Button_ShowModified() => svnManager.GetModule<SVNStatus>().ShowOnlyModified();
    public void Button_Revert() => svnManager.GetModule<SVNRevert>().RevertAll();
    public void Button_Commit() => svnManager.GetModule<SVNCommit>().CommitAll();
    public void Button_CommitSelected() => svnManager.GetModule<SVNCommit>().CommitSelected();
    public void Button_Add() => _ = svnManager.GetModule<SVNAdd>().AddAll();
    public void Button_FixMissing() => svnManager.GetModule<SVNMissing>().FixMissingFiles();

    public void Button_CancelCommit() => svnManager.GetModule<SVNCommit>().CancelOperation();
}
