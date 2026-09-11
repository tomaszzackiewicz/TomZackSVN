using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SVN.Core;

/// <summary>
/// Panel PROTECTED — tabela plików chronionych BEZ migotania.
///
/// === ANTI-FLICKER:
///  1. Clear + Build w jednym frame.  2. Stale-while-revalidate (statyczny
///  _lastSnapshot → natychmiast przy otwarciu, atomowa podmiana).
///  3. Placeholder zamiast pustki.  4. CTS — wygrywa najnowszy.
///  5. OnDisable canceluje.  6. Cache per WorkingCopy.
///  7. Identyczny snapshot → brak rebuildu.
///
/// === ZMIANA PROJEKTU (fix — stare wiersze nie przeżywają przełączenia):
///  OnProjectChanged: statyczny cache unieważniany ZAWSZE (także gdy panel
///  nieaktywny); panel aktywny czyści wiersze natychmiast; refresh po 0.75 s.
///
/// === ŚWIEŻOŚĆ LOCK CACHE — trzy poziomy:
///  1. Eventy/notify: cache aktualizuje NADAWCA zdarzenia przed sygnałem.
///  2. Otwarcie / ręczny Refresh / zmiana projektu: wymuszony refresh.
///  3. Safety-net: pusty cache + brak właścicieli → re-sync po 2 s.
///
/// === WIERSZE: 4. akcja = revertAction (svn revert). State-aware Unprotect
///  (Publish/Finish/Unprotect). Lock/Unlock przez LockSingleFileAsync /
///  UnlockSingleFileAsync. ActionCompleted wiersza → refresh tabeli.
/// </summary>
public class ProtectedPanel : MonoBehaviour
{
    /// <summary>Dostęp także dla NIEAKTYWNEGO GameObjectu.</summary>
    public static ProtectedPanel Instance { get; private set; }

    [Header("Tabela — Content ScrollView + prefab wiersza")]
    [SerializeField] private Transform rowsContainer = null;
    [SerializeField] private GameObject rowPrefab = null;

    [Header("Stany UI (opcjonalne — ale to ONE dają feedback refresha)")]
    [SerializeField] private GameObject placeholderRoot = null;
    [SerializeField] private TMP_Text placeholderLabel = null;
    [SerializeField] private TMP_Text countLabel = null;
    [SerializeField] private TMP_Text updatedLabel = null;

    [Header("Przyciski (opcjonalne)")]
    [SerializeField] private Button refreshButton = null;

    private SVNUI svnUI;
    private SVNManager svnManager;

    // === Statyczny cache — przeżywa zamykanie/otwieranie panelu ===
    private static List<SVNFileProtection.ProtectionInfo> _lastSnapshot;
    private static string _cacheRoot;
    private static DateTime _lastUpdateTimeUtc = DateTime.MinValue;

    private CancellationTokenSource _refreshCts;
    private readonly List<GameObject> _rowObjects = new List<GameObject>();

    private bool HasValidCache =>
        _lastSnapshot != null &&
        svnManager != null &&
        string.Equals(_cacheRoot, svnManager.WorkingDir, StringComparison.OrdinalIgnoreCase);

#if UNITY_EDITOR
    // Enter Play Mode bez Domain Reload → reset statycznego cache:
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _lastSnapshot = null;
        _cacheRoot = null;
        _lastUpdateTimeUtc = DateTime.MinValue;
    }
