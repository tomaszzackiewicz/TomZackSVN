using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using SVN.Core;

public class EditMessagePopup : MonoBehaviour
{
    public static EditMessagePopup Instance { get; private set; }

    public TMP_InputField inputField;
    public TextMeshProUGUI titleText;

    private long currentRevision;
    private SVNManager currentManager;
    private Action<string> onSuccess;

    private int _saveInProgress;
    private CancellationTokenSource _saveCts;

    // === FIX (listener leak): trzymaj delegat w polu, żeby móc go usunąć.
    private UnityEngine.Events.UnityAction<string> _onSubmitHandler;

    private void Awake()
    {
        Instance = this;
        gameObject.SetActive(false);
    }

    private void Start()
    {
        if (inputField == null) return;

        // === FIX (listener leak): jednorazowe podpięcie w Start, usunięcie w
        // OnDestroy. Wcześniej lambda w AddListener zostawała na zawsze i przy
        // re-instancjonowaniu popupu (change scene / reload) odpalała się 2x.
        _onSubmitHandler = _ => SaveAndClose();
        inputField.onSubmit.RemoveListener(_onSubmitHandler);
        inputField.onSubmit.AddListener(_onSubmitHandler);
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        if (inputField != null && _onSubmitHandler != null)
            inputField.onSubmit.RemoveListener(_onSubmitHandler);

        _saveCts?.Cancel();
    }

    public static void Show(long revision, string currentMessage, SVNManager manager, Action<string> onEdited)
    {
        if (Instance == null)
        {
            SVNLogBridge.LogError("EditMessagePopup not in scene!");
            return;
        }

        Instance.currentRevision = revision;
        Instance.currentManager = manager;
        Instance.onSuccess = onEdited;
        Instance.inputField.text = currentMessage;
        Instance.titleText.text = $"Edit message for r{revision} and press Enter";
        Instance.gameObject.SetActive(true);

        Instance.inputField.Select();
        Instance.inputField.ActivateInputField();
    }

    private async void SaveAndClose()
    {
        // === FIX K2: pojedynczość operacji.
        if (Interlocked.Exchange(ref _saveInProgress, 1) == 1) return;

        // === FIX (NRE): Show() może nie zostać wywołane (ktoś otworzył popup
        // z innego miejsca) — bez tego GetRepoUrlAsync rzuca NRE w głębi.
        if (currentManager == null)
        {
            SVNLogBridge.LogError("[EditMessagePopup] Manager not set — cannot save.");
            Interlocked.Exchange(ref _saveInProgress, 0);
            return;
        }

        var cts = new CancellationTokenSource();
        _saveCts = cts;
        var token = cts.Token;

        bool succeeded = false;
        string newMessage = null;
        Action<string> callbackToFire = null;

        try
        {
            newMessage = inputField.text.Trim();
            if (string.IsNullOrEmpty(newMessage)) return;

            // === FIX K1: sanityzacja — control chars (poza tabem) odrzucone;
            // reszta idzie bezpiecznie przez -F (plik), nie przez argument.
            foreach (char c in newMessage)
            {
                if (char.IsControl(c) && c != '\t')
                {
                    SVNLogBridge.LogError("Message contains illegal control characters.");
                    return;
                }
            }

            string repoUrl = await SvnRunner.GetRepoUrlAsync(currentManager.WorkingDir, token);
            token.ThrowIfCancellationRequested();

            // === FIX K1: treść przez plik (-F) — cudzysłowy/newline w wiadomości
            // są bezpieczne; wcześniej '{newMessage}' w komendzie rozrywało
            // argument przy '"' i newline (command injection / parsowanie śmieci).
            string msgFile = Path.Combine(Path.GetTempPath(), $"svn_propmsg_{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(msgFile, newMessage, new UTF8Encoding(false), token);

                string args = $"propset --revprop -r {currentRevision} svn:log -F \"{msgFile}\" \"{repoUrl}\"";
                await SvnRunner.RunAsync(args, currentManager.WorkingDir, true, token);
            }
            finally
            {
                try { if (File.Exists(msgFile)) File.Delete(msgFile); } catch { }
            }

            // Brak wyjątku = sukces.
            succeeded = true;
            callbackToFire = onSuccess;
        }
        catch (OperationCanceledException)
        {
            // zamknięto popup/obiekt w trakcie — cicho.
        }
        catch (Exception ex)
        {
            // === FIX Ś2: czytelny komunikat dla najczęstszej przyczyny porażki.
            string msg = ex.Message ?? "";
            if (msg.Contains("E175008") || msg.Contains("pre-revprop-change", StringComparison.OrdinalIgnoreCase))
                SVNLogBridge.LogError("Server does not allow editing revision properties (pre-revprop-change hook). Ask your SVN admin.");
            else
                SVNLogBridge.LogError("Failed to edit log: " + msg);
        }
        finally
        {
            Interlocked.Exchange(ref _saveInProgress, 0);
            _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
        }

        if (!succeeded) return;

        // === FIX (callback w try): callback użytkownika POZA try/catch własnej
        // operacji. Wyjątek z callbacku (a to kod wołającego) NIE może być
        // mylony z błędem propset → osobny try/catch i osobny komunikat.
        try
        {
            callbackToFire?.Invoke(newMessage);
        }
        catch (Exception cbEx)
        {
            SVNLogBridge.LogError($"[EditMessagePopup] onSuccess threw: {cbEx.Message}");
        }

        CloseInternal();
    }

    public void Cancel()
    {
        // === FIX K2: cancel + czyszczenie callbacku (stara operacja nie odpali
        // cudzego onSuccess po zamknięciu).
        _saveCts?.Cancel();
        onSuccess = null;
        CloseInternal();
    }

    private void CloseInternal()
    {
        gameObject.SetActive(false);
    }
}