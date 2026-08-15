using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public enum NodeType { Trunk, Branch, Tag, Unknown }

    public readonly struct BranchInfo
    {
        public readonly string Name;
        public readonly NodeType Type;

        public BranchInfo(string name, NodeType type)
        {
            Name = name;
            Type = type;
        }

        public static readonly BranchInfo Trunk = new("trunk", NodeType.Trunk);
        public static readonly BranchInfo Unknown = new("unknown", NodeType.Unknown);
    }

    public class SVNRevGraph : SVNBase
    {
        private const string VERT_TRUNK = "┃ ";
        private const string VERT_BRANCH = "│ ";
        private const string VERT_TAG = "┊ ";
        private const string VERT_UNKNOWN = "│ ";
        private const string VERT_MERGED = "┆ ";

        private const string SHAPE_TRUNK = "■";
        private const string SHAPE_BRANCH = "●";
        private const string SHAPE_TAG = "◆";
        private const string SHAPE_UNKNOWN = "○";
        private const string SHAPE_MERGE_FROM_TRUNK = "▣";
        private const string SHAPE_MERGE_FROM_BRANCH = "◉";
        private const string SHAPE_MERGE_FROM_TAG = "◈";

        private const string SPACER = " ";
        private const string COLOR_INACTIVE = "#00000000";
        private const string COLOR_MERGED_INACTIVE = "#88888844";
        private const string COLOR_BLACK = "#000000";

        private const int MaxPoolSize = 200;
        private const long MaxFrameBudgetMs = 10;

        private static readonly Regex MergePathRegex = new(
            @"(?:^|[\s\(\)\[\]\{\}""'`])/?(\^?/)?(?<type>branches|trunk|tags)(/(?<name>[^\s,;:\)]+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(200));

        private static readonly Regex BranchQuoteRegex = new(
            @"branch\s*['""](?<name>[^'""]+)['""]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(200));

        private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled);

        public SVNRevGraph(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private readonly List<GameObject> _instantiatedItems = new();
        private readonly Stack<GameObject> _graphItemPool = new();

        private readonly Dictionary<string, NodeType> _branchTypes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _mergedBranches = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _branchFirstRev = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _branchLastRev = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _branchParent = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _branchColorCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly BranchColorSystem _branchColorSystem = new();

        private Coroutine _renderCoroutine;
        private float _renderYPosition;
        private float _renderItemHeight;
        private float _renderSpacing;

        public IReadOnlyList<GameObject> InstantiatedItems => _instantiatedItems;

        public void RenderGraph(List<SVNRevisionNode> nodes)
        {
            if (nodes == null || nodes.Count == 0)
            {
                SVNLogBridge.LogToOutput("[SVN] No revisions to render.");
                return;
            }

            if (svnUI == null || svnUI.GraphContainer == null)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] GraphContainer is not assigned in SVNUI.");
                return;
            }

            if (_renderCoroutine != null)
            {
                svnUI.StopCoroutine(_renderCoroutine);
                _renderCoroutine = null;
            }

            _renderCoroutine = svnUI.StartCoroutine(RenderGraphRoutine(nodes));
        }

        private IEnumerator RenderGraphRoutine(List<SVNRevisionNode> nodes)
        {
            var workingNodes = new List<SVNRevisionNode>(nodes);
            workingNodes.Sort((a, b) => a.Revision.CompareTo(b.Revision));

            DisableAutoLayout();
            ReleaseActiveItemsToPool();
            ResetState();

            var layoutGroup = svnUI.GraphContainer?.GetComponent<VerticalLayoutGroup>();
            _renderSpacing = layoutGroup != null ? layoutGroup.spacing : 0f;
            _renderYPosition = 0f;

            if (svnUI.GraphItemPrefab != null)
            {
                var prefabRect = svnUI.GraphItemPrefab.GetComponent<RectTransform>();
                if (prefabRect != null)
                {
                    _renderItemHeight = prefabRect.rect.height;
                    if (_renderItemHeight <= 0)
                    {
                        var le = svnUI.GraphItemPrefab.GetComponent<UnityEngine.UI.LayoutElement>();
                        if (le != null) _renderItemHeight = le.preferredHeight;
                    }
                }
            }

            var branchColumns = AnalyzeBranches(workingNodes, out int columnCount);

            foreach (var branch in branchColumns.Keys)
                _branchColorCache[branch] = _branchColorSystem.GetColor(branch);

            var columnBranches = BuildColumnLookup(branchColumns, columnCount);

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            for (int i = workingNodes.Count - 1; i >= 0; i--)
            {
                RenderNode(workingNodes[i], branchColumns, columnBranches, columnCount);

                if (stopwatch.ElapsedMilliseconds >= MaxFrameBudgetMs)
                {
                    stopwatch.Reset();
                    yield return null;
                    stopwatch.Start();
                }
            }

            stopwatch.Stop();

            EnableAutoLayout();
            RefreshLayout();

            SVNLogBridge.LogToOutput($"[SVN] Render complete. {_instantiatedItems.Count} revisions rendered.");
            _renderCoroutine = null;
        }

        private void DisableAutoLayout()
        {
            if (svnUI.GraphContainer == null) return;

            var layoutGroup = svnUI.GraphContainer.GetComponent<VerticalLayoutGroup>();
            if (layoutGroup != null) layoutGroup.enabled = false;

            var contentSizeFitter = svnUI.GraphContainer.GetComponent<ContentSizeFitter>();
            if (contentSizeFitter != null) contentSizeFitter.enabled = false;
        }

        private void EnableAutoLayout()
        {
            if (svnUI.GraphContainer == null) return;

            var layoutGroup = svnUI.GraphContainer.GetComponent<VerticalLayoutGroup>();
            if (layoutGroup != null) layoutGroup.enabled = true;

            var contentSizeFitter = svnUI.GraphContainer.GetComponent<ContentSizeFitter>();
            if (contentSizeFitter != null) contentSizeFitter.enabled = true;
        }

        private void ReleaseActiveItemsToPool()
        {
            if (svnUI.GraphContainer == null) return;

            for (int i = svnUI.GraphContainer.childCount - 1; i >= 0; i--)
            {
                var child = svnUI.GraphContainer.GetChild(i);
                if (child == null) continue;

                var go = child.gameObject;

                if (!go.TryGetComponent<SVNGraphItem>(out _))
                {
                    UnityEngine.Object.Destroy(go);
                    continue;
                }

                go.SetActive(false);
                go.transform.SetParent(null);

                if (_graphItemPool.Count < MaxPoolSize)
                {
                    _graphItemPool.Push(go);
                }
                else
                {
                    UnityEngine.Object.Destroy(go);
                }
            }
        }

        private void ResetState()
        {
            _instantiatedItems.Clear();
            _branchTypes.Clear();
            _mergedBranches.Clear();
            _branchFirstRev.Clear();
            _branchLastRev.Clear();
            _branchParent.Clear();
            _branchColorCache.Clear();
            _branchColorSystem.Reset();
        }

        private Dictionary<string, int> AnalyzeBranches(List<SVNRevisionNode> nodes, out int columnCount)
        {
            var branchColumns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            columnCount = 0;

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var info = GetBranchInfo(node);

                RegisterBranch(node, info, branchColumns, ref columnCount);

                if (info.Type != NodeType.Trunk &&
                    _branchFirstRev.TryGetValue(info.Name, out long firstRev) &&
                    firstRev == node.Revision)
                {
                    DetectAndStoreBranchParent(node, info.Name);
                }

                if (IsMergeCommit(node))
                    DetectAndRegisterMergeSource(node, info.Name, branchColumns, ref columnCount);
            }

            return branchColumns;
        }

        private void RegisterBranch(SVNRevisionNode node, BranchInfo info, Dictionary<string, int> branchColumns, ref int nextColumn)
        {
            if (!branchColumns.ContainsKey(info.Name))
            {
                branchColumns[info.Name] = nextColumn++;
                _branchTypes[info.Name] = info.Type;
                _branchFirstRev[info.Name] = node.Revision;
            }

            _branchLastRev[info.Name] = node.Revision;
        }

        private void DetectAndStoreBranchParent(SVNRevisionNode node, string branchName)
        {
            string parent = DetectBranchParent(node, branchName);
            _branchParent[branchName] = string.IsNullOrEmpty(parent) ? "trunk" : parent;
        }

        private void DetectAndRegisterMergeSource(SVNRevisionNode node, string currentBranch,
            Dictionary<string, int> branchColumns, ref int nextColumn)
        {
            string mergeSrcBranch = DetectMergeSourceBranch(node, currentBranch, branchColumns.Keys);

            if (string.IsNullOrEmpty(mergeSrcBranch))
                return;

            _mergedBranches.Add(mergeSrcBranch);

            if (!branchColumns.ContainsKey(mergeSrcBranch))
                RegisterMergeSourceBranch(mergeSrcBranch, branchColumns, ref nextColumn, node.Revision);
            else
                UpdateBranchLastRev(mergeSrcBranch, node.Revision);
        }

        private void RegisterMergeSourceBranch(string branchName, Dictionary<string, int> branchColumns,
            ref int nextColumn, long revision)
        {
            branchColumns[branchName] = nextColumn++;

            NodeType type;
            if (branchName == "trunk")
                type = NodeType.Trunk;
            else if (_branchTypes.TryGetValue(branchName, out var existingType) && existingType == NodeType.Tag)
                type = NodeType.Tag;
            else
                type = NodeType.Branch;

            _branchTypes[branchName] = type;

            if (!_branchFirstRev.ContainsKey(branchName))
                _branchFirstRev[branchName] = revision;

            if (!_branchLastRev.ContainsKey(branchName) || _branchLastRev[branchName] < revision)
                _branchLastRev[branchName] = revision;
        }

        private void UpdateBranchLastRev(string branchName, long revision)
        {
            if (!_branchLastRev.ContainsKey(branchName) || _branchLastRev[branchName] < revision)
                _branchLastRev[branchName] = revision;
        }

        private string[] BuildColumnLookup(Dictionary<string, int> branchColumns, int columnCount)
        {
            var result = new string[columnCount];
            foreach (var kv in branchColumns)
            {
                if (kv.Value >= 0 && kv.Value < columnCount)
                    result[kv.Value] = kv.Key;
            }
            return result;
        }

        private void RenderNode(SVNRevisionNode node, Dictionary<string, int> branchColumns,
            string[] columnBranches, int columnCount)
        {
            var info = GetBranchInfo(node);

            if (!branchColumns.TryGetValue(info.Name, out int col))
            {
                SVNLogBridge.LogErrorToOutput($"[SVN RevGraph] Branch '{info.Name}' not found in column map.");
                return;
            }

            string colHex = _branchColorCache.TryGetValue(info.Name, out var color) ? color : "#555555";

            bool isMerge = IsMergeCommit(node);
            string mergeSrcBranch = ResolveMergeSourceBranch(node, info.Name, branchColumns);

            string fullPrefix = BuildPrefix(node, info, mergeSrcBranch);
            string shape = ResolveShape(info, isMerge, mergeSrcBranch);

            string graphText = BuildGraphText(columnCount, col, node, info, shape, mergeSrcBranch, isMerge, columnBranches);

            InstantiateGraphItem(graphText, node, info.Name, colHex, fullPrefix);
        }

        private string ResolveMergeSourceBranch(SVNRevisionNode node, string currentBranch,
            Dictionary<string, int> branchColumns)
        {
            if (!IsMergeCommit(node)) return null;

            return DetectMergeSourceBranch(node, currentBranch, branchColumns.Keys);
        }

        private string BuildPrefix(SVNRevisionNode node, BranchInfo info, string mergeSrcBranch)
        {
            var prefix = new StringBuilder();

            if (info.Type != NodeType.Trunk &&
                _branchFirstRev.TryGetValue(info.Name, out long firstRev) &&
                node.Revision == firstRev)
            {
                string parent = _branchParent.TryGetValue(info.Name, out var p) ? p : "trunk";
                prefix.Append($"<color={COLOR_BLACK}>[branched from {parent}]</color> ");
            }

            if (!string.IsNullOrEmpty(mergeSrcBranch))
                prefix.Append($"<color={COLOR_BLACK}>[merged from {mergeSrcBranch}]</color> ");

            return prefix.ToString();
        }

        private string ResolveShape(BranchInfo info, bool isMerge, string mergeSrcBranch)
        {
            if (!isMerge)
            {
                return info.Type switch
                {
                    NodeType.Trunk => SHAPE_TRUNK,
                    NodeType.Branch => SHAPE_BRANCH,
                    NodeType.Tag => SHAPE_TAG,
                    NodeType.Unknown => SHAPE_UNKNOWN,
                    _ => SHAPE_BRANCH
                };
            }

            if (mergeSrcBranch == "trunk")
                return SHAPE_MERGE_FROM_TRUNK;

            if (!string.IsNullOrEmpty(mergeSrcBranch) &&
                _branchTypes.TryGetValue(mergeSrcBranch, out var srcType) &&
                srcType == NodeType.Tag)
                return SHAPE_MERGE_FROM_TAG;

            return SHAPE_MERGE_FROM_BRANCH;
        }

        private string BuildGraphText(int columnCount, int currentCol, SVNRevisionNode node,
            BranchInfo info, string shape, string mergeSrcBranch, bool isMerge, string[] columnBranches)
        {
            var g = new StringBuilder(columnCount * 8);

            for (int c = 0; c < columnCount; c++)
            {
                string laneBranch = columnBranches[c];
                string laneColor = laneBranch != null && _branchColorCache.TryGetValue(laneBranch, out var cached)
                    ? cached
                    : "#555555";

                string finalColor = laneColor;
                string laneText;

                bool isCurrent = (c == currentCol);

                bool isActive = laneBranch != null &&
                                _branchFirstRev.TryGetValue(laneBranch, out long firstRev) &&
                                _branchLastRev.TryGetValue(laneBranch, out long lastRev) &&
                                node.Revision >= firstRev &&
                                node.Revision <= lastRev;

                if (isCurrent)
                {
                    if (isMerge && !string.IsNullOrEmpty(mergeSrcBranch))
                    {
                        finalColor = _branchColorCache.TryGetValue(mergeSrcBranch, out var mergeColor) ? mergeColor : laneColor;
                    }

                    laneText = shape + SPACER;
                }
                else if (isActive)
                {
                    laneText = GetVerticalLine(laneBranch);
                }
                else
                {
                    bool isMergedAndInactive = laneBranch != null &&
                                               _mergedBranches.Contains(laneBranch) &&
                                               _branchLastRev.TryGetValue(laneBranch, out long lastRev2) &&
                                               node.Revision > lastRev2;

                    if (isMergedAndInactive)
                    {
                        laneText = VERT_MERGED;
                        finalColor = COLOR_MERGED_INACTIVE;
                    }
                    else
                    {
                        laneText = GetVerticalLine(laneBranch);
                        finalColor = COLOR_INACTIVE;
                    }
                }

                g.Append($"<color={finalColor}>{laneText}</color>");
            }

            return g.ToString();
        }

        private string GetVerticalLine(string branchName)
        {
            if (branchName != null && _branchTypes.TryGetValue(branchName, out var type))
            {
                return type switch
                {
                    NodeType.Trunk => VERT_TRUNK,
                    NodeType.Branch => VERT_BRANCH,
                    NodeType.Tag => VERT_TAG,
                    NodeType.Unknown => VERT_UNKNOWN,
                    _ => VERT_BRANCH
                };
            }

            return VERT_BRANCH;
        }

        private void InstantiateGraphItem(string graphText, SVNRevisionNode node, string branchName,
    string colorHex, string prefix)
        {
            if (svnUI.GraphItemPrefab == null || svnUI.GraphContainer == null)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] GraphItemPrefab or GraphContainer is null.");
                return;
            }

            GameObject itemGo;

            if (_graphItemPool.Count > 0)
            {
                itemGo = _graphItemPool.Pop();
                if (itemGo != null)
                {
                    itemGo.transform.SetParent(svnUI.GraphContainer, false);
                    itemGo.SetActive(true);

                    itemGo.transform.localPosition = Vector3.zero;
                    itemGo.transform.localRotation = Quaternion.identity;
                    itemGo.transform.localScale = Vector3.one;
                }
                else
                {
                    itemGo = UnityEngine.Object.Instantiate(svnUI.GraphItemPrefab, svnUI.GraphContainer);
                }
            }
            else
            {
                itemGo = UnityEngine.Object.Instantiate(svnUI.GraphItemPrefab, svnUI.GraphContainer);
            }

            _instantiatedItems.Add(itemGo);

            if (itemGo.TryGetComponent<RectTransform>(out var rt))
            {
                rt.anchorMin = new Vector2(0, 1);
                rt.anchorMax = new Vector2(0, 1);
                rt.pivot = new Vector2(0, 1);
                rt.anchoredPosition = new Vector2(0, -_renderYPosition);

                float heightToAdd = _renderItemHeight > 0 ? _renderItemHeight : 40f;
                _renderYPosition += heightToAdd + _renderSpacing;
            }

            if (itemGo.TryGetComponent<SVNGraphItem>(out var item))
                item.Setup(graphText, node, branchName, colorHex, svnManager, prefix);
        }

        private string DetectBranchParent(SVNRevisionNode node, string currentBranch)
        {
            if (node.ChangedPaths == null) return null;

            foreach (string path in node.ChangedPaths)
            {
                if (!path.TrimStart().StartsWith("A ", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!path.Contains("(from ", StringComparison.OrdinalIgnoreCase))
                    continue;

                int fromIdx = path.IndexOf("(from ", StringComparison.OrdinalIgnoreCase);
                if (fromIdx < 0) continue;

                string fromPart = path.Substring(fromIdx + 6);
                int spaceIdx = fromPart.IndexOf(':');
                if (spaceIdx > 0)
                    fromPart = fromPart.Substring(0, spaceIdx);

                fromPart = fromPart.Trim();
                string parentBranch = ExtractBranchFromPath(fromPart);

                if (!string.IsNullOrEmpty(parentBranch) && parentBranch != currentBranch)
                    return parentBranch;
            }
            return null;
        }

        private string DetectMergeSourceBranch(SVNRevisionNode node, string currentBranch, ICollection<string> knownBranches)
        {
            string msg = node.Message ?? "";

            try
            {
                var pathMatches = MergePathRegex.Matches(msg);
                foreach (Match m in pathMatches)
                {
                    string type = m.Groups["type"].Value.ToLowerInvariant();
                    string name = m.Groups["name"].Value;
                    string found = type == "trunk" ? "trunk" : (string.IsNullOrEmpty(name) ? null : name);

                    if (!string.IsNullOrEmpty(found) && found != currentBranch)
                        return found;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                SVNLogBridge.LogErrorToOutput("[SVN RevGraph] Regex timeout while detecting merge source.");
            }

            foreach (string branch in knownBranches)
            {
                if (branch == currentBranch) continue;
                if (WordContains(msg, branch))
                    return branch;
            }

            try
            {
                var branchWordMatch = BranchQuoteRegex.Match(msg);
                if (branchWordMatch.Success)
                {
                    string name = branchWordMatch.Groups["name"].Value;
                    if (name != currentBranch) return name;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                SVNLogBridge.LogErrorToOutput("[SVN RevGraph] Regex timeout while matching branch quote.");
            }

            if (node.ChangedPaths != null)
            {
                foreach (string path in node.ChangedPaths)
                {
                    string b = ExtractBranchFromPath(path);
                    if (!string.IsNullOrEmpty(b) && b != currentBranch)
                        return b;
                }
            }

            return null;
        }

        private static bool WordContains(string text, string word)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word)) return false;

            int idx = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                bool leftBoundary = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                bool rightBoundary = idx + word.Length >= text.Length || !char.IsLetterOrDigit(text[idx + word.Length]);

                if (leftBoundary && rightBoundary)
                    return true;

                idx = text.IndexOf(word, idx + 1, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private string ExtractBranchFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            int branchesIdx = path.IndexOf("/branches/", StringComparison.OrdinalIgnoreCase);
            if (branchesIdx >= 0)
            {
                string afterBranches = path.Substring(branchesIdx + 10);
                int nextSlash = afterBranches.IndexOf('/');
                return nextSlash > 0 ? afterBranches.Substring(0, nextSlash) : afterBranches;
            }

            if (IsExactTrunkPath(path))
                return "trunk";

            int tagsIdx = path.IndexOf("/tags/", StringComparison.OrdinalIgnoreCase);
            if (tagsIdx >= 0)
            {
                string afterTags = path.Substring(tagsIdx + 6);
                int nextSlash = afterTags.IndexOf('/');
                return nextSlash > 0 ? afterTags.Substring(0, nextSlash) : afterTags;
            }

            return null;
        }

        private static bool IsExactTrunkPath(string path)
        {
            if (path.Equals("/trunk", StringComparison.OrdinalIgnoreCase))
                return true;

            if (path.StartsWith("/trunk/", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private bool IsMergeCommit(SVNRevisionNode node)
        {
            if (node == null) return false;
            if (node.HasMergeInfoChange) return true;

            string msg = node.Message;
            if (!string.IsNullOrEmpty(msg))
            {
                string lower = msg.ToLowerInvariant();
                if (lower.Contains("merge") || lower.Contains("merged") || lower.Contains("reintegrate"))
                    return true;
            }

            if (node.ChangedPaths != null)
            {
                string firstBranch = null;
                foreach (string path in node.ChangedPaths)
                {
                    string b = ExtractBranchFromPath(path);
                    if (string.IsNullOrEmpty(b)) continue;

                    if (firstBranch == null)
                        firstBranch = b;
                    else if (!firstBranch.Equals(b, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private BranchInfo GetBranchInfo(SVNRevisionNode node)
        {
            if (node.ChangedPaths == null || node.ChangedPaths.Count == 0)
            {
                SVNLogBridge.LogErrorToOutput("[SVN RevGraph] Revision has no changed paths, defaulting to unknown.");
                return BranchInfo.Unknown;
            }

            foreach (string path in node.ChangedPaths)
            {
                if (IsExactTrunkPath(path))
                    return BranchInfo.Trunk;
            }

            foreach (string path in node.ChangedPaths)
            {
                int branchesIdx = path.IndexOf("/branches/", StringComparison.OrdinalIgnoreCase);
                if (branchesIdx >= 0)
                {
                    string afterBranches = path.Substring(branchesIdx + 10);
                    int nextSlash = afterBranches.IndexOf('/');
                    string name = nextSlash > 0 ? afterBranches.Substring(0, nextSlash) : afterBranches;
                    return new BranchInfo(name, NodeType.Branch);
                }

                int tagsIdx = path.IndexOf("/tags/", StringComparison.OrdinalIgnoreCase);
                if (tagsIdx >= 0)
                {
                    string afterTags = path.Substring(tagsIdx + 6);
                    int nextSlash = afterTags.IndexOf('/');
                    string name = nextSlash > 0 ? afterTags.Substring(0, nextSlash) : afterTags;
                    return new BranchInfo(name, NodeType.Tag);
                }
            }

            SVNLogBridge.LogErrorToOutput("[SVN RevGraph] Could not determine branch, defaulting to unknown.");
            return BranchInfo.Unknown;
        }

        public void CollapseAll() => ToggleAll(false);
        public void ExpandAll() => ToggleAll(true);

        private void ToggleAll(bool expanded)
        {
            _instantiatedItems.RemoveAll(item => item == null);

            foreach (var go in _instantiatedItems)
            {
                if (go.TryGetComponent<SVNGraphItem>(out var item))
                    item.SetExpanded(expanded);
            }

            RefreshLayout();
        }

        private void RefreshLayout()
        {
            if (svnUI.GraphContainer != null)
            {
                LayoutRebuilder.MarkLayoutForRebuild(svnUI.GraphContainer as RectTransform);
            }
        }

        public void ExportHistoryToTxt()
        {
            if (_instantiatedItems == null || _instantiatedItems.Count == 0)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Graph revision is empty.");
                return;
            }

            var sortedItems = _instantiatedItems
                .Where(go => go != null)
                .Select(go => go.TryGetComponent<SVNGraphItem>(out var item) ? item : null)
                .Where(item => item != null)
                .OrderByDescending(item => item.GetRevision())
                .ToList();

            var authorStats = new Dictionary<string, (int Commits, int FileChanges)>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in sortedItems)
            {
                string author = item.GetAuthor();
                if (string.IsNullOrWhiteSpace(author)) author = "Unknown";

                var paths = item.GetChangedPaths();
                int changesThisRev = paths != null ? paths.Count : 0;

                if (authorStats.TryGetValue(author, out var current))
                {
                    authorStats[author] = (current.Commits + 1, current.FileChanges + changesThisRev);
                }
                else
                {
                    authorStats[author] = (1, changesThisRev);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== SVN REVISION HISTORY REPORT ===");
            sb.AppendLine($"Generated: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Total Revisions: {sortedItems.Count}");
            sb.AppendLine("===================================");

            sb.AppendLine();
            sb.AppendLine("=== AUTHOR STATISTICS ===");

            var sortedAuthors = authorStats
                .OrderByDescending(kvp => kvp.Value.Commits)
                .ThenBy(kvp => kvp.Key);

            foreach (var kvp in sortedAuthors)
            {
                sb.AppendLine($"  {kvp.Key,-25} | Commits: {kvp.Value.Commits,-6} | Touched Files: {kvp.Value.FileChanges,-6}");
            }

            sb.AppendLine("==============================");
            sb.AppendLine();

            foreach (var item in sortedItems)
            {
                sb.AppendLine($"[r{item.GetRevision()}]");
                sb.AppendLine($"Date:    {item.GetDate()}");
                sb.AppendLine($"Author:  {item.GetAuthor()}");
                sb.AppendLine($"Branch:  [{item.GetBranchName()}]");
                sb.AppendLine($"Message: {item.GetMessage()}");

                List<string> paths = item.GetChangedPaths();
                if (paths != null && paths.Count > 0)
                {
                    sb.AppendLine("Changes:");
                    foreach (string path in paths)
                    {
                        string cleanPath = HtmlTagRegex.Replace(path, string.Empty);
                        sb.AppendLine($"  {cleanPath}");
                    }
                }
                sb.AppendLine("-----------------------------------");
            }

            var external = svnManager.GetModule<SVNExternal>();
            external?.SaveHistoryToFile(sb.ToString());
        }

        private class BranchColorSystem
        {
            private readonly Dictionary<string, string> _branchColors = new(StringComparer.OrdinalIgnoreCase);
            private int _index = 0;
            private const float GOLDEN_ANGLE = 137.508f;

            public string GetColor(string branch)
            {
                if (_branchColors.TryGetValue(branch, out var existing))
                    return existing;

                float hue = Mathf.Repeat(_index * GOLDEN_ANGLE, 360f);
                _index++;
                Color c = Color.HSVToRGB(hue / 360f, 0.65f, 0.85f);
                string hex = "#" + ColorUtility.ToHtmlStringRGB(c);
                _branchColors[branch] = hex;
                return hex;
            }

            public void Reset()
            {
                _branchColors.Clear();
                _index = 0;
            }
        }

        public void ClearGraph()
        {
            if (_renderCoroutine != null)
            {
                svnUI.StopCoroutine(_renderCoroutine);
                _renderCoroutine = null;
            }

            ReleaseActiveItemsToPool();
            _instantiatedItems.Clear();
            ResetState();
        }
    }
}