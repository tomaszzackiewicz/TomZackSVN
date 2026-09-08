using UnityEngine;
using SVN.Core;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;
using TMPro;

/// <summary>
/// Panel LOCKÓW — tabela obcych locków (Steal/Break).
///
/// === TRZY ŚCIEŻKI ODŚWIEŻANIA (jedna prawda: lock cache w SVNManager):
///  1. RĘCZNY (przycisk Refresh / ogon Steal-Break): RefreshAllAsync —
///     JEDNO svn status -u → lock cache (preloaded, ×0 odpytań) → tabela
///     → RaiseRepositoryStateChanged (sygnał dla pozostałych paneli).
///  2. EVENT (poll z SVNPollingService co 30 s / akcje modułów):
///     OnRepositoryStateChanged → RebuildLocksTableFromCache — tabela
///     Z CACHE (zero svn!), rebuild TYLKO przy zmianie fingerprintu.
///  3. ZMIANA PROJEKTU: natychmiastowe czyszczenie + pełny cykl po 0.75 s
///     (kaskada przełączania WorkingDir musi się uspokoić).
///
///  Zero wołań panel→panel. Timer pollingu mieszka w SVNPollingService.
/// </summary>
public class LockPanel : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject lockEntryPrefab;
    [SerializeField] private Transform locksContainer;
    [SerializeField] private TMP_Text stealLockConsole;

    private bool isProcessing = false;
    private SVNUI svnUI;
    private SVNManager svnManager;

    private string _lastLocksFingerprint;   // gate rebuildów z cache
    private int _lastForeignLocksCount = -1;

    private void Awake()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
    }

    private async void OnEnable()
    {
        if (!Application.isPlaying) return;

        if (svnManager != null)
        {
            svnManager.OnProjectChanged += OnProjectChangedHandler;
            svnManager.OnRepositoryStateChanged += OnRepositoryStateChangedHandler;
        }

        if (svnManager != null && string.IsNullOrEmpty(svnManager.WorkingDir) && svnManager.IsProcessing)
        {
            LogToPanel("<color=yellow>[System]</color> Waiting for project initialization...", append: false);

            while (svnManager.IsProcessing && gameObject.activeInHierarchy)
            {
                await Task.Yield();
            }
        }

        if (this == null || !gameObject.activeInHierarchy) return;

        if (!string.IsNullOrEmpty(svnManager?.WorkingDir))
        {
            _ = RefreshAllAsync();
        }
        else
        {
            LogToPanel("<color=#FFAA00>[System]</color> No project loaded.", append: false);
        }
    }

    private void OnDisable()
    {
        if (svnManager != null)
        {
            svnManager.OnProjectChanged -= OnProjectChangedHandler;
            svnManager.OnRepositoryStateChanged -= OnRepositoryStateChangedHandler;
        }

        ClearContainer();
        _lastLocksFingerprint = null;   // zamknięty panel = stan nieznany
    }

    // ================= ZMIANA PROJEKTU =================

    private void OnProjectChangedHandler(SVNProject project)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null || !isActiveAndEnabled) return;

            // Stare wpisy z poprzedniego projektu nie mogą wisieć:
            ClearContainer();
            _lastLocksFingerprint = null;
            LogToPanel("<color=yellow>[System]</color> Project changed — reloading locks...", append: false);

            _ = RefreshAfterProjectSwitchAsync();
        });
    }

    private async Task RefreshAfterProjectSwitchAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(0.75)).ConfigureAwait(false);
        }
        catch { }

        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null || !gameObject.activeInHierarchy) return;
            _ = RefreshAllAsync();   // nowe WC: locks + cache + event
        });
    }

    // ================= EVENT: repository state =================
    // Źródła: SVNPollingService (poll co 30 s), akcje modułów
    // (lock/unlock/protect...), własny RefreshAllAsync (fingerprint już
    // ustawiony → handler robi skip — zero podwójnego rebuildu).

    private void OnRepositoryStateChangedHandler()
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null || !isActiveAndEnabled) return;
            RebuildLocksTableFromCache();
        });
    }

    private void RebuildLocksTableFromCache()
    {
        if (isProcessing || !Application.isPlaying) return;

        var locks = svnManager?.LockCache?.Locks;
        if (locks == null) return;

        string me = (svnManager.CurrentUserName ?? "NULL").Trim().ToLower();

        var othersLocks = new List<SVNLockDetails>();
        foreach (var kv in locks)
        {
            var l = kv.Value;
            if (l == null || string.IsNullOrEmpty(l.Owner)) continue;
            if (l.Owner.Trim().ToLower() == me) continue;
            othersLocks.Add(l);
        }

        string fingerprint = ComputeFingerprint(othersLocks);

        // Baseline bez szumu (pierwszy event przy pustym stanie):
        if (_lastLocksFingerprint == null && othersLocks.Count == 0)
        {
            _lastLocksFingerprint = fingerprint;
            _lastForeignLocksCount = 0;
            return;
        }

        if (fingerprint == _lastLocksFingerprint)
            return;   // nic się nie zmieniło — cisza

        ClearContainer();
        if (othersLocks.Count > 0)
            Populate(othersLocks);

        LogToPanel(_lastForeignLocksCount >= 0
            ? $"<color=#4FC3F7>[Sync]</color> Foreign locks {_lastForeignLocksCount} → {othersLocks.Count} — tables updated"
            : $"<color=#4FC3F7>[Sync]</color> Foreign locks: {othersLocks.Count} — tables updated");

        _lastLocksFingerprint = fingerprint;
        _lastForeignLocksCount = othersLocks.Count;
    }

    // ================= REFRESH — pełny cykl (ręczny) =================

    /// <summary>Przycisk „Refresh" — pełny cykl (locks + cache + event).</summary>
    public void Button_RefreshAll() => _ = RefreshAllAsync();

    /// <summary>ALIAS pod stary binding — identyczne zachowanie.</summary>
    public void Button_RefreshLocks() => _ = RefreshAllAsync();

    /// <summary>
    /// Pełny cykl: JEDNO svn status -u → lock cache (preloaded) → tabela →
    /// JEDEN event. Feedback: DWIE linie — potwierdzenie startu + podsumowanie
    /// obejmujące locki I protection (snapshot protection dociągnięty tu
    /// tylko do liczb; tabela protected odświeża się sama z eventu).
    /// </summary>
    private async Task RefreshAllAsync()
    {
        if (isProcessing || !Application.isPlaying) return;

        isProcessing = true;
        LogToPanel("<color=#8B0000>[Refresh]</color> Syncing with server...", append: false);
        ClearContainer();

        try
        {
            await svnManager.CancelBackgroundTasksAsync();

            var lockModule = svnManager.GetModule<SVNLock>();
            if (lockModule == null)
            {
                LogToPanel("<color=#FFAA00>[Refresh]</color> SVNLock module unavailable.");
                return;
            }

            // 1. JEDYNE 'svn status -u' w cyklu:
            var allLocks = await lockModule.GetDetailedLocks(svnManager.WorkingDir);

            // 2. Cache z TYCH SAMYCH danych (ReplaceAll + drzewo lock-flags):
            await lockModule.RefreshLockCacheAsync(true, preloadedLocks: allLocks);

            // 3. Tabela obcych locków:
            string me = (svnManager.CurrentUserName ?? "NULL").Trim().ToLower();

            var othersLocks = allLocks.Where(l =>
            {
                if (string.IsNullOrEmpty(l.Owner)) return false;
                return l.Owner.Trim().ToLower() != me;
            }).ToList();

            if (othersLocks.Count > 0)
                Populate(othersLocks);

            // 4. Protection — TYLKO do podsumowania (FillLockInfo czyta
            //    świeży cache z kroku 2, więc liczby są aktualne):
            string protectedPart = "protection: unavailable";
            var protection = svnManager.GetModule<SVNFileProtection>();

            if (protection != null)
            {
                try
                {
                    var snapshot = await protection.GetProtectionSnapshotAsync()
                        .ConfigureAwait(false);

                    if (snapshot != null)
                    {
                        int total = snapshot.Count;
                        int locked = snapshot.Count(i =>
                            i != null && !string.IsNullOrWhiteSpace(i.LockOwner));

                        protectedPart = total == 0
                            ? "protection: none"
                            : locked > 0
                                ? $"protection: {total} ({locked} locked, {total - locked} free)"
                                : $"protection: {total} (all free)";
                    }
                }
                catch { /* podsumowanie bez protection — refresh NIE pada */ }
            }

            // 5. PODSUMOWANIE — jedna linia, zero żargonu:
            string locksPart =
                allLocks.Count == 0
                    ? "no locks"
                    : othersLocks.Count == 0
                        ? $"no foreign locks ({allLocks.Count} total)"
                        : $"{othersLocks.Count} foreign lock{(othersLocks.Count == 1 ? "" : "s")} ({allLocks.Count} total)";

            LogToPanel($"<color=green>[Refresh]</color> {locksPart} — {protectedPart}");

            // 6. Fingerprint + licznik PRZED eventem (własny event → skip):
            _lastLocksFingerprint = ComputeFingerprint(othersLocks);
            _lastForeignLocksCount = othersLocks.Count;

            // 7. JEDYNY sygnał — ProtectedPanel odświeża się sam:
            svnManager.RaiseRepositoryStateChanged();
        }
        catch (Exception ex)
        {
            LogToPanel($"<color=#FFAA00>[Refresh]</color> Sync failed: {ex.Message}");
        }
        finally
        {
            isProcessing = false;
        }
    }

    private static string ComputeFingerprint(List<SVNLockDetails> locks)
    {
        if (locks == null || locks.Count == 0) return "";

        return string.Join("|", locks
            .OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .Select(l => $"{l.Path}|{l.Owner}"));
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

    // ================= STEAL / BREAK — akcja + 1 pełny cykl =================

    private async void ExecuteSteal(SVNLockDetails lockDetails)
    {
        if (isProcessing || lockDetails == null || !Application.isPlaying) return;

        isProcessing = true;
        try
        {
            await svnManager.CancelBackgroundTasksAsync();

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

        if (this != null && gameObject.activeInHierarchy)
            _ = RefreshAllAsync();   // locks + cache + event → protected
    }

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
            _ = RefreshAllAsync();   // locks + cache + event → protected
    }

    // ================= POMOCNICZE =================

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
}