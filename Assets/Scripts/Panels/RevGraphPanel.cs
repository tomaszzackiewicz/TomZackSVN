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

        // Przygotowujemy filtr do porównań (małe litery)
        string filterLower = filterText.ToLower();
        bool hasFilter = !string.IsNullOrEmpty(filterText);

        foreach (var itemGo in graphModule.InstantiatedItems)
        {
            if (itemGo == null) continue;

            SVNGraphItem item = itemGo.GetComponent<SVNGraphItem>();
            if (item == null) continue;

            // 1. Sprawdzamy podstawowe pola (branch, wiadomość, autor, rewizja)
            bool matches = !hasFilter ||
                           item.GetBranchName().ToLower().Contains(filterLower) ||
                           item.GetMessage().ToLower().Contains(filterLower) ||
                           item.GetAuthor().ToLower().Contains(filterLower) ||
                           item.GetRevision().ToString().Contains(filterLower);

            // 2. Jeśli brak dopasowania w nagłówku, przeszukujemy listę plików
            if (!matches && hasFilter)
            {
                var paths = item.GetChangedPaths();
                if (paths != null)
                {
                    foreach (string path in paths)
                    {
                        if (path.ToLower().Contains(filterLower))
                        {
                            matches = true;
                            break;
                        }
                    }
                }
            }

            itemGo.SetActive(matches);

            if (matches)
            {
                item.ApplyHighlight(hasFilter ? filterText : null);
            }
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
                SVNLogBridge.LogError("Module SVNRevGraph not found in SVNManager!");
            }
        }
        catch (System.Exception ex)
        {
            SVNLogBridge.LogError($"[SVN Graph Error] {ex.Message}");
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
                    string action = p.Attributes["action"]?.Value ?? "";
                    node.ChangedPaths.Add($"{action} {p.InnerText}");
                }
            }

            nodes.Add(node);
        }
        return nodes;
    }

    public void Button_CollpaseAll() => svnManager.GetModule<SVNRevGraph>().CollapseAll();
    public void Button_ExportHistoryToTxt() => svnManager.GetModule<SVNRevGraph>().ExportHistoryToTxt();
}