using UnityEngine;
using TMPro;
using SVN.Core;
using System.Collections.Generic;
using UnityEngine.UI;

public class SVNGraphItem : MonoBehaviour
{
    [Header("UI References")]
    public TextMeshProUGUI graphVisualText;
    public TextMeshProUGUI revisionText;
    public TextMeshProUGUI authorText;
    public TextMeshProUGUI messageText;
    public TextMeshProUGUI branchNameText;
    public TextMeshProUGUI dateText;

    [Header("Scrollable File List")]
    public GameObject filesContainer;
    public Transform scrollContent;
    public TextMeshProUGUI summaryText;
    public GameObject fileButtonPrefab;

    private List<string> changedPaths = new List<string>();
    private long revisionNumber;
    private bool isExpanded = false;
    private SVNManager svnManager;

    private string currentAuthor;
    private string currentBranchName;
    private string currentMessage;

    public string GetBranchName() => currentBranchName;
    public string GetMessage() => currentMessage;
    public string GetAuthor() => currentAuthor;
    public long GetRevision() => revisionNumber;
    public string GetDate() => dateText != null ? dateText.text : "Unknown Date";


    public void Setup(string visual, SVNRevisionNode node, string branchName, string hexColor, SVNManager mgr)
    {
        this.svnManager = mgr;
        this.revisionNumber = node.Revision;
        this.currentBranchName = branchName;
        this.currentMessage = node.Message;
        this.changedPaths = node.ChangedPaths;

        graphVisualText.text = visual;
        revisionText.text = $"<color=black><b>r{node.Revision}</b></color>";
        authorText.text = node.Author;
        currentAuthor = node.Author;

        if (branchNameText != null)
            branchNameText.text = $"<color={hexColor}>[{branchName}]</color>";

        string cleanMsg = node.Message;
        int idx = cleanMsg.LastIndexOf(" /");
        if (idx != -1) cleanMsg = cleanMsg.Substring(0, idx).Trim();
        messageText.text = cleanMsg;

        if (dateText != null)
        {
            if (System.DateTime.TryParse(node.Date, out System.DateTime dt))
                dateText.text = dt.ToString("yyyy-MM-dd HH:mm");
            else
                dateText.text = node.Date;
        }

        filesContainer.SetActive(false);
    }

    public void OnItemClicked()
    {
        isExpanded = !isExpanded;

        if (isExpanded)
        {
            BuildFileButtons();
        }
        else
        {
            ClearFiles();
        }

        filesContainer.SetActive(isExpanded);
        RefreshLayout();
    }

    private void BuildFileButtons()
    {
        ClearFiles();

        if (changedPaths == null || changedPaths.Count == 0)
        {
            summaryText.text = "<color=#BBBBBB><i>No file data available.</i></color>";
            return;
        }

        int added = 0, modified = 0, deleted = 0;

        foreach (var path in changedPaths)
        {
            string color = "#FFFFFF";
            if (path.StartsWith("A")) { color = "#55FF55"; added++; }
            else if (path.StartsWith("M")) { color = "#FFFF55"; modified++; }
            else if (path.StartsWith("D")) { color = "#FF5555"; deleted++; }

            GameObject go = Instantiate(fileButtonPrefab, scrollContent);
            SVNFileItem script = go.GetComponent<SVNFileItem>();
            if (script != null)
            {
                script.Setup($"[{path[0]}]", path.Substring(1).Trim(), color, revisionNumber, svnManager);
            }
        }

        summaryText.text = $"<size=85%><b>Summary: </b><color=#55FF55>{added}A</color> <color=#FFFF55>{modified}M</color> <color=#FF5555>{deleted}D</color></size>";
    }

    private void ClearFiles()
    {
        foreach (Transform child in scrollContent) Destroy(child.gameObject);
    }

    private void RefreshLayout()
    {
        if (transform.parent != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(transform.parent.GetComponent<RectTransform>());
    }

    public void SetExpanded(bool state)
    {
        if (state == false && isExpanded)
        {
            isExpanded = false;
            ClearFiles();
            if (filesContainer != null)
                filesContainer.SetActive(false);

            RefreshLayout();
        }
    }

    public List<string> GetChangedPaths()
    {
        return changedPaths;
    }
}