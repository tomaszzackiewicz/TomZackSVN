using SVN.Core;
using UnityEngine;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

public class ResolvePanel : MonoBehaviour
{
    private SVNManager _svnManager;
    private SVNResolve _resolveModule;
    private SVNExternal _externalModule;

    private volatile bool _isRefreshing;
    private CancellationTokenSource _panelLifecycleCts;

    private void Awake()
    {
        ResolveReferences();
        WireBackupControls();
    }

    private async void OnEnable()
    {
        if (_svnManager == null || _resolveModule == null)
            ResolveReferences();

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

        SyncBackupControls();
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

    // Tree — single reliable action per strategy. Under the hood each of these
    // delegates to the force core (Phase 1: standard resolve, Phase 3: structural
    // fallback). There is no separate "Force" variant — the old non-force path
    // silently failed on W195024 tree conflicts, so both have been unified here.
    public void Button_ResolveAllTreeMine() { if (CanExecute()) _resolveModule.ResolveAllTreeMine(); }
    public void Button_ResolveAllTreeTheirs() { if (CanExecute()) _resolveModule.ResolveAllTreeTheirs(); }
    public void Button_ResolveAllTreeBase() { if (CanExecute()) _resolveModule.ResolveAllTreeBase(); }
    public void Button_CancelResolve() { _resolveModule?.CancelResolve(); }

    #region Backup controls

    [SerializeField] private UnityEngine.UI.Toggle BackupEnabledToggle;
    [SerializeField] private TMPro.TMP_InputField BackupRetentionInput;
    [SerializeField] private TMPro.TMP_InputField BackupCapInput;

    private const int BackupRetentionMaxDays = 3650;
    private const double BackupCapMaxGB = 1024;

    private float _purgeArmedUntil = -1f;

    private void WireBackupControls()
    {
        if (BackupEnabledToggle != null)
        {
            BackupEnabledToggle.onValueChanged.RemoveListener(Toggle_BackupEnabled);
            BackupEnabledToggle.onValueChanged.AddListener(Toggle_BackupEnabled);
        }
        if (BackupRetentionInput != null)
        {
            BackupRetentionInput.onEndEdit.RemoveListener(Input_BackupRetentionEndEdit);
            BackupRetentionInput.onEndEdit.AddListener(Input_BackupRetentionEndEdit);
        }
        if (BackupCapInput != null)
        {
            BackupCapInput.onEndEdit.RemoveListener(Input_BackupCapEndEdit);
            BackupCapInput.onEndEdit.AddListener(Input_BackupCapEndEdit);
        }
    }

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

    public void Toggle_BackupEnabled(bool enabled)
    {
        if (_resolveModule == null) { ResolveReferences(); if (_resolveModule == null) return; }
        _resolveModule.SetBackupEnabled(enabled);
    }

    public void Input_BackupRetentionEndEdit(string raw)
    {
        if (_resolveModule == null) { ResolveReferences(); if (_resolveModule == null) return; }

        string text = raw?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            SyncBackupControls();
            return;
        }

        if (text.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            _resolveModule.SetBackupRetentionDays(0);
            SyncBackupControls();
            return;
        }

        if (!TryParseFlexible(text, out double days))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Backup] Invalid retention — enter days (e.g. 14) or 0 = off.</color>");
            SyncBackupControls();
            return;
        }

        if (days < 0) days = 0;
        if (days > BackupRetentionMaxDays) days = BackupRetentionMaxDays;

        _resolveModule.SetBackupRetentionDays((int)Math.Round(days));
        SyncBackupControls();
    }

    public void Input_BackupCapEndEdit(string raw)
    {
        if (_resolveModule == null) { ResolveReferences(); if (_resolveModule == null) return; }

        string text = raw?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            SyncBackupControls();
            return;
        }

        if (text.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            _resolveModule.SetBackupMaxSizeMB(0);
            SyncBackupControls();
            return;
        }

        if (!TryParseFlexible(text, out double gb))
        {
            SVNLogBridge.LogLine("<color=#FFAA00>[Backup] Invalid cap — enter GB (e.g. 10) or 0 = no cap.</color>");
            SyncBackupControls();
            return;
        }

        if (gb < 0) gb = 0;
        if (gb > BackupCapMaxGB) gb = BackupCapMaxGB;

        _resolveModule.SetBackupMaxSizeMB((int)Math.Round(gb * 1024));
        SyncBackupControls();
    }

    private static bool TryParseFlexible(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private void SyncBackupControls()
    {
        if (_resolveModule == null) return;

        if (BackupEnabledToggle != null)
            BackupEnabledToggle.SetIsOnWithoutNotify(_resolveModule.IsBackupEnabled);

        if (BackupRetentionInput != null)
            BackupRetentionInput.SetTextWithoutNotify(
                _resolveModule.GetBackupRetentionDays().ToString(CultureInfo.InvariantCulture));

        if (BackupCapInput != null)
        {
            int mb = _resolveModule.GetBackupMaxSizeMB();
            BackupCapInput.SetTextWithoutNotify(
                mb <= 0 ? "0" : (mb / 1024.0).ToString("0.##", CultureInfo.InvariantCulture));
        }
    }

    #endregion
}