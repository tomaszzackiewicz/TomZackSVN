using SVN.Core;
using UnityEngine;

public class ResolvePanel : MonoBehaviour
{
    private SVNManager _svnManager;
    private SVNResolve _resolveModule;
    private SVNExternal _externalModule;

    private void Awake() => ResolveReferences();
    private void Start() => _svnManager?.GetModule<SVNResolve>()?.AutoRefreshConflictList();
    private void OnEnable() { if (_svnManager == null || _resolveModule == null) ResolveReferences(); }

    private void ResolveReferences()
    {
        _svnManager = SVNManager.Instance;
        if (_svnManager == null) return;
        _resolveModule = _svnManager.GetModule<SVNResolve>();
        _externalModule = _svnManager.GetModule<SVNExternal>();
    }

    private bool CanExecute()
    {
        if (_resolveModule == null) ResolveReferences();
        if (_resolveModule == null) return false;
        return !_svnManager.IsProcessing;
    }

    public void Button_OpenInEditor() { if (CanExecute()) _resolveModule.OpenInEditor(); }
    public void Button_MarkAsResolved() { if (CanExecute()) _resolveModule.MarkAsResolved(); }
    public void Button_ResolveTheirs() { if (CanExecute()) _resolveModule.ResolveTheirs(); }
    public void Button_ResolveMine() { if (CanExecute()) _resolveModule.ResolveMine(); }
    public void Button_DeleteAllObstructions() { if (CanExecute()) _resolveModule.DeleteAllObstructions(); }
    public void Button_ResolveAllTheirs() { if (CanExecute()) _resolveModule.ResolveAllTheirs(); }
    public void Button_ResolveAllMine() { if (CanExecute()) _resolveModule.ResolveAllMine(); }
    public void Button_ResolveFilePath() => _externalModule?.BrowseResolveFilePath();
}