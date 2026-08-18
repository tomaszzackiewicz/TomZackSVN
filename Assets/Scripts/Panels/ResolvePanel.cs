using SVN.Core;
using UnityEngine;
using System;
using System.Threading;
using System.Threading.Tasks;

public class ResolvePanel : MonoBehaviour
{
    private SVNManager _svnManager;
    private SVNResolve _resolveModule;
    private SVNExternal _externalModule;

    private bool _isRefreshing;
    private CancellationTokenSource _refreshCts;

    private void Awake() => ResolveReferences();

    private async void OnEnable()
    {
        if (_svnManager == null || _resolveModule == null)
            ResolveReferences();

        await WaitForWorkingDirIfNeeded();

        if (this != null && !string.IsNullOrEmpty(_svnManager?.WorkingDir))
            TriggerSafeRefresh();
    }

    private void OnDisable()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }

    private async Task WaitForWorkingDirIfNeeded()
    {
        if (_svnManager == null) return;

        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;

        const float timeoutSeconds = 10f;
        float elapsed = 0f;

        while (this != null
               && gameObject.activeInHierarchy
               && string.IsNullOrEmpty(_svnManager.WorkingDir)
               && _svnManager.IsProcessing
               && elapsed < timeoutSeconds
               && !token.IsCancellationRequested)
        {
            await Task.Yield();
            elapsed += Time.unscaledDeltaTime;
        }
    }

    private async void TriggerSafeRefresh()
    {
        if (_isRefreshing || _resolveModule == null) return;

        _isRefreshing = true;
        try
        {
            await _resolveModule.AutoRefreshConflictListAsync();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ResolvePanel] Refresh failed: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

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

        return !_resolveModule.IsResolveBusy;
    }

    public void Button_OpenInEditor()
    {
        if (CanExecute()) _resolveModule.OpenInEditor();
    }

    public void Button_MarkAsResolved()
    {
        if (CanExecute()) _resolveModule.MarkAsResolved();
    }

    public void Button_ResolveTheirs()
    {
        if (CanExecute()) _resolveModule.ResolveTheirs();
    }

    public void Button_ResolveMine()
    {
        if (CanExecute()) _resolveModule.ResolveMine();
    }

    public void Button_DeleteAllObstructions()
    {
        if (CanExecute()) _resolveModule.DeleteAllObstructions();
    }

    public void Button_ResolveAllTheirs()
    {
        if (CanExecute()) _resolveModule.ResolveAllTheirs();
    }

    public void Button_ResolveAllMine()
    {
        if (CanExecute()) _resolveModule.ResolveAllMine();
    }

    public void Button_ResolveFilePath()
    {
        if (_externalModule == null) ResolveReferences();
        _externalModule?.BrowseResolveFilePath();
    }

    public void Button_ResolveAllTreeMine()
    {
        if (CanExecute()) _resolveModule.ResolveAllTreeMine();
    }

    public void Button_ResolveAllTreeTheirs()
    {
        if (CanExecute()) _resolveModule.ResolveAllTreeTheirs();
    }

    public void Button_ResolveAllTreeBase()
    {
        if (CanExecute()) _resolveModule.ResolveAllTreeBase();
    }

    public void Button_CancelResolve()
    {
        _resolveModule?.CancelResolve();
    }
}