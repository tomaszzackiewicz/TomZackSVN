using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public enum NodeType { Trunk, Branch, Tag }

    public struct BranchInfo
    {
        public string Name;
        public NodeType Type;
    }

    public class SVNRevGraph : SVNBase
    {
        public SVNRevGraph(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private List<GameObject> instantiatedItems = new List<GameObject>();

        public List<GameObject> InstantiatedItems => instantiatedItems;

        public void RenderGraph(List<SVNRevisionNode> nodes)
        {
            foreach (Transform t in svnUI.GraphContainer)
            {
                Object.Destroy(t.gameObject);
            }
            instantiatedItems.Clear();

            Dictionary<string, int> branchToColumn = new Dictionary<string, int>();
            int nextFreeColumn = 0;

            string[] railColors = { "#55FF55", "#5555FF", "#FFFF55", "#FF55FF", "#00FFFF", "#FF9900" };
            string tagColorHex = "#FFA500";

            foreach (var node in nodes)
            {
                BranchInfo info = GetBranchInfo(node);

                if (!branchToColumn.ContainsKey(info.Name))
                {
                    branchToColumn[info.Name] = nextFreeColumn++;
                }

                int currentColumn = branchToColumn[info.Name];

                string branchColor = (info.Type == NodeType.Tag) ? tagColorHex : railColors[currentColumn % railColors.Length];

                StringBuilder treeStr = new StringBuilder();
                int maxVisibleColumns = Mathf.Max(3, nextFreeColumn);
                string gap = "   ";

                for (int i = 0; i < maxVisibleColumns; i++)
                {
                    string colHex = railColors[i % railColors.Length];

                    if (i == currentColumn)
                    {
                        string symbol = (info.Type == NodeType.Tag) ? "T" : "*";
                        treeStr.Append($"<color={branchColor}><b>{symbol}</b></color>{gap}");
                    }
                    else if (i < nextFreeColumn)
                    {
                        treeStr.Append($"<color={colHex}>|</color>{gap}");
                    }
                    else
                    {
                        treeStr.Append($" {gap}");
                    }
                }

                GameObject itemGo = Object.Instantiate(svnUI.GraphItemPrefab, svnUI.GraphContainer);

                instantiatedItems.Add(itemGo);

                SVNGraphItem item = itemGo.GetComponent<SVNGraphItem>();
                if (item != null)
                {
                    item.Setup(treeStr.ToString(), node, info.Name, branchColor);
                }
            }

            Debug.Log($"[SVN] Render complete. Generated and registered {instantiatedItems.Count} rows.");
        }

        private BranchInfo GetBranchInfo(SVNRevisionNode node)
        {
            if (node.ChangedPaths == null) return new BranchInfo { Name = "trunk", Type = NodeType.Trunk };

            foreach (string path in node.ChangedPaths)
            {
                if (path.Contains("/branches/"))
                {
                    string[] parts = path.Split('/');
                    int idx = System.Array.IndexOf(parts, "branches");
                    if (idx >= 0 && idx + 1 < parts.Length)
                        return new BranchInfo { Name = parts[idx + 1], Type = NodeType.Branch };
                }
                if (path.Contains("/tags/"))
                {
                    string[] parts = path.Split('/');
                    int idx = System.Array.IndexOf(parts, "tags");
                    if (idx >= 0 && idx + 1 < parts.Length)
                        return new BranchInfo { Name = parts[idx + 1], Type = NodeType.Tag };
                }
                if (path.Contains("/trunk"))
                {
                    return new BranchInfo { Name = "trunk", Type = NodeType.Trunk };
                }
            }
            return new BranchInfo { Name = "trunk", Type = NodeType.Trunk };
        }

        public void CollapseAll()
        {
            foreach (GameObject go in instantiatedItems)
            {
                if (go != null)
                {
                    SVNGraphItem item = go.GetComponent<SVNGraphItem>();
                    if (item != null)
                    {
                        item.SetExpanded(false);

                        LayoutRebuilder.ForceRebuildLayoutImmediate(go.GetComponent<RectTransform>());
                    }
                }
            }

            RefreshLayout();
        }

        public void ExpandAll()
        {
            foreach (GameObject go in instantiatedItems)
            {
                if (go != null)
                {
                    SVNGraphItem item = go.GetComponent<SVNGraphItem>();
                    if (item != null)
                    {
                        item.SetExpanded(true);

                        RectTransform rect = go.GetComponent<RectTransform>();
                        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                    }
                }
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
    }
}