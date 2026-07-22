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

    #region Lifecycle
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
            return;

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
        CancelLoading();
    }

    private void OnDestroy()
    {
        CancelLoading();

        if (branchFilterInput != null)
            branchFilterInput.onValueChanged.RemoveListener(OnFilterChanged);
    }
    #endregion

    #region Filter
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
    #endregion

    #region Graph Loading
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
    #endregion

    #region Helpers
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
    #endregion

    #region SVN Log Parsing
    private async Task<List<SVNRevisionNode>> FetchLogEntriesAsync(CancellationToken token = default)
    {
        string xmlOutput = await SvnRunner.RunAsync("log --xml --verbose ^/", _svnManager.WorkingDir, token: token);
        var nodes = new List<SVNRevisionNode>();

        if (string.IsNullOrEmpty(xmlOutput))
            return nodes;

        try
        {
            ParseLogXml(xmlOutput, nodes, token);
        }
        catch (XmlException ex)
        {
            SVNLogBridge.LogError($"[SVN] Failed to parse log XML: {ex.Message}");
        }

        return nodes;
    }

    private static void ParseLogXml(string xmlOutput, List<SVNRevisionNode> nodes, CancellationToken token)
    {
        using var stringReader = new StringReader(xmlOutput);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            Async = false
        });

        SVNRevisionNode currentNode = null;

        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
                continue;

            switch (reader.Name)
            {
                case "logentry":
                    currentNode = new SVNRevisionNode();
                    if (reader.GetAttribute("revision") is string rev && long.TryParse(rev, out long revision))
                        currentNode.Revision = revision;
                    nodes.Add(currentNode);
                    break;

                case "author" when currentNode != null:
                    currentNode.Author = reader.ReadElementContentAsString();
                    break;

                case "date" when currentNode != null:
                    currentNode.Date = reader.ReadElementContentAsString();
                    break;

                case "msg" when currentNode != null:
                    currentNode.Message = reader.ReadElementContentAsString();
                    break;

                case "path" when currentNode != null:
                    ParsePathElement(reader, currentNode);
                    break;
            }
        }
    }

    private static void ParsePathElement(XmlReader reader, SVNRevisionNode currentNode)
    {
        string action = reader.GetAttribute("action") ?? "";
        string propMods = reader.GetAttribute("prop-mods") ?? "";
        string filePath = reader.ReadElementContentAsString();

        currentNode.ChangedPaths.Add($"{action} {filePath}");

        if (propMods == "true" && IsBranchPath(filePath))
        {
            currentNode.HasMergeInfoChange = true;
        }
    }

    private static bool IsBranchPath(string filePath)
    {
        return filePath == "/trunk" ||
               filePath.StartsWith("/branches/", StringComparison.OrdinalIgnoreCase) ||
               filePath.StartsWith("/tags/", StringComparison.OrdinalIgnoreCase);
    }
    #endregion
}
