using UnityEngine;
using TMPro;
using System.Threading.Tasks;

public class SVNFileItem : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI fileText;

    private string fullPath;
    private long revision;
    private SVN.Core.SVNManager svnManager;

    // === FIX K1: -10f zamiast 0 — Time.time≈0 na starcie + lastClickTime=0
    // czyniło PIERWSZY klik (w pierwszych 0.3 s play) pełnym "double-clickiem"
    // (akcja bez potwierdzenia). Wzorzec z SVNRevert/SVNMerge.
    private float lastClickTime = -10f;
    private const float doubleClickThreshold = 0.3f;

    public void Setup(string statusTag, string path, string color, long rev,
                   SVN.Core.SVNManager mgr, string displayPath = null)
    {
        this.fullPath = path?.Trim() ?? "";    // ← CZYSTA ścieżka dla svn cat
        this.revision = rev;
        this.svnManager = mgr;

        if (fileText == null) { /* ... */ return; }

        string display = string.IsNullOrEmpty(displayPath) ? fullPath : displayPath;
        fileText.text = $"<color={color}><b>{statusTag}</b></color> {display}";
    }

    public void Button_OpenFile()
    {
        // === FIX (async void): przejście na Task + fire-and-forget. Wcześniej
        // async void OpenFile() — każde rozszerzenie o await przed try/catch
        // mogło uciszyć wyjątek (UnobservedTaskException pattern).
        _ = OpenFileAsync();
    }

    public void OpenFile() => Button_OpenFile();

    private async Task OpenFileAsync()
    {
        float timeSinceLastClick = Time.time - lastClickTime;

        if (timeSinceLastClick <= doubleClickThreshold)
        {
            if (svnManager != null)
            {
                try
                {
                    await svnManager.CatAndOpenFile(fullPath, revision);
                }
                catch (System.Exception ex)
                {
                    SVN.Core.SVNLogBridge.LogError($"[SVNFileItem] Open failed: {ex.Message}");
                }
            }

            lastClickTime = -10f;
        }
        else
        {
            lastClickTime = Time.time;
        }
    }
}