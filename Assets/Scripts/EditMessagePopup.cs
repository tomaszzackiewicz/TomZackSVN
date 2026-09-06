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

    // === FIX K2: guard operacji (double-Enter = dwa równoległe propsety
    // — rewrite race na repo) + delayed-dispose CTS.
    private int _saveInProgress;
    private CancellationTokenSource _saveCts;

    private void Awake()
    {
        Instance = this;
        gameObject.SetActive(false);
    }

    private void Start()
    {
        inputField.onSubmit.AddListener(_ => SaveAndClose());
    }

    private void OnDestroy()
    {
        // === FIX Ś1: czyszczenie singletonu.
        if (Instance == this)
            Instance = null;

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

        var cts = new CancellationTokenSource();
        _saveCts = cts;
        var token = cts.Token;

        try
        {
            string newMessage = inputField.text.Trim();
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

            // Brak wyjątku = sukces (RunAsync ma throwOnError=true).
            onSuccess?.Invoke(newMessage);
            CloseInternal();
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