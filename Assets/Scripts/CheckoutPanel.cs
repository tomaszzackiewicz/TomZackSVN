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

    private SVNExternal svnExternal;
    private SVNCheckout svnCheckout;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnExternal = new SVNExternal(svnUI, svnManager);
        svnCheckout = new SVNCheckout(svnUI, svnManager);
    }

    public void Button_Browse() => svnExternal.BrowseLocalPath();
    public void Button_Checkout() => svnCheckout.Checkout();
    public void Button_Cancel() => svnCheckout.CancelCheckout();
}
