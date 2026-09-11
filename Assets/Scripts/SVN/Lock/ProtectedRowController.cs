using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using SVN.Core;

/// <summary>
/// Wiersz panelu PROTECTED – dokładnie cztery przyciski:
///   Lock / Unlock / Locked    – toggle stanu locka
///   Unprotect / Publish / Finish – zależnie od stanu ochrony
///   Cancel / Restore          – anulowanie pending / przywracanie ochrony
///   Explorer                  – Eksplorator Windows z zaznaczonym plikiem
///
/// Zgodne z dokumentacją: przyciski zmieniają etykietę i akcję w zależności
/// od ProtectionInfo.State i LockOwner. Po każdej akcji emitowane jest
/// zdarzenie ActionCompleted, aby panel mógł odświeżyć wiersze.
/// </summary>
public class ProtectedRowController : MonoBehaviour
{
    public event Action ActionCompleted;

    [Header("Etykiety przycisku-toggle (edytowalne)")]
    [SerializeField] private string lockLabelText = "Lock";
    [SerializeField] private string unlockLabelText = "Unlock";
    [SerializeField] private string lockedLabelText = "Locked";

    [Header("Teksty")]
    [SerializeField] private TMP_Text pathLabel = null;
    [SerializeField] private TMP_Text stateLabel = null;
    [SerializeField] private TMP_Text lockOwnerLabel = null;

    [Header("Przyciski")]
    [SerializeField] private Button lockButton = null;
    [SerializeField] private Button unprotectButton = null;
    [SerializeField] private Button revertButton = null;
    [SerializeField] private Button explorerButton = null;

    [Header("Etykiety przycisków (opcjonalne, jeśli nie dzieci)")]
    [SerializeField] private TMP_Text lockButtonLabel = null;
    [SerializeField] private TMP_Text unprotectButtonLabel = null;
    [SerializeField] private TMP_Text revertButtonLabel = null;

    [Header("Wyróżnienie wiersza")]
    [SerializeField] private Image rowBackground = null;
    [SerializeField] private Color baseColor = new Color(1f, 1f, 1f, 0.05f);
    [SerializeField] private Color lockedColor = new Color(1f, 0.55f, 0.15f, 0.18f);

    private SVNFileProtection.ProtectionInfo _info;
    private Action<string, string> _unlockAction;
    private Action<string, string> _lockAction;
    private Action<string, string> _unprotectAction;
    private Action<string, string> _revertAction;
    private TMP_Text _logArea;

    private Dictionary<Button, SVNHoverHandler> _hoverCache;

    private UnityAction _onLockClick;
    private UnityAction _onUnprotectClick;
    private UnityAction _onRevertClick;
    private UnityAction _onExplorerClick;

    private void Awake()
    {
        EnsureHandlers();

        _hoverCache = new Dictionary<Button, SVNHoverHandler>();
        foreach (var btn in GetComponentsInChildren<Button>(true))
        {
            if (btn == null) continue;
            var hover = btn.GetComponent<SVNHoverHandler>();
            if (hover == null) hover = btn.gameObject.AddComponent<SVNHoverHandler>();
            _hoverCache[btn] = hover;
        }
    }

    private void EnsureHandlers()
    {
        _onLockClick ??= OnLockClick;
        _onUnprotectClick ??= OnUnprotectClick;
        _onRevertClick ??= OnRevertClick;
        _onExplorerClick ??= OnExplorerClick;
    }

    private void OnDestroy()
    {
        EnsureHandlers();
        RemoveListeners();
        ActionCompleted = null;
    }

    private void WireButtons()
    {
        EnsureHandlers();

        if (lockButton != null && lockButton.gameObject.activeSelf && lockButton.interactable)
            lockButton.onClick.AddListener(_onLockClick);

        if (unprotectButton != null)
            unprotectButton.onClick.AddListener(_onUnprotectClick);

        if (revertButton != null)
            revertButton.onClick.AddListener(_onRevertClick);

        if (explorerButton != null)
            explorerButton.onClick.AddListener(_onExplorerClick);
    }

    private void RemoveListeners()
    {
        EnsureHandlers();

        if (lockButton != null && _onLockClick != null)
            lockButton.onClick.RemoveListener(_onLockClick);

        if (unprotectButton != null && _onUnprotectClick != null)
            unprotectButton.onClick.RemoveListener(_onUnprotectClick);

        if (revertButton != null && _onRevertClick != null)
            revertButton.onClick.RemoveListener(_onRevertClick);

        if (explorerButton != null && _onExplorerClick != null)
            explorerButton.onClick.RemoveListener(_onExplorerClick);
    }

