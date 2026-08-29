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

    private volatile bool _isRefreshing;
    private CancellationTokenSource _refreshCts;
    private CancellationTokenSource _panelLifecycleCts;

    private void Awake() => ResolveReferences();

    private async void OnEnable()
    {
        if (_svnManager == null || _resolveModule == null)
            ResolveReferences();

        _panelLifecycleCts?.Cancel();
        _panelLifecycleCts?.Dispose();
        _panelLifecycleCts = new CancellationTokenSource();

        try
        {
            await WaitForWorkingDirIfNeeded(_panelLifecycleCts.Token);

            if (this != null && !string.IsNullOrEmpty(_svnManager?.WorkingDir))
                TriggerSafeRefresh(_panelLifecycleCts.Token);
        }
        catch (OperationCanceledException)
        {

        }
    }

    private void OnDisable()
    {
        _panelLifecycleCts?.Cancel();
        _panelLifecycleCts?.Dispose();
        _panelLifecycleCts = null;

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }

    private async Task WaitForWorkingDirIfNeeded(CancellationToken token)
    {
        if (_svnManager == null) return;

        const int maxWaitMs = 10000;
        int waitedMs = 0;

        while (this != null
               && gameObject.activeInHierarchy
               && string.IsNullOrEmpty(_svnManager.WorkingDir)
               && _svnManager.IsProcessing
               && waitedMs < maxWaitMs)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(100, token);
            waitedMs += 100;
        }
    }

    private async void TriggerSafeRefresh(CancellationToken token)
    {
        if (_isRefreshing || _resolveModule == null) return;

        _isRefreshing = true;
        try
        {
            await _resolveModule.AutoRefreshConflictListAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (this != null)
                SVNLogBridge.LogLine($"<color=#FFAA00>[ResolvePanel] Refresh failed: {ex.Message}</color>");
        }
        finally
        {
            if (this != null) _isRefreshing = false;
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

        return !_resolveModule.IsResolveBusy && !_isRefreshing;
    }

    public void Button_RefreshConflicts()
    {
        if (CanExecute() && _panelLifecycleCts != null)
            TriggerSafeRefresh(_panelLifecycleCts.Token);
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

    public void Button_ResolveAllTreeTheirsForce()
    {
        if (CanExecute()) _resolveModule.ResolveAllTreeTheirsForce();
    }

    public void Button_ResolveAllTreeMineForce()
    {
        if (CanExecute()) _resolveModule.ResolveAllTreeMineForce();
    }

    public void Button_ResolveAllTreeBaseForce()
    {
        if (CanExecute()) _resolveModule.ResolveAllTreeBaseForce();
    }
}