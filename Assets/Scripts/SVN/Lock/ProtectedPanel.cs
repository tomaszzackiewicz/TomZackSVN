using UnityEngine;
using SVN.Core;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;
using TMPro;

/// <summary>
/// Panel PROTECTED — wzorzec 1:1 z LockPanel, oś property svn:needs-lock.
///
/// === FIX STALE-STATE: po KAŻDEJ akcji świat jest wymuszanie odświeżany
/// (ClearLockCache + RefreshLockCacheAsync(force)) PRZED refreshem panelu —
/// LockCache ma TTL i przy "ważnym" cache snapshot czytałby starego
/// właściciela locka → wiersz renderował stan sprzed akcji (objaw:
/// "stan zmienia się dopiero po restarcie aplikacji").
/// </summary>
public class ProtectedPanel : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject protectedEntryPrefab;
    [SerializeField] private Transform protectedContainer;
    [SerializeField] private TMP_Text protectedConsole;

    // Dostęp wyłącznie z main thread (jak LockPanel — brak
    // ConfigureAwait(false) w tej klasie jest zamierzony i poprawny):
    private bool isProcessing = false;
    private SVNUI svnUI;
    private SVNManager svnManager;

    private void Awake()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    private async void OnEnable()
    {
        if (!Application.isPlaying) return;

        // === 1:1 LockPanel: czekanie na inicjalizację projektu:
        if (svnManager != null && string.IsNullOrEmpty(svnManager.WorkingDir) && svnManager.IsProcessing)
        {
            LogToPanel("<color=yellow>[System]</color> Waiting for project initialization...", append: false);

            while (svnManager.IsProcessing && gameObject.activeInHierarchy)
            {
                await Task.Yield();
            }
        }

        // === FIX K2 (1:1): panel mógł zostać zniszczony/wyłączony podczas czekania:
        if (this == null || !gameObject.activeInHierarchy) return;

        // === SZYNA: subskrypcja — zmiany świata (steal/break w LockPanel,
        // ręczny Refresh) odświeżają tę tabelę:
        if (svnManager != null)
            svnManager.OnRepositoryStateChanged += OnRepositoryStateChanged;

        if (!string.IsNullOrEmpty(svnManager?.WorkingDir))
        {
            Button_RefreshProtected();
        }
        else
        {
            LogToPanel("<color=#FFAA00>[System]</color> No project loaded.", append: false);
        }
    }

    private void OnDisable()
    {
        ClearContainer();

        // === SZYNA: odpinamy subskrypcję:
        if (svnManager != null)
            svnManager.OnRepositoryStateChanged -= OnRepositoryStateChanged;
    }

    // === SZYNA: reakcja na zmiany świata SVN — odśwież tabelę.
    // Handler NIE emituje (ochrona przed ping-pongiem panel↔panel):
    private void OnRepositoryStateChanged()
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null || !gameObject.activeInHierarchy) return;
            Button_RefreshProtected();
        });
    }

    public void Button_RefreshProtected() => SafeFireAndForget(RefreshAndShowAsync);

    private async Task RefreshAndShowAsync()
    {
        if (isProcessing || !Application.isPlaying) return;

        isProcessing = true;
        LogToPanel("<color=#1E90FF>[System]</color> Fetching protection states...", append: false);
        ClearContainer();

        try
        {
            await svnManager.CancelBackgroundTasksAsync();

            // === FIX STALE-STATE: ręczny refresh też startuje od świeżych
            // locków (cache TTL mógł trzyać stary stan między sesjami panelu):
            await RefreshLocksForcedAsync();

            var protection = svnManager.GetModule<SVNFileProtection>();
            if (protection == null)
            {
                LogToPanel("<color=#FFAA00>[Error]</color> SVNFileProtection module unavailable.");
                return;
            }

            var snapshot = await protection.GetProtectionSnapshotAsync();
            LogToPanel($"<color=white>[Info]</color> Found {snapshot.Count} protection state(s).");

            if (snapshot.Count == 0)
            {
                LogToPanel("<color=green>[Info]</color> No protected files in this working copy.");
            }
            else
            {
                LogToPanel($"<color=yellow>[UI]</color> Spawning {snapshot.Count} entries...");
                Populate(snapshot);
                LogToPanel("<color=green>[Success]</color> List updated.");
            }
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=#FFAA00>[Error]</color> Sync failed: {ex.Message}");
        }
        finally
        {
            isProcessing = false;
        }
    }

    private void Populate(List<SVNFileProtection.ProtectionInfo> items)
    {
        if (!Application.isPlaying) return;
        if (protectedEntryPrefab == null || protectedContainer == null) return;

        foreach (var item in items)
        {
            GameObject entry = Instantiate(protectedEntryPrefab, protectedContainer);
            ProtectedRowController uiItem = entry.GetComponent<ProtectedRowController>();

            if (uiItem != null)
            {
                uiItem.Setup(
                    item,
                    (path, name) => ExecuteLock(path, name),
                    (path, name) => ExecuteUnlock(path, name),
                    (path, name) => ExecuteUnprotect(path, name),
                    (path, name) => ExecuteRevert(path, name),
                    protectedConsole
                );
            }
        }
    }

    // ===================================================================
    //  POMOCNICZE STANU ŚWIATA
    // ===================================================================

    /// <summary>
    /// === FIX STALE-STATE: wymuszone odświeżenie locków PRZED snapshotem.
    /// ClearLockCache (statyczny cache w SVNStatus) + RefreshLockCacheAsync
    /// (cache w managerze; force pomija IsValid/TTL). Bez tego snapshot
    /// czytałby starego właściciela locka → wiersz w stanie sprzed akcji.
    /// </summary>
    private async Task RefreshLocksForcedAsync()
    {
        try
        {
            SVNStatus.ClearLockCache();
            var lockModule = svnManager.GetModule<SVNLock>();
            if (lockModule != null)
                await lockModule.RefreshLockCacheAsync(force: true);
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=#FFAA00>[Warn]</color> Lock refresh failed (continuing): {ex.Message}");
        }
    }

    // ===================================================================
    //  AKCJE (wzorzec ExecuteSteal/ExecuteBreak 1:1)
    // ===================================================================

    private async void ExecuteLock(string absPath, string displayName)
    {
        if (isProcessing || string.IsNullOrEmpty(absPath) || !Application.isPlaying) return;

        isProcessing = true;
        try
        {
            await svnManager.CancelBackgroundTasksAsync();

            string safeComment = SanitizeComment(svnUI?.LockCommentInput?.text);
            string cmd = string.IsNullOrEmpty(safeComment)
                ? $"lock \"{absPath}\""
                : $"lock -m \"{safeComment}\" \"{absPath}\"";

            await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);

            LogToPanel($"<color=green>[Success]</color> Locked: {displayName}");

            // === FIX STALE-STATE: świat odświeżony PRZED snapshotem panelu:
            await RefreshLocksForcedAsync();

            var statusModule = svnManager.GetModule<SVNStatus>();
            if (statusModule != null) await statusModule.RefreshAfterAction();

            // === SZYNA (emit z AKCJI): tabela locków widzi nowy lock:
            svnManager.RaiseRepositoryStateChanged();
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=#FFAA00>[Error]</color> Lock failed: {ex.Message}");
        }
        finally
        {
            isProcessing = false;
        }

        // === FIX K1: refresh PO zwolnieniu flagi (inaczej isProcessing
        // zjadłby refresh i wiersz zostawał w starym stanie):
        if (this != null && gameObject.activeInHierarchy)
            Button_RefreshProtected();
    }

    private async void ExecuteUnlock(string absPath, string displayName)
    {
        if (isProcessing || string.IsNullOrEmpty(absPath) || !Application.isPlaying) return;

        isProcessing = true;
        try
        {
            await svnManager.CancelBackgroundTasksAsync();

            await SvnRunner.RunAsync($"unlock --force \"{absPath}\"", svnManager.WorkingDir);

            LogToPanel($"<color=green>[Success]</color> Unlocked: {displayName}");

            // === FIX STALE-STATE: jw.
            await RefreshLocksForcedAsync();

            var statusModule = svnManager.GetModule<SVNStatus>();
            if (statusModule != null) await statusModule.RefreshAfterAction();

            // === SZYNA (emit z AKCJI): tabela locków traci wpis.
            svnManager.RaiseRepositoryStateChanged();
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=#FFAA00>[Error]</color> Unlock failed: {ex.Message}");
        }
        finally
        {
            isProcessing = false;
        }

        if (this != null && gameObject.activeInHierarchy)
            Button_RefreshProtected();
    }

    private async void ExecuteUnprotect(string absPath, string displayName)
    {
        if (isProcessing || string.IsNullOrEmpty(absPath) || !Application.isPlaying) return;

        isProcessing = true;
        try
        {
            await svnManager.CancelBackgroundTasksAsync();

            var protection = svnManager.GetModule<SVNFileProtection>();
            if (protection == null) { LogToPanel("<color=#FFAA00>[Error]</color> SVNFileProtection module unavailable."); return; }

            // Delegacja — CAŁA polityka locków (BLOCKED na cudzym, PENDING
            // retry, commit --no-unlock) siedzi w module; moduł loguje
            // szczegóły do panelu logów locków (LogToLockPanel):
            await protection.UnprotectFiles(new List<string> { absPath });

            LogToPanel($"<color=green>[Success]</color> Unprotect finished: {displayName}");

            // === FIX STALE-STATE: property się zmieniła — świat odświeżony:
            await RefreshLocksForcedAsync();
            svnManager.DiskChangesDetected = true;

            var statusModule = svnManager.GetModule<SVNStatus>();
            if (statusModule != null) await statusModule.RefreshAfterAction();

            // === SZYNA (emit z AKCJI): property zmieniona — tabela locków
            // i drzewo powinny to zobaczyć:
            svnManager.RaiseRepositoryStateChanged();
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=#FFAA00>[Error]</color> Unprotect failed: {ex.Message}");
        }
        finally
        {
            isProcessing = false;
        }

        if (this != null && gameObject.activeInHierarchy)
            Button_RefreshProtected();
    }

    private async void ExecuteRevert(string absPath, string displayName)
    {
        if (isProcessing || string.IsNullOrEmpty(absPath) || !Application.isPlaying) return;

        isProcessing = true;
        try
        {
            await svnManager.CancelBackgroundTasksAsync();

            // Bezpiecznik treści — revert cofa też TREŚĆ pliku; dozwolony
            // tylko przy czystej kolumnie 1 (zmieniona property, czysta treść):
            var st = await SvnRunner.RunDetailedAsync($"status \"{absPath}\"", svnManager.WorkingDir, false, false);
            string output = (st.output ?? "").Trim();
            if (output.Length > 0 && output[0] != ' ')
            {
                LogToPanel($"<color=#FFAA00>[Refused]</color> {displayName} has uncommitted content changes — revert would discard them. Commit first.");
                return;
            }

            await SvnRunner.RunAsync($"revert \"{absPath}\"", svnManager.WorkingDir);

            LogToPanel($"<color=green>[Success]</color> Reverted: {displayName}");

            // === FIX STALE-STATE: property wróciła — świat odświeżony:
            await RefreshLocksForcedAsync();
            svnManager.DiskChangesDetected = true;

            var statusModule = svnManager.GetModule<SVNStatus>();
            if (statusModule != null) await statusModule.RefreshAfterAction();

            // === SZYNA (emit z AKCJI):
            svnManager.RaiseRepositoryStateChanged();
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=#FFAA00>[Error]</color> Revert failed: {ex.Message}");
        }
        finally
        {
            isProcessing = false;
        }

        if (this != null && gameObject.activeInHierarchy)
            Button_RefreshProtected();
    }

    // ===================================================================
    //  INFRASTRUKTURA (1:1 LockPanel)
    // ===================================================================

    private void ClearContainer()
    {
        if (protectedContainer == null) return;
        foreach (Transform child in protectedContainer) Destroy(child.gameObject);
    }

    private void LogToPanel(string msg, bool append = true)
    {
        if (protectedConsole != null)
        {
            if (append)
                protectedConsole.text += msg + "\n";
            else
                protectedConsole.text = msg + "\n";
        }
    }

    private static string SanitizeComment(string comment)
    {
        if (string.IsNullOrEmpty(comment)) return "";
        const string allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,:;!?'()-_";
        var sb = new System.Text.StringBuilder(comment.Length);
        foreach (char c in comment)
            if (allowed.IndexOf(c) >= 0) sb.Append(c);
        string s = sb.ToString().Trim();
        return s.Length > 200 ? s.Substring(0, 200) : s;
    }

    private static void SafeFireAndForget(Func<Task> operation) => _ = FireAndForgetInternal(operation);
    private static async Task FireAndForgetInternal(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) { SVNLogBridge.LogError($"[ProtectedPanel] Unhandled error: {ex.Message}"); }
    }
}