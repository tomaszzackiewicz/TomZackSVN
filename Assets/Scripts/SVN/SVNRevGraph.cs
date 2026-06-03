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
        // ----- STAŁE ZNAKÓW (każda kolumna = 2 znaki) -----
        private const string VERT_TRUNK = "│ ";
        private const string VERT_BRANCH = "│ ";
        private const string VERT_TAG = "│ ";
        private const string VERT_MERGED = "┆ ";   // przerywana dla zmergowanej

        private const string SHAPE_TRUNK = "■";
        private const string SHAPE_BRANCH = "●";
        private const string SHAPE_TAG = "◆";
        private const string SHAPE_MERGE = "◉";
        private const string SHAPE_MERGE_FROM_TRUNK = "▣";
        private const string SHAPE_MERGE_FROM_TAG = "◈";

        private const string SPACER = " ";
        // -------------------------------------------------

        public SVNRevGraph(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private List<GameObject> instantiatedItems = new List<GameObject>();
        private Dictionary<string, NodeType> branchTypes = new();
        private HashSet<string> mergedBranches = new();

        // Nowe słowniki dla zakresów rewizji i informacji o starcie gałęzi
        private Dictionary<string, long> branchFirstRev = new();
        private Dictionary<string, long> branchLastRev = new();
        private Dictionary<string, string> branchParent = new(); // klucz: nazwa gałęzi, wartość: nazwa rodzica

        public List<GameObject> InstantiatedItems => instantiatedItems;

        public void RenderGraph(List<SVNRevisionNode> nodes)
        {
            // ---------------------------------------------------------
            // 1. SORTOWANIE ROSNĄCO do analizy struktury (aby trunk dostał kolumnę 0)
            nodes.Sort((a, b) => a.Revision.CompareTo(b.Revision));
            // ---------------------------------------------------------

            foreach (Transform t in svnUI.GraphContainer)
                UnityEngine.Object.Destroy(t.gameObject);

            instantiatedItems.Clear();
            branchTypes.Clear();
            mergedBranches.Clear();
            branchFirstRev.Clear();
            branchLastRev.Clear();
            branchParent.Clear();

            Dictionary<string, int> branchColumns = new();
            int nextColumn = 0;

            // =====================================================
            // 2. ANALIZA STRUKTURY (od najstarszych)
            // =====================================================
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                BranchInfo info = GetBranchInfo(node);

                if (!branchColumns.ContainsKey(info.Name))
                {
                    branchColumns[info.Name] = nextColumn++;
                    branchTypes[info.Name] = info.Type;
                    branchFirstRev[info.Name] = node.Revision;   // pierwszy commit gałęzi
                }

                // Ostatni commit gałęzi (aktualizujemy zawsze)
                branchLastRev[info.Name] = node.Revision;

                // Wykrywanie gałęzi-rodzica tylko dla pierwszego commita danej gałęzi
                if (branchFirstRev[info.Name] == node.Revision && info.Type != NodeType.Trunk)
                {
                    string parent = DetectBranchParent(node, info.Name);
                    if (!string.IsNullOrEmpty(parent))
                        branchParent[info.Name] = parent;
                    else
                        branchParent[info.Name] = "trunk"; // domyślnie
                }

                // Merge
                if (IsMergeCommit(node))
                {
                    string mergeSrcBranch = DetectMergeSourceBranch(node, info.Name, branchColumns.Keys);
                    if (!string.IsNullOrEmpty(mergeSrcBranch))
                    {
                        mergedBranches.Add(mergeSrcBranch);
                        if (!branchColumns.ContainsKey(mergeSrcBranch))
                        {
                            branchColumns[mergeSrcBranch] = nextColumn++;
                            branchTypes[mergeSrcBranch] = mergeSrcBranch == "trunk"
                                ? NodeType.Trunk
                                : (mergeSrcBranch.StartsWith("tags/") || branchTypes.ContainsKey(mergeSrcBranch)
                                    ? NodeType.Tag : NodeType.Branch);
                            // Uwaga: gałąź źródłowa może nie mieć jeszcze ustalonego zakresu,
                            // ale to nie przeszkadza – jeśli pojawi się później jej commit, to zakres się uzupełni.
                            // Dla bezpieczeństwa ustawiamy firstRev na ten merge (jeśli nie ma wcześniejszego)
                            if (!branchFirstRev.ContainsKey(mergeSrcBranch))
                                branchFirstRev[mergeSrcBranch] = node.Revision;
                        }
                        // Aktualizujemy lastRev dla źródłowej gałęzi (może być późniejszy merge)
                        if (!branchLastRev.ContainsKey(mergeSrcBranch) || branchLastRev[mergeSrcBranch] < node.Revision)
                            branchLastRev[mergeSrcBranch] = node.Revision;
                    }
                    else
                    {
                        // Fallback: pierwsza inna gałąź
                        foreach (var kv in branchColumns)
                        {
                            if (kv.Key != info.Name)
                            {
                                mergeSrcBranch = kv.Key;
                                mergedBranches.Add(mergeSrcBranch);
                                break;
                            }
                        }
                    }
                }
            }

            // =====================================================
            // 3. RYSOWANIE (od najnowszych)
            // =====================================================
            // Sortujemy malejąco, by najnowsze były na górze
            nodes.Sort((a, b) => b.Revision.CompareTo(a.Revision));

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                BranchInfo info = GetBranchInfo(node);

                int col = branchColumns[info.Name];
                string colHex = BranchColorSystem.GetColor(info.Name);
                StringBuilder g = new StringBuilder();

                bool isMerge = IsMergeCommit(node);
                string mergeSrcBranch = DetectMergeSourceBranch(node, info.Name, branchColumns.Keys);
                if (isMerge && string.IsNullOrEmpty(mergeSrcBranch))
                {
                    foreach (var kv in branchColumns)
                        if (kv.Key != info.Name) { mergeSrcBranch = kv.Key; break; }
                }

                // Prefiks początku gałęzi (tylko jeśli to pierwszy commit gałęzi)
                string branchStartPrefix = "";
                if (info.Type != NodeType.Trunk && node.Revision == branchFirstRev[info.Name])
                {
                    string parent = branchParent.ContainsKey(info.Name) ? branchParent[info.Name] : "trunk";
                    branchStartPrefix = $"<color=#000000>[branched from {parent}]</color> ";
                }

                // Prefiks merge
                string mergePrefix = "";
                if (isMerge && !string.IsNullOrEmpty(mergeSrcBranch))
                    mergePrefix = $"<color=#000000>[merged from {mergeSrcBranch}]</color> ";

                string fullPrefix = branchStartPrefix + mergePrefix;

                // Wybór kształtu
                string shape;
                if (isMerge)
                {
                    if (mergeSrcBranch == "trunk")
                        shape = SHAPE_MERGE_FROM_TRUNK;
                    else if (branchTypes.ContainsKey(mergeSrcBranch) && branchTypes[mergeSrcBranch] == NodeType.Tag)
                        shape = SHAPE_MERGE_FROM_TAG;
                    else
                        shape = SHAPE_MERGE;
                }
                else
                {
                    shape = info.Type switch
                    {
                        NodeType.Trunk => SHAPE_TRUNK,
                        NodeType.Branch => SHAPE_BRANCH,
                        NodeType.Tag => SHAPE_TAG,
                        _ => SHAPE_BRANCH
                    };
                }

                for (int c = 0; c < nextColumn; c++)
                {
                    string laneBranch = null;
                    foreach (var kv in branchColumns)
                        if (kv.Value == c) { laneBranch = kv.Key; break; }

                    string laneColor = laneBranch != null ? BranchColorSystem.GetColor(laneBranch) : "#555555";
                    string laneText;
                    string finalColor = laneColor;

                    bool isCurrent = (c == col);
                    bool isActive = laneBranch != null &&
                                    node.Revision >= branchFirstRev[laneBranch] &&
                                    node.Revision <= branchLastRev[laneBranch];

                    if (isCurrent)
                    {
                        if (isMerge && !string.IsNullOrEmpty(mergeSrcBranch))
                            finalColor = BranchColorSystem.GetColor(mergeSrcBranch);

                        laneText = shape + SPACER;
                    }
                    else if (isActive)
                    {
                        NodeType laneType = branchTypes.ContainsKey(laneBranch)
                            ? branchTypes[laneBranch]
                            : NodeType.Branch;

                        laneText = laneType switch
                        {
                            NodeType.Trunk => VERT_TRUNK,
                            NodeType.Branch => VERT_BRANCH,
                            NodeType.Tag => VERT_TAG,
                            _ => VERT_BRANCH
                        };
                    }
                    else
                    {
                        NodeType laneType = branchTypes.ContainsKey(laneBranch)
                            ? branchTypes[laneBranch]
                            : NodeType.Branch;

                        bool isMergedAndInactive = mergedBranches.Contains(laneBranch) &&
                                                   node.Revision > branchLastRev[laneBranch];

                        if (isMergedAndInactive)
                        {
                            laneText = VERT_MERGED;
                            finalColor = "#88888844";
                        }
                        else
                        {
                            laneText = laneType switch
                            {
                                NodeType.Trunk => VERT_TRUNK,
                                NodeType.Branch => VERT_BRANCH,
                                NodeType.Tag => VERT_TAG,
                                _ => VERT_BRANCH
                            };
                            finalColor = "#00000000";
                        }
                    }

                    g.Append($"<color={finalColor}>{laneText}</color>");
                }

                GameObject itemGo = UnityEngine.Object.Instantiate(svnUI.GraphItemPrefab, svnUI.GraphContainer);
                instantiatedItems.Add(itemGo);

                SVNGraphItem item = itemGo.GetComponent<SVNGraphItem>();
                if (item != null)
                {
                    item.Setup(g.ToString(), node, info.Name, colHex, svnManager, fullPrefix);
                }
            }

            SVNLogBridge.LogLine($"[SVN] Render complete. {instantiatedItems.Count} revisions rendered.");
        }

        // -----------------------------------------------------------
        // POZOSTAŁE METODY (bez zmian)
        // -----------------------------------------------------------
        private string DetectBranchParent(SVNRevisionNode node, string currentBranch)
        {
            if (node.ChangedPaths == null) return null;

            foreach (string path in node.ChangedPaths)
            {
                if (path.StartsWith("A ") && path.Contains("(from "))
                {
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
            }
            if (currentBranch != "trunk")
                return "trunk";
            return null;
        }

        private string DetectMergeSourceBranch(SVNRevisionNode node, string currentBranch, ICollection<string> knownBranches)
        {
            string msg = node.Message ?? "";

            var pathMatches = System.Text.RegularExpressions.Regex.Matches(
                msg,
                @"(?:^|[\s\(\)\[\]\{\}""'`])/?(\^?/)?(?<type>branches|trunk|tags)(/(?<name>[^\s,;:\)]+))?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match m in pathMatches)
            {
                string type = m.Groups["type"].Value.ToLower();
                string name = m.Groups["name"].Value;
                string found = type == "trunk" ? "trunk" : (string.IsNullOrEmpty(name) ? null : name);
                if (!string.IsNullOrEmpty(found) && found != currentBranch) return found;
            }

            foreach (string branch in knownBranches)
            {
                if (branch == currentBranch) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(msg, $@"\b{System.Text.RegularExpressions.Regex.Escape(branch)}\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return branch;
            }

            var branchWordMatch = System.Text.RegularExpressions.Regex.Match(msg, @"branch\s*['""](?<name>[^'""]+)['""]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (branchWordMatch.Success)
            {
                string name = branchWordMatch.Groups["name"].Value;
                if (name != currentBranch) return name;
            }

            if (node.ChangedPaths != null)
            {
                foreach (string path in node.ChangedPaths)
                {
                    string b = ExtractBranchFromPath(path);
                    if (!string.IsNullOrEmpty(b) && b != currentBranch) return b;
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
                if (idx >= 0 && idx + 1 < parts.Length) return parts[idx + 1];
            }
            else if (path.Contains("/trunk")) return "trunk";
            else if (path.Contains("/tags/"))
            {
                string[] parts = path.Split('/');
                int idx = System.Array.IndexOf(parts, "tags");
                if (idx >= 0 && idx + 1 < parts.Length) return parts[idx + 1];
            }
            return null;
        }

        private bool IsMergeCommit(SVNRevisionNode node)
        {
            if (node == null) return false;
            if (!string.IsNullOrEmpty(node.Message))
            {
                string msg = node.Message.ToLower();
                if (msg.Contains("merge") || msg.Contains("merged") || msg.Contains("reintegrate")) return true;
            }
            if (node.ChangedPaths != null)
            {
                var branches = new HashSet<string>();
                foreach (string path in node.ChangedPaths)
                {
                    string b = ExtractBranchFromPath(path);
                    if (!string.IsNullOrEmpty(b)) branches.Add(b);
                }
                if (branches.Count >= 2) return true;
            }
            return false;
        }

        public static class BranchColorSystem
        {
            private static Dictionary<string, string> branchColors = new();
            private static int index = 0;
            private const float GOLDEN_ANGLE = 137.508f;

            public static string GetColor(string branch)
            {
                if (branchColors.TryGetValue(branch, out var existing)) return existing;

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