using SVN.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;

public class RevGraphPanel : MonoBehaviour
{
    [SerializeField] private TMPro.TMP_InputField branchFilterInput;

    private SVNUI _svnUI;
    private SVNManager _svnManager;
    private SVNRevGraph _graphModule;
    private Coroutine _debounceCoroutine;
    private bool _graphLoaded;
    private CancellationTokenSource _loadCts;
    private string _lastWorkingDir;
    private readonly HashSet<SVNGraphItem> _selectedItems = new();

    private void Awake()
    {
        _svnManager = SVNManager.Instance;
        _svnUI = SVNUI.Instance;
    }

    private void Start()
    {
        if (_svnManager != null)
            _graphModule = _svnManager.GetModule<SVNRevGraph>();

        if (branchFilterInput != null)
            branchFilterInput.onValueChanged.AddListener(OnFilterChanged);
    }

    private async void OnEnable()
    {
        if (!CanLoadGraph())
        {
            if (_svnManager != null && _svnManager.IsProcessing)
            {
                SVNLogBridge.LogLine("<color=yellow>[Graph]</color> Waiting for project initialization...");

                while (_svnManager.IsProcessing && gameObject.activeInHierarchy)
                {
                    await Task.Yield();
                }
            }

            if (!CanLoadGraph())
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Please select a project first.</color>");
                return;
            }
        }

        if (HasWorkingDirChanged())
        {
            _graphLoaded = false;
            _lastWorkingDir = _svnManager.WorkingDir;
        }

