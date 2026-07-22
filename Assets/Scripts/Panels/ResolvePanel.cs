using SVN.Core;
using UnityEngine;

public class ResolvePanel : MonoBehaviour
{
    private SVNManager _svnManager;
    private SVNResolve _resolveModule;
    private SVNExternal _externalModule;

    private void Awake() => ResolveReferences();

    private void Start()
    {
        _svnManager?.GetModule<SVNLoad>()?.UpdateUIFromManager();
    }

    private void OnEnable()
    {
        if (_svnManager == null || _resolveModule == null)
            ResolveReferences();
    }

    private void ResolveReferences()
    {
        _svnManager = SVNManager.Instance;

        if (_svnManager == null)
        {
            Debug.LogError("[ResolvePanel] SVNManager.Instance is not available. Resolve operations will not work.", this);
            return;
        }

        _resolveModule = _svnManager.GetModule<SVNResolve>();
        _externalModule = _svnManager.GetModule<SVNExternal>();

        if (_resolveModule == null)
            Debug.LogError("[ResolvePanel] SVNResolve module is not registered in SVNManager.", this);

        if (_externalModule == null)
            Debug.LogWarning("[ResolvePanel] SVNExternal module is not registered. BrowseResolveFilePath will not work.", this);
    }

    private bool EnsureResolveReady()
    {
        if (_resolveModule != null) return true;
        ResolveReferences();
        return _resolveModule != null;
    }

    private bool EnsureExternalReady()
    {
        if (_externalModule != null) return true;
        ResolveReferences();
        return _externalModule != null;
    }

    public void Button_OpenInEditor()
    {
        if (EnsureResolveReady()) _resolveModule.OpenInEditor();
    }

    public void Button_MarkAsResolved()
    {
        if (EnsureResolveReady()) _resolveModule.MarkAsResolved();
    }

    public void Button_ResolveTheirs()
    {
        if (EnsureResolveReady()) _resolveModule.ResolveTheirs();
    }

    public void Button_ResolveMine()
    {
        if (EnsureResolveReady()) _resolveModule.ResolveMine();
    }

    public void Button_ResolveFilePath()
    {
        if (EnsureExternalReady()) _externalModule.BrowseResolveFilePath();
    }

    public void Button_DeleteAllObstructions()
    {
        if (EnsureResolveReady()) _resolveModule.DeleteAllObstructions();
    }

    public void Button_ResolveAllTheirs()
    {
        if (EnsureResolveReady()) _resolveModule.ResolveAllTheirs();
    }

    public void Button_ResolveAllMine()
    {
        if (EnsureResolveReady()) _resolveModule.ResolveAllMine();
    }
}