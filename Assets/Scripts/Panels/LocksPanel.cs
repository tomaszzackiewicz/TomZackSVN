using SFB;
using SVN.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Panel LOCKÓW — routing przycisków.
///
/// === KONWENCJA NAZW (od ŹRÓDŁA scope, nie od etykiety przycisku):
///  ...FromInput     → czyta POLE TEKSTOWE (lockScopeInput / '.' / Browse)
///  ...FromSelection → czyta ZAZNACZENIE w drzewie (checkbox: folder→poddrzewo,
///                     plik→plik; brak = odmowa z instrukcją)
///
/// Wrappery o starych nazwach pozostają jako aliasy — On Click dalej działa.
///
/// === NATYCHMIASTOWY REFRESH TABELI PROTECTED (nowość):
/// Każda akcja zmieniająca stan (Protect/Unprotect/Lock/Unlock — Selected,
/// All, In-Path) jest AWAITOWANA, a PO zakończeniu woła NotifyProtectedTable()
/// → tabela w ProtectedPanel dociąga świeży snapshot od razu, nie przy
/// następnym otwarciu. Dlatego zamieniono fire-and-forget (`_ = ...`)
/// na await — notify „obok" akcji odświeżyłby STARE dane, a anti-flicker
/// (identyczny snapshot = brak rebuildu) zamroziłby je na dobre.
/// Wyjątek: UnprotectAllButton (async void w module) → notify teraz + po 2 s.
///
/// === KOMUNIKATY: LogToLockPanel → konsola panelu (LockDisplayArea),
/// fallback LogLine. Zasada: user kliknął → odpowiedź w konsoli panelu;
/// błąd konfiguracji → Debug.LogWarning (Unity Console).
/// </summary>
public class LocksPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    [Header("AutoLock — przeciągnij tu przycisk AutoLock, aby widzieć stan ON/OFF")]
    [SerializeField] private Button autoLockButton = null;

    [Header("Scope (wspólny dla Lock/Protect in path — wymagane; '.' = całe WC)")]
    [SerializeField] private TMP_InputField lockScopeInput = null;
    [SerializeField] private Button lockScopeBrowseButton = null;

    [Header("Tabela Protected — refresh po akcjach (opcjonalne; puste = auto-find)")]
    [SerializeField] private ProtectedPanel protectedTablePanel = null;

    // === Kopia loggera z SVNLock: komunikaty przycisków → konsola panelu.
    private void LogToLockPanel(string message, bool append = true)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (svnUI?.LockDisplayArea != null)
                SVNLogBridge.UpdateUIField(svnUI.LockDisplayArea, message, append: append);
            else
                SVNLogBridge.LogLine(message, append);
        });
    }

    private void OnEnable()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        UpdateAutoLockLabel();
        if (svnManager != null)
            svnManager.OnProjectChanged += OnProjectChangedForLabel;
    }

    private void OnDisable()
    {
        if (svnManager != null)
            svnManager.OnProjectChanged -= OnProjectChangedForLabel;
    }

    private void OnProjectChangedForLabel(SVNProject project)
    {
        UnityMainThreadDispatcher.Enqueue(UpdateAutoLockLabel);
    }

    private void UpdateAutoLockLabel()
    {
        var autoLock = svnManager?.GetModule<SVNAutoLock>();
        if (autoLock == null || autoLockButton == null) return;

        var label = autoLockButton.GetComponentInChildren<TMPro.TMP_Text>();
        if (label != null)
        {
            label.text = autoLock.Enabled ? "AutoLock: ON" : "AutoLock: OFF";
            return;
        }

        var legacyLabel = autoLockButton.GetComponentInChildren<UnityEngine.UI.Text>();
        if (legacyLabel != null)
            legacyLabel.text = autoLock.Enabled ? "AutoLock: ON" : "AutoLock: OFF";
    }

    // ===================================================================
    //  TABELA PROTECTED — NATYCHMIASTOWY REFRESH PO AKCJACH
    // ===================================================================

    /// <summary>
    /// Refresh tabeli Protected zaraz po zakończonej akcji. Main-thread
    /// (FindObjectOfType wymaga głównego wątku), panel znaleziony raz i
    /// cache'owany. Panel nieaktywny (zamknięte okno) → no-op; przy otwarciu
    /// OnEnable i tak odświeża.
    /// </summary>
    private void NotifyProtectedTable()
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            ProtectedPanel panel = protectedTablePanel;

            if (panel == null)
            {
                panel = FindObjectOfType<ProtectedPanel>();
                if (panel != null)
                    protectedTablePanel = panel;     // cache — nie szukamy ponownie
            }

            panel?.NotifyProtectionChanged();
        });
    }

    /// <summary>
    /// Safety net dla akcji, których nie da się awaitować (async void w
    /// module): drugi notify z opóźnieniem. CTS w ProtectedPanel sprawia,
    /// że nowszy refresh wygrywa — wyścig jest bezpieczny z definicji.
    /// </summary>
    private async Task NotifyProtectedTableDelayedAsync(TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        NotifyProtectedTable();
    }

    // ================= PRZYCISKI — LOCKI =================

    public async void Button_Lock()
    {
        var lockModule = svnManager?.GetModule<SVNLock>();
        if (lockModule == null)
        {
            Debug.LogWarning("[LocksPanel] SVNLock module is not registered.");
            return;
        }

        try
        {
            await lockModule.LockModified().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogToLockPanel($"<color=#FFAA00>[Lock] Error:</color> {ex.Message}");
        }

        NotifyProtectedTable();
    }

    public async void Button_Unlock()
    {
        var lockModule = svnManager?.GetModule<SVNLock>();
        if (lockModule == null)
        {
            Debug.LogWarning("[LocksPanel] SVNLock module is not registered.");
            return;
        }

        try
        {
            await lockModule.UnlockAll().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogToLockPanel($"<color=#FFAA00>[Unlock] Error:</color> {ex.Message}");
        }

        NotifyProtectedTable();
    }

    public void Button_ShowLocks()
        => svnManager?.GetModule<SVNLock>()?.ShowAllLocksButton();

    public async void Button_CleanupLocks()
    {
        var lockModule = svnManager?.GetModule<SVNLock>();
        if (lockModule == null)
        {
            Debug.LogWarning("[LocksPanel] SVNLock module is not registered.");
            return;
        }

        try
        {
            await lockModule.CleanupLocks().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogToLockPanel($"<color=#FFAA00>[Cleanup] Error:</color> {ex.Message}");
        }

        NotifyProtectedTable();
    }

    // ================= PRZYCISKI — PROTECTED =================

    public void Button_ShowProtected()
        => svnManager?.GetModule<SVNFileProtection>()?.ShowProtectedButton();

    public async void Button_Protect()
    {
        var protection = svnManager?.GetModule<SVNFileProtection>();
        if (protection == null)
        {
            Debug.LogWarning("[LocksPanel] SVNFileProtection module is not registered in SVNManager.");
            return;
        }

        try
        {
            await protection.ProtectFiles(GetSelectedFiles()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogToLockPanel($"<color=#FFAA00>[Protect] Error:</color> {ex.Message}");
        }

        NotifyProtectedTable();
    }

    public async void Button_Unprotect()
    {
        var protection = svnManager?.GetModule<SVNFileProtection>();
        if (protection == null)
        {
            Debug.LogWarning("[LocksPanel] SVNFileProtection module is not registered in SVNManager.");
            return;
        }

        try
        {
            await protection.UnprotectFiles(GetSelectedFiles()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogToLockPanel($"<color=#FFAA00>[Unprotect] Error:</color> {ex.Message}");
        }

        NotifyProtectedTable();
    }

    public void Button_UnprotectAll()
    {
        var protection = svnManager?.GetModule<SVNFileProtection>();
        if (protection == null)
        {
            Debug.LogWarning("[LocksPanel] SVNFileProtection module is not registered in SVNManager.");
            return;
        }

        // UnprotectAllButton = async void w module (brak Taska do await) —
        // nie da się poczekać na koniec. Safety net: notify TERAZ + po 2 s:
        protection.UnprotectAllButton();

        NotifyProtectedTable();
        _ = NotifyProtectedTableDelayedAsync(TimeSpan.FromSeconds(2));
    }

    // ===================================================================
    //  LOCK PROTECTED — scope z INPUTA (pole tekstowe / Browse / '.')
    // ===================================================================

    /// <summary>Przycisk „Lock Protected in Path". Scope = lockScopeInput.</summary>
    public async void Button_LockProtectedFromInput()
    {
        var lockModule = svnManager?.GetModule<SVNLock>();
        if (lockModule == null)
        {
            Debug.LogWarning("[LocksPanel] SVNLock module is not registered.");
            return;
        }

        string scope = lockScopeInput != null ? lockScopeInput.text : "";

        try
        {
            await lockModule.LockAllProtectedAsync(scope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogToLockPanel($"<color=#FFAA00>[Lock] Error:</color> {ex.Message}");
        }

        NotifyProtectedTable();
    }

    // ===================================================================
    //  LOCK PROTECTED — scope z ZAZNACZENIA w drzewie
    // ===================================================================

    /// <summary>Przycisk „Lock Protected in Selected Path". Brak = odmowa.</summary>
    public async void Button_LockProtectedFromSelection()
    {
        var lockModule = svnManager?.GetModule<SVNLock>();
        if (lockModule == null)
        {
            Debug.LogWarning("[LocksPanel] SVNLock module is not registered.");
            return;
        }

        string scope = ResolveScopeFromSelection();
        if (string.IsNullOrWhiteSpace(scope))
        {
            LogToLockPanel(
                "<color=#FFAA00>[Lock] Nothing selected — select a folder (or file) in the tree first, " +
                "or type a path / '.' in the scope field.</color>");
            return;
        }

        try
        {
            await lockModule.LockAllProtectedAsync(scope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogToLockPanel($"<color=#FFAA00>[Lock] Error:</color> {ex.Message}");
        }

        NotifyProtectedTable();
    }

    // ===================================================================
    //  PROTECT FILES IN PATH — scope z INPUTA
    // ===================================================================

    /// <summary>Przycisk „Protect Files in Path". Scope = lockScopeInput.</summary>
    public async void Button_ProtectFilesFromInput()
    {
        var protection = svnManager?.GetModule<SVNFileProtection>();
        if (protection == null)
        {
            Debug.LogWarning("[LocksPanel] SVNFileProtection module is not registered in SVNManager.");
            return;
        }

        string scope = lockScopeInput != null ? lockScopeInput.text : "";

        try
        {
            await protection.ProtectInPathAsync(scope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogToLockPanel($"<color=#FFAA00>[Protect] Error:</color> {ex.Message}");
        }

        NotifyProtectedTable();
    }

    // ===================================================================
    //  PROTECT FILES IN PATH — scope z ZAZNACZENIA w drzewie
    // ===================================================================

    /// <summary>Przycisk „Protect Files in Selected Path". Brak = odmowa.</summary>
    public async void Button_ProtectFilesFromSelection()
    {
        var protection = svnManager?.GetModule<SVNFileProtection>();
        if (protection == null)
        {
            Debug.LogWarning("[LocksPanel] SVNFileProtection module is not registered in SVNManager.");
            return;
        }

        string scope = ResolveScopeFromSelection();
        if (string.IsNullOrWhiteSpace(scope))
        {
            LogToLockPanel(
                "<color=#FFAA00>[Protect] Nothing selected — select a folder (or file) in the tree first, " +
                "or type a path / '.' in the scope field.</color>");
            return;
        }

        try
        {
            await protection.ProtectInPathAsync(scope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogToLockPanel($"<color=#FFAA00>[Protect] Error:</color> {ex.Message}");
        }

        NotifyProtectedTable();
    }

    // ===================================================================
    //  BROWSE (wspólny dla obu „…in Path" — wpisuje do pola scope)
    // ===================================================================

    /// <summary>Browse — SFB, jak SVNExternal. Pole dostaje ścieżkę ABSOLUTNĄ;
    /// normalizacja w modułach przy akcji.</summary>
    public void Button_BrowseLockScope()
    {
        string root = string.IsNullOrEmpty(svnManager?.WorkingDir)
            ? ""
            : svnManager.WorkingDir;

        string[] paths = StandaloneFileBrowser.OpenFolderPanel(
            "Choose scope folder", root, false);

        if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
            return;   // anulowano — pole nietknięte

        string picked = paths[0].Replace('\\', '/').TrimEnd('/');

        if (lockScopeInput != null)
            lockScopeInput.text = picked;
    }

    // ===================================================================
    //  AUTOLOCK
    // ===================================================================

    /// <summary>Toggle AutoLocka — komunikat stanu → konsola panelu.</summary>
    public void Button_ToggleAutoLock()
    {
        var autoLock = svnManager?.GetModule<SVNAutoLock>();
        if (autoLock == null)
        {
            Debug.LogWarning("[LocksPanel] SVNAutoLock module is not registered in SVNManager.");
            return;
        }

        autoLock.Enabled = !autoLock.Enabled;
        string state = autoLock.Enabled ? "<color=green>ON</color>" : "<color=red>OFF</color>";
        LogToLockPanel($"<color=#00E5FF>[AutoLock]</color> Auto-lock is now: {state}");

        UpdateAutoLockLabel();
    }

    // ================= CZYSZCZENIE WIDOKU =================

    public void Button_ClearLocksView()
    {
        if (svnUI != null && svnUI.LockDisplayArea != null)
            SVNLogBridge.UpdateUIField(svnUI.LockDisplayArea, string.Empty, "LOCKS_VIEW", append: false);
    }

    // ===================================================================
    //  ALIASY (stare nazwy — On Click w istniejących przyciskach dalej działają)
    // ===================================================================

    /// <summary>ALIAS (stara nazwa) → Button_LockProtectedFromInput.</summary>
    public void Button_LockProtected() => Button_LockProtectedFromInput();

    /// <summary>ALIAS (stara nazwa) → Button_LockProtectedFromSelection.</summary>
    public void Button_LockProtectedInPath() => Button_LockProtectedFromSelection();

    /// <summary>ALIAS (stara nazwa) → Button_ProtectFilesFromInput.</summary>
    public void Button_ProtectFilesInPath() => Button_ProtectFilesFromInput();

    // ===================================================================
    //  POMOCNICZE
    // ===================================================================

    /// <summary>
    /// Scope z zaznaczenia: preferowany FOLDER (poddrzewo); fallback PLIK;
    /// null = nic nie zaznaczone.
    /// </summary>
    private string ResolveScopeFromSelection()
    {
        var data = svnManager.GetModule<SVNStatus>()?.GetCurrentData();
        if (data == null) return null;

        string root = (svnManager?.WorkingDir ?? "").Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(root)) return null;

        foreach (var e in data)
        {
            if (e == null || !e.IsChecked || !e.IsFolder) continue;
            if (string.IsNullOrWhiteSpace(e.FullPath)) continue;
            return e.FullPath.Replace('\\', '/').TrimStart('/');
        }

        foreach (var e in data)
        {
            if (e == null || e.IsChecked || e.IsFolder) continue;
            if (string.IsNullOrWhiteSpace(e.FullPath)) continue;
            return e.FullPath.Replace('\\', '/').TrimStart('/');
        }

        return null;
    }

    private List<string> GetSelectedFiles()
    {
        var result = new List<string>();

        var data = svnManager.GetModule<SVNStatus>()?.GetCurrentData();
        if (data == null) return result;

        string root = (svnManager?.WorkingDir ?? "").Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(root)) return result;

        foreach (var e in data)
        {
            if (e == null || e.IsFolder) continue;
            if (!e.IsChecked) continue;
            if (string.IsNullOrWhiteSpace(e.FullPath)) continue;

            string p = e.FullPath.Replace('\\', '/').TrimStart('/');
            string abs = p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                ? p
                : root + "/" + p;

            result.Add(SvnRunner.ForceCleanPath(abs));
        }

        return result;
    }
}