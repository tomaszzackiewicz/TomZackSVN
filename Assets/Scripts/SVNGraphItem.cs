using UnityEngine;
using TMPro;
using SVN.Core;
using System.Collections.Generic;
using System.Text;
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

    [Header("Expandable File List")]
    public GameObject filesContainer;
    public TextMeshProUGUI filesText;

    private List<string> changedPaths = new List<string>();
    private string currentBranchName;
    private string currentMessage;
    private long revisionNumber;
    private bool isExpanded = false;

    public string GetBranchName() => currentBranchName;
    public string GetMessage() => currentMessage;

    public void Setup(string visual, SVNRevisionNode node, string branchName, string hexColor)
    {
        this.revisionNumber = node.Revision;
        graphVisualText.text = visual;
        revisionText.text = $"<color=black><b>r{node.Revision}</b></color>";

        if (branchNameText != null)
        {
            branchNameText.text = $"<color={hexColor}>[{branchName}]</color>";
        }

        authorText.text = node.Author;

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

        this.currentBranchName = branchName;
        this.currentMessage = node.Message;
        this.changedPaths = node.ChangedPaths;

        PrepareFilesText();
        filesContainer.SetActive(false);
    }

    private void PrepareFilesText()
    {
        if (changedPaths == null || changedPaths.Count == 0)
        {
            filesText.text = "<color=#BBBBBB><i>No file data available.</i></color>";
            return;
        }

        int added = 0, modified = 0, deleted = 0, replaced = 0, conflicted = 0;

        foreach (var path in changedPaths)
        {
            if (path.StartsWith("A")) added++;
            else if (path.StartsWith("M")) modified++;
            else if (path.StartsWith("D")) deleted++;
            else if (path.StartsWith("R")) replaced++;
            else if (path.StartsWith("C")) conflicted++;
        }

        StringBuilder sb = new StringBuilder();

        sb.Append("<size=85%><color=#EEEEEE><b>Summary: </b></color>");
        if (added > 0) sb.Append($"<color=#55FF55>{added} Added</color>  ");
        if (modified > 0) sb.Append($"<color=#FFFF55>{modified} Modified</color>  ");
        if (deleted > 0) sb.Append($"<color=#FF5555>{deleted} Deleted</color>  ");
        if (replaced > 0) sb.Append($"<color=#CC88FF>{replaced} Replaced</color>  ");
        if (conflicted > 0) sb.Append($"<color=#FFB347>{conflicted} Conflicted</color>");
        sb.AppendLine("</size>\n");

        foreach (var path in changedPaths)
        {
            string color = "#FFFFFF";
            string statusTag = "";

            if (path.StartsWith("A")) { color = "#55FF55"; statusTag = "[A]"; }
            else if (path.StartsWith("M")) { color = "#FFFF55"; statusTag = "[M]"; }
            else if (path.StartsWith("D")) { color = "#FF5555"; statusTag = "[D]"; }
            else if (path.StartsWith("R")) { color = "#CC88FF"; statusTag = "[R]"; }
            else if (path.StartsWith("C")) { color = "#FFB347"; statusTag = "[C]"; }
            else { statusTag = "[?]"; }

            sb.AppendLine($"<size=90%><color={color}><b>{statusTag}</b> {path.Substring(1).Trim()}</color></size>");
        }

        filesText.text = sb.ToString();
    }

    public void OnItemClicked()
    {
        isExpanded = !isExpanded;
        filesContainer.SetActive(isExpanded);

        LayoutRebuilder.ForceRebuildLayoutImmediate(transform.parent.GetComponent<RectTransform>());
    }

    public void LogChangedFiles()
    {
        if (changedPaths == null || changedPaths.Count == 0)
        {
            Debug.Log("No changed paths data found (missing --verbose flag in logs?)");
            return;
        }

        string report = $"Revision r{revisionNumber} changed {changedPaths.Count} files:\n";
        foreach (var path in changedPaths)
        {
            report += $"- {path}\n";
        }
        Debug.Log(report);
    }

    public void OnClick()
    {
        Debug.Log($"Clicked r{revisionNumber}");
    }
}