    public void Setup(
    SVNFileProtection.ProtectionInfo info,
    Action<string, string> unlockAction,
    Action<string, string> lockAction,
    Action<string, string> unprotectAction,
    Action<string, string> revertAction,
    TMP_Text logArea)
    {
        EnsureHandlers();
        RemoveListeners();

        _info = info;
        _unlockAction = unlockAction;
        _lockAction = lockAction;
        _unprotectAction = unprotectAction;
        _revertAction = revertAction;
        _logArea = logArea;

        try
        {
            Render();
            WireButtons();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ProtectedRow] Setup failed for '{info?.Path}': {ex}");
        }
    }

    public void UpdateInfo(SVNFileProtection.ProtectionInfo info)
    {
        _info = info;
        Render();
    }

    private void Render()
    {
        if (_info == null) return;

        bool protectedFile = _info.State == SVNFileProtection.ProtectionState.Protected;
        bool pendingAdd = _info.State == SVNFileProtection.ProtectionState.PendingAdd;
        bool pendingRem = _info.State == SVNFileProtection.ProtectionState.PendingRemoval;
        bool hasLock = !string.IsNullOrWhiteSpace(_info.LockOwner);

        // ---- Status label ----
        if (stateLabel != null)
        {
            if (protectedFile && _info.LockIsMine)
                stateLabel.text = "<color=#66FF66><b>[EDITABLE]</b></color>";
            else if (protectedFile && hasLock)
                stateLabel.text = "<color=#FF5555><b>[FOREIGN]</b></color>";
            else if (protectedFile)
                stateLabel.text = "<color=#FFA500><b>[READ-ONLY]</b></color>";
            else if (pendingAdd)
                stateLabel.text = "<color=#4FC3F7><b>[PENDING+]</b></color>";
            else
                stateLabel.text = "<color=#FFA500><b>[PENDING-]</b></color>";
        }

        // ---- Path label ----
        if (pathLabel != null)
            pathLabel.text = _info.Path;

        // ---- Lock owner label ----
        if (lockOwnerLabel != null)
        {
            if (_info.LockIsMine) lockOwnerLabel.text = "lock: you";
            else if (hasLock) lockOwnerLabel.text = $"lock: {_info.LockOwner}";
            else if (protectedFile) lockOwnerLabel.text = "no lock — read-only for everyone";
            else if (pendingAdd) lockOwnerLabel.text = "protection not published yet";
            else lockOwnerLabel.text = "removal not committed yet";
        }

        // ---- Lock button ----
        var lockText = lockButtonLabel != null ? lockButtonLabel : lockButton?.GetComponentInChildren<TMP_Text>(true);
        if (lockButton != null)
        {
            if (!protectedFile)
            {
                lockButton.gameObject.SetActive(false);
            }
            else if (_info.LockIsMine)
            {
                lockButton.gameObject.SetActive(true);
                lockButton.interactable = true;
                if (lockText != null) lockText.text = unlockLabelText;
                BindHover(lockButton, "Release your lock. File returns to read-only after Update.");
            }
            else if (hasLock)
            {
                lockButton.gameObject.SetActive(true);
                lockButton.interactable = false;
                if (lockText != null) lockText.text = lockedLabelText;
                BindHover(lockButton, $"Locked by {_info.LockOwner}. Steal/break is available in the LOCKS panel.");
            }
            else
            {
                lockButton.gameObject.SetActive(true);
                lockButton.interactable = true;
                if (lockText != null) lockText.text = lockLabelText;
                BindHover(lockButton, "Take the lock — file becomes editable.");
            }
        }

        // ---- Unprotect / Publish / Finish button ----
        var unprotectText = unprotectButtonLabel != null ? unprotectButtonLabel : unprotectButton?.GetComponentInChildren<TMP_Text>(true);
        if (unprotectButton != null)
        {
            // NOWE: obcy lock → moduł i tak zwróci BLOCKED (polityka: nie ruszamy
            // pliku, na którym ktoś aktywnie pracuje). Przycisk komunikuje to
            // PRZED kliknięciem, zamiast klikać się w nic:
            bool foreignLock = hasLock && !_info.LockIsMine;

            unprotectButton.gameObject.SetActive(true);
            unprotectButton.interactable = !foreignLock;

            string blockedNote = foreignLock
                ? $" [BLOCKED: locked by {_info.LockOwner} — steal/break the lock in the LOCKS panel first]"
                : "";

            if (pendingAdd)
            {
                if (unprotectText != null) unprotectText.text = "Publish";
                BindHover(unprotectButton, "Commit the protection (retry)." + blockedNote);
            }
            else if (pendingRem)
            {
                if (unprotectText != null) unprotectText.text = "Finish";
                BindHover(unprotectButton, "Commit the removal (retry)." + blockedNote);
            }
            else
            {
                if (unprotectText != null) unprotectText.text = "Unprotect";
                BindHover(unprotectButton, "Remove svn:needs-lock." + blockedNote);
            }
        }

        // ---- Cancel / Restore button ----
        var revertText = revertButtonLabel != null ? revertButtonLabel : revertButton?.GetComponentInChildren<TMP_Text>(true);
        if (revertButton != null)
        {
            bool revertActive = pendingAdd || pendingRem;

            revertButton.gameObject.SetActive(true);
            revertButton.interactable = revertActive;

            if (revertText != null)
                revertText.text = pendingRem ? "Restore" : "Cancel";

            BindHover(revertButton, revertActive
                ? (pendingRem ? "svn revert — bring the protection back." : "svn revert — cancel the unpublished protection.")
                : "Nothing to cancel.");
        }

        // ---- Explorer ----
        if (explorerButton != null)
            BindHover(explorerButton, "Open file location in Windows Explorer.");

        // ---- Row background ----
        if (rowBackground != null)
            rowBackground.color = hasLock ? lockedColor : baseColor;
    }

    private void OnLockClick()
    {
        if (_info == null) return;

        if (_info.LockIsMine)
            _unlockAction?.Invoke(_info.Path, _info.LockOwner);
        else
            _lockAction?.Invoke(_info.Path, _info.LockOwner);

        ActionCompleted?.Invoke();
    }

    private void OnUnprotectClick()
    {
        if (_info == null) return;

        // W zależności od stanu:
        // PendingAdd → ProtectFiles (retry publish)
        // PendingRemoval → UnprotectFiles (retry finish)
        // Protected → UnprotectFiles
        _unprotectAction?.Invoke(_info.Path, _info.LockOwner);

        ActionCompleted?.Invoke();
    }

    private void OnRevertClick()
    {
        if (_info == null) return;

        // Cancel/Restore – svn revert:
        _revertAction?.Invoke(_info.Path, _info.LockOwner);

        ActionCompleted?.Invoke();
    }

    private void OnExplorerClick()
    {
        if (_info == null || string.IsNullOrWhiteSpace(_info.Path)) return;

        try
        {
            // System działa na '/' (svn), ale explorer.exe /select, chce '\' —
            // z '/' parser Explorera otwiera folder domyślny (Desktop):
            string abs = MakeAbsolutePath(_info.Path).Replace('/', '\\');

            if (System.IO.File.Exists(abs))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{abs}\"");
                return;
            }

            string dir = System.IO.Path.GetDirectoryName(abs);

            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                LogToPanel($"Explorer: file missing locally — opened parent folder: {_info.Path}");
                return;
            }

            LogToPanel($"Explorer: path not found: {_info.Path}");
        }
        catch (Exception ex)
        {
            LogToPanel($"Explorer failed: {ex.Message}");
        }
    }

    private string MakeAbsolutePath(string path)
    {
        string p = (path ?? "").Replace('\\', '/').Trim();
        if (p.IndexOf(':') >= 0) return p;

        string root = (SVNManager.Instance?.WorkingDir ?? "").Replace('\\', '/').TrimEnd('/');
        return root + "/" + p.TrimStart('/');
    }

    private void BindHover(Button btn, string tooltip)
    {
        if (btn != null && _hoverCache != null && _hoverCache.TryGetValue(btn, out var handler))
            handler.TooltipText = tooltip;
    }

    private void LogToPanel(string message)
    {
        if (_logArea is TextMeshProUGUI ugui)
            SVNLogBridge.UpdateUIField(ugui, message, append: true);
        else
            SVNLogBridge.LogLine(message, true);
    }
}