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

    private string _cachedXmlOutput;

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
                while (_svnManager.IsProcessing && gameObject.activeInHierarchy) await Task.Yield();
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
            SVNLogBridge.LogLine("<color=yellow>[Graph]</color> Loading revision structure...");
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
        if (branchFilterInput != null) branchFilterInput.onValueChanged.RemoveListener(OnFilterChanged);
    }

    public void OnFilterChanged(string filterText)
    {
        if (_debounceCoroutine != null) StopCoroutine(_debounceCoroutine);
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
            SVNLogBridge.LogLine("<color=yellow>[Graph Filter]</color> Graph is not yet loaded.");
            return;
        }

        string filterLower = filterText.Trim();
        bool hasFilter = !string.IsNullOrEmpty(filterLower);
        int matchedCount = 0, totalCount = 0;

        foreach (var itemGo in items)
        {
            if (itemGo == null || !itemGo.TryGetComponent<SVNGraphItem>(out var item)) continue;
            totalCount++;
            bool matches = !hasFilter || MatchesFilter(item, filterLower);
            itemGo.SetActive(matches);
            if (matches) { matchedCount++; item.ApplyHighlight(hasFilter ? filterText : null); }
        }

        SVNLogBridge.LogLine($"<color=yellow>[Graph Filter]</color> Found {matchedCount} matching \"{filterText}\".");
    }

    private static bool MatchesFilter(SVNGraphItem item, string filterLower)
    {
        if (item.GetBranchName().IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (item.GetMessage().IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (item.GetAuthor().IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (item.GetRevision().ToString().Contains(filterLower)) return true;

        var paths = item.GetChangedPaths();
        if (paths == null) return false;
        foreach (string fullPath in paths)
        {
            string filePath = fullPath.Length > 2 ? fullPath.Substring(2).Trim() : fullPath;
            if (filePath.IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string fileName = Path.GetFileName(filePath);
            if (!string.IsNullOrEmpty(fileName) && fileName.IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0) return true;
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

            List<SVNRevisionNode> nodes = await FetchLogStructureAsync(token);
            if (token.IsCancellationRequested) return;

            if (_graphModule == null)
            {
                SVNLogBridge.LogError("Module SVNRevGraph not found in SVNManager!");
                return;
            }

            GraphData graphData = await Task.Run(() => SVNGraphRenderer.AnalyzeBranches(nodes), token);
            if (token.IsCancellationRequested) return;

            if (graphData == null)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Failed to analyze branches.");
                return;
            }

            _graphModule.RenderGraphWithData(graphData, nodes);

            SVNLogBridge.LogLine("<color=green>Graph structure loaded successfully.</color>");

            if (branchFilterInput != null && !string.IsNullOrEmpty(branchFilterInput.text))
                ApplyFilter(branchFilterInput.text);

            if (!string.IsNullOrEmpty(_cachedXmlOutput))
            {
                StartCoroutine(PopulateFilesInBackgroundRoutine(token));
            }
        }
        catch (OperationCanceledException)
        {
            SVNLogBridge.LogLine("<color=yellow>[Graph] Loading cancelled.</color>");
        }
        catch (Exception ex)
        {
            SVNLogBridge.LogError($"[SVN Graph Error] {ex.Message}");
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
        if (_svnManager == null) _svnManager = SVNManager.Instance;
        return _svnManager != null && !string.IsNullOrEmpty(_svnManager.WorkingDir);
    }

    private bool HasWorkingDirChanged() => _svnManager != null && _svnManager.WorkingDir != _lastWorkingDir;

    private void CancelLoading()
    {
        if (_loadCts != null) { _loadCts.Cancel(); _loadCts.Dispose(); _loadCts = null; }
    }

    private async Task<List<SVNRevisionNode>> FetchLogStructureAsync(CancellationToken token = default)
    {
        string xmlOutput = await SvnRunner.RunAsync("log --xml --verbose ^/", _svnManager.WorkingDir, token: token);
        if (string.IsNullOrEmpty(xmlOutput)) return new List<SVNRevisionNode>();

        _cachedXmlOutput = xmlOutput;

        try
        {
            return await Task.Run(() =>
            {
                var nodes = new List<SVNRevisionNode>();
                ParseLogXmlStream(xmlOutput, nodes, token, fastMode: true);
                return nodes;
            }, token);
        }
        catch (XmlException ex)
        {
            SVNLogBridge.LogErrorToOutput($"[SVN] Failed to parse log XML: {ex.Message}");
            return new List<SVNRevisionNode>();
        }
    }

    private static void ParseLogXmlStream(string xmlOutput, List<SVNRevisionNode> nodes, CancellationToken token, bool fastMode = false)
    {
        using (var reader = XmlReader.Create(new StringReader(xmlOutput)))
        {
            while (reader.Read())
            {
                if (token.IsCancellationRequested) break;

                if (reader.NodeType == XmlNodeType.Element && reader.Name == "logentry")
                {
                    var node = new SVNRevisionNode();
                    if (reader.GetAttribute("revision") != null && long.TryParse(reader.GetAttribute("revision"), out long rev))
                        node.Revision = rev;

                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "logentry") break;
                        if (reader.NodeType != XmlNodeType.Element) continue;

                        switch (reader.Name)
                        {
                            case "author": node.Author = reader.ReadElementContentAsString(); break;
                            case "date": node.Date = reader.ReadElementContentAsString(); break;
                            case "msg": node.Message = reader.ReadElementContentAsString(); break;
                            case "paths":
                                bool foundBranchIndicator = false;

                                while (reader.Read())
                                {
                                    if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "paths") break;
                                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "path")
                                    {
                                        string action = reader.GetAttribute("action") ?? "";
                                        string propMods = reader.GetAttribute("prop-mods") ?? "";
                                        string copyPath = reader.GetAttribute("copyfrom-path");
                                        string copyRevStr = reader.GetAttribute("copyfrom-rev");

                                        string filePath = reader.ReadElementContentAsString();

                                        if (propMods == "true" && IsBranchPath(filePath))
                                            node.HasMergeInfoChange = true;

                                        if (action == "A" || action == "R")
                                        {
                                            node.ChangedPaths.Add($"{action} {filePath}");

                                            if (!string.IsNullOrEmpty(copyPath))
                                            {
                                                node.CopyFromPath = copyPath;
                                                if (long.TryParse(copyRevStr, out long copyRev))
                                                    node.CopyFromRev = copyRev;
                                            }
                                        }
                                        else if (fastMode)
                                        {
                                            if (!foundBranchIndicator)
                                            {
                                                node.ChangedPaths.Add($"{action} {filePath}");
                                                foundBranchIndicator = true;
                                            }
                                        }
                                        else
                                        {
                                            node.ChangedPaths.Add($"{action} {filePath}");
                                        }
                                    }
                                }
                                break;
                        }
                    }
                    nodes.Add(node);
                }
            }
        }
    }

    private IEnumerator PopulateFilesInBackgroundRoutine(CancellationToken token)
    {
        SVNLogBridge.LogLine("<color=yellow>[Graph] Fetching file details in background...</color>");

        var filesDict = new Dictionary<long, List<string>>();

        Task parseTask = Task.Run(() =>
        {
            using (var reader = XmlReader.Create(new StringReader(_cachedXmlOutput)))
            {
                long currentRev = -1;
                while (reader.Read())
                {
                    if (token.IsCancellationRequested) break;
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "logentry")
                    {
                        if (reader.GetAttribute("revision") != null && long.TryParse(reader.GetAttribute("revision"), out long rev))
                            currentRev = rev;

                        while (reader.Read())
                        {
                            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "logentry") break;
                            if (reader.NodeType == XmlNodeType.Element && reader.Name == "path")
                            {
                                string action = reader.GetAttribute("action") ?? "";
                                string filePath = reader.ReadElementContentAsString();

                                if (!filesDict.ContainsKey(currentRev))
                                    filesDict[currentRev] = new List<string>();

                                filesDict[currentRev].Add($"{action} {filePath}");
                            }
                        }
                    }
                }
            }
        }, token);

        while (!parseTask.IsCompleted) yield return null;

        if (token.IsCancellationRequested) yield break;

        var items = _graphModule.InstantiatedItems;
        int processed = 0;

        foreach (var itemGo in items)
        {
            if (itemGo == null || !itemGo.TryGetComponent<SVNGraphItem>(out var item)) continue;

            long rev = item.GetRevision();
            if (filesDict.TryGetValue(rev, out var paths))
            {
                item.SetChangedPaths(paths);
            }

            processed++;
            if (processed % 100 == 0) yield return null;
        }

        _cachedXmlOutput = null;
        SVNLogBridge.LogLine("<color=green>[Graph] All file details loaded.</color>");
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
        if (gameObject.activeInHierarchy) _ = LoadGraphAsync();
    }

    public void OnItemClicked(SVNGraphItem item, bool multi)
    {
        if (item == null) return;
        if (!multi)
        {
            foreach (var s in _selectedItems) { if (s != null) s.SetSelected(false); }
            _selectedItems.Clear();
        }

        if (_selectedItems.Contains(item)) { item.SetSelected(false); _selectedItems.Remove(item); }
        else { item.SetSelected(true); _selectedItems.Add(item); }
    }

    public IReadOnlyCollection<SVNGraphItem> GetSelectedItems()
    {
        _selectedItems.RemoveWhere(x => x == null);
        return _selectedItems;
    }
}