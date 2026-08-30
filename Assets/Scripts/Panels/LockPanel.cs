using UnityEngine;
using SVN.Core;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;
using TMPro;

public class LockPanel : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject lockEntryPrefab;
    [SerializeField] private Transform locksContainer;
    [SerializeField] private TMP_Text stealLockConsole;

    // UWAGA: dostęp wyłącznie z main thread (wszystkie kontynuacje wracają na main —
    // brak ConfigureAwait(false) w tej klasie jest zamierzony i poprawny).
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

        if (svnManager != null && string.IsNullOrEmpty(svnManager.WorkingDir) && svnManager.IsProcessing)
        {
            LogToPanel("<color=yellow>[System]</color> Waiting for project initialization...", append: false);

            while (svnManager.IsProcessing && gameObject.activeInHierarchy)
            {
                await Task.Yield();
            }
        }

        // === FIX K2: panel mógł zostać zniszczony/wyłączony podczas czekania —
        // bez tego gardu kontynuacja Instantiate'owała na martwym obiekcie.
        if (this == null || !gameObject.activeInHierarchy) return;

        if (!string.IsNullOrEmpty(svnManager?.WorkingDir))
        {
            Button_RefreshLocks();
        }
        else
        {
            LogToPanel("<color=#FFAA00>[System]</color> No project loaded.", append: false);
        }
    }

    private void OnDisable()
    {
        ClearContainer();
    }

    public void Button_RefreshLocks() => SafeFireAndForget(RefreshAndShowAsync);

    private async Task RefreshAndShowAsync()
    {
        if (isProcessing || !Application.isPlaying) return;

        isProcessing = true;
        LogToPanel("<color=orange>[System]</color> Fetching locks...", append: false);
        ClearContainer();

        try
        {
            await svnManager.CancelBackgroundTasksAsync();

            // === FIX drobiazg: null-safe moduł (błąd inicjalizacji = NRE wcześniej).
            var lockModule = svnManager.GetModule<SVNLock>();
            if (lockModule == null)
            {
                LogToPanel("<color=#FFAA00>[Error]</color> SVNLock module unavailable.");
                return;
            }

            var allLocks = await lockModule.GetDetailedLocks(svnManager.WorkingDir);
            LogToPanel($"<color=white>[Info]</color> Found {allLocks.Count} total locks on server.");

            string currentUserName = (svnManager.CurrentUserName ?? "NULL").Trim().ToLower();

            var othersLocks = allLocks.Where(l =>
            {
                if (string.IsNullOrEmpty(l.Owner)) return false;
                return l.Owner.Trim().ToLower() != currentUserName;
            }).ToList();

            if (othersLocks.Count == 0)
            {
                LogToPanel("<color=green>[Info]</color> No locks from other users found.");
            }
            else
            {
                LogToPanel($"<color=yellow>[UI]</color> Spawning {othersLocks.Count} entries...");
                Populate(othersLocks);
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

    private void Populate(List<SVNLockDetails> locks)
    {
        if (!Application.isPlaying) return;
        if (lockEntryPrefab == null || locksContainer == null) return;

        foreach (var lockItem in locks)
        {
            GameObject entry = Instantiate(lockEntryPrefab, locksContainer);
            LockUIItem uiItem = entry.GetComponent<LockUIItem>();

            if (uiItem != null)
            {
                uiItem.Setup(
                    lockItem.Path,
                    lockItem.Owner,
                    lockItem.CreationDate,
                    lockItem.Comment,
                    false,
                    () => ExecuteSteal(lockItem),
                    () => ExecuteBreak(lockItem),
                    stealLockConsole
                );
            }
        }
    }

    // === FIX K1: refresh PO zwolnieniu flagi. Wcześniej Button_RefreshLocks()
    // wołane było Z WNĘTRZA try z podniesioną flagą → RefreshAndShowAsync
    // zwracał się natychmiast na 'if (isProcessing) return;' → po steal/break
    // lista NIGDY się nie odświeżała (zeszły lock wisiał do ręcznego Refresh).
    private async void ExecuteSteal(SVNLockDetails lockDetails)
    {
        if (isProcessing || lockDetails == null || !Application.isPlaying) return;

        isProcessing = true;
        try
        {
            await svnManager.CancelBackgroundTasksAsync();

            // === FIX drobiazg: escape cudzysłowów w username (komenda -m "...").
            string safeUser = (svnManager.CurrentUserName ?? "Unknown").Replace("\"", "'");
            string cmd = $"lock --force -m \"Administrative takeover by {safeUser}\" \"{lockDetails.FullPath}\"";
            await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);

            LogToPanel($"<color=green>[Success]</color> Stole lock: {lockDetails.Path}");

            SVNStatus.ClearLockCache();
            var statusModule = svnManager.GetModule<SVNStatus>();
            if (statusModule != null) await statusModule.RefreshAfterAction();
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=#FFAA00>[Error]</color> Steal failed: {ex.Message}");
        }
        finally
        {
            isProcessing = false;
        }

        // Po zwolnieniu flagi — refresh realnie się wykona.
        if (this != null && gameObject.activeInHierarchy)
            Button_RefreshLocks();
    }

    // === FIX K1: jw.
    private async void ExecuteBreak(SVNLockDetails lockDetails)
    {
        if (isProcessing || lockDetails == null || !Application.isPlaying) return;

        isProcessing = true;
        try
        {
            await svnManager.CancelBackgroundTasksAsync();

            string cmd = $"unlock --force \"{lockDetails.FullPath}\"";
            await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);

            LogToPanel($"<color=green>[Success]</color> Lock broken: {lockDetails.Path}");

            SVNStatus.ClearLockCache();
            var statusModule = svnManager.GetModule<SVNStatus>();
            if (statusModule != null) await statusModule.RefreshAfterAction();
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=#FFAA00>[Error]</color> Break failed: {ex.Message}");
        }
        finally
        {
            isProcessing = false;
        }

        if (this != null && gameObject.activeInHierarchy)
            Button_RefreshLocks();
    }

    private void ClearContainer()
    {
        if (locksContainer == null) return;
        foreach (Transform child in locksContainer) Destroy(child.gameObject);
    }

    private void LogToPanel(string msg, bool append = true)
    {
        if (stealLockConsole != null)
        {
            if (append)
                stealLockConsole.text += msg + "\n";
            else
                stealLockConsole.text = msg + "\n";
        }
    }

    private static void SafeFireAndForget(Func<Task> operation) => _ = FireAndForgetInternal(operation);
    private static async Task FireAndForgetInternal(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) { SVNLogBridge.LogError($"[LockPanel] Unhandled error: {ex.Message}"); }
    }
}