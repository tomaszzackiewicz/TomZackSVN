using SVN.Core;
using UnityEngine;

public class IgnoredPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        svnManager.GetModule<SVNIgnore>().RefreshIgnoredPanel();
    }

    public void Button_RefreshRules() => svnManager.GetModule<SVNIgnore>().RefreshIgnoredPanel();
    public void Button_ReloadIgnoreRules() => svnManager.GetModule<SVNIgnore>().ReloadIgnoreRules();
    public void Button_PushLocalRulesToSvn() => svnManager.GetModule<SVNIgnore>().PushLocalRulesToSvn();
}