#endif

    // ================= LIFECYCLE =================

    private void Awake()
    {
        Instance = this;
    }

    private void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        if (refreshButton != null)
            refreshButton.onClick.AddListener(Button_Refresh);

        if (svnManager != null)
        {
            svnManager.OnProjectChanged += OnProjectChangedHandler;
            svnManager.OnRepositoryStateChanged += OnRepositoryStateChangedHandler;
        }

        // 1. Natychmiast cache (stale-while-revalidate, bez czyszczenia):
        if (HasValidCache && _lastSnapshot.Count > 0)
        {
            RebuildRows(_lastSnapshot);
            UpdateMetaLabels("cached");
        }
        else
        {
            ClearRows();   // NIC z poprzedniego projektu nie zostaje
            ShowPlaceholder("Loading protected files…");
            UpdateMetaLabels();
        }

        // 2. Refresh z wymuszonym świeżym lock cache (kolory od 1. klatki):
        RefreshProtectedAsync(ensureFreshLockCache: true);
    }

    private void OnDisable()
    {
        if (svnManager != null)
        {
            svnManager.OnProjectChanged -= OnProjectChangedHandler;
            svnManager.OnRepositoryStateChanged -= OnRepositoryStateChangedHandler;
        }

        if (refreshButton != null)
            refreshButton.onClick.RemoveListener(Button_Refresh);

        CancelRefresh();
    }

    private void OnDestroy()
    {
        CancelRefresh();
        if (Instance == this)
            Instance = null;
    }

    // ================= ZMIANA PROJEKTU =================

    private void OnProjectChangedHandler(SVNProject project)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null) return;

            // 1. ZAWSZE unieważnij statyczny cache — także gdy panel NIEAKTYWNY:
            _lastSnapshot = null;
            _cacheRoot = null;
            _lastUpdateTimeUtc = DateTime.MinValue;

            if (!isActiveAndEnabled) return;

            // 2. Aktywny: natychmiast czyścimy stary widok:
            ClearRows();
            ShowPlaceholder("Loading protected files…");
            UpdateMetaLabels();

            // 3. Refresh ODROCZONY — kaskada zmiany projektu ma 0.75 s:
            _ = RefreshAfterProjectSwitchAsync(TimeSpan.FromSeconds(0.75));
        });
    }

    private async Task RefreshAfterProjectSwitchAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }
        catch { }

        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null || !isActiveAndEnabled) return;
            RefreshProtectedAsync(ensureFreshLockCache: true);
        });
    }

    private void OnRepositoryStateChangedHandler()
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null || !isActiveAndEnabled) return;
            RefreshProtectedAsync();
        });
    }

    // ================= PUBLIC API =================

    public void Button_Refresh()
        => RefreshProtectedAsync(forceRebuild: true, ensureFreshLockCache: true);

    public void NotifyProtectionChanged()
    {
        UnityMainThreadDispatcher.Enqueue(() => RefreshProtectedAsync());
    }

    // ================= REFRESH =================

    /// <summary>
    /// === FIX (async void): całe ciało po utworzeniu CTS w try/catch.
    /// Poprzednio `token.ThrowIfCancellationRequested()` po `RefreshLockCacheAsync`
    /// było POZA try — przy cancel (drugi refresh w trakcie) leciał
    /// OperationCanceledException z async void jako UNHANDLED (czerwony błąd
    /// w konsoli + potencjalne przerwanie łańcucha OnRowActionCompleted).
    ///
    /// === FIX (CTS): CancelAndDisposeLater zamiast immediate Dispose —
    /// spójność z resztą kodu.
    /// </summary>
    private async void RefreshProtectedAsync(bool forceRebuild = false, bool ensureFreshLockCache = false)
    {
        if (svnManager == null)
        {
            ShowPlaceholder("SVNManager not ready — reopen the panel.", isError: true);
            return;
        }

        var protection = svnManager.GetModule<SVNFileProtection>();
        if (protection == null)
        {
            ShowPlaceholder("SVNFileProtection module unavailable.", isError: true);
            return;
        }

        // === FIX (CTS): CancelAndDisposeLater zamiast Cancel+Dispose ===
        SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _refreshCts, null));

        var newCts = new CancellationTokenSource();
        _refreshCts = newCts;
        CancellationToken token = newCts.Token;

        if (_rowObjects.Count == 0)
            ShowPlaceholder("Loading protected files…");

        try
        {
            // Poziom 2: wymuszony świeży lock cache (otwarcie/ręczny/projekt).
            if (ensureFreshLockCache)
            {
                var lockModule = svnManager.GetModule<SVNLock>();
                if (lockModule != null)
                {
                    try
                    {
                        await lockModule.RefreshLockCacheAsync(true).ConfigureAwait(false);
                    }
                    catch { /* nie przerywamy — snapshot może być starszy */ }

                    token.ThrowIfCancellationRequested();   // ← teraz w try
                }
            }

            string root = svnManager.WorkingDir ?? "";

            List<SVNFileProtection.ProtectionInfo> snapshot =
                await FetchSnapshotWithTimeoutAsync(protection, TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (snapshot == null)
                throw new InvalidOperationException("Protection snapshot is NULL.");

            snapshot = snapshot
                .Where(i => i != null)
                .OrderBy(i => i.Path ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Poziom 3: SAFETY-NET — wyścig na guard-flag:
            bool lockCacheEmpty = svnManager.LockCache != null &&
                                  svnManager.LockCache.Locks.Count == 0;

            bool suspicious = lockCacheEmpty && snapshot.Any(i =>
                i.State == SVNFileProtection.ProtectionState.Protected &&
                string.IsNullOrWhiteSpace(i.LockOwner));

            if (suspicious)
            {
                Debug.Log("[ProtectedPanel] Lock cache empty at snapshot time — auto re-sync in 2 s.");
                _ = ResyncAfterDelayAsync(TimeSpan.FromSeconds(2));
            }

            List<SVNFileProtection.ProtectionInfo> previous = _lastSnapshot;
            bool contentChanged = !SnapshotsEqual(previous, snapshot);

            _lastSnapshot = snapshot;
            _lastUpdateTimeUtc = DateTime.UtcNow;
            _cacheRoot = root;

            // Atomowa podmiana — JEDEN Enqueue, clear + build w jednym frame:
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (token.IsCancellationRequested || this == null) return;

                Debug.Log($"[ProtectedPanel] Refreshed: {snapshot.Count} item(s), " +
                          $"changed={contentChanged}, forced={forceRebuild}");

                HidePlaceholder();

                if (snapshot.Count == 0)
                {
                    ClearRows();
                    ShowPlaceholder("No protected files.");
                    UpdateMetaLabels(forceRebuild ? "refreshed" : null);
                    return;
                }

                if (contentChanged || forceRebuild)
                    RebuildRows(snapshot);

                UpdateMetaLabels(forceRebuild ? "refreshed" : null);
            });
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (token.IsCancellationRequested || this == null) return;

                if (_rowObjects.Count == 0)
                    ShowPlaceholder("Refresh timed out — click Refresh to retry.", isError: true);
                else
                    UpdateMetaLabels("refresh timed out — showing cache");
            });
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ProtectedPanel] Refresh failed: {ex.Message}");

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (token.IsCancellationRequested || this == null) return;

                if (_rowObjects.Count == 0)
                    ShowPlaceholder("Refresh failed — click Refresh to retry.", isError: true);
                else
                    UpdateMetaLabels("refresh failed — showing cache");
            });
        }
    }

    private void CancelRefresh()
    {
        SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _refreshCts, null));
    }

    private static async Task<List<SVNFileProtection.ProtectionInfo>> FetchSnapshotWithTimeoutAsync(
        SVNFileProtection protection,
        TimeSpan timeout)
    {
        Task<List<SVNFileProtection.ProtectionInfo>> task = protection.GetProtectionSnapshotAsync();

        Task completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);

        if (completed != task)
            throw new TimeoutException();

        return await task.ConfigureAwait(false);
    }

    private async Task ResyncAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }
        catch { }

        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null || !isActiveAndEnabled) return;
            RefreshProtectedAsync(ensureFreshLockCache: true);
        });
    }

    // ================= BUILD (atomowy) =================

    private void RebuildRows(List<SVNFileProtection.ProtectionInfo> snapshot)
    {
        ClearRows();

        if (rowsContainer == null || rowPrefab == null)
        {
            Debug.LogError(
                "[ProtectedPanel] rowsContainer / rowPrefab NOT ASSIGNED — rows not built. " +
                "Assign them in the Inspector.");
            ShowPlaceholder("Row prefab/container not assigned — check the Inspector.", isError: true);
            return;
        }

        if (snapshot == null)
        {
            Debug.LogWarning("[ProtectedPanel] RebuildRows called with null snapshot.");
            return;
        }

        Debug.Log($"[ProtectedPanel] RebuildRows: {snapshot.Count} item(s), container={rowsContainer.name}, prefab={rowPrefab.name}");

        int built = 0;

        foreach (var info in snapshot)
        {
            if (info == null) continue;

            GameObject rowObject;
            try
            {
                rowObject = Instantiate(rowPrefab, rowsContainer);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProtectedPanel] Instantiate failed: {ex}");
                continue;
            }

            if (rowObject == null)
            {
                Debug.LogError("[ProtectedPanel] Instantiate returned null.");
                continue;
            }

            // KRYTYCZNE: prefab mógł być zapisany jako inactive w projekcie.
            // Bez tego klon też jest inactive → Awake/OnEnable/Setup nie działają
            // → wiersz niewidoczny, bez błędu.
            if (!rowObject.activeSelf)
                rowObject.SetActive(true);

            _rowObjects.Add(rowObject);

            try
            {
                SetupRow(rowObject, info);
                built++;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProtectedPanel] SetupRow failed for '{info.Path}': {ex}");
            }
        }

        Debug.Log($"[ProtectedPanel] Built {built}/{snapshot.Count} row(s).");
    }

    private void SetupRow(GameObject rowObject, SVNFileProtection.ProtectionInfo info)
    {
        if (rowObject == null || info == null) return;

        var row = rowObject.GetComponent<ProtectedRowController>();
        if (row == null)
            row = rowObject.GetComponentInChildren<ProtectedRowController>(true);

        if (row != null)
        {
            row.Setup(
                info,
                OnRowUnlock,
                OnRowLock,
                OnRowUnprotect,
                OnRowRevert,
                GetRowLogArea());

            row.ActionCompleted -= OnRowActionCompleted;
            row.ActionCompleted += OnRowActionCompleted;

            return;
        }

        // Fallback: brak ProtectedRowController na prefabie.
        var text = rowObject.GetComponentInChildren<TMP_Text>(true);
        if (text != null)
        {
            text.text = BuildFallbackRowText(info);
        }
        else
        {
            Debug.LogWarning(
                $"[ProtectedPanel] Prefab has no ProtectedRowController AND no TMP_Text — " +
                $"row for '{info.Path}' will be empty. Add ProtectedRowController to the prefab.");
        }
    }

    private void OnRowActionCompleted()
    {
        UnityMainThreadDispatcher.Enqueue(() => RefreshProtectedAsync());
    }

    private TMP_Text GetRowLogArea()
    {
        if (svnUI != null && svnUI.LockDisplayArea != null)
            return svnUI.LockDisplayArea;
        return updatedLabel;
    }

    private static string BuildFallbackRowText(SVNFileProtection.ProtectionInfo info)
    {
        string owner = string.IsNullOrWhiteSpace(info.LockOwner) ? "—" : info.LockOwner;
        return $"{info.Path}   [{info.State}]   lock: {owner}";
    }

    private void ClearRows()
    {
        for (int i = 0; i < _rowObjects.Count; i++)
        {
            var go = _rowObjects[i];
            if (go == null) continue;

            // Natychmiast ukryj — Destroy jest odroczony do końca klatki,
            // a my chcemy mieć pewność, że stary wiersz nie mignie w nowym układzie.
            if (go.activeSelf)
                go.SetActive(false);

            Destroy(go);
        }
        _rowObjects.Clear();
    }

    // ================= HANDLERY WIERSZY =================
    // Sygnatura Action<string,string>: arg1 = ścieżka, arg2 = LockOwner.

    private async void OnRowLock(string path, string lockOwner)
    {
        var lockModule = svnManager?.GetModule<SVNLock>();
        if (lockModule == null) return;

        bool ok = await lockModule
            .LockSingleFileAsync(MakeRowPathAbsolute(path))
            .ConfigureAwait(false);

        if (!ok)
            LogRow($"<color=#FFAA00>[ProtectedPanel]</color> Lock not executed (busy/error): {path}");

        UnityMainThreadDispatcher.Enqueue(() => RefreshProtectedAsync());
    }

    private async void OnRowUnlock(string path, string lockOwner)
    {
        var lockModule = svnManager?.GetModule<SVNLock>();
        if (lockModule == null) return;

        bool ok = await lockModule
            .UnlockSingleFileAsync(MakeRowPathAbsolute(path))
            .ConfigureAwait(false);

        if (!ok)
            LogRow($"<color=#FFAA00>[ProtectedPanel]</color> Unlock not executed (busy/error): {path}");

        UnityMainThreadDispatcher.Enqueue(() => RefreshProtectedAsync());
    }

    private async void OnRowUnprotect(string path, string lockOwner)
    {
        var protection = svnManager?.GetModule<SVNFileProtection>();
        if (protection == null) return;

        string abs = MakeRowPathAbsolute(path);
        var info = FindInfoByPath(path);

        if (info != null && info.State == SVNFileProtection.ProtectionState.PendingAdd)
        {
            LogRow($"<color=#00E5FF>[ProtectedPanel]</color> Publish (retry): {path}");
            await RunProtectionTaskAsync(
                () => protection.ProtectFiles(new List<string> { abs }), path);
            return;
        }

        await RunProtectionTaskAsync(
            () => protection.UnprotectFiles(new List<string> { abs }), path);
    }

    private void OnRowRevert(string path, string lockOwner)
    {
        _ = RunRowCommandAsync(
            $"revert \"{MakeRowPathAbsolute(path)}\"",
            $"Reverted: {path}");
    }

    private async Task RunProtectionTaskAsync(Func<Task> task, string path)
    {
        try
        {
            await task().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRow($"<color=#FFAA00>[ProtectedPanel]</color> {ex.Message}");
        }

        UnityMainThreadDispatcher.Enqueue(() => RefreshProtectedAsync());
    }

    private async Task RunRowCommandAsync(string svnCommand, string successMessage)
    {
        if (svnManager == null) return;

        string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await SvnRunner.RunAsync(svnCommand, root, token: cts.Token)
                .ConfigureAwait(false);

            LogRow($"<color=green>[ProtectedPanel]</color> {successMessage}");

            UnityMainThreadDispatcher.Enqueue(() => RefreshProtectedAsync());
        }
        catch (Exception ex)
        {
            LogRow($"<color=#FFAA00>[ProtectedPanel]</color> {ex.Message}");
        }
    }

    private SVNFileProtection.ProtectionInfo FindInfoByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _lastSnapshot == null)
            return null;

        string target = path.Replace('\\', '/').Trim().TrimEnd('/');

        foreach (var i in _lastSnapshot)
        {
            if (i == null || string.IsNullOrWhiteSpace(i.Path)) continue;

            string p = i.Path.Replace('\\', '/').Trim().TrimEnd('/');

            if (string.Equals(p, target, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return null;
    }

    private string MakeRowPathAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        string p = path.Replace('\\', '/').Trim();
        string root = (svnManager?.WorkingDir ?? "").Replace('\\', '/').TrimEnd('/');

        if (p.IndexOf(':') >= 0)
            return p;

        return root + "/" + p.TrimStart('/');
    }

    private void LogRow(string message)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (svnUI?.LockDisplayArea != null)
                SVNLogBridge.UpdateUIField(svnUI.LockDisplayArea, message, append: true);
            else
                SVNLogBridge.LogLine(message, true);
        });
    }

    // ================= PLACEHOLDER + META =================

    private void ShowPlaceholder(string message, bool isError = false)
    {
        GameObject root = placeholderRoot != null
            ? placeholderRoot
            : (placeholderLabel != null ? placeholderLabel.gameObject : null);

        if (root != null)
            root.SetActive(true);

        if (placeholderLabel != null)
        {
            placeholderLabel.text = message;
            placeholderLabel.color = isError
                ? new Color(1f, 0.45f, 0.25f)
                : new Color(0.72f, 0.72f, 0.72f);
        }
    }

    private void HidePlaceholder()
    {
        if (placeholderRoot != null)
            placeholderRoot.SetActive(false);
        else if (placeholderLabel != null)
            placeholderLabel.gameObject.SetActive(false);
    }

    private void UpdateMetaLabels(string note = null)
    {
        if (countLabel != null)
            countLabel.text = _rowObjects.Count > 0
                ? $"{_rowObjects.Count} protected"
                : "0 protected";

        if (updatedLabel != null)
        {
            string when = _lastUpdateTimeUtc == DateTime.MinValue
                ? "—"
                : _lastUpdateTimeUtc.ToLocalTime().ToString("HH:mm:ss");

            updatedLabel.text = string.IsNullOrEmpty(note)
                ? $"Updated: {when}"
                : $"Updated: {when} — {note}";
        }
    }

    // ================= POMOCNICZE =================

    private static bool SnapshotsEqual(
        List<SVNFileProtection.ProtectionInfo> a,
        List<SVNFileProtection.ProtectionInfo> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];

            if (x == null || y == null)
            {
                if (!ReferenceEquals(x, y)) return false;
                continue;
            }

            if (!string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase)) return false;
            if (!Equals(x.State, y.State)) return false;
            if (!string.Equals(x.LockOwner ?? "", y.LockOwner ?? "", StringComparison.Ordinal)) return false;
            if (x.LockIsMine != y.LockIsMine) return false;
        }

        return true;
    }
}