using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;
using SVN.Core;

public class RevGraphPanel : MonoBehaviour
{
    public TMPro.TMP_InputField branchFilterInput;

    private SVNUI svnUI;
    private SVNManager svnManager;
    private SVNRevGraph graphModule;

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        graphModule = svnManager.GetModule<SVNRevGraph>();

        branchFilterInput.onValueChanged.AddListener(OnFilterChanged);
    }

    public void OnFilterChanged(string filterText)
    {
        if (graphModule == null) return;

        filterText = filterText.ToLower();

        bool hasFilter = !string.IsNullOrEmpty(filterText);

        foreach (var itemGo in graphModule.InstantiatedItems)
        {
            if (itemGo == null) continue;

            SVNGraphItem item = itemGo.GetComponent<SVNGraphItem>();
            if (item == null) continue;

            string branch = item.GetBranchName().ToLower();
            string message = item.GetMessage().ToLower();
            string author = item.GetAuthor().ToLower();
            string revision = item.GetRevision().ToString();

            bool matches = !hasFilter ||
                           branch.Contains(filterText) ||
                           message.Contains(filterText) ||
                           author.Contains(filterText) ||
                           revision.Contains(filterText);

            itemGo.SetActive(matches);
        }
    }
    public async void Button_RefreshGraph()
    {
        SVNLogBridge.LogLine("<b>[SVN]</b> Fetching revision history...");

        try
        {
            List<SVNRevisionNode> nodes = await FetchLogEntries();

            var graphModule = svnManager.GetModule<SVNRevGraph>();
            if (graphModule != null)
            {
                graphModule.RenderGraph(nodes);
                SVNLogBridge.LogLine("<color=green>Graph updated successfully.</color>");
            }
            else
            {
                Debug.LogError("Module SVNRevGraph not found in SVNManager!");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[SVN Graph Error] {ex.Message}");
            SVNLogBridge.LogLine($"<color=red>Error fetching graph:</color> {ex.Message}");
        }
    }

    private async Task<List<SVNRevisionNode>> FetchLogEntries()
    {
        string xmlOutput = await SvnRunner.RunAsync("log --xml --verbose ^/", svnManager.WorkingDir);

        List<SVNRevisionNode> nodes = new List<SVNRevisionNode>();
        if (string.IsNullOrEmpty(xmlOutput)) return nodes;

        XmlDocument doc = new XmlDocument();
        doc.LoadXml(xmlOutput);
        XmlNodeList logEntries = doc.SelectNodes("//logentry");

        foreach (XmlNode entry in logEntries)
        {
            SVNRevisionNode node = new SVNRevisionNode();

            node.Revision = long.Parse(entry.Attributes["revision"].Value);
            node.Author = entry.SelectSingleNode("author")?.InnerText ?? "n/a";
            node.Date = entry.SelectSingleNode("date")?.InnerText ?? "";
            node.Message = entry.SelectSingleNode("msg")?.InnerText ?? "";

            XmlNodeList pathNodes = entry.SelectNodes("paths/path");
            if (pathNodes != null)
            {
                foreach (XmlNode p in pathNodes)
                {
                    // KEY FIX: Get the "action" attribute (A, M, or D)
                    string action = p.Attributes["action"]?.Value ?? "";

                    // Add the action character to the start of the string
                    // Now StartsWith("A") etc. will work in SVNGraphItem
                    node.ChangedPaths.Add($"{action} {p.InnerText}");
                }
            }

            nodes.Add(node);
        }
        return nodes;
    }

    public void Button_CollpaseAll() => svnManager.GetModule<SVNRevGraph>().CollapseAll();
    public void Button_ExpandAll() => svnManager.GetModule<SVNRevGraph>().ExpandAll();
}