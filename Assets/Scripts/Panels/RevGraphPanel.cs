using SVN.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
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
    private CancellationTokenSource _loadCts;

    private async void OnEnable()
    {
        if (svnManager == null) svnManager = SVNManager.Instance;
        if (svnManager == null) return;
        if (string.IsNullOrEmpty(svnManager.WorkingDir)) return;

        if (!_graphLoaded)
        {
            _graphLoaded = true;
            SVNLogBridge.LogLine("<color=yellow>[Graph]</color> Loading revision history...");
            await LoadGraphAsync();
        }
    }

    private void Start()
    {
        svnUI = SVNUI.Instance;
        svnManager = SVNManager.Instance;
        graphModule = svnManager.GetModule<SVNRevGraph>();

        branchFilterInput.onValueChanged.AddListener(OnFilterChanged);
    }

    private void OnDestroy()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
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

        var items = graphModule.InstantiatedItems;
        if (items == null || items.Count == 0)
        {
            SVNLogBridge.LogLine("<color=yellow>[Graph Filter]</color> Graph is not yet loaded. Please wait for it to finish.");
            return;
        }

        string filterLower = filterText.ToLower().Trim();
        bool hasFilter = !string.IsNullOrEmpty(filterLower);
        int matchedCount = 0;
        int totalCount = 0;

        foreach (var itemGo in items)
        {
            if (itemGo == null) continue;

            SVNGraphItem item = itemGo.GetComponent<SVNGraphItem>();
            if (item == null) continue;

            totalCount++;

            bool matches = !hasFilter;

            if (hasFilter)
            {
                matches =
                    item.GetBranchName().ToLower().Contains(filterLower) ||
                    item.GetMessage().ToLower().Contains(filterLower) ||
                    item.GetAuthor().ToLower().Contains(filterLower) ||
                    item.GetRevision().ToString().Contains(filterLower);

                if (!matches)
                {
                    var paths = item.GetChangedPaths();
                    if (paths != null)
                    {
                        foreach (string fullPath in paths)
                        {
                            string filePath = fullPath.Length > 2 ? fullPath.Substring(2).Trim() : fullPath;
                            if (filePath.ToLower().Contains(filterLower))
                            {
                                matches = true;
                                break;
                            }

                            string fileName = System.IO.Path.GetFileName(filePath);
                            if (!string.IsNullOrEmpty(fileName) && fileName.ToLower().Contains(filterLower))
                            {
                                matches = true;
                                break;
                            }
                        }
                    }
                }
            }

            itemGo.SetActive(matches);

            if (matches)
            {
                matchedCount++;
                if (hasFilter)
                    item.ApplyHighlight(filterText);
                else
                    item.ApplyHighlight(null);
            }
        }

        SVNLogBridge.LogLine(
            $"<color=grey>[Graph Filter]</color> Processed {totalCount} revisions. " +
            $"Found {matchedCount} matching \"{filterText}\".");
    }

    public async Task LoadGraphAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        try
        {
            if (string.IsNullOrEmpty(svnManager.WorkingDir))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Please select a project first.</color>");
                return;
            }

            List<SVNRevisionNode> nodes = await FetchLogEntries(token);

            if (token.IsCancellationRequested)
                return;

            var graphModule = svnManager.GetModule<SVNRevGraph>();
            if (graphModule != null)
            {
                graphModule.RenderGraph(nodes);
                SVNLogBridge.LogLine("<color=green>Graph updated successfully.</color>");
                
                if (branchFilterInput != null && !string.IsNullOrEmpty(branchFilterInput.text))
                    ApplyFilter(branchFilterInput.text);
            }
            else
            {
                SVNLogBridge.LogError("Module SVNRevGraph not found in SVNManager!");
            }
        }
        catch (OperationCanceledException)
        {
            SVNLogBridge.LogLine("<color=yellow>[Graph] Loading cancelled.</color>");
        }
        catch (System.Exception ex)
        {
            SVNLogBridge.LogError($"[SVN Graph Error] {ex.Message}");
            SVNLogBridge.LogLine($"<color=#FFAA00>Error fetching graph:</color> {ex.Message}");
        }
    }

    public async void Button_RefreshGraph()
    {
        _graphLoaded = true;
        SVNLogBridge.LogLine("<color=yellow>[Graph]</color> Refreshing graph...");
        await LoadGraphAsync();
    }

    public void Button_CollpaseAll() => graphModule?.CollapseAll();
    public void Button_ExportHistoryToTxt() => graphModule?.ExportHistoryToTxt();

    private async Task<List<SVNRevisionNode>> FetchLogEntries(CancellationToken token = default)
    {
        string xmlOutput = await SvnRunner.RunAsync("log --xml --verbose ^/", svnManager.WorkingDir, token: token);
        var nodes = new List<SVNRevisionNode>();

        if (string.IsNullOrEmpty(xmlOutput))
            return nodes;

        using (var stringReader = new System.IO.StringReader(xmlOutput))
        using (var reader = XmlReader.Create(stringReader,
            new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true }))
        {
            SVNRevisionNode currentNode = null;

            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();

                if (reader.NodeType != XmlNodeType.Element) continue;

                switch (reader.Name)
                {
                    case "logentry":
                        currentNode = new SVNRevisionNode();
                        if (reader.GetAttribute("revision") is string rev && long.TryParse(rev, out long revision))
                            currentNode.Revision = revision;
                        nodes.Add(currentNode);
                        break;

                    case "author":
                        if (currentNode != null) currentNode.Author = reader.ReadElementContentAsString();
                        break;

                    case "date":
                        if (currentNode != null) currentNode.Date = reader.ReadElementContentAsString();
                        break;

                    case "msg":
                        if (currentNode != null) currentNode.Message = reader.ReadElementContentAsString();
                        break;

                    case "path":
                        if (currentNode == null) break;
                        string action = reader.GetAttribute("action") ?? "";
                        string propMods = reader.GetAttribute("prop-mods") ?? "";
                        string filePath = reader.ReadElementContentAsString();
                        currentNode.ChangedPaths.Add($"{action} {filePath}");
                        if (propMods == "true" &&
                            (filePath == "/trunk" || filePath.StartsWith("/branches/") || filePath.StartsWith("/tags/")))
                        {
                            currentNode.HasMergeInfoChange = true;
                        }
                        break;
                }
            }
        }

        return nodes;
    }
}