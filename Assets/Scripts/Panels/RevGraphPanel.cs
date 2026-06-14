using SVN.Core;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;

public class RevGraphPanel : MonoBehaviour
{
    public TMPro.TMP_InputField branchFilterInput;

    private SVNUI svnUI;
    private SVNManager svnManager;
    private SVNRevGraph graphModule;
    private Coroutine _debounceCoroutine;
    private bool _graphLoaded = false;

    private async void OnEnable()
    {
        if (svnManager == null) svnManager = SVNManager.Instance;
        if (svnManager == null) return;
        if (string.IsNullOrEmpty(svnManager.WorkingDir)) return;

        if (!_graphLoaded)
        {
            _graphLoaded = true;
            await RefreshGraph();
        }
    }

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;

        graphModule = svnManager.GetModule<SVNRevGraph>();

        branchFilterInput.onValueChanged.AddListener(OnFilterChanged);
    }

    public void OnFilterChanged(string filterText)
    {
        if (_debounceCoroutine != null)
            StopCoroutine(_debounceCoroutine);
        _debounceCoroutine = StartCoroutine(ApplyFilterAfterDelay(filterText));
    }

    private IEnumerator ApplyFilterAfterDelay(string filterText)
    {
        yield return new WaitForSeconds(0.3f);
        ApplyFilter(filterText);
    }

    private void ApplyFilter(string filterText)
    {
        if (graphModule == null) return;

        string filterLower = filterText.ToLower();
        bool hasFilter = !string.IsNullOrEmpty(filterText);

        foreach (var itemGo in graphModule.InstantiatedItems)
        {
            if (itemGo == null) continue;

            SVNGraphItem item = itemGo.GetComponent<SVNGraphItem>();
            if (item == null) continue;

            bool matches = !hasFilter ||
                           item.GetBranchName().ToLower().Contains(filterLower) ||
                           item.GetMessage().ToLower().Contains(filterLower) ||
                           item.GetAuthor().ToLower().Contains(filterLower) ||
                           item.GetRevision().ToString().Contains(filterLower);

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

    public async Task RefreshGraph()
    {
        if (string.IsNullOrEmpty(svnManager.WorkingDir))
        {
            SVNLogBridge.LogLine("<color=red>Please select a project first.</color>");
            return;
        }

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
        string xmlOutput = await SvnRunner.RunAsync(
            "log --xml --verbose ^/",
            svnManager.WorkingDir);

        var nodes = new List<SVNRevisionNode>();

        if (string.IsNullOrEmpty(xmlOutput))
            return nodes;

        using (var stringReader = new System.IO.StringReader(xmlOutput))
        using (var reader = XmlReader.Create(stringReader,
            new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true
            }))
        {
            SVNRevisionNode currentNode = null;

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                switch (reader.Name)
                {
                    case "logentry":
                        {
                            currentNode = new SVNRevisionNode();

                            if (reader.GetAttribute("revision") is string rev &&
                                long.TryParse(rev, out long revision))
                            {
                                currentNode.Revision = revision;
                            }

                            nodes.Add(currentNode);
                            break;
                        }

                    case "author":
                        {
                            if (currentNode != null)
                                currentNode.Author = reader.ReadElementContentAsString();
                            break;
                        }

                    case "date":
                        {
                            if (currentNode != null)
                                currentNode.Date = reader.ReadElementContentAsString();
                            break;
                        }

                    case "msg":
                        {
                            if (currentNode != null)
                                currentNode.Message = reader.ReadElementContentAsString();
                            break;
                        }

                    case "path":
                        {
                            if (currentNode == null)
                                break;

                            string action =
                                reader.GetAttribute("action") ?? "";

                            string propMods =
                                reader.GetAttribute("prop-mods") ?? "";

                            string filePath =
                                reader.ReadElementContentAsString();

                            currentNode.ChangedPaths.Add(
                                $"{action} {filePath}");

                            if (propMods == "true" &&
                                (filePath == "/trunk" ||
                                 filePath.StartsWith("/branches/") ||
                                 filePath.StartsWith("/tags/")))
                            {
                                currentNode.HasMergeInfoChange = true;
                            }

                            break;
                        }
                }
            }
        }

        return nodes;
    }

    public async void Button_RefreshGraph() 
    {
        _graphLoaded = true;
        await RefreshGraph(); 
    }
    public void Button_CollpaseAll() => svnManager.GetModule<SVNRevGraph>().CollapseAll();
    public void Button_ExportHistoryToTxt() => svnManager.GetModule<SVNRevGraph>().ExportHistoryToTxt();
}