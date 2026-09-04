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
    private CancellationTokenSource _panelLifecycleCts;

    private void Awake() => ResolveReferences();

    private async void OnEnable()
    {
        if (_svnManager == null || _resolveModule == null)
            ResolveReferences();

        // === FIX K1: delayed dispose poprzedniego CTS — natychmiastowy
        // Cancel+Dispose przy szybkim disable→enable rzucał ODE (nie-OCE!)
        // w wiszącym Task.Delay(100, token) i leciał poza catch(OCE) jako
        // nieobsłużony wyjątek async void.
        var oldCts = _panelLifecycleCts;
        _panelLifecycleCts = new CancellationTokenSource();
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
            _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
        }

        try
        {
            await WaitForWorkingDirIfNeeded(_panelLifecycleCts.Token);

            if (this != null && !string.IsNullOrEmpty(_svnManager?.WorkingDir))
                TriggerSafeRefresh(_panelLifecycleCts.Token);
        }
        catch (OperationCanceledException) { }
    }

    private void OnDisable()
    {
        // === FIX K1: jw. — cancel + delayed dispose.
        var cts = _panelLifecycleCts;
        _panelLifecycleCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
        }
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

        SyncBackupLabels();
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

    public void Button_OpenInEditor() { if (CanExecute()) _resolveModule.OpenInEditor(); }
    public void Button_MarkAsResolved() { if (CanExecute()) _resolveModule.MarkAsResolved(); }
    public void Button_ResolveTheirs() { if (CanExecute()) _resolveModule.ResolveTheirs(); }
    public void Button_ResolveMine() { if (CanExecute()) _resolveModule.ResolveMine(); }
    public void Button_DeleteAllObstructions() { if (CanExecute()) _resolveModule.DeleteAllObstructions(); }
    public void Button_ResolveAllTheirs() { if (CanExecute()) _resolveModule.ResolveAllTheirs(); }
    public void Button_ResolveAllMine() { if (CanExecute()) _resolveModule.ResolveAllMine(); }

    public void Button_ResolveFilePath()
    {
        if (_externalModule == null) ResolveReferences();
        _externalModule?.BrowseResolveFilePath();
    }

    public void Button_ResolveAllTreeMine() { if (CanExecute()) _resolveModule.ResolveAllTreeMine(); }
    public void Button_ResolveAllTreeTheirs() { if (CanExecute()) _resolveModule.ResolveAllTreeTheirs(); }
    public void Button_ResolveAllTreeBase() { if (CanExecute()) _resolveModule.ResolveAllTreeBase(); }
    public void Button_CancelResolve() { _resolveModule?.CancelResolve(); }
    public void Button_ResolveAllTreeTheirsForce() { if (CanExecute()) _resolveModule.ResolveAllTreeTheirsForce(); }
    public void Button_ResolveAllTreeMineForce() { if (CanExecute()) _resolveModule.ResolveAllTreeMineForce(); }
    public void Button_ResolveAllTreeBaseForce() { if (CanExecute()) _resolveModule.ResolveAllTreeBaseForce(); }

    #region Backup buttons

    // Opcjonalne labelki (TMP) pokazujące aktualną politykę — podłącz
    // w inspectorze; bez podpięcia wszystko działa (wartości w logu).
    [SerializeField] private TMPro.TextMeshProUGUI BackupRetentionLabel;
    [SerializeField] private TMPro.TextMeshProUGUI BackupCapLabel;

    private float _purgeArmedUntil = -1f;

    public void Button_BackupInfo()
    {
        if (_resolveModule == null) ResolveReferences();
        _resolveModule?.ShowBackupInfo();
    }

    public void Button_CleanBackups()
    {
        if (_resolveModule == null) ResolveReferences();
        _resolveModule?.CleanBackups();
    }

    public void Button_OpenBackupFolder()
    {
        if (_resolveModule == null) ResolveReferences();
        _resolveModule?.OpenBackupFolder();
    }

    // === Purge: potwierdzenie dwuklikiem w 5s (brak dialogów w runtime UI).
    // Guard CanExecute: purge NIE może startować w trakcie resolve — backup
    // właśnie tworzony przez resolve nie może zniknąć spod niego.
    public void Button_PurgeAllBackups()
    {
        if (!CanExecute()) return;
        if (_resolveModule == null) { ResolveReferences(); if (_resolveModule == null) return; }

        if (Time.unscaledTime < _purgeArmedUntil)
        {
            _purgeArmedUntil = -1f;
            _resolveModule.PurgeAllBackups();
        }
        else
        {
            _purgeArmedUntil = Time.unscaledTime + 5f;
            SVNLogBridge.LogLine("<color=#FF4444><b>[Backup] PURGE ALL?</b> Click again within 5s to confirm — " +
                                 "deletes ALL backups permanently. Source files in the working copy are NOT touched.</color>");
        }
    }

    public void Button_CycleBackupRetention()
    {
        if (_resolveModule == null) ResolveReferences();
        if (_resolveModule == null) return;

        string desc = _resolveModule.CycleBackupRetention();
        if (BackupRetentionLabel != null) BackupRetentionLabel.text = desc;
    }

    public void Button_CycleBackupMaxSize()
    {
        if (_resolveModule == null) ResolveReferences();
        if (_resolveModule == null) return;

        string desc = _resolveModule.CycleBackupMaxSize();
        if (BackupCapLabel != null) BackupCapLabel.text = desc;
    }

    private void SyncBackupLabels()
    {
        if (_resolveModule == null) return;
        if (BackupRetentionLabel != null) BackupRetentionLabel.text = _resolveModule.DescribeBackupRetention();
        if (BackupCapLabel != null) BackupCapLabel.text = _resolveModule.DescribeBackupMaxSize();
    }

    #endregion
}