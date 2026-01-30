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

    public void Button_BrowseDestFolder() => svnManager.SVNExternal.BrowseLocalPath();
    public void Button_BrowsePrivateKey() => svnManager.SVNExternal.BrowsePrivateKeyPath();
    public void Button_Checkout() => svnManager.SVNCheckout.Checkout();
    public void Button_Cancel() => svnManager.SVNCheckout.CancelCheckout();
}
