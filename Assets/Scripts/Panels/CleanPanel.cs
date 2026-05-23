using SVN.Core;
using UnityEngine;

public class CleanPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    public void Button_Clean() => svnManager.GetModule<SVNClean>().LightCleanup();
    public void Button_DiscardUntracked() => svnManager.GetModule<SVNClean>().DiscardUnversioned();
    public void Button_Vacuum() => svnManager.GetModule<SVNClean>().VacuumCleanup();
    public void Button_DeepRepair() => svnManager.GetModule<SVNClean>().DeepRepair();
    public void Button_HardReset() => svnManager.GetModule<SVNClean>().HardReset();
    public void Button_RepairStructure() => svnManager.GetModule<SVNClean>().RepairStructure();
}
