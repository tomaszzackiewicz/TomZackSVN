using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public class SVNRevGraph : SVNBase
    {
        private const int MaxPoolSize = 250;
        private const long MaxFrameBudgetMs = 10;
        private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled);

        private readonly List<GameObject> _instantiatedItems = new();
        private readonly Stack<GameObject> _graphItemPool = new();

        private Coroutine _renderCoroutine;
        private float _renderYPosition;
        private float _renderItemHeight;
        private float _renderSpacing;
        private GraphData _graphData;
        private bool _showGraph = false;

        public IReadOnlyList<GameObject> InstantiatedItems => _instantiatedItems;
        public GraphData CurrentGraphData => _graphData;

        public void SetShowGraph(bool show) => _showGraph = show;

        public SVNRevGraph(SVNUI ui, SVNManager manager) : base(ui, manager) { }

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
            _graphData = null;
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

        private IEnumerator RenderGraphRoutine(List<SVNRevisionNode> nodes)
        {
            var working = new List<SVNRevisionNode>(nodes);
            working.Sort((a, b) => a.Revision.CompareTo(b.Revision));

            SVNLogBridge.LogToOutput($"[SVN] Analyzing {working.Count} revisions...");

            _graphData = SVNGraphRenderer.AnalyzeBranches(working);

            if (_graphData == null)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Failed to analyze branches.");
                _renderCoroutine = null;
                yield break;
            }

            DisableAutoLayout();
            ReleaseActiveItemsToPool();
            _instantiatedItems.Clear();

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

            if (_renderItemHeight <= 0)
                _renderItemHeight = 40f;

            SVNLogBridge.LogToOutput($"[SVN] Rendering history view, item height: {_renderItemHeight}...");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            for (int i = working.Count - 1; i >= 0; i--)
            {
                var node = working[i];

                if (!_graphData.NodeDetails.TryGetValue(node.Revision, out var details))
                    continue;

                if (details.BranchName == "unknown")
                {
                    details.BranchName = "trunk";
                    details.Type = NodeType.Trunk;
                    _graphData.NodeDetails[node.Revision] = details;
                }

                string branchColor = _graphData.BranchColors.TryGetValue(details.BranchName, out var c) ? c : "#555555";

                string contextLabel = "";

                if (details.IsBranchPoint)
                {
                    string parentColor = _graphData.BranchColors.TryGetValue(details.ParentBranch, out var pc) ? pc : "#888888";
                    string fromRev = details.CopyFromRev > 0 ? $" r{details.CopyFromRev}" : "";
                    contextLabel = $"<color={parentColor}><size=90%>+ from {details.ParentBranch}{fromRev}</size></color>";
                }
                else if (!string.IsNullOrEmpty(details.MergeSource))
                {
                    string srcColor = _graphData.BranchColors.TryGetValue(details.MergeSource, out var sc) ? sc : "#FFFFFF";
                    contextLabel = $"<color={srcColor}><size=90%>↓ merged from {details.MergeSource}</size></color>";
                }

                InstantiateGraphItem("", node, details.BranchName, branchColor, contextLabel, details.Type, details.IsBranchPoint, details);

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

            SVNLogBridge.LogToOutput($"[SVN] Render complete. {_instantiatedItems.Count} revisions, {_graphData.ColumnCount} branches tracked.");
            _renderCoroutine = null;
        }

        private void InstantiateGraphItem(string graphUnused, SVNRevisionNode node, string branchName,
    string branchColor, string contextLabel, NodeType branchType, bool isBranchPoint,
    GraphData.NodeInfo details = default)
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
                    itemGo = UnityEngine.Object.Instantiate(svnUI.GraphItemPrefab, svnUI.GraphContainer);
            }
            else
                itemGo = UnityEngine.Object.Instantiate(svnUI.GraphItemPrefab, svnUI.GraphContainer);

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
                item.Setup(graphUnused, node, branchName, branchColor, svnManager, contextLabel, branchType, isBranchPoint, details);
        }

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
    }
}