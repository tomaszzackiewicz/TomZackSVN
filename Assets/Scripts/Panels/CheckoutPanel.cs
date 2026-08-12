using SVN.Core;
using UnityEngine;

public class CheckoutPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    public void Button_BrowseDestFolder() => svnManager.GetModule<SVNExternal>().BrowseDestinationFolderPathCheckout();
    public void Button_BrowsePrivateKey() => svnManager.GetModule<SVNExternal>().BrowsePrivateKeyPathCheckout();

    public void Button_UpdateProjectInfo() => svnManager.GetModule<SVNCheckout>().UpdateProjectInfo();
    public void Button_Export() => svnManager.GetModule<SVNCheckout>().ExportRepository();
    public void Button_Checkout() => svnManager.GetModule<SVNCheckout>().StartCheckout();
    public void Button_Pause() => svnManager.GetModule<SVNCheckout>().PauseCheckout();

    public void Button_Cancel()
    {
        var repair = svnManager.GetModule<SVNRepoRepair>();
        var checkout = svnManager.GetModule<SVNCheckout>();

        if (repair != null && repair.IsProcessing)
            repair.CancelRepair();
        else
            checkout?.CancelCheckout();
    }

    public void Button_Resume() => svnManager.GetModule<SVNCheckout>().ResumeCheckout();
    public void Button_RepairWorkingCopy() => svnManager.GetModule<SVNRepoRepair>().ForceRepairWorkingCopy();
}