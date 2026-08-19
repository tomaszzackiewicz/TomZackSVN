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
        // ------------------------------------------------------------------
        // Visual constants (git-style) – stała szerokość kolumny = 2 znaki
        // ------------------------------------------------------------------
        private const string VERT_TRUNK = "█ ";
        private const string VERT_BRANCH = "│ ";
        private const string VERT_TAG = "┊ ";
        private const string VERT_FADED = "┆ ";
        private const string VERT_EMPTY = "  ";

        private const string SHAPE_TRUNK = "■";
        private const string SHAPE_BRANCH = "●";
        private const string SHAPE_TAG = "◆";
        private const string SHAPE_UNKNOWN = "○";
        private const string SHAPE_MERGE = "◉";
        private const string SHAPE_BRANCH_POINT = "▣";

        private const string COLOR_TRUNK = "#3B82F6";
        private const string COLOR_INACTIVE = "#00000000";
        private const string COLOR_FADED = "#88888866";
        private const string COLOR_BLACK = "#000000";

        private const int MaxPoolSize = 250;
        private const long MaxFrameBudgetMs = 10;

        private static readonly Regex MergePathRegex = new(
            @"(?:^|[\s\(\)\[\]\{\}""'`])/?(\^?/)?(?<type>branches|trunk|tags)(/(?<name>[^\s,;:\)]+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(250));

        private static readonly Regex BranchQuoteRegex = new(
            @"branch\s*['""](?<name>[^'""]+)['""]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(200));

        private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled);

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------
        private readonly List<GameObject> _instantiatedItems = new();
        private readonly Stack<GameObject> _graphItemPool = new();

        private readonly Dictionary<string, NodeType> _branchTypes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _branchFirstRev = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _branchLastRev = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _branchParent = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _branchColorCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _mergedBranches = new(StringComparer.OrdinalIgnoreCase);
        private readonly BranchColorSystem _colorSystem = new();

        private Coroutine _renderCoroutine;
        private float _renderYPosition;
        private float _renderItemHeight;
        private float _renderSpacing;

        public IReadOnlyList<GameObject> InstantiatedItems => _instantiatedItems;

        public SVNRevGraph(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------
        public void RenderGraph(List<SVNRevisionNode> nodes)
        {
            if (nodes == null || nodes.Count == 0)
            {
                SVNLogBridge.LogToOutput("[SVN] No revisions to render.");
                return;
            }

            if (svnUI?.GraphContainer == null)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] GraphContainer is not assigned.");
                return;
            }

            if (_renderCoroutine != null)
            {
                svnUI.StopCoroutine(_renderCoroutine);
                _renderCoroutine = null;
            }

            _renderCoroutine = svnUI.StartCoroutine(RenderGraphRoutine(nodes));
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

        public void CollapseAll() => ToggleAll(false);
        public void ExpandAll() => ToggleAll(true);

        public void ExportHistoryToTxt()
        {
            if (_instantiatedItems == null || _instantiatedItems.Count == 0)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Graph is empty.");
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
                string author = string.IsNullOrWhiteSpace(item.GetAuthor()) ? "Unknown" : item.GetAuthor();
                int changes = item.GetChangedPaths()?.Count ?? 0;

                if (authorStats.TryGetValue(author, out var cur))
                    authorStats[author] = (cur.Commits + 1, cur.FileChanges + changes);
                else
                    authorStats[author] = (1, changes);
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== SVN REVISION HISTORY REPORT ===");
            sb.AppendLine($"Generated: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Total Revisions: {sortedItems.Count}");
            sb.AppendLine("===================================");
            sb.AppendLine();
            sb.AppendLine("=== AUTHOR STATISTICS ===");

            foreach (var kvp in authorStats.OrderByDescending(k => k.Value.Commits).ThenBy(k => k.Key))
            {
                sb.AppendLine($" {kvp.Key,-25} | Commits: {kvp.Value.Commits,-6} | Files: {kvp.Value.FileChanges,-6}");
            }

            sb.AppendLine("==============================");
            sb.AppendLine();

            foreach (var item in sortedItems)
            {
                sb.AppendLine($"[r{item.GetRevision()}]");
                sb.AppendLine($"Date: {item.GetDate()}");
                sb.AppendLine($"Author: {item.GetAuthor()}");
                sb.AppendLine($"Branch: [{item.GetBranchName()}]");
                sb.AppendLine($"Message: {item.GetMessage()}");

                var paths = item.GetChangedPaths();
                if (paths != null && paths.Count > 0)
                {
                    sb.AppendLine("Changes:");
                    foreach (string path in paths)
                        sb.AppendLine($" {HtmlTagRegex.Replace(path, "")}");
                }

                sb.AppendLine("-----------------------------------");
            }

            svnManager.GetModule<SVNExternal>()?.SaveHistoryToFile(sb.ToString());
        }

        // ------------------------------------------------------------------
        // Render pipeline
        // ------------------------------------------------------------------
        private IEnumerator RenderGraphRoutine(List<SVNRevisionNode> nodes)
        {
            var working = new List<SVNRevisionNode>(nodes);
            working.Sort((a, b) => a.Revision.CompareTo(b.Revision));

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
                        var le = svnUI.GraphItemPrefab.GetComponent<LayoutElement>();
                        if (le != null) _renderItemHeight = le.preferredHeight;
                    }
                }
            }

            // 1. Analiza branchy + lane assignment (bez reuse'u kolumn)
            var laneMap = BuildLaneMap(working, out int columnCount);

            foreach (var branch in laneMap.Keys)
                _branchColorCache[branch] = _colorSystem.GetColor(branch);

            var columnToBranch = new string[columnCount];
            foreach (var kv in laneMap)
            {
                if (kv.Value >= 0 && kv.Value < columnCount)
                    columnToBranch[kv.Value] = kv.Key;
            }

            var stopwatch = Stopwatch.StartNew();

            // 2. Render od najnowszej do najstarszej (jak git log)
            for (int i = working.Count - 1; i >= 0; i--)
            {
                RenderNode(working[i], laneMap, columnToBranch, columnCount);

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

            SVNLogBridge.LogToOutput($"[SVN] Render complete. {_instantiatedItems.Count} revisions, {columnCount} lanes.");
            _renderCoroutine = null;
        }

        // ------------------------------------------------------------------
        // Lane management (bez zwalniania kolumn – świadomie)
        // ------------------------------------------------------------------
        private Dictionary<string, int> BuildLaneMap(List<SVNRevisionNode> nodes, out int columnCount)
        {
            var laneMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int nextColumn = 1; // 0 dla trunk

            laneMap["trunk"] = 0;
            _branchTypes["trunk"] = NodeType.Trunk;

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var info = GetBranchInfo(node);

                if (!laneMap.ContainsKey(info.Name))
                {
                    laneMap[info.Name] = nextColumn++;
                    _branchTypes[info.Name] = info.Type;
                    _branchFirstRev[info.Name] = node.Revision;
                }

                _branchLastRev[info.Name] = node.Revision;

                // Parent detection
                if (info.Type != NodeType.Trunk &&
                    _branchFirstRev.TryGetValue(info.Name, out long first) &&
                    first == node.Revision)
                {
                    string parent = DetectBranchParent(node, info.Name);
                    _branchParent[info.Name] = string.IsNullOrEmpty(parent) ? "trunk" : parent;
                }

                // Merge detection
                if (IsMergeCommit(node))
                {
                    string src = DetectMergeSourceBranch(node, info.Name, laneMap.Keys);
                    if (!string.IsNullOrEmpty(src))
                    {
                        _mergedBranches.Add(src);

                        if (!laneMap.ContainsKey(src))
                        {
                            laneMap[src] = nextColumn++;
                            _branchTypes[src] = src == "trunk" ? NodeType.Trunk : NodeType.Branch;

                            if (!_branchFirstRev.ContainsKey(src))
                                _branchFirstRev[src] = node.Revision;
                        }

                        if (!_branchLastRev.ContainsKey(src) || _branchLastRev[src] < node.Revision)
                            _branchLastRev[src] = node.Revision;
                    }
                }
            }

            columnCount = nextColumn;
            return laneMap;
        }

        // ------------------------------------------------------------------
        // Node rendering
        // ------------------------------------------------------------------
        private void RenderNode(SVNRevisionNode node, Dictionary<string, int> laneMap,
            string[] columnToBranch, int columnCount)
        {
            var info = GetBranchInfo(node);

            if (!laneMap.TryGetValue(info.Name, out int col))
            {
                SVNLogBridge.LogErrorToOutput($"[SVN RevGraph] Branch '{info.Name}' missing in lane map.");
                return;
            }

            string colHex = _branchColorCache.TryGetValue(info.Name, out var c) ? c : "#555555";
            bool isMerge = IsMergeCommit(node);
            string mergeSrc = isMerge ? DetectMergeSourceBranch(node, info.Name, laneMap.Keys) : null;

            bool isBranchPoint = info.Type != NodeType.Trunk &&
                                 _branchFirstRev.TryGetValue(info.Name, out long firstRev) &&
                                 firstRev == node.Revision;

            string shape = ResolveShape(info, isMerge, isBranchPoint, mergeSrc);
            string prefix = BuildPrefix(node, info, mergeSrc, isBranchPoint);
            string graphText = BuildGraphText(columnCount, col, node, info, shape, mergeSrc, isMerge, columnToBranch, laneMap);

            InstantiateGraphItem(graphText, node, info.Name, colHex, prefix);
        }

        private string ResolveShape(BranchInfo info, bool isMerge, bool isBranchPoint, string mergeSrc)
        {
            if (isBranchPoint) return SHAPE_BRANCH_POINT;
            if (isMerge) return SHAPE_MERGE;

            return info.Type switch
            {
                NodeType.Trunk => SHAPE_TRUNK,
                NodeType.Branch => SHAPE_BRANCH,
                NodeType.Tag => SHAPE_TAG,
                _ => SHAPE_UNKNOWN
            };
        }

        private string BuildPrefix(SVNRevisionNode node, BranchInfo info, string mergeSrc, bool isBranchPoint)
        {
            var sb = new StringBuilder();

            if (isBranchPoint)
            {
                string parent = _branchParent.TryGetValue(info.Name, out var p) ? p : "trunk";
                sb.Append($"<color={COLOR_BLACK}>[branched from {parent}]</color> ");
            }

            if (!string.IsNullOrEmpty(mergeSrc))
                sb.Append($"<color={COLOR_BLACK}>[merged from {mergeSrc}]</color> ");

            return sb.ToString();
        }

        private string BuildGraphText(int columnCount, int currentCol, SVNRevisionNode node,
            BranchInfo info, string shape, string mergeSrc, bool isMerge,
            string[] columnToBranch, Dictionary<string, int> laneMap)
        {
            var g = new StringBuilder(columnCount * 10);

            int mergeSrcCol = -1;
            if (!string.IsNullOrEmpty(mergeSrc) && laneMap.TryGetValue(mergeSrc, out int msc))
                mergeSrcCol = msc;

            for (int c = 0; c < columnCount; c++)
            {
                string laneBranch = columnToBranch[c];
                string laneColor = laneBranch != null && _branchColorCache.TryGetValue(laneBranch, out var cached)
                    ? cached
                    : "#555555";

                if (laneBranch == "trunk")
                    laneColor = COLOR_TRUNK;

                string finalColor = laneColor;
                string laneText;

                bool isCurrent = c == currentCol;

                bool isActive = laneBranch != null &&
                                _branchFirstRev.TryGetValue(laneBranch, out long first) &&
                                _branchLastRev.TryGetValue(laneBranch, out long last) &&
                                node.Revision >= first &&
                                node.Revision <= last;

                bool isFaded = laneBranch != null &&
                               _branchTypes.TryGetValue(laneBranch, out var laneType) &&
                               laneType == NodeType.Branch &&
                               _branchLastRev.TryGetValue(laneBranch, out long last2) &&
                               node.Revision > last2;

                if (isCurrent)
                {
                    if (isMerge && !string.IsNullOrEmpty(mergeSrc) &&
                        _branchColorCache.TryGetValue(mergeSrc, out var mergeColor))
                    {
                        finalColor = mergeSrc == "trunk" ? COLOR_TRUNK : mergeColor;
                    }
                    else if (laneBranch == "trunk")
                    {
                        finalColor = COLOR_TRUNK;
                    }

                    laneText = shape + " ";
                }
                else if (isMerge && mergeSrcCol >= 0 && c == mergeSrcCol)
                {
                    finalColor = mergeSrc == "trunk" ? COLOR_TRUNK :
                                 (_branchColorCache.TryGetValue(mergeSrc, out var mc) ? mc : laneColor);

                    laneText = (currentCol > mergeSrcCol) ? "╭─" : "╰─";
                }
                else if (isMerge && mergeSrcCol >= 0 &&
                         ((c > mergeSrcCol && c < currentCol) || (c < mergeSrcCol && c > currentCol)))
                {
                    finalColor = mergeSrc == "trunk" ? COLOR_TRUNK :
                                 (_branchColorCache.TryGetValue(mergeSrc, out var mc) ? mc : "#888888");

                    laneText = "──";
                }
                else if (isActive)
                {
                    laneText = GetVertical(laneBranch);
                    if (laneBranch == "trunk")
                        finalColor = COLOR_TRUNK;
                }
                else if (isFaded)
                {
                    laneText = VERT_FADED;
                    finalColor = COLOR_FADED;
                }
                else
                {
                    laneText = VERT_EMPTY;
                    finalColor = COLOR_INACTIVE;
                }

                g.Append($"<color={finalColor}>{laneText}</color>");
            }

            return g.ToString();
        }

        private string GetVertical(string branchName)
        {
            if (branchName != null && _branchTypes.TryGetValue(branchName, out var type))
            {
                return type switch
                {
                    NodeType.Trunk => VERT_TRUNK,
                    NodeType.Tag => VERT_TAG,
                    NodeType.Branch => VERT_BRANCH,
                    _ => VERT_BRANCH
                };
            }

            return VERT_BRANCH;
        }

        // ------------------------------------------------------------------
        // Instantiation + pooling
        // ------------------------------------------------------------------
        private void InstantiateGraphItem(string graphText, SVNRevisionNode node, string branchName,
            string colorHex, string prefix)
        {
            if (svnUI.GraphItemPrefab == null || svnUI.GraphContainer == null) return;

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

                float h = _renderItemHeight > 0 ? _renderItemHeight : 40f;
                _renderYPosition += h + _renderSpacing;
            }

            if (itemGo.TryGetComponent<SVNGraphItem>(out var item))
                item.Setup(graphText, node, branchName, colorHex, svnManager, prefix);
        }

        // ------------------------------------------------------------------
        // Branch / merge detection
        // ------------------------------------------------------------------
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;

            path = path.TrimStart();

            // Typowy format: "M /trunk/..." albo "A /branches/foo (from ...)"
            if (path.Length >= 2 &&
                (path[0] == 'A' || path[0] == 'M' || path[0] == 'D' || path[0] == 'R' ||
                 path[0] == 'a' || path[0] == 'm' || path[0] == 'd' || path[0] == 'r') &&
                path[1] == ' ')
            {
                path = path.Substring(2).TrimStart();
            }

            return path;
        }

        private BranchInfo GetBranchInfo(SVNRevisionNode node)
        {
            if (node.ChangedPaths == null || node.ChangedPaths.Count == 0)
                return BranchInfo.Unknown;

            // 1. Trunk
            foreach (string path in node.ChangedPaths)
            {
                if (IsExactTrunkPath(path))
                    return BranchInfo.Trunk;
            }

            // 2. Branches / tags
            foreach (string path in node.ChangedPaths)
            {
                string normalized = NormalizePath(path);

                int bIdx = normalized.IndexOf("/branches/", StringComparison.OrdinalIgnoreCase);
                if (bIdx >= 0)
                {
                    string after = normalized.Substring(bIdx + 10);
                    int slash = after.IndexOf('/');
                    int paren = after.IndexOf('(');

                    if (paren > 0 && (slash < 0 || paren < slash))
                        after = after.Substring(0, paren).Trim();

                    string name = slash > 0 ? after.Substring(0, slash) : after.Trim();
                    if (!string.IsNullOrEmpty(name))
                        return new BranchInfo(name, NodeType.Branch);
                }

                int tIdx = normalized.IndexOf("/tags/", StringComparison.OrdinalIgnoreCase);
                if (tIdx >= 0)
                {
                    string after = normalized.Substring(tIdx + 6);
                    int slash = after.IndexOf('/');
                    int paren = after.IndexOf('(');

                    if (paren > 0 && (slash < 0 || paren < slash))
                        after = after.Substring(0, paren).Trim();

                    string name = slash > 0 ? after.Substring(0, slash) : after.Trim();
                    if (!string.IsNullOrEmpty(name))
                        return new BranchInfo(name, NodeType.Tag);
                }
            }

            return BranchInfo.Unknown;
        }

        private static bool IsExactTrunkPath(string path)
        {
            path = NormalizePath(path);
            if (string.IsNullOrEmpty(path)) return false;

            return path.Equals("/trunk", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/trunk/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("trunk", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("trunk/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("^/trunk", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("^/trunk/", StringComparison.OrdinalIgnoreCase);
        }

        private string DetectBranchParent(SVNRevisionNode node, string currentBranch)
        {
            if (node.ChangedPaths == null) return null;

            foreach (string path in node.ChangedPaths)
            {
                string normalized = NormalizePath(path);

                if (!normalized.Contains("(from ", StringComparison.OrdinalIgnoreCase))
                    continue;

                int fromIdx = normalized.IndexOf("(from ", StringComparison.OrdinalIgnoreCase);
                if (fromIdx < 0) continue;

                string fromPart = normalized.Substring(fromIdx + 6);
                int colon = fromPart.IndexOf(':');
                if (colon > 0) fromPart = fromPart.Substring(0, colon);

                fromPart = fromPart.Trim().TrimEnd(')');

                string parent = ExtractBranchFromPath(fromPart);
                if (!string.IsNullOrEmpty(parent) && parent != currentBranch)
                    return parent;
            }

            return null;
        }

        private string DetectMergeSourceBranch(SVNRevisionNode node, string currentBranch, ICollection<string> known)
        {
            // Próba z komunikatu i changed paths
            string msg = node.Message ?? "";

            try
            {
                foreach (Match m in MergePathRegex.Matches(msg))
                {
                    string type = m.Groups["type"].Value.ToLowerInvariant();
                    string name = m.Groups["name"].Value;
                    string found = type == "trunk" ? "trunk" : (string.IsNullOrEmpty(name) ? null : name);

                    if (!string.IsNullOrEmpty(found) && found != currentBranch)
                        return found;
                }
            }
            catch (RegexMatchTimeoutException) { }

            foreach (string branch in known)
            {
                if (branch == currentBranch) continue;
                if (WordContains(msg, branch))
                    return branch;
            }

            try
            {
                var m = BranchQuoteRegex.Match(msg);
                if (m.Success)
                {
                    string name = m.Groups["name"].Value;
                    if (name != currentBranch) return name;
                }
            }
            catch (RegexMatchTimeoutException) { }

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
                string first = null;
                foreach (string path in node.ChangedPaths)
                {
                    string b = ExtractBranchFromPath(path);
                    if (string.IsNullOrEmpty(b)) continue;

                    if (first == null) first = b;
                    else if (!first.Equals(b, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private string ExtractBranchFromPath(string path)
        {
            path = NormalizePath(path);
            if (string.IsNullOrEmpty(path)) return null;

            int bIdx = path.IndexOf("/branches/", StringComparison.OrdinalIgnoreCase);
            if (bIdx >= 0)
            {
                string after = path.Substring(bIdx + 10);
                int slash = after.IndexOf('/');
                int paren = after.IndexOf('(');

                if (paren > 0 && (slash < 0 || paren < slash))
                    after = after.Substring(0, paren).Trim();

                return slash > 0 ? after.Substring(0, slash) : after.Trim();
            }

            if (IsExactTrunkPath(path)) return "trunk";

            int tIdx = path.IndexOf("/tags/", StringComparison.OrdinalIgnoreCase);
            if (tIdx >= 0)
            {
                string after = path.Substring(tIdx + 6);
                int slash = after.IndexOf('/');
                int paren = after.IndexOf('(');

                if (paren > 0 && (slash < 0 || paren < slash))
                    after = after.Substring(0, paren).Trim();

                return slash > 0 ? after.Substring(0, slash) : after.Trim();
            }

            return null;
        }

        private static bool WordContains(string text, string word)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word)) return false;

            int idx = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                bool left = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                bool right = idx + word.Length >= text.Length || !char.IsLetterOrDigit(text[idx + word.Length]);

                if (left && right) return true;

                idx = text.IndexOf(word, idx + 1, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        private void DisableAutoLayout()
        {
            if (svnUI.GraphContainer == null) return;

            var lg = svnUI.GraphContainer.GetComponent<VerticalLayoutGroup>();
            if (lg != null) lg.enabled = false;

            var csf = svnUI.GraphContainer.GetComponent<ContentSizeFitter>();
            if (csf != null) csf.enabled = false;
        }

        private void EnableAutoLayout()
        {
            if (svnUI.GraphContainer == null) return;

            var lg = svnUI.GraphContainer.GetComponent<VerticalLayoutGroup>();
            if (lg != null) lg.enabled = true;

            var csf = svnUI.GraphContainer.GetComponent<ContentSizeFitter>();
            if (csf != null) csf.enabled = true;
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
                    _graphItemPool.Push(go);
                else
                    UnityEngine.Object.Destroy(go);
            }
        }

        private void ResetState()
        {
            _instantiatedItems.Clear();
            _branchTypes.Clear();
            _branchFirstRev.Clear();
            _branchLastRev.Clear();
            _branchParent.Clear();
            _branchColorCache.Clear();
            _mergedBranches.Clear();
            _colorSystem.Reset();
        }

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
                LayoutRebuilder.MarkLayoutForRebuild(svnUI.GraphContainer as RectTransform);
        }

        // ------------------------------------------------------------------
        // Color system
        // ------------------------------------------------------------------
        private class BranchColorSystem
        {
            private readonly Dictionary<string, string> _colors = new(StringComparer.OrdinalIgnoreCase);
            private int _index;
            private const float GoldenAngle = 137.508f;

            public string GetColor(string branch)
            {
                if (_colors.TryGetValue(branch, out var existing))
                    return existing;

                float hue = Mathf.Repeat(_index * GoldenAngle, 360f);
                _index++;

                Color c = Color.HSVToRGB(hue / 360f, 0.65f, 0.85f);
                string hex = "#" + ColorUtility.ToHtmlStringRGB(c);

                _colors[branch] = hex;
                return hex;
            }

            public void Reset()
            {
                _colors.Clear();
                _index = 0;
            }
        }
    }
}