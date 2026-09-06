using SVN.Core;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Panel LOCKÓW — routing przycisków (oś serwerowa: kto trzyma locka;
/// steal/break siedzi w LockPanel/ProtectedPanel po stronie swoich
/// hostów). Po czyszczeniu ery self-contained: ZERO wiedzy o protected —
/// tamtą osią zarządza ProtectedPanel (własny OnEnable, własne
/// ExecuteX, własny prefab ProtectedRow).
/// </summary>
public class LocksPanel : MonoBehaviour
{
    private SVNUI svnUI;
    private SVNManager svnManager;

    [Header("AutoLock — przeciągnij tu przycisk AutoLock, aby widzieć stan ON/OFF")]
    [SerializeField] private Button autoLockButton = null;

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
        // Event może przyjść z puli wątków — UI tylko na MAIN.
        UnityMainThreadDispatcher.Enqueue(UpdateAutoLockLabel);
    }

    private void UpdateAutoLockLabel()
    {
        var autoLock = svnManager?.GetModule<SVNAutoLock>();
        if (autoLock == null || autoLockButton == null) return;

        // TMP (TextMeshPro) — standard w Twoim UI:
        var label = autoLockButton.GetComponentInChildren<TMPro.TMP_Text>();
        if (label != null)
        {
            label.text = autoLock.Enabled ? "AutoLock: ON" : "AutoLock: OFF";
            return;
        }

        // Fallback: stary Unity Text (gdyby przycisk akurat go używał):
        var legacyLabel = autoLockButton.GetComponentInChildren<UnityEngine.UI.Text>();
        if (legacyLabel != null)
            legacyLabel.text = autoLock.Enabled ? "AutoLock: ON" : "AutoLock: OFF";
    }

    // ================= PRZYCISKI — LOCKI =================

    public void Button_Lock() => svnManager.GetModule<SVNLock>().LockModifiedButton();
    public void Button_Unlock() => svnManager.GetModule<SVNLock>().UnlockAllButton();
    public void Button_ShowLocks() => svnManager.GetModule<SVNLock>().ShowAllLocksButton();
    public void Button_CleanupLocks() => svnManager.GetModule<SVNLock>().CleanupLocksButton();

    // ================= PRZYCISKI — PROTECTED (tylko routing do modułów;
    //  tabela protected = ProtectedPanel, NIE ten panel) =================

    /// <summary>Tekstowy panel Protected (stary, tekstowy przegląd —
    /// jeśli go trzymasz). Tabela z przyciskami = ProtectedPanel.</summary>
    public void Button_ShowProtected() => svnManager.GetModule<SVNFileProtection>().ShowProtectedButton();

    public void Button_UnprotectAll() => svnManager.GetModule<SVNFileProtection>()?.UnprotectAllButton();

    // ================= PROTECT / UNPROTECT (zaznaczenie z drzewa) =================

    public void Button_Protect()
    {
        var protection = svnManager.GetModule<SVNFileProtection>();
        if (protection == null)
        {
            Debug.LogWarning("[LocksPanel] SVNFileProtection module is not registered in SVNManager.");
            return;
        }
        _ = protection.ProtectFiles(GetSelectedFiles());
    }

    public void Button_Unprotect()
    {
        var protection = svnManager.GetModule<SVNFileProtection>();
        if (protection == null)
        {
            Debug.LogWarning("[LocksPanel] SVNFileProtection module is not registered in SVNManager.");
            return;
        }
        _ = protection.UnprotectFiles(GetSelectedFiles());
    }

    // ================= AUTOLOCK =================

    /// <summary>Włącza/wyłącza AutoLock. Setter modułu SAM zapamiętuje stan per project.</summary>
    public void Button_ToggleAutoLock()
    {
        var autoLock = svnManager.GetModule<SVNAutoLock>();
        if (autoLock == null)
        {
            Debug.LogWarning("[LocksPanel] SVNAutoLock module is not registered in SVNManager.");
            return;
        }

        autoLock.Enabled = !autoLock.Enabled;
        string state = autoLock.Enabled ? "<color=green>ON</color>" : "<color=red>OFF</color>";
        SVNLogBridge.LogLine($"<color=#00E5FF>[AutoLock]</color> Auto-lock is now: {state}");

        UpdateAutoLockLabel();
    }

    // ================= CZYSZCZENIE WIDOKU =================

    public void Button_ClearLocksView()
    {
        // === FIX: czyścimy WŁAŚCIWE pole panelu locków — LockDisplayArea, czyli
        // dokładnie to, do którego piszą SVNLock/SVNAutoLock (LogToLockPanel).
        // Poprzednio czyszczono LogText (inne pole), więc widok locków zostawał.
        if (svnUI != null && svnUI.LockDisplayArea != null)
            SVNLogBridge.UpdateUIField(svnUI.LockDisplayArea, string.Empty, "LOCKS_VIEW", append: false);

        // === FIX: bez komunikatu po czyszczeniu — ma być czysto.
    }

    // ================= POMOCNICZE =================

    /// <summary>
    /// Zaznaczone PLIKI ze GŁÓWNEGO drzewa (flat), ścieżki absolutne, BEZ
    /// filtra statusu — o '?', '!', 'M' rozstrzygają MODUŁY (Protect ma
    /// pełną ścieżkę dla '?': propset → E155007/E155010 → add → commit).
    /// (FIX: wcześniej SVNCommit.GetSelectedFiles + filtr '?' → "No files
    /// selected" dla plików unversioned.)
    /// </summary>
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