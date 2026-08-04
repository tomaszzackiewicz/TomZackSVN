using SVN.Core;
using UnityEngine;

public class LocksPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    public void Button_Lock() => svnManager.GetModule<SVNLock>().LockModifiedButton();
    public void Button_Unlock() => svnManager.GetModule<SVNLock>().UnlockAllButton();
    public void Button_ShowLocks() => svnManager.GetModule<SVNLock>().ShowAllLocksButton();
    public void Button_CleanupLocks() => svnManager.GetModule<SVNLock>().CleanupLocksButton();
    public void Button_ClearLocksView()
    {
        if (svnUI.LogText != null)
        {
            SVNLogBridge.UpdateUIField(svnUI.LogText, string.Empty, "LOCKS_VIEW", append: false);
            SVNLogBridge.LogLine("<color=#777777>Locks view cleared.</color>");
        }
    }
}
