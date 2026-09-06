using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;
using SVN.Core;

/// <summary>
/// Wiersz panelu PROTECTED (oś property svn:needs-lock — NIE oś locków
/// serwerowych; steal/break siedzi w panelu Locków i tam zostaje).
///
/// Kontrakt LockUIItem-style: panel (ProtectedPanel) dostarcza w Setup
/// dane + delegatki akcji + konsolę. Wiersz NIE woła modułów sam —
/// renderuje stan i przekazuje kliknięcie do panelu (guard isProcessing,
/// refresh po akcji — wszystko po stronie panelu).
///
/// === WYGLĄD: czarne przyciski (spójność wizualna z LockUIItem w
/// lock-tabeli). Kolor ustawiany JAWNIE w Awake (raz) — niezależnie od
/// prefabu, oba panele zawsze identyczne. disabledColor ustawiony
/// jawnie — czytelna szarość dla LOCKED*/CANCEL.
///
/// === UKŁAD: przycisk CANCEL/RESTORE zawsze widoczny, wyłączony gdy
/// brak PENDING (tooltip wyjaśnia). lockBtn ukryty w PENDING (inna
/// kategoria akcji).
///
/// Stany i akcje:
///   PROTECTED, brak locka   → Lock       (weź locka → plik edytowalny u Ciebie)
///   PROTECTED, Twój lock    → Unlock     (release; plik wraca read-only po Update)
///   PROTECTED, cudzy lock   → Locked (szary; wskazanie do panelu Locków)
///   PENDING+  → Publish / Cancel
///   PENDING−  → Finish  / Restore
///   Dodatkowo zawsze: Unprotect (panel → UnprotectFiles — pełna polityka
///   locków w module), Explorer.
/// </summary>
public class ProtectedRowController : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI nameText;
    [SerializeField] private TextMeshProUGUI infoText;
    [SerializeField] private Button lockBtn;
    [SerializeField] private Button unprotectBtn;
    [SerializeField] private Button revertBtn;
    [SerializeField] private Button explorerBtn;

    // === Spójność wizualna z lock-tabelą (LockUIItem): czarne przyciski.
    // Publiczne — chcesz zmienić motyw, zmieniasz tu (albo przeciągasz
    // w Inspectorze, bo SerializeField):
    [Header("Wspólny motyw przycisków (jak lock-tabela)")]
    [SerializeField] private Color normalColor = new Color(0f, 0f, 0f, 0.85f);        // czarny, lekko prześwitujący
    [SerializeField] private Color highlightedColor = new Color(0.15f, 0.15f, 0.15f, 0.9f);
    [SerializeField] private Color pressedColor = new Color(0f, 0f, 0f, 1f);
    [SerializeField] private Color disabledColor = new Color(0.35f, 0.35f, 0.35f, 0.5f); // czytelna szarość (Locked/Cancel)
    [SerializeField] private float colorMultiplier = 1f;

    private SVNFileProtection.ProtectionInfo _info;
    private string _absPath;

    // === Delegatki akcji z panelu (kontrakt LockUIItem):
    private Action<string, string> _lockAction;
    private Action<string, string> _unlockAction;
    private Action<string, string> _unprotectAction;
    private Action<string, string> _revertAction;
    private TMP_Text _console;

    private Dictionary<Button, SVNHoverHandler> _hoverCache;

    private UnityAction _onLockClick;
    private UnityAction _onUnprotectClick;
    private UnityAction _onRevertClick;
    private UnityAction _onExplorerClick;

    private void Awake()
    {
        _onLockClick = OnLockClick;
        _onUnprotectClick = OnUnprotectClick;
        _onRevertClick = OnRevertClick;
        _onExplorerClick = OnExplorerClick;

        _hoverCache = new Dictionary<Button, SVNHoverHandler>();
        foreach (var btn in GetComponentsInChildren<Button>(true))
        {
            var h = btn.GetComponent<SVNHoverHandler>();
            if (h == null) h = btn.gameObject.AddComponent<SVNHoverHandler>();
            _hoverCache[btn] = h;

            // === UJEDNOLICENIE: wspólny ColorBlock na WSZYSTKICH przyciskach
            // wiersza (raz, w Awake — nie per-Setup; kolory prefabowe
            // nadpisane świadomie, spójność z lock-tabelą niezależnie od
            // assetu):
            ApplyButtonTheme(btn);
        }
    }

    private void OnDestroy() => RemoveListeners();

    /// <summary>Nadpisuje ColorBlock przycisku wspólnym motywem (czarny).
    /// Target Graphic bez zmian — tint robi standardowy mechanizm Button.</summary>
    private void ApplyButtonTheme(Button btn)
    {
        if (btn == null) return;

        var cb = btn.colors;
        cb.normalColor = normalColor;
        cb.highlightedColor = highlightedColor;
        cb.pressedColor = pressedColor;
        cb.disabledColor = disabledColor;
        cb.colorMultiplier = colorMultiplier;
        cb.fadeDuration = 0.08f;
        btn.colors = cb;
    }

    // ===================================================================
    //  SETUP — dane + akcje + konsola (jak LockUIItem.Setup w LockPanel)
    // ===================================================================

    public void Setup(
        SVNFileProtection.ProtectionInfo info,
        Action<string, string> lockAction,
        Action<string, string> unlockAction,
        Action<string, string> unprotectAction,
        Action<string, string> revertAction,
        TMP_Text console)
    {
        _info = info;
        if (info == null) return;

        _lockAction = lockAction;
        _unlockAction = unlockAction;
        _unprotectAction = unprotectAction;
        _revertAction = revertAction;
        _console = console;

        // Setup biegnie na main thread (Populate w panelu):
        string root = (SVNManager.Instance?.WorkingDir ?? "").Replace('\\', '/').TrimEnd('/');
        _absPath = root + "/" + info.Path;

        RemoveListeners();
        Render();
        WireButtons();
    }

    // ===================================================================
    //  RENDER — maszyna stanów
    // ===================================================================

    private void Render()
    {
        bool protected_ = _info.State == SVNFileProtection.ProtectionState.Protected;
        bool pendingAdd = _info.State == SVNFileProtection.ProtectionState.PendingAdd;
        bool pendingRem = _info.State == SVNFileProtection.ProtectionState.PendingRemoval;

        // === Kolumna statusu:
        if (statusText != null)
        {
            if (protected_ && _info.LockIsMine)
                statusText.text = "<color=#66FF66><b>[EDITABLE]</b></color>";
            else if (protected_ && _info.LockOwner != null)
                statusText.text = "<color=#FF5555><b>[FOREIGN]</b></color>";
            else if (protected_)
                statusText.text = "<color=#FFA500><b>[READ-ONLY]</b></color>";
            else if (pendingAdd)
                statusText.text = "<color=#4FC3F7><b>[PENDING+]</b></color>";
            else
                statusText.text = "<color=#FFA500><b>[PENDING-]</b></color>";
        }

        if (nameText != null)
        {
            nameText.text = _info.Path;
            nameText.fontStyle = (pendingAdd || pendingRem) ? FontStyles.Italic : FontStyles.Normal;
        }

        // === Info (kolumna zamiast przycisku-steal):
        if (infoText != null)
        {
            if (_info.LockIsMine) infoText.text = "lock: you";
            else if (_info.LockOwner != null) infoText.text = $"lock: {_info.LockOwner}";
            else if (protected_) infoText.text = "no lock — read-only for everyone";
            else if (pendingAdd) infoText.text = "protection not published yet";
            else infoText.text = "removal not committed yet";
        }

        // === Przycisk LOCK (bez STEAL — kompetencja panelu Locków):
        var lt = lockBtn != null ? lockBtn.GetComponentInChildren<TMPro.TMP_Text>() : null;
        if (lockBtn != null)
        {
            if (!protected_)
            {
                // PENDING: lock nie ma zastosowania (ochrona nieopublikowana):
                lockBtn.gameObject.SetActive(false);
            }
            else if (_info.LockIsMine)
            {
                lockBtn.gameObject.SetActive(true);
                lockBtn.interactable = true;
                if (lt != null) lt.text = "Unlock";
                BindHover(lockBtn, "Release your lock. File returns to read-only in your copy after the next Update.");
            }
            else if (_info.LockOwner != null)
            {
                // Cudzy lock: bez akcji w TYM panelu — wskazanie do panelu Locków:
                lockBtn.gameObject.SetActive(true);
                lockBtn.interactable = false;
                if (lt != null) lt.text = "Locked";
                BindHover(lockBtn, $"Locked by {_info.LockOwner}. Steal/break is available in the LOCKS panel (intentional decision).");
            }
            else
            {
                lockBtn.gameObject.SetActive(true);
                lockBtn.interactable = true;
                if (lt != null) lt.text = "Lock";
                BindHover(lockBtn, "Take the lock — the file becomes editable in your working copy.");
            }
        }

        // === Przycisk UNPROTECT / PUBLISH / FINISH:
        var ut = unprotectBtn != null ? unprotectBtn.GetComponentInChildren<TMPro.TMP_Text>() : null;
        if (unprotectBtn != null)
        {
            unprotectBtn.gameObject.SetActive(true);
            unprotectBtn.interactable = true;
            if (pendingAdd)
            {
                if (ut != null) ut.text = "Publish";
                BindHover(unprotectBtn, "Commit the protection (retry the failed commit).");
            }
            else if (pendingRem)
            {
                if (ut != null) ut.text = "Finish";
                BindHover(unprotectBtn, "Commit the removal (retry after the lock is released/stolen in the Locks panel).");
            }
            else
            {
                if (ut != null) ut.text = "Unprotect";
                BindHover(unprotectBtn, "Remove svn:needs-lock. Foreign lock = blocked (policy).");
            }
        }

        // === Przycisk CANCEL / RESTORE — zawsze widoczny, wyłączony (szary
        // przez disabledColor) gdy plik nie jest w stanie PENDING:
        var rt = revertBtn != null ? revertBtn.GetComponentInChildren<TMPro.TMP_Text>() : null;
        if (revertBtn != null)
        {
            bool revertActive = pendingAdd || pendingRem;

            revertBtn.gameObject.SetActive(true);
            revertBtn.interactable = revertActive;

            if (rt != null)
                rt.text = pendingRem ? "Restore" : "Cancel";

            BindHover(revertBtn, revertActive
                ? (pendingRem
                    ? "svn revert — bring the protection back (discards the local removal)."
                    : "svn revert — cancel the unpublished protection.")
                : "Nothing to cancel — this file has no pending local property change.");
        }

        if (explorerBtn != null)
            BindHover(explorerBtn, "Open file location in Windows Explorer.");
    }

    private void WireButtons()
    {
        if (lockBtn != null && lockBtn.gameObject.activeSelf && lockBtn.interactable)
            lockBtn.onClick.AddListener(_onLockClick);
        if (unprotectBtn != null) unprotectBtn.onClick.AddListener(_onUnprotectClick);
        // revertBtn: listener zawsze (szary przycisk sam nie klika — Unity;
        // isProcessing w panelu to drugi guard):
        if (revertBtn != null) revertBtn.onClick.AddListener(_onRevertClick);
        if (explorerBtn != null) explorerBtn.onClick.AddListener(_onExplorerClick);
    }

    private void RemoveListeners()
    {
        lockBtn?.onClick.RemoveListener(_onLockClick);
        unprotectBtn?.onClick.RemoveListener(_onUnprotectClick);
        revertBtn?.onClick.RemoveListener(_onRevertClick);
        explorerBtn?.onClick.RemoveListener(_onExplorerClick);
    }

    // ===================================================================
    //  AKCJE — tylko delegują do panelu (guard/refresh po stronie panelu)
    // ===================================================================

    private void OnLockClick()
    {
        // Lock lub Unlock — cudzy lock nie dociera tu jako aktywny przycisk
        // (Render wyłącza Locked):
        if (_info.LockIsMine)
            _unlockAction?.Invoke(_absPath, _info.Path);
        else
            _lockAction?.Invoke(_absPath, _info.Path);
    }

    private void OnUnprotectClick()
    {
        // Unprotect / Publish / Finish — panel deleguje do modułu
        // (UnprotectFiles ma wbudowane retry-pending i politykę locków):
        _unprotectAction?.Invoke(_absPath, _info.Path);
    }

    private void OnRevertClick()
    {
        _revertAction?.Invoke(_absPath, _info.Path);
    }

    private void OnExplorerClick()
    {
        SVNManager.Instance?.GetModule<SVNExternal>()?.OpenInExplorerAndSelect(_info.Path);
    }

    // ===================================================================
    //  INFRASTRUKTURA
    // ===================================================================

    private void BindHover(Button btn, string tooltip)
    {
        if (btn != null && _hoverCache.TryGetValue(btn, out var h))
            h.TooltipText = tooltip;
    }
}