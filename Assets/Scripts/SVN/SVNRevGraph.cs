using System;
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
        private Dictionary<(string Name, NodeType Type), bool> branchStarted = new();
        private Dictionary<string, NodeType> branchTypes = new();

        public List<GameObject> InstantiatedItems => instantiatedItems;

        public void RenderGraph(List<SVNRevisionNode> nodes)
        {
            foreach (Transform t in svnUI.GraphContainer)
                UnityEngine.Object.Destroy(t.gameObject);

            instantiatedItems.Clear();
            branchStarted.Clear();
            branchTypes.Clear();

            Dictionary<string, int> branchColumns = new();
            Dictionary<string, int> branchStartRow = new();
            Dictionary<string, int> branchEndRow = new();
            int nextColumn = 0;

            // =====================================================
            // 1. ANALIZA STRUKTURY
            // =====================================================
            for (int r = 0; r < nodes.Count; r++)
            {
                var node = nodes[r];
                BranchInfo info = GetBranchInfo(node);

                if (!branchColumns.ContainsKey(info.Name))
                {
                    branchColumns[info.Name] = nextColumn++;
                    branchStartRow[info.Name] = r;
                    branchTypes[info.Name] = info.Type;
                }

                branchEndRow[info.Name] = r;

                if (IsMergeCommit(node))
                {
                    string mergeSrcBranch = DetectMergeSourceBranch(node, info.Name, branchColumns.Keys);
                    if (!string.IsNullOrEmpty(mergeSrcBranch))
                    {
                        if (!branchColumns.ContainsKey(mergeSrcBranch))
                        {
                            branchColumns[mergeSrcBranch] = nextColumn++;
                            branchStartRow[mergeSrcBranch] = r;
                            branchTypes[mergeSrcBranch] = mergeSrcBranch == "trunk"
                                ? NodeType.Trunk
                                : NodeType.Branch;
                        }

                        if (!branchEndRow.ContainsKey(mergeSrcBranch) ||
                            branchEndRow[mergeSrcBranch] < r)
                            branchEndRow[mergeSrcBranch] = r;
                    }
                }
            }

            // =====================================================
            // 2. RYSOWANIE
            // =====================================================
            for (int r = 0; r < nodes.Count; r++)
            {
                var node = nodes[r];
                BranchInfo info = GetBranchInfo(node);

                int col = branchColumns[info.Name];
                string colHex = BranchColorSystem.GetColor(info.Name);

                StringBuilder g = new StringBuilder();

                bool isMerge = IsMergeCommit(node);
                string mergeSrcBranch = DetectMergeSourceBranch(node, info.Name, branchColumns.Keys);
                int srcCol = -1;
                if (isMerge && !string.IsNullOrEmpty(mergeSrcBranch) &&
                    branchColumns.ContainsKey(mergeSrcBranch))
                    srcCol = branchColumns[mergeSrcBranch];

                // [FALLBACK] Jeśli to merge i nie znaleziono źródła z wiadomości,
                // spróbuj dedukować z aktywnych gałęzi w tym wierszu
                if (isMerge && srcCol == -1)
                {
                    List<string> activeOthers = new List<string>();
                    foreach (var kv in branchColumns)
                    {
                        if (kv.Key != info.Name && r >= branchStartRow[kv.Key] && r <= branchEndRow[kv.Key])
                            activeOthers.Add(kv.Key);
                    }
                    if (activeOthers.Count == 1)
                    {
                        mergeSrcBranch = activeOthers[0];
                        srcCol = branchColumns[mergeSrcBranch];
                    }
                    else if (activeOthers.Count == 0 && info.Name != "trunk")
                    {
                        // często merge z trunka na gałąź bez wzmianki o źródle
                        mergeSrcBranch = "trunk";
                        if (branchColumns.ContainsKey("trunk"))
                            srcCol = branchColumns["trunk"];
                    }
                    // dla trunk: jeśli mamy jedną konkretną gałąź w grafie, możemy zgadnąć
                    else if (info.Name == "trunk" && activeOthers.Count == 0)
                    {
                        // znajdź dowolną gałąź, która jest aktywna gdziekolwiek – raczej nie będzie to poprawne
                    }
                }

                char shape = info.Type switch
                {
                    NodeType.Trunk => '■',
                    NodeType.Branch => '●',
                    NodeType.Tag => '◆',
                    _ => '●'
                };

                for (int i = 0; i < nextColumn; i++)
                {
                    string laneBranch = null;
                    foreach (var kv in branchColumns)
                        if (kv.Value == i) { laneBranch = kv.Key; break; }

                    string laneColor = laneBranch != null
                        ? BranchColorSystem.GetColor(laneBranch)
                        : "#555555";

                    string laneText;
                    string finalColor = laneColor;

                    bool isCurrent = (i == col);
                    bool isActive = laneBranch != null &&
                                    (r >= branchStartRow[laneBranch] &&
                                     r <= branchEndRow[laneBranch]);

                    bool isCrossing = isMerge && srcCol != -1 &&
                                      ((i > col && i < srcCol) || (i > srcCol && i < col));

                    // -------------------------------------------------
                    // CURRENT COLUMN
                    // -------------------------------------------------
                    if (isCurrent)
                    {
                        var key = (info.Name, info.Type);
                        if (!branchStarted.ContainsKey(key))
                            branchStarted[key] = false;

                        if (isMerge && srcCol != -1)
                        {
                            bool fromLeft = srcCol < col;
                            laneText = $"{shape}{(fromLeft ? "┤ " : "├ ")}";
                            branchStarted[key] = true;
                        }
                        else
                        {
                            bool first = !branchStarted[key];
                            laneText = $"{shape}─ ";
                            if (first) branchStarted[key] = true;
                        }
                    }
                    // -------------------------------------------------
                    // ACTIVE LANE
                    // -------------------------------------------------
                    else if (isActive)
                    {
                        NodeType laneType = branchTypes.ContainsKey(laneBranch)
                            ? branchTypes[laneBranch]
                            : NodeType.Branch;

                        if (isCrossing)
                        {
                            laneText = "┼──";
                            finalColor = BranchColorSystem.GetColor(mergeSrcBranch);
                        }
                        else if (isMerge && i == srcCol)
                        {
                            laneText = (col > srcCol) ? "├──" : "┤──";
                            finalColor = BranchColorSystem.GetColor(mergeSrcBranch);
                        }
                        else
                        {
                            laneText = laneType switch
                            {
                                NodeType.Trunk => "┃  ",
                                NodeType.Branch => "│  ",
                                NodeType.Tag => "┆  ",
                                _ => "│  "
                            };
                        }
                    }
                    // -------------------------------------------------
                    // EMPTY LANE
                    // -------------------------------------------------
                    else
                    {
                        if (isCrossing)
                        {
                            laneText = "───";
                            finalColor = BranchColorSystem.GetColor(mergeSrcBranch);
                        }
                        else
                        {
                            NodeType laneType = laneBranch != null && branchTypes.ContainsKey(laneBranch)
                                ? branchTypes[laneBranch]
                                : NodeType.Branch;

                            laneText = laneType switch
                            {
                                NodeType.Trunk => "┃  ",
                                NodeType.Branch => "│  ",
                                NodeType.Tag => "┆  ",
                                _ => "│  "
                            };
                            finalColor = "#00000000";
                        }
                    }

                    g.Append($"<color={finalColor}>{laneText}</color>");
                }

                GameObject itemGo = UnityEngine.Object.Instantiate(
                    svnUI.GraphItemPrefab,
                    svnUI.GraphContainer
                );

                instantiatedItems.Add(itemGo);

                SVNGraphItem item = itemGo.GetComponent<SVNGraphItem>();
                if (item != null)
                {
                    item.Setup(g.ToString(), node, info.Name, colHex, svnManager);
                }
            }

            SVNLogBridge.LogLine($"[SVN] Render complete. {instantiatedItems.Count} revisions rendered.");
        }

        // ================================================================
        // ULEPSZONE WYKRYWANIE ŹRÓDŁA MERGA
        // ================================================================
        private string DetectMergeSourceBranch(SVNRevisionNode node, string currentBranch, ICollection<string> knownBranches)
        {
            string msg = node.Message ?? "";
            string lowerMsg = msg.ToLower();

            // 1. Szukamy /branches/xxx lub /trunk lub /tags/xxx
            var pathMatches = System.Text.RegularExpressions.Regex.Matches(
                msg,
                @"(?:^|[\s\(\)\[\]\{\}""'`])/?(\^?/)?(?<type>branches|trunk|tags)(/(?<name>[^\s,;:\)]+))?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match m in pathMatches)
            {
                string type = m.Groups["type"].Value.ToLower();
                string name = m.Groups["name"].Value;
                string found = type == "trunk" ? "trunk" : (string.IsNullOrEmpty(name) ? null : name);
                if (!string.IsNullOrEmpty(found) && found != currentBranch)
                    return found;
            }

            // 2. Szukamy znanych nazw gałęzi w całej wiadomości
            foreach (string branch in knownBranches)
            {
                if (branch == currentBranch) continue;
                // szukamy całego słowa (granice wyrazów)
                if (System.Text.RegularExpressions.Regex.IsMatch(
                    msg, $@"\b{System.Text.RegularExpressions.Regex.Escape(branch)}\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return branch;
            }

            // 3. Próba wyłapania fraz typu "branch 'xxx'"
            var branchWordMatch = System.Text.RegularExpressions.Regex.Match(
                msg, @"branch\s*['""](?<name>[^'""]+)['""]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (branchWordMatch.Success)
            {
                string name = branchWordMatch.Groups["name"].Value;
                if (name != currentBranch)
                    return name;
            }

            // 4. Stare przeszukiwanie ChangedPaths (rzadko działa)
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
            if (path.Contains("/branches/"))
            {
                string[] parts = path.Split('/');
                int idx = System.Array.IndexOf(parts, "branches");
                if (idx >= 0 && idx + 1 < parts.Length)
                    return parts[idx + 1];
            }
            else if (path.Contains("/trunk"))
                return "trunk";
            else if (path.Contains("/tags/"))
            {
                string[] parts = path.Split('/');
                int idx = System.Array.IndexOf(parts, "tags");
                if (idx >= 0 && idx + 1 < parts.Length)
                    return parts[idx + 1];
            }
            return null;
        }

        // ================================================================
        // WYKRYWANIE CZY COMMIT JEST MERGEM
        // ================================================================
        private bool IsMergeCommit(SVNRevisionNode node)
        {
            if (node == null) return false;

            if (!string.IsNullOrEmpty(node.Message))
            {
                string msg = node.Message.ToLower();
                if (msg.Contains("merge") ||
                    msg.Contains("merged") ||
                    msg.Contains("reintegrate"))
                    return true;
            }

            // fallback: różne gałęzie w ChangedPaths (mało prawdopodobne)
            if (node.ChangedPaths != null)
            {
                var branches = new HashSet<string>();
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

        // ================================================================
        // SYSTEM KOLORÓW
        // ================================================================
        public static class BranchColorSystem
        {
            private static Dictionary<string, string> branchColors = new();
            private static int index = 0;
            private const float GOLDEN_ANGLE = 137.508f;

            public static string GetColor(string branch)
            {
                if (branchColors.TryGetValue(branch, out var existing))
                    return existing;

                float hue = Mathf.Repeat(index * GOLDEN_ANGLE, 360f);
                index++;
                Color c = Color.HSVToRGB(hue / 360f, 0.65f, 0.85f);
                string hex = ColorUtility.ToHtmlStringRGB(c);
                hex = "#" + hex;
                branchColors[branch] = hex;
                return hex;
            }

            public static void Reset()
            {
                branchColors.Clear();
                index = 0;
            }
        }

        // ================================================================
        // POZOSTAŁE METODY
        // ================================================================
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

        public void ExportHistoryToTxt()
        {
            if (instantiatedItems == null || instantiatedItems.Count == 0)
            {
                SVNLogBridge.LogError("[SVN] Graph revision is empty.");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== SVN REVISION HISTORY REPORT ===");
            sb.AppendLine($"Generated: {DateTime.Now}");
            sb.AppendLine($"Total Revisions: {instantiatedItems.Count}");
            sb.AppendLine("===================================\n");

            foreach (GameObject go in instantiatedItems)
            {
                if (go == null) continue;
                SVNGraphItem item = go.GetComponent<SVNGraphItem>();
                if (item != null)
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
                            string cleanPath = System.Text.RegularExpressions.Regex.Replace(path, "<.*?>", string.Empty);
                            sb.AppendLine($"  {cleanPath}");
                        }
                    }
                    sb.AppendLine("-----------------------------------");
                }
            }

            var external = svnManager.GetModule<SVNExternal>();
            if (external != null)
            {
                external.SaveHistoryToFile(sb.ToString());
            }
        }

        private class GraphNode
        {
            public SVNRevisionNode Revision;
            public int Row;
            public int Lane;
            public List<GraphNode> Parents = new();
            public string Branch;
        }
    }
}