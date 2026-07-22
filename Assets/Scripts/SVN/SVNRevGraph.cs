using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public enum NodeType { Trunk, Branch, Tag }

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
    }

    public class SVNRevGraph : SVNBase
    {
        #region Constants
        private const string VERT_TRUNK = "│ ";
        private const string VERT_BRANCH = "│ ";
        private const string VERT_TAG = "│ ";
        private const string VERT_MERGED = "┆ ";

        private const string SHAPE_TRUNK = "■";
        private const string SHAPE_BRANCH = "●";
        private const string SHAPE_TAG = "◆";
        private const string SHAPE_MERGE_FROM_TRUNK = "▣";
        private const string SHAPE_MERGE_FROM_BRANCH = "◉";
        private const string SHAPE_MERGE_FROM_TAG = "◈";

        private const string SPACER = " ";
        private const string COLOR_INACTIVE = "#00000000";
        private const string COLOR_MERGED_INACTIVE = "#88888844";
        private const string COLOR_BLACK = "#000000";

        private static readonly Regex MergePathRegex = new(
            @"(?:^|[\s\(\)\[\]\{\}""'`])/?(\^?/)?(?<type>branches|trunk|tags)(/(?<name>[^\s,;:\)]+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BranchQuoteRegex = new(
            @"branch\s*['""](?<name>[^'""]+)['""]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled);
        #endregion

        public SVNRevGraph(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        #region Fields
        private readonly List<GameObject> _instantiatedItems = new();
        private readonly Dictionary<string, NodeType> _branchTypes = new();
        private readonly HashSet<string> _mergedBranches = new();
        private readonly Dictionary<string, long> _branchFirstRev = new();
        private readonly Dictionary<string, long> _branchLastRev = new();
        private readonly Dictionary<string, string> _branchParent = new();
        private readonly Dictionary<string, string> _branchColorCache = new();

        public IReadOnlyList<GameObject> InstantiatedItems => _instantiatedItems;
        #endregion

        #region RenderGraph
        public void RenderGraph(List<SVNRevisionNode> nodes)
        {
            if (nodes == null || nodes.Count == 0)
            {
                SVNLogBridge.LogLine("[SVN] No revisions to render.");
                return;
            }

            // Sort ascending for analysis phase
            nodes.Sort((a, b) => a.Revision.CompareTo(b.Revision));

            ClearGraph();
            ResetState();

            // === PHASE 1: Analyze branches and columns ===
            var branchColumns = AnalyzeBranches(nodes, out int columnCount);

            // Sort descending for rendering (newest first)
            nodes.Sort((a, b) => b.Revision.CompareTo(a.Revision));

            // Pre-compute branch colors
            foreach (var branch in branchColumns.Keys)
            {
                _branchColorCache[branch] = BranchColorSystem.GetColor(branch);
            }

            // === PHASE 2: Render nodes ===
            var columnBranches = BuildColumnLookup(branchColumns, columnCount);

            foreach (var node in nodes)
            {
                RenderNode(node, branchColumns, columnBranches, columnCount);
            }

            SVNLogBridge.LogLine($"[SVN] Render complete. {_instantiatedItems.Count} revisions rendered.");
        }

        private void ClearGraph()
        {
            if (svnUI.GraphContainer == null) return;

            foreach (Transform t in svnUI.GraphContainer)
            {
                if (t != null)
                    UnityEngine.Object.Destroy(t.gameObject);
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
            BranchColorSystem.Reset();
        }
        #endregion

        #region Branch Analysis
        private Dictionary<string, int> AnalyzeBranches(List<SVNRevisionNode> nodes, out int columnCount)
        {
            var branchColumns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            columnCount = 0;

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var info = GetBranchInfo(node);

                RegisterBranch(node, info, branchColumns, ref columnCount);

                if (info.Type != NodeType.Trunk && _branchFirstRev[info.Name] == node.Revision)
                {
                    DetectAndStoreBranchParent(node, info.Name);
                }

                if (IsMergeCommit(node))
                {
                    DetectAndRegisterMergeSource(node, info.Name, branchColumns, ref columnCount);
                }
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
            var knownBranches = branchColumns.Keys;
            string mergeSrcBranch = DetectMergeSourceBranch(node, currentBranch, knownBranches);

            if (string.IsNullOrEmpty(mergeSrcBranch))
            {
                // Fallback: use first other branch
                foreach (var kv in branchColumns)
                {
                    if (kv.Key != currentBranch)
                    {
                        mergeSrcBranch = kv.Key;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(mergeSrcBranch))
            {
                _mergedBranches.Add(mergeSrcBranch);

                if (!branchColumns.ContainsKey(mergeSrcBranch))
                {
                    RegisterMergeSourceBranch(mergeSrcBranch, branchColumns, ref nextColumn, node.Revision);
                }
                else
                {
                    UpdateBranchLastRev(mergeSrcBranch, node.Revision);
                }
            }
        }

        private void RegisterMergeSourceBranch(string branchName, Dictionary<string, int> branchColumns,
            ref int nextColumn, long revision)
        {
            branchColumns[branchName] = nextColumn++;

            NodeType type;
            if (branchName == "trunk")
            {
                type = NodeType.Trunk;
            }
            else if (_branchTypes.TryGetValue(branchName, out var existingType) && existingType == NodeType.Tag)
            {
                type = NodeType.Tag;
            }
            else
            {
                type = NodeType.Branch;
            }

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
        #endregion

        #region Node Rendering
        private void RenderNode(SVNRevisionNode node, Dictionary<string, int> branchColumns,
            string[] columnBranches, int columnCount)
        {
            var info = GetBranchInfo(node);
            int col = branchColumns[info.Name];
            string colHex = _branchColorCache[info.Name];

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

            string mergeSrc = DetectMergeSourceBranch(node, currentBranch, branchColumns.Keys);

            if (string.IsNullOrEmpty(mergeSrc))
            {
                foreach (var kv in branchColumns)
                {
                    if (kv.Key != currentBranch)
                    {
                        mergeSrc = kv.Key;
                        break;
                    }
                }
            }

            return mergeSrc;
        }

        private string BuildPrefix(SVNRevisionNode node, BranchInfo info, string mergeSrcBranch)
        {
            var prefix = new StringBuilder();

            if (info.Type != NodeType.Trunk && node.Revision == _branchFirstRev[info.Name])
            {
                string parent = _branchParent.TryGetValue(info.Name, out var p) ? p : "trunk";
                prefix.Append($"<color={COLOR_BLACK}>[branched from {parent}]</color> ");
            }

            if (!string.IsNullOrEmpty(mergeSrcBranch))
            {
                prefix.Append($"<color={COLOR_BLACK}>[merged from {mergeSrcBranch}]</color> ");
            }

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
                    _ => SHAPE_BRANCH
                };
            }

            if (mergeSrcBranch == "trunk")
                return SHAPE_MERGE_FROM_TRUNK;

            if (_branchTypes.TryGetValue(mergeSrcBranch, out var srcType) && srcType == NodeType.Tag)
                return SHAPE_MERGE_FROM_TAG;

            return SHAPE_MERGE_FROM_BRANCH;
        }

        private string BuildGraphText(int columnCount, int currentCol, SVNRevisionNode node,
            BranchInfo info, string shape, string mergeSrcBranch, bool isMerge, string[] columnBranches)
        {
            var g = new StringBuilder(columnCount * 8);

            for (int c = 0; c < columnCount; c++)
            {
                string laneBranch = c < columnCount ? columnBranches[c] : null;
                string laneColor = laneBranch != null ? _branchColorCache[laneBranch] : "#555555";
                string finalColor = laneColor;
                string laneText;

                bool isCurrent = (c == currentCol);
                bool isActive = laneBranch != null &&
                                node.Revision >= _branchFirstRev[laneBranch] &&
                                node.Revision <= _branchLastRev[laneBranch];

                if (isCurrent)
                {
                    if (isMerge && !string.IsNullOrEmpty(mergeSrcBranch))
                        finalColor = _branchColorCache.GetValueOrDefault(mergeSrcBranch, laneColor);

                    laneText = shape + SPACER;
                }
                else if (isActive)
                {
                    laneText = GetVerticalLine(laneBranch);
                }
                else
                {
                    bool isMergedAndInactive = _mergedBranches.Contains(laneBranch) &&
                                               node.Revision > _branchLastRev[laneBranch];

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
            if (!_branchTypes.TryGetValue(branchName, out var type))
                return VERT_BRANCH;

            return type switch
            {
                NodeType.Trunk => VERT_TRUNK,
                NodeType.Branch => VERT_BRANCH,
                NodeType.Tag => VERT_TAG,
                _ => VERT_BRANCH
            };
        }

        private void InstantiateGraphItem(string graphText, SVNRevisionNode node, string branchName,
            string colorHex, string prefix)
        {
            if (svnUI.GraphItemPrefab == null || svnUI.GraphContainer == null)
            {
                SVNLogBridge.LogError("[SVN] GraphItemPrefab or GraphContainer is null.");
                return;
            }

            GameObject itemGo = UnityEngine.Object.Instantiate(svnUI.GraphItemPrefab, svnUI.GraphContainer);
            _instantiatedItems.Add(itemGo);

            if (itemGo.TryGetComponent<SVNGraphItem>(out var item))
            {
                item.Setup(graphText, node, branchName, colorHex, svnManager, prefix);
            }
        }
        #endregion

        #region Branch Detection
        private string DetectBranchParent(SVNRevisionNode node, string currentBranch)
        {
            if (node.ChangedPaths == null) return null;

            foreach (string path in node.ChangedPaths)
            {
                if (!path.StartsWith("A ") || !path.Contains("(from "))
                    continue;

                int fromIdx = path.IndexOf("(from ");
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

            // Try regex on message first
            var pathMatches = MergePathRegex.Matches(msg);
            foreach (Match m in pathMatches)
            {
                string type = m.Groups["type"].Value.ToLowerInvariant();
                string name = m.Groups["name"].Value;
                string found = type == "trunk" ? "trunk" : (string.IsNullOrEmpty(name) ? null : name);

                if (!string.IsNullOrEmpty(found) && found != currentBranch)
                    return found;
            }

            // Try known branches by name
            foreach (string branch in knownBranches)
            {
                if (branch == currentBranch) continue;
                if (Regex.IsMatch(msg, $"\\b{Regex.Escape(branch)}\\b", RegexOptions.IgnoreCase))
                    return branch;
            }

            // Try "branch 'name'" pattern
            var branchWordMatch = BranchQuoteRegex.Match(msg);
            if (branchWordMatch.Success)
            {
                string name = branchWordMatch.Groups["name"].Value;
                if (name != currentBranch) return name;
            }

            // Try changed paths
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

        private string ExtractBranchFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            // Check for branches
            int branchesIdx = path.IndexOf("/branches/", StringComparison.OrdinalIgnoreCase);
            if (branchesIdx >= 0)
            {
                string afterBranches = path.Substring(branchesIdx + 10);
                int nextSlash = afterBranches.IndexOf('/');
                return nextSlash > 0 ? afterBranches.Substring(0, nextSlash) : afterBranches;
            }

            // Check for trunk - must be exact match
            if (path.Equals("/trunk", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/trunk/", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/trunk", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/trunk/", StringComparison.OrdinalIgnoreCase))
            {
                return "trunk";
            }

            // Check for tags
            int tagsIdx = path.IndexOf("/tags/", StringComparison.OrdinalIgnoreCase);
            if (tagsIdx >= 0)
            {
                string afterTags = path.Substring(tagsIdx + 6);
                int nextSlash = afterTags.IndexOf('/');
                return nextSlash > 0 ? afterTags.Substring(0, nextSlash) : afterTags;
            }

            return null;
        }
        #endregion

        #region Merge Detection
        private bool IsMergeCommit(SVNRevisionNode node)
        {
            if (node == null) return false;
            if (node.HasMergeInfoChange) return true;

            if (!string.IsNullOrEmpty(node.Message))
            {
                string msg = node.Message.ToLowerInvariant();
                if (msg.Contains("merge") || msg.Contains("merged") || msg.Contains("reintegrate"))
                    return true;
            }

            if (node.ChangedPaths != null)
            {
                var branches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in node.ChangedPaths)
                {
                    string b = ExtractBranchFromPath(path);
                    if (!string.IsNullOrEmpty(b))
                        branches.Add(b);
                }
                if (branches.Count >= 2)
                    return true;
            }

            return false;
        }
        #endregion

        #region Branch Info
        private BranchInfo GetBranchInfo(SVNRevisionNode node)
        {
            if (node.ChangedPaths == null || node.ChangedPaths.Count == 0)
                return BranchInfo.Trunk;

            foreach (string path in node.ChangedPaths)
            {
                // Check branches first
                int branchesIdx = path.IndexOf("/branches/", StringComparison.OrdinalIgnoreCase);
                if (branchesIdx >= 0)
                {
                    string afterBranches = path.Substring(branchesIdx + 10);
                    int nextSlash = afterBranches.IndexOf('/');
                    string name = nextSlash > 0 ? afterBranches.Substring(0, nextSlash) : afterBranches;
                    return new BranchInfo(name, NodeType.Branch);
                }

                // Check tags
                int tagsIdx = path.IndexOf("/tags/", StringComparison.OrdinalIgnoreCase);
                if (tagsIdx >= 0)
                {
                    string afterTags = path.Substring(tagsIdx + 6);
                    int nextSlash = afterTags.IndexOf('/');
                    string name = nextSlash > 0 ? afterTags.Substring(0, nextSlash) : afterTags;
                    return new BranchInfo(name, NodeType.Tag);
                }

                // Check trunk
                if (path.Equals("/trunk", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/trunk/", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("/trunk", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("/trunk/", StringComparison.OrdinalIgnoreCase))
                {
                    return BranchInfo.Trunk;
                }
            }

            return BranchInfo.Trunk;
        }
        #endregion

        #region Expand/Collapse
        public void CollapseAll()
        {
            ToggleAll(false);
        }

        public void ExpandAll()
        {
            ToggleAll(true);
        }

        private void ToggleAll(bool expanded)
        {
            bool anyChanged = false;

            foreach (GameObject go in _instantiatedItems)
            {
                if (go == null) continue;

                if (go.TryGetComponent<SVNGraphItem>(out var item))
                {
                    item.SetExpanded(expanded);
                    anyChanged = true;
                }
            }

            if (anyChanged)
                RefreshLayout();
        }

        private void RefreshLayout()
        {
            if (svnUI.GraphContainer != null)
            {
                LayoutRebuilder.MarkLayoutForRebuild(svnUI.GraphContainer as RectTransform);
            }
        }
        #endregion

        #region Export
        public void ExportHistoryToTxt()
        {
            if (_instantiatedItems == null || _instantiatedItems.Count == 0)
            {
                SVNLogBridge.LogError("[SVN] Graph revision is empty.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== SVN REVISION HISTORY REPORT ===");
            sb.AppendLine($"Generated: {DateTime.Now}");
            sb.AppendLine($"Total Revisions: {_instantiatedItems.Count}");
            sb.AppendLine("===================================\n");

            foreach (GameObject go in _instantiatedItems)
            {
                if (go == null) continue;

                if (!go.TryGetComponent<SVNGraphItem>(out var item))
                    continue;

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
        #endregion

        #region Color System
        public static class BranchColorSystem
        {
            private static readonly Dictionary<string, string> _branchColors = new();
            private static int _index = 0;
            private const float GOLDEN_ANGLE = 137.508f;

            public static string GetColor(string branch)
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

            public static void Reset()
            {
                _branchColors.Clear();
                _index = 0;
            }
        }
        #endregion

        #region Internal Types
        private class GraphNode
        {
            public SVNRevisionNode Revision;
            public int Row;
            public int Lane;
            public List<GraphNode> Parents = new();
            public string Branch;
        }
        #endregion
    }

    #region Extension Methods
    public static class DictionaryExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue)
        {
            return dict.TryGetValue(key, out var value) ? value : defaultValue;
        }
    }
    #endregion
}
