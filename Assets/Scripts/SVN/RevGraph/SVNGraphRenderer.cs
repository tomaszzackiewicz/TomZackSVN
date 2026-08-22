using System;
using System.Collections.Generic;
using UnityEngine;

namespace SVN.Core
{
    public static class SVNGraphRenderer
    {
        private const string COLOR_TRUNK = "#3B82F6";

        #region Public API

        public static GraphData AnalyzeBranches(List<SVNRevisionNode> nodes)
        {
            if (nodes == null || nodes.Count == 0)
                return null;

            var data = new GraphData();
            int nextCol = 1;

            data.LaneMap["trunk"] = 0;
            data.BranchTypes["trunk"] = NodeType.Trunk;

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var info = GetBranchInfo(node);

                if (!data.LaneMap.ContainsKey(info.Name))
                {
                    data.LaneMap[info.Name] = nextCol++;
                    data.BranchTypes[info.Name] = info.Type;
                    data.BranchFirstRev[info.Name] = node.Revision;
                }

                data.BranchLastRev[info.Name] = node.Revision;

                var nodeInfo = new GraphData.NodeInfo
                {
                    BranchName = info.Name,
                    Type = info.Type,
                    IsBranchPoint = false,
                    MergeSource = null,
                    ParentBranch = "trunk",
                    ChangedFilesCount = node.ChangedPaths != null ? node.ChangedPaths.Count : 0,
                    HasMergeInfoChange = node.HasMergeInfoChange,
                    CopyFromPath = node.CopyFromPath,
                    CopyFromRev = node.CopyFromRev
                };

                if (node.ChangedPaths != null)
                {
                    foreach (var p in node.ChangedPaths)
                    {
                        if (string.IsNullOrEmpty(p) || p.Length < 1) continue;
                        char s = char.ToUpper(p[0]);
                        if (s == 'A') nodeInfo.AddedCount++;
                        else if (s == 'M') nodeInfo.ModifiedCount++;
                        else if (s == 'D') nodeInfo.DeletedCount++;
                    }
                }

                if (info.Type != NodeType.Trunk &&
                    data.BranchFirstRev.TryGetValue(info.Name, out long first) &&
                    first == node.Revision)
                {
                    nodeInfo.IsBranchPoint = true;

                    if (!string.IsNullOrEmpty(node.CopyFromPath))
                    {
                        string parent = ExtractBranch(node.CopyFromPath);
                        nodeInfo.ParentBranch = string.IsNullOrEmpty(parent) ? "trunk" : parent;
                    }
                    else
                    {
                        string parent = DetectBranchParent(node, info.Name);
                        nodeInfo.ParentBranch = string.IsNullOrEmpty(parent) ? "trunk" : parent;
                    }

                    data.BranchParent[info.Name] = nodeInfo.ParentBranch;
                }

                if (IsMergeCommit(node))
                {
                    string src = DetectMergeSource(node, info.Name, data.LaneMap.Keys);

                    if (string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(node.Message))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(
                            node.Message,
                            @"(?:^|[\s/])branches/([^/\s\r\n]+)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (match.Success)
                        {
                            src = match.Groups[1].Value;
                            if (!data.LaneMap.ContainsKey(src))
                            {
                                data.LaneMap[src] = nextCol++;
                                data.BranchTypes[src] = NodeType.Branch;
                                data.BranchFirstRev[src] = node.Revision;
                                data.BranchLastRev[src] = node.Revision;
                            }
                        }
                    }

                    nodeInfo.MergeSource = src;

                    if (!string.IsNullOrEmpty(src))
                    {
                        data.MergedBranches.Add(src);
                        if (!data.BranchLastRev.ContainsKey(src) || data.BranchLastRev[src] < node.Revision)
                            data.BranchLastRev[src] = node.Revision;
                    }
                }

                data.NodeDetails[node.Revision] = nodeInfo;
            }

