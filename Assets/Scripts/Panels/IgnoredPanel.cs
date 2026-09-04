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

        // === FIX NRE: GetModule może zwrócić null (moduły niezainicjalizowane,
        // panel włączony przed Awake managera) — wcześniej goły call padał.
        svnManager?.GetModule<SVNIgnore>()?.RefreshIgnoredPanel();
    }

    public void Button_RefreshRules() => svnManager?.GetModule<SVNIgnore>()?.RefreshIgnoredPanel();
    public void Button_ReloadIgnoreRules() => svnManager?.GetModule<SVNIgnore>()?.ReloadIgnoreRules();
    public void Button_PushLocalRulesToSvn() => svnManager?.GetModule<SVNIgnore>()?.PushLocalRulesToSvn();
    public void Button_OpenIgnoredFilesInEditor() => svnManager?.GetModule<SVNIgnore>()?.OpenIgnoredFilesInEditor();
    public void Button_OpenIgnoreConfigInEditor() => svnManager?.GetModule<SVNIgnore>()?.OpenIgnoreConfigInEditor();
    public void Button_DeleteSvnGlobalIgnoreProperty() => svnManager?.GetModule<SVNIgnore>()?.DeleteSvnGlobalIgnoreProperty();
}