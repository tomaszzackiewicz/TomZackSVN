using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public class SVNRevGraph : SVNBase
    {
        private const int MaxPoolSize = 300;
        private const long MaxFrameBudgetMs = 16;
        private const int ItemsPerFrame = 50;

        private static readonly Regex HtmlTagRegex = new Regex("<.*?>", RegexOptions.Compiled);
        private static readonly StringBuilder SharedSb = new StringBuilder(512);

        private readonly List<GameObject> _instantiatedItems = new();
        private readonly Stack<GameObject> _graphItemPool = new();
        private readonly List<SVNGraphItem> _cachedGraphItems = new();

        private Transform _poolContainer;
        private Coroutine _renderCoroutine;

        private float _renderYPosition;
        private float _renderItemHeight = -1f;
        private float _renderSpacing;
        private GraphData _graphData;
        private bool _showGraph = false;

        private struct RenderItemData
        {
            public SVNRevisionNode Node;
            public string BranchName;
            public string BranchColor;
            public string ContextLabel;
            public NodeType Type;
            public bool IsBranchPoint;
            public GraphData.NodeInfo Details;
        }

        public IReadOnlyList<GameObject> InstantiatedItems => _instantiatedItems;
        public GraphData CurrentGraphData => _graphData;

        public void SetShowGraph(bool show) => _showGraph = show;

        public SVNRevGraph(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            InitPoolContainer();
        }

        private void InitPoolContainer()
        {
            if (_poolContainer == null && svnUI?.GraphContainer != null)
            {
                var poolGo = new GameObject("[SVNGraphItem_Pool]");
                _poolContainer = poolGo.transform;
                _poolContainer.SetParent(svnUI.GraphContainer.parent, false);
                poolGo.SetActive(false);
            }
        }

        public void RenderGraphWithData(GraphData graphData, List<SVNRevisionNode> nodes)
        {
            if (graphData == null || nodes == null || nodes.Count == 0)
            {
                SVNLogBridge.LogToOutput("[SVN] No data to render.");
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

            InitPoolContainer();
            _renderCoroutine = svnUI.StartCoroutine(RenderGraphRoutineWithData(nodes, graphData));
        }

        public void RenderGraph(List<SVNRevisionNode> nodes)
        {
            if (nodes == null || nodes.Count == 0)
            {
                SVNLogBridge.LogToOutput("[SVN] No revisions to render.");
                return;
            }

            GraphData data = SVNGraphRenderer.AnalyzeBranches(nodes);
            if (data == null)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Failed to analyze branches.");
                return;
            }

            RenderGraphWithData(data, nodes);
        }

        private IEnumerator RenderGraphRoutineWithData(List<SVNRevisionNode> nodes, GraphData graphData)
        {
            var working = new List<SVNRevisionNode>(nodes);
            working.Sort((a, b) => b.Revision.CompareTo(a.Revision));

            _graphData = graphData;
            var renderData = BuildRenderData(working, graphData);

            DisableAutoLayout();
            ReleaseActiveItemsToPool();

            _instantiatedItems.Clear();
            _instantiatedItems.Capacity = Math.Max(_instantiatedItems.Capacity, renderData.Count);

            _cachedGraphItems.Clear();
            _cachedGraphItems.Capacity = Math.Max(_cachedGraphItems.Capacity, renderData.Count);

            var layoutGroup = svnUI.GraphContainer?.GetComponent<VerticalLayoutGroup>();
            _renderSpacing = layoutGroup != null ? layoutGroup.spacing : 0f;
            _renderYPosition = 0f;

            if (_renderItemHeight <= 0f && svnUI.GraphItemPrefab != null)
            {
                var prefabRect = svnUI.GraphItemPrefab.GetComponent<RectTransform>();
                if (prefabRect != null)
                    _renderItemHeight = prefabRect.rect.height;

                if (_renderItemHeight <= 0f)
                {
                    var le = svnUI.GraphItemPrefab.GetComponent<LayoutElement>();
                    if (le != null) _renderItemHeight = le.preferredHeight;
                }

                if (_renderItemHeight <= 0f) _renderItemHeight = 40f;
            }

            SVNLogBridge.LogToOutput($"[SVN] Rendering {renderData.Count} items (optimized)...");

            var stopwatch = Stopwatch.StartNew();
            int processedThisFrame = 0;

            for (int i = 0; i < renderData.Count; i++)
            {
                var data = renderData[i];

                InstantiateGraphItem(
                    data.Node,
                    data.BranchName,
                    data.BranchColor,
                    data.ContextLabel,
                    data.Type,
                    data.IsBranchPoint,
                    data.Details
                );

                processedThisFrame++;

                if (processedThisFrame >= ItemsPerFrame || stopwatch.ElapsedMilliseconds >= MaxFrameBudgetMs)
                {
                    processedThisFrame = 0;
                    stopwatch.Restart();
                    yield return null;
                }
            }

            if (svnUI.GraphContainer is RectTransform containerRect)
            {
                containerRect.sizeDelta = new Vector2(containerRect.sizeDelta.x, _renderYPosition);
            }

            svnUI.StartCoroutine(EnableLayoutSafely());

            SVNLogBridge.LogToOutput($"[SVN] Render complete. {_instantiatedItems.Count} revisions, {_graphData.ColumnCount} branches tracked.");
            _renderCoroutine = null;
        }

        private List<RenderItemData> BuildRenderData(List<SVNRevisionNode> nodes, GraphData graphData)
        {
            var renderData = new List<RenderItemData>(nodes.Count);

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];

                if (!graphData.NodeDetails.TryGetValue(node.Revision, out var details))
                    continue;

                if (details.BranchName == "unknown")
                {
                    details.BranchName = "trunk";
                    details.Type = NodeType.Trunk;
                    graphData.NodeDetails[node.Revision] = details;
                }

                string branchColor = graphData.BranchColors.TryGetValue(details.BranchName, out var c) ? c : "#555555";
                string contextLabel = BuildContextLabel(details, graphData);

                renderData.Add(new RenderItemData
                {
                    Node = node,
                    BranchName = details.BranchName,
                    BranchColor = branchColor,
                    ContextLabel = contextLabel,
                    Type = details.Type,
                    IsBranchPoint = details.IsBranchPoint,
                    Details = details
                });
            }

            return renderData;
        }

        private string BuildContextLabel(GraphData.NodeInfo details, GraphData graphData)
        {
            if (details.IsBranchPoint)
            {
                string parentColor = graphData.BranchColors.TryGetValue(details.ParentBranch, out var pc) ? pc : "yellow";
                SharedSb.Clear();
                SharedSb.Append("<color=").Append(parentColor).Append("><size=90%>+ from ").Append(details.ParentBranch);
                if (details.CopyFromRev > 0)
                    SharedSb.Append(" r").Append(details.CopyFromRev);
                SharedSb.Append("</size></color>");
                return SharedSb.ToString();
            }

            if (!string.IsNullOrEmpty(details.MergeSource))
            {
                string srcColor = graphData.BranchColors.TryGetValue(details.MergeSource, out var sc) ? sc : "#FFFFFF";
                return string.Concat("<color=", srcColor, "><size=90%>↓ merged from ", details.MergeSource, "</size></color>");
            }

            return string.Empty;
        }

        private IEnumerator EnableLayoutSafely()
        {
            yield return null;
            EnableAutoLayout();
        }

        private void InstantiateGraphItem(
            SVNRevisionNode node,
            string branchName,
            string branchColor,
            string contextLabel,
            NodeType branchType,
            bool isBranchPoint,
            GraphData.NodeInfo details)
        {
            if (svnUI.GraphItemPrefab == null || svnUI.GraphContainer == null)
                return;

            GameObject itemGo;

            if (_graphItemPool.Count > 0)
            {
                itemGo = _graphItemPool.Pop();
                if (itemGo == null)
                {
                    itemGo = UnityEngine.Object.Instantiate(svnUI.GraphItemPrefab, svnUI.GraphContainer);
                }
                else
                {
                    itemGo.transform.SetParent(svnUI.GraphContainer, false);
                    itemGo.SetActive(true);
                }
            }
            else
            {
                itemGo = UnityEngine.Object.Instantiate(svnUI.GraphItemPrefab, svnUI.GraphContainer);
            }

            _instantiatedItems.Add(itemGo);

            if (itemGo.TryGetComponent<RectTransform>(out var rt))
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(0f, -_renderYPosition);
                _renderYPosition += _renderItemHeight + _renderSpacing;
            }

            if (itemGo.TryGetComponent<SVNGraphItem>(out var item))
            {
                _cachedGraphItems.Add(item);
                item.Setup("", node, branchName, branchColor, svnManager,
                    contextLabel, branchType, isBranchPoint, details);
            }
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

                if (_poolContainer != null)
                    go.transform.SetParent(_poolContainer, false);

                if (_graphItemPool.Count < MaxPoolSize)
                    _graphItemPool.Push(go);
                else
                    UnityEngine.Object.Destroy(go);
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
            _cachedGraphItems.Clear();
            _graphData = null;
        }

        public void CollapseAll() => ToggleAll(false);
        public void ExpandAll() => ToggleAll(true);

        private void ToggleAll(bool expanded)
        {
            for (int i = 0; i < _cachedGraphItems.Count; i++)
            {
                if (_cachedGraphItems[i] != null)
                    _cachedGraphItems[i].SetExpanded(expanded);
            }

            RefreshLayout();
        }

        private void RefreshLayout()
        {
            if (svnUI.GraphContainer != null)
                LayoutRebuilder.MarkLayoutForRebuild(svnUI.GraphContainer as RectTransform);
        }

        public void ExportHistoryToTxt()
        {
            if (_cachedGraphItems == null || _cachedGraphItems.Count == 0)
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Graph is empty.");
                return;
            }

            // === FIX D3: sort na KOPII — wcześniej Sort() in-place na _cachedGraphItems
            // trwale zmieniał kolejność cache'u (efekt uboczny "getteru raportu";
            // nieszkodliwe dla działania, ale nieoczekiwany side-effect).
            var items = new List<SVNGraphItem>(_cachedGraphItems);
            items.Sort((a, b) => b.GetRevision().CompareTo(a.GetRevision()));

            var authorStats = new Dictionary<string, (int Commits, int FileChanges)>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;

                string author = string.IsNullOrWhiteSpace(item.GetAuthor()) ? "Unknown" : item.GetAuthor();
                int changes = item.GetChangedPaths()?.Count ?? 0;

                if (authorStats.TryGetValue(author, out var cur))
                    authorStats[author] = (cur.Commits + 1, cur.FileChanges + changes);
                else
                    authorStats[author] = (1, changes);
            }

            SharedSb.Clear();
            SharedSb.AppendLine("=== SVN REVISION HISTORY REPORT ===");
            SharedSb.AppendLine(string.Concat("Generated: ", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            SharedSb.AppendLine(string.Concat("Total Revisions: ", items.Count));
            SharedSb.AppendLine("===================================");
            SharedSb.AppendLine();
            SharedSb.AppendLine("=== AUTHOR STATISTICS ===");

            var sortedStats = new List<KeyValuePair<string, (int Commits, int FileChanges)>>(authorStats);
            sortedStats.Sort((a, b) => b.Value.Commits != a.Value.Commits
                ? b.Value.Commits.CompareTo(a.Value.Commits)
                : string.Compare(a.Key, b.Key, StringComparison.Ordinal));

            for (int i = 0; i < sortedStats.Count; i++)
            {
                var kvp = sortedStats[i];
                SharedSb.AppendLine(string.Format(" {0,-25} | Commits: {1,-6} | Files: {2,-6}", kvp.Key, kvp.Value.Commits, kvp.Value.FileChanges));
            }

            SharedSb.AppendLine("==============================");
            SharedSb.AppendLine();

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;

                SharedSb.Append("[r").Append(item.GetRevision()).AppendLine("]");
                SharedSb.Append("Date: ").AppendLine(item.GetDate());
                SharedSb.Append("Author: ").AppendLine(item.GetAuthor());
                SharedSb.Append("Branch: [").Append(item.GetBranchName()).AppendLine("]");
                SharedSb.Append("Message: ").AppendLine(item.GetMessage());

                var paths = item.GetChangedPaths();
                if (paths != null && paths.Count > 0)
                {
                    SharedSb.AppendLine("Changes:");
                    for (int j = 0; j < paths.Count; j++)
                    {
                        SharedSb.Append(" ").AppendLine(HtmlTagRegex.Replace(paths[j], ""));
                    }
                }

                SharedSb.AppendLine("-----------------------------------");
            }

            svnManager.GetModule<SVNExternal>()?.SaveHistoryToFile(SharedSb.ToString());
        }
    }
}