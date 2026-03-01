using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNStatus : SVNBase
    {
        private List<SvnTreeElement> _flatTreeData = new List<SvnTreeElement>();
        
        private List<SvnTreeElement> _commitTreeData;

        private bool _isCurrentViewIgnored = false;
        long totalCommitBytes = 0;
        public SVNStatus(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void ToggleFolderVisibility(SvnTreeElement folder)
        {
            List<SvnTreeElement> targetData = folder.IsCommitDelegate ? _commitTreeData : _flatTreeData;

            if (targetData == null) return;

            foreach (var e in targetData)
            {
                string parentPath = GetParentPath(e.FullPath);
                if (string.IsNullOrEmpty(parentPath))
                {
                    continue;
                }

                var parent = targetData.FirstOrDefault(x => x.FullPath == parentPath);
                if (parent != null)
                {
                    e.IsVisible = parent.IsVisible && parent.IsExpanded;
                }
            }

            if (folder.IsCommitDelegate)
            {
                if (svnUI.SVNCommitTreeDisplay != null)
                    svnUI.SVNCommitTreeDisplay.RefreshUI(targetData, this);
            }
            else
            {
                if (svnUI.SvnTreeView != null)
                    svnUI.SvnTreeView.RefreshUI(targetData, this);
            }
        }

        private string GetParentPath(string path)
        {
            int lastSlash = path.LastIndexOf('/');
            return lastSlash > 0 ? path.Substring(0, lastSlash) : "";
        }
        public void ShowOnlyModified()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");

            ClearSVNTreeView();

            if (svnUI.TreeDisplay != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Refreshing...", "TREE", append: false);
            }

            if (svnUI.CommitTreeDisplay != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "Refreshing...", "COMMIT_TREE", append: false);
            }

            _isCurrentViewIgnored = false;
            _ = ExecuteRefreshWithAutoExpand();
        }

        public async Task ExecuteRefreshWithAutoExpand()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            try
            {
                if (svnUI != null)
                {
                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);

                    if (svnUI.CommitTreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                }

                string root = svnManager.WorkingDir;
                var statusDict = _isCurrentViewIgnored
                    ? await svnManager.GetModule<SVNIgnore>().GetIgnoredOnlyAsync(root)
                    : await GetChangesDictionaryAsync(root);

                if (statusDict == null || statusDict.Count == 0)
                {
                    ResetTreeView();
                    _flatTreeData.Clear();
                    _commitTreeData?.Clear();

                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected. (Everything up to date)</i>", "TREE", append: false);

                    if (svnUI.CommitTreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "<i>Nothing to commit.</i>", "COMMIT_TREE", append: false);

                    if (svnUI.CommitSizeText != null) svnUI.CommitSizeText.text = "Total Commit Size: <color=#FFFF00>0 B</color>";

                    UpdateAllStatisticsUI(new SvnStats(), _isCurrentViewIgnored);
                    return;
                }

                _flatTreeData = BuildFlatTreeStructureText(root, statusDict);

                if (svnUI.SvnTreeView != null)
                {
                    foreach (var e in _flatTreeData) e.IsCommitDelegate = false;
                    svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
                }

                if (svnUI.SVNCommitTreeDisplay != null || svnUI.CommitTreeDisplay != null)
                {
                    _commitTreeData = PrepareCommitTree(_flatTreeData);

                    if (svnUI.SVNCommitTreeDisplay != null)
                        svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);

                    if (svnUI.CommitTreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);

                    if (svnUI.CommitSizeText != null)
                    {
                        string totalSize = FormatSize(totalCommitBytes);
                        svnUI.CommitSizeText.text = $"Total Commit Size: <color=#FFFF00>{totalSize}</color>";
                    }
                }

                UpdateAllStatisticsUI(CalculateStats(statusDict), _isCurrentViewIgnored);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Refresh Error: {ex.Message}");
            }
            finally { IsProcessing = false; }
        }

        public void ClearCurrentData()
        {
            _flatTreeData?.Clear();
            _commitTreeData?.Clear();
            svnManager.CurrentStatusDict?.Clear();
        }

        public void ClearSVNTreeView()
        {
            SvnTreeView[] svnTreeViews = GameObject.FindObjectsByType<SvnTreeView>(FindObjectsSortMode.None);
            foreach (var svnTreeView in svnTreeViews)
            {
                svnTreeView.ClearView();
            }
        }

        public void ResetTreeView()
        {
            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected. (Everything up to date)</i>", "TREE", append: false);
        }

        private SvnStats CalculateStats(Dictionary<string, (string status, string size)> statusDict)
        {
            SvnStats stats = new SvnStats();
            if (statusDict == null) return stats;

            foreach (var item in statusDict.Values)
            {
                string s = item.status;

                if (s.Contains("M")) stats.ModifiedCount++;
                else if (s.Contains("A")) stats.AddedCount++;
                else if (s.Contains("?")) stats.NewFilesCount++;
                else if (s.Contains("D") || s.Contains("!")) stats.DeletedCount++;
                else if (s.Contains("C")) stats.ConflictsCount++;
                else if (s.Contains("I"))
                {
                    stats.IgnoredCount++;
                    stats.IgnoredFileCount++;
                }

                if (s == "DIR") stats.FolderCount++;
                else stats.FileCount++;
            }

            return stats;
        }

        private List<SvnTreeElement> PrepareCommitTree(List<SvnTreeElement> fullTree)
        {
            var commitTree = fullTree.Select(e =>
            {
                var clone = e.Clone();
                clone.IsChecked = e.IsChecked;
                clone.IsVisible = false;
                clone.IsCommitDelegate = true;
                return clone;
            }).ToList();

            foreach (var element in commitTree)
            {
                if (!string.IsNullOrEmpty(element.Status) && element.Status != " " && element.Status != "DIR")
                {
                    ShowElementAndParents(element, commitTree);
                }
            }

            return commitTree;
        }

        private void ShowElementAndParents(SvnTreeElement element, List<SvnTreeElement> list)
        {
            element.IsVisible = true;

            string[] pathParts = element.FullPath.Split('/');
            if (pathParts.Length <= 1) return;

            string parentPath = string.Join("/", pathParts.Take(pathParts.Length - 1));
            var parent = list.Find(e => e.FullPath == parentPath);

            if (parent != null && !parent.IsVisible)
            {
                ShowElementAndParents(parent, list);
            }
        }

        private List<SvnTreeElement> BuildFlatTreeStructureText(string root, Dictionary<string, (string status, string size)> statusDict)
        {
            var previousSelectionStates = new Dictionary<string, bool>();
            if (_flatTreeData != null)
            {
                foreach (var e in _flatTreeData)
                {
                    if (!previousSelectionStates.ContainsKey(e.FullPath))
                        previousSelectionStates.Add(e.FullPath, e.IsChecked);
                }
            }

            var elements = new List<SvnTreeElement>();
            var sortedPaths = statusDict.Keys.OrderBy(p => p).ToList();
            totalCommitBytes = 0;

            Debug.Log($"<color=green>[SVN BUILDER]</color> Building structure. Root: {root}");

            foreach (var relPath in sortedPaths)
            {
                string normalizedPath = relPath.Replace('\\', '/');

                if (normalizedPath.Contains(":/"))
                {
                    int trunkIdx = normalizedPath.LastIndexOf("trunk/");
                    if (trunkIdx != -1)
                    {
                        normalizedPath = normalizedPath.Substring(trunkIdx);
                    }
                }

                string[] parts = normalizedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                string currentPath = "";

                for (int i = 0; i < parts.Length; i++)
                {
                    string partName = parts[i];
                    currentPath = string.IsNullOrEmpty(currentPath) ? partName : $"{currentPath}/{partName}";

                    if (elements.Any(e => e.FullPath == currentPath)) continue;

                    string physicalPath = (root.TrimEnd('/') + "/" + currentPath).Replace("\\", "/");

                    // --- UPDATED FOLDER LOGIC ---
                    // Item is treated as a directory if:
                    // 1. It acts as a parent in the path hierarchy
                    // 2. It exists on disk as a Directory
                    // 3. It's missing from disk (Status: !) but was identified as DIR by SVN
                    bool isActuallyFolder = (i < parts.Length - 1) ||
                                            Directory.Exists(physicalPath) ||
                                            (statusDict.ContainsKey(currentPath) && (statusDict[currentPath].status == "DIR" || statusDict[currentPath].size == "DIR"));

                    string displayStatus = " ";
                    if (i == parts.Length - 1 && statusDict.ContainsKey(relPath))
                        displayStatus = statusDict[relPath].status;
                    else if (isActuallyFolder)
                        displayStatus = statusDict.ContainsKey(currentPath) ? statusDict[currentPath].status : "DIR";

                    string fileSize = "";
                    if (!isActuallyFolder && i == parts.Length - 1)
                    {
                        if (File.Exists(physicalPath))
                        {
                            long bytes = new FileInfo(physicalPath).Length;
                            fileSize = FormatSize(bytes);
                            if (displayStatus != " " && displayStatus != "DIR")
                                totalCommitBytes += bytes;
                        }
                        else
                        {
                            fileSize = "---";
                        }
                    }

                    bool isChecked = !string.IsNullOrWhiteSpace(displayStatus) &&
                                     displayStatus != " " &&
                                     displayStatus != "?" &&
                                     displayStatus != "I";

                    if (previousSelectionStates.TryGetValue(currentPath, out bool previousValue))
                    {
                        isChecked = previousValue;
                    }

                    elements.Add(new SvnTreeElement
                    {
                        FullPath = currentPath,
                        Name = partName,
                        Depth = i,
                        Status = displayStatus,
                        IsFolder = isActuallyFolder,
                        IsChecked = isChecked,
                        IsExpanded = true,
                        IsVisible = true,
                        Size = fileSize
                    });
                }
            }

            var reversedElements = elements.Where(e => e.IsFolder).OrderByDescending(e => e.Depth).ToList();
            foreach (var folder in reversedElements)
            {
                bool hasAnyCheckedChild = elements.Any(e =>
                    e.FullPath.StartsWith(folder.FullPath + "/") &&
                    e.IsChecked);

                if (hasAnyCheckedChild)
                {
                    folder.IsChecked = true;
                }
            }

            return elements;
        }

        private string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int digit = (int)Math.Log(bytes, 1024);
            return (bytes / Math.Pow(1024, digit)).ToString("F2") + " " + units[digit];
        }

        public void UpdateAllStatisticsUI(SvnStats stats, bool isIgnoredView)
        {
            if (svnUI == null) return;

            if (svnUI.StatsText != null)
            {
                string statsContent = isIgnoredView
                    ? $"<color=#444444><b>VIEW: IGNORED</b></color> | Folders: {stats.IgnoredFolderCount} | Files: {stats.IgnoredFileCount} | Total Ignored: <color=#FFFFFF>{stats.IgnoredCount}</color>"
                    : $"Folders: {stats.FolderCount} | Files: {stats.FileCount} | <color=#FFD700>Mod (M): {stats.ModifiedCount}</color> | <color=#00FF00>Add (A): {stats.AddedCount}</color> | <color=#00E5FF>New (?): {stats.NewFilesCount}</color> | <color=#FF4444>Del (D/!): {stats.DeletedCount}</color> | <color=#FF00FF>Conf (C): {stats.ConflictsCount}</color>";

                SVNLogBridge.UpdateUIField(svnUI.StatsText, statsContent, "STATS", append: false);
            }

            if (svnUI.CommitStatsText != null)
            {
                if (isIgnoredView)
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitStatsText, "<color=#FFCC00>Switch to 'Modified' view to see commit details.</color>", "STATS", append: false);
                }
                else
                {
                    int totalToCommit = stats.ModifiedCount + stats.AddedCount + stats.NewFilesCount + stats.DeletedCount;
                    string conflictPart = stats.ConflictsCount > 0 ? $" | <color=#FF0000><b> CONFLICTS (C): {stats.ConflictsCount} (Resolve first!)</b></color>" : "";
                    string commitStats = $"<b>Pending Changes:</b> <color=#FFD700>M: {stats.ModifiedCount}</color> | <color=#00FF00>A: {stats.AddedCount}</color> | <color=#00E5FF>?: {stats.NewFilesCount}</color> | <color=#FF4444>D/!: {stats.DeletedCount}</color> | <color=#FFFFFF><b>Total: {totalToCommit}</b></color>{conflictPart}";

                    SVNLogBridge.UpdateUIField(svnUI.CommitStatsText, commitStats, "STATS", append: false);
                }
            }
        }

        public static async Task<Dictionary<string, (string status, string size)>> GetChangesDictionaryAsync(string workingDir)
        {
            workingDir = workingDir.Replace("\\", "/").TrimEnd('/');
            string output = await SvnRunner.RunAsync("status", workingDir);
            var statusDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output)) return statusDict;

            Debug.Log($"<color=green>[SVN]</color> Parsowanie zmian w: {workingDir}");

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 8) continue;

                char rawCode = line[0];
                string stat = rawCode.ToString().ToUpper();

                if ("MA?!DC".Contains(stat))
                {
                    string rawPath = line.Substring(8).Trim();
                    string cleanPath = SvnRunner.CleanSvnPath(rawPath).Replace("\\", "/");

                    string fullPhysicalPath = Path.Combine(workingDir, cleanPath).Replace("\\", "/");
                    string typeInfo = "FILE";

                    if (Directory.Exists(fullPhysicalPath))
                    {
                        typeInfo = "DIR";
                    }
                    else if (stat == "!" || stat == "D")
                    {
                        if (!Path.GetFileName(cleanPath).Contains(".")) typeInfo = "DIR";
                    }

                    statusDict[cleanPath] = (stat, typeInfo);
                }
            }
            return statusDict;
        }

        public void ToggleChildrenSelection(SvnTreeElement parentFolder, bool isChecked)
        {
            UpdateListSelection(_flatTreeData, parentFolder.FullPath, isChecked);

            UpdateListSelection(_commitTreeData, parentFolder.FullPath, isChecked);
        }

        private void UpdateListSelection(List<SvnTreeElement> list, string path, bool isChecked)
        {
            if (list == null) return;
            string prefix = path + "/";

            var folder = list.FirstOrDefault(e => e.FullPath == path);
            if (folder != null) folder.IsChecked = isChecked;

            foreach (var e in list)
            {
                if (e.FullPath.StartsWith(prefix))
                {
                    e.IsChecked = isChecked;
                }
            }
        }

        public List<SvnTreeElement> GetCurrentData()
        {
            return _flatTreeData;
        }

        public void NotifySelectionChanged()
        {
            if (svnUI.SvnTreeView != null)
                svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);

            var commitPanel = UnityEngine.Object.FindFirstObjectByType<CommitPanel>(UnityEngine.FindObjectsInactive.Include);

            if (commitPanel != null && commitPanel.gameObject.activeInHierarchy)
            {
                if (svnUI.SVNCommitTreeDisplay != null && _commitTreeData != null)
                {
                    svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
                }
            }
        }
    }
}