using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class SVNFileItem : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI fileText;
    private string fullPath;
    private long revision;
    private SVN.Core.SVNManager svnManager;

    public void Setup(string statusTag, string path, string color, long rev, SVN.Core.SVNManager mgr)
    {
        this.fullPath = path.Trim();
        this.revision = rev;
        this.svnManager = mgr;

        fileText.text = $"<color={color}><b>{statusTag}</b></color> {fullPath}";
    }

    public void Button_OpenFile()
    {
        if (svnManager != null)
        {
            svnManager.CatAndOpenFile(fullPath, revision);
        }
    }
}