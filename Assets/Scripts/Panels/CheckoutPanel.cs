using SVN.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst.Intrinsics;
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
    public void Button_Checkout() => svnManager.GetModule<SVNCheckout>().Checkout();
    public void Button_Cancel() => svnManager.GetModule<SVNCheckout>().CancelCheckout();
    public void Button_Resume() => svnManager.GetModule<SVNCheckout>().ResumeCheckout();
}
