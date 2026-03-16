using UnityEngine;
using TMPro;

public class SVNFileItem : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI fileText;

    private string fullPath;
    private long revision;
    private SVN.Core.SVNManager svnManager;

    private float lastClickTime = 0f;
    private const float doubleClickThreshold = 0.3f;

    public void Setup(string statusTag, string path, string color, long rev, SVN.Core.SVNManager mgr)
    {
        this.fullPath = path.Trim();
        this.revision = rev;
        this.svnManager = mgr;

        fileText.text = $"<color={color}><b>{statusTag}</b></color> {fullPath}";
    }

    public void Button_OpenFile()
    {
        float timeSinceLastClick = Time.time - lastClickTime;

        if (timeSinceLastClick <= doubleClickThreshold)
        {
            // DOUBLE CLICK
            if (svnManager != null)
            {
                svnManager.CatAndOpenFile(fullPath, revision);
            }

            lastClickTime = 0f;
        }
        else
        {
            // SINGLE CLICK
            lastClickTime = Time.time;
        }
    }
}