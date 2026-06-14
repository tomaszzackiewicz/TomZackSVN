using TMPro;
using UnityEngine;
using SVN.Core;

public class EditMessagePopup : MonoBehaviour
{
    public static EditMessagePopup Instance;

    public TMP_InputField inputField;
    public TextMeshProUGUI titleText;

    private long currentRevision;
    private SVNManager currentManager;
    private System.Action<string> onSuccess;

    private void Awake()
    {
        Instance = this;
        gameObject.SetActive(false);
    }

    public static void Show(long revision, string currentMessage, SVNManager manager, System.Action<string> onEdited)
    {
        if (Instance == null)
        {
            Debug.LogError("EditMessagePopup not in scene!");
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

    private void Start()
    {
        inputField.onSubmit.AddListener((string text) => SaveAndClose());
    }

    private async void SaveAndClose()
    {
        string newMessage = inputField.text.Trim();
        if (string.IsNullOrEmpty(newMessage)) return;

        string repoUrl = await SvnRunner.GetRepoUrlAsync(currentManager.WorkingDir);
        string args = $"propset --revprop -r {currentRevision} svn:log \"{newMessage}\" \"{repoUrl}\"";
        string output = await SvnRunner.RunAsync(args, currentManager.WorkingDir);

        if (!string.IsNullOrEmpty(output) && output.Contains("property 'svn:log' set"))
        {
            onSuccess?.Invoke(newMessage);
        }
        else
        {
            SVNLogBridge.LogError("Failed to edit log: " + output);
        }
    }
}