        if (!_graphLoaded)
        {
            _graphLoaded = true;
            SVNLogBridge.LogLine("<color=yellow>[Graph]</color> Loading revision history...");
            await LoadGraphAsync();
        }
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        _debounceCoroutine = null;
        CancelLoading();
    }

    private void OnDestroy()
    {
        StopAllCoroutines();
        _debounceCoroutine = null;
        CancelLoading();

        if (branchFilterInput != null)
            branchFilterInput.onValueChanged.RemoveListener(OnFilterChanged);
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
        if (_graphModule == null) return;

        var items = _graphModule.InstantiatedItems;
        if (items == null || items.Count == 0)
        {
            SVNLogBridge.LogLine("<color=yellow>[Graph Filter]</color> Graph is not yet loaded. Please wait for it to finish.");
            return;
        }

        string filterLower = filterText.Trim();
        bool hasFilter = !string.IsNullOrEmpty(filterLower);
        int matchedCount = 0;
        int totalCount = 0;

        foreach (var itemGo in items)
        {
            if (itemGo == null) continue;

            if (!itemGo.TryGetComponent<SVNGraphItem>(out var item))
                continue;

            totalCount++;

            bool matches = !hasFilter || MatchesFilter(item, filterLower);

            itemGo.SetActive(matches);

            if (matches)
            {
                matchedCount++;
                item.ApplyHighlight(hasFilter ? filterText : null);
            }
        }

        SVNLogBridge.LogLine(
            $"<color=grey>[Graph Filter]</color> Processed {totalCount} revisions. " +
            $"Found {matchedCount} matching \"{filterText}\".");
    }

    private static bool MatchesFilter(SVNGraphItem item, string filterLower)
    {
        if (item.GetBranchName().IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (item.GetMessage().IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (item.GetAuthor().IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (item.GetRevision().ToString().Contains(filterLower))
            return true;

        var paths = item.GetChangedPaths();
        if (paths == null)
            return false;

        foreach (string fullPath in paths)
        {
            string filePath = fullPath.Length > 2 ? fullPath.Substring(2).Trim() : fullPath;

            if (filePath.IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string fileName = Path.GetFileName(filePath);
            if (!string.IsNullOrEmpty(fileName) &&
                fileName.IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public async Task LoadGraphAsync()
    {
        CancelLoading();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        try
        {
            if (!CanLoadGraph())
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Please select a project first.</color>");
                return;
            }

            List<SVNRevisionNode> nodes = await FetchLogEntriesAsync(token);

            if (token.IsCancellationRequested)
                return;

            if (_graphModule != null)
            {
                _graphModule.RenderGraph(nodes);
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
        catch (Exception ex)
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

    public void Button_CollapseAll() => _graphModule?.CollapseAll();
    public void Button_ExpandAll() => _graphModule?.ExpandAll();
    public void Button_ExportHistoryToTxt() => _graphModule?.ExportHistoryToTxt();

    private bool CanLoadGraph()
    {
        if (_svnManager == null)
            _svnManager = SVNManager.Instance;

        return _svnManager != null && !string.IsNullOrEmpty(_svnManager.WorkingDir);
    }

    private bool HasWorkingDirChanged()
    {
        return _svnManager != null && _svnManager.WorkingDir != _lastWorkingDir;
    }

    private void CancelLoading()
    {
        if (_loadCts != null)
        {
            _loadCts.Cancel();
            _loadCts.Dispose();
            _loadCts = null;
        }
    }

    private async Task<List<SVNRevisionNode>> FetchLogEntriesAsync(CancellationToken token = default)
    {
        Debug.Log("===== FetchLogEntriesAsync STARTED =====");

        string xmlOutput = await SvnRunner.RunAsync("log --xml --verbose ^/", _svnManager.WorkingDir, token: token);

        Debug.Log($"===== XML length: {xmlOutput?.Length ?? -1} =====");

        if (!string.IsNullOrEmpty(xmlOutput))
        {
            int len = Math.Min(1000, xmlOutput.Length);
            Debug.Log($"[XML START]\n{xmlOutput.Substring(0, len)}\n[XML END]");
        }
        else
        {
            Debug.LogError("===== XML is EMPTY or NULL =====");
        }

        if (string.IsNullOrEmpty(xmlOutput))
            return new List<SVNRevisionNode>();

        try
        {
            return await Task.Run(() =>
            {
                var nodes = new List<SVNRevisionNode>();
                ParseLogXml(xmlOutput, nodes, token);
                return nodes;
            }, token);
        }
        catch (XmlException ex)
        {
            SVNLogBridge.LogErrorToOutput($"[SVN] Failed to parse log XML: {ex.Message}");
            return new List<SVNRevisionNode>();
        }
    }

    private static void ParseLogXml(string xmlOutput, List<SVNRevisionNode> nodes, CancellationToken token)
    {
        Debug.Log("===== USING NEW XMLDOCUMENT PARSER =====");

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xmlOutput);

        XmlNodeList logEntries = doc.SelectNodes("//logentry");

        if (logEntries == null)
        {
            Debug.LogError("[ParseLogXml] No logentry nodes found!");
            return;
        }

        int dateCount = 0;

        foreach (XmlNode logentry in logEntries)
        {
            token.ThrowIfCancellationRequested();

            var node = new SVNRevisionNode();

            XmlNode revAttr = logentry.Attributes?["revision"];
            if (revAttr != null && long.TryParse(revAttr.Value, out long rev))
                node.Revision = rev;

            XmlNode authorNode = logentry.SelectSingleNode("author");
            if (authorNode != null)
                node.Author = authorNode.InnerText;

            XmlNode dateNode = logentry.SelectSingleNode("date");
            if (dateNode != null)
            {
                node.Date = dateNode.InnerText;
                dateCount++;
            }

            XmlNode msgNode = logentry.SelectSingleNode("msg");
            if (msgNode != null)
                node.Message = msgNode.InnerText;

            XmlNodeList pathNodes = logentry.SelectNodes("paths/path");
            if (pathNodes != null)
            {
                foreach (XmlNode pathNode in pathNodes)
                {
                    string action = pathNode.Attributes?["action"]?.Value ?? "";
                    string propMods = pathNode.Attributes?["prop-mods"]?.Value ?? "";
                    string filePath = pathNode.InnerText;

                    node.ChangedPaths.Add($"{action} {filePath}");

                    if (propMods == "true" && IsBranchPath(filePath))
                        node.HasMergeInfoChange = true;

                    if (action == "A" || action == "R")
                    {
                        string copyPath = pathNode.Attributes?["copyfrom-path"]?.Value;
                        string copyRevStr = pathNode.Attributes?["copyfrom-rev"]?.Value;

                        if (!string.IsNullOrEmpty(copyPath))
                        {
                            node.CopyFromPath = copyPath;
                            if (long.TryParse(copyRevStr, out long copyRev))
                                node.CopyFromRev = copyRev;
                        }
                    }
                }
            }

            nodes.Add(node);
        }

        Debug.Log($"[ParseLogXml] Parsed {nodes.Count} nodes, {dateCount} dates found");

        for (int i = 0; i < Math.Min(3, nodes.Count); i++)
        {
            Debug.Log($"[ParseLogXml] Node {i}: r{nodes[i].Revision}, Date='{nodes[i].Date}', Author='{nodes[i].Author}'");
        }
    }

    private static bool IsBranchPath(string filePath)
    {
        return filePath == "/trunk" ||
               filePath.StartsWith("/branches/", StringComparison.OrdinalIgnoreCase) ||
               filePath.StartsWith("/tags/", StringComparison.OrdinalIgnoreCase);
    }

    public void ForceRefresh()
    {
        _graphLoaded = false;
        if (gameObject.activeInHierarchy)
            _ = LoadGraphAsync();
    }

    public void OnItemClicked(SVNGraphItem item, bool multi)
    {
        if (item == null) return;

        if (!multi)
        {
            foreach (var s in _selectedItems)
            {
                if (s != null)
                    s.SetSelected(false);
            }
            _selectedItems.Clear();
        }

        if (_selectedItems.Contains(item))
        {
            item.SetSelected(false);
            _selectedItems.Remove(item);
        }
        else
        {
            item.SetSelected(true);
            _selectedItems.Add(item);
        }
    }

    public IReadOnlyCollection<SVNGraphItem> GetSelectedItems()
    {
        _selectedItems.RemoveWhere(x => x == null);
        return _selectedItems;
    }
}