            data.ColumnCount = nextCol;
            data.ColumnToBranch = new string[nextCol];
            foreach (var kv in data.LaneMap)
            {
                if (kv.Value >= 0 && kv.Value < nextCol)
                    data.ColumnToBranch[kv.Value] = kv.Key;
            }

            int colorIdx = 0;
            foreach (var branch in data.LaneMap.Keys)
            {
                float hue = Mathf.Repeat(colorIdx * 137.508f, 360f);
                colorIdx++;
                Color c = Color.HSVToRGB(hue / 360f, 0.75f, 1.0f);
                data.BranchColors[branch] = branch == "trunk" ? COLOR_TRUNK : "#" + ColorUtility.ToHtmlStringRGB(c);
            }

            return data;
        }

        public static string DetectMergeSource(SVNRevisionNode node, string currentBranch, ICollection<string> known)
        {
            string msg = node.Message ?? "";

            foreach (string branch in known)
            {
                if (branch == currentBranch) continue;
                if (branch.Length < 3) continue;

                if (WordContains(msg, branch))
                    return branch;
            }

            if (node.ChangedPaths != null)
            {
                foreach (string path in node.ChangedPaths)
                {
                    string b = ExtractBranch(path);
                    if (!string.IsNullOrEmpty(b) && b != currentBranch)
                        return b;
                }
            }

            return null;
        }

        public static BranchInfo GetBranchInfo(SVNRevisionNode node)
        {
            if (node.ChangedPaths == null || node.ChangedPaths.Count == 0)
                return BranchInfo.Unknown;

            foreach (string path in node.ChangedPaths)
            {
                if (IsTrunkPath(path))
                    return BranchInfo.Trunk;
            }

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

            // Zmieniamy Debug.LogError na mniej kosztowny log
            SVNLogBridge.LogToOutput($"<color=#FFAA00>[SVN Branch Parser] Unknown branch for r{node.Revision}. Path: '{NormalizePath(node.ChangedPaths[0])}'</color>");
            return BranchInfo.Unknown;
        }

        public static bool IsMergeCommit(SVNRevisionNode node)
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
                    string b = ExtractBranch(path);
                    if (string.IsNullOrEmpty(b)) continue;
                    if (first == null) first = b;
                    else if (!first.Equals(b, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        #endregion

        #region Private

        private static bool IsTrunkPath(string path)
        {
            path = NormalizePath(path);
            if (string.IsNullOrEmpty(path)) return false;
            return path.Equals("/trunk", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/trunk/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("trunk", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("trunk/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            path = path.Trim().Trim('\r', '\n', ' ');

            if (path.Length >= 2 && "AMDRamdr".Contains(char.ToUpper(path[0])) && path[1] == ' ')
                path = path.Substring(2).Trim();

            return path;
        }

        private static string DetectBranchParent(SVNRevisionNode node, string currentBranch)
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

                string parent = ExtractBranch(fromPart);
                if (!string.IsNullOrEmpty(parent) && parent != currentBranch)
                    return parent;
            }
            return null;
        }

        private static string ExtractBranch(string path)
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

            if (IsTrunkPath(path)) return "trunk";

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

        public static bool WordContains(string text, string word)
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

        #endregion
    }

    public class GraphData
    {
        public struct NodeInfo
        {
            public string BranchName;
            public NodeType Type;
            public string MergeSource;
            public string ParentBranch;
            public bool IsBranchPoint;
            public int ChangedFilesCount;

            public int AddedCount;
            public int ModifiedCount;
            public int DeletedCount;
            public bool HasMergeInfoChange;
            public string CopyFromPath;
            public long CopyFromRev;
        }

        public Dictionary<string, int> LaneMap = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, NodeType> BranchTypes = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> BranchFirstRev = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> BranchLastRev = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> BranchParent = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> BranchColors = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> MergedBranches = new(StringComparer.OrdinalIgnoreCase);
        public string[] ColumnToBranch;
        public int ColumnCount;
        public Dictionary<long, NodeInfo> NodeDetails = new();
    }
}