using UnityEngine;
using TMPro;

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

    public void Setup(string statusTag, string path, string color, long rev, SVN.Core.SVNManager mgr)
    {
        this.fullPath = path?.Trim() ?? "";
        this.revision = rev;
        this.svnManager = mgr;

        // === FIX K2: guard jak w pozostałych itemach UI.
        if (fileText == null)
        {
            Debug.LogError("[SVNFileItem] fileText is not assigned in Inspector!", this);
            return;
        }

        fileText.text = $"<color={color}><b>{statusTag}</b></color> {fullPath}";
    }

    public void Button_OpenFile()
    {
        OpenFile();
    }

    public async void OpenFile()
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