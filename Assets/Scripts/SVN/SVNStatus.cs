using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using System.Threading;

namespace SVN.Core
{
    public class SVNStatus : SVNBase
    {
        private List<SvnTreeElement> _flatTreeData = new List<SvnTreeElement>();

        private List<SvnTreeElement> _commitTreeData;

        private bool _isCurrentViewIgnored = false;
        long totalCommitBytes = 0;
        private CancellationTokenSource _cts;

        public SVNStatus(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void ToggleFolderVisibility(SvnTreeElement folder)
        {
            List<SvnTreeElement> targetData = folder.IsCommitDelegate ? _commitTreeData : _flatTreeData;

            if (targetData == null || targetData.Count == 0) return;

            int startIndex = targetData.IndexOf(folder);
            if (startIndex == -1) return;

            string folderPath = folder.FullPath;
            string prefix = folderPath.EndsWith("/") ? folderPath : folderPath + "/";

            var localLookup = new Dictionary<string, SvnTreeElement>(32)
            {
                [folderPath] = folder
            };

            for (int i = startIndex + 1; i < targetData.Count; i++)
            {
                var e = targetData[i];

                if (!e.FullPath.StartsWith(prefix))
                {
                    break;
                }

                localLookup[e.FullPath] = e;

                string parentPath = GetParentPath(e.FullPath);
                if (string.IsNullOrEmpty(parentPath))
                {
                    continue;
                }

                if (localLookup.TryGetValue(parentPath, out var parent))
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

        public async Task RefreshAfterAction()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");
            ClearSVNTreeView();

            await ExecuteRefreshWithAutoExpand(force: true);
        }

        public async void ShowOnlyModified()
        {
            await RefreshModifiedInternal();
        }

        public async Task RefreshModifiedInternal()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");

            ClearSVNTreeView();

            if (svnUI.TreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Refreshing...", "TREE", append: false);

            if (svnUI.CommitTreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "Refreshing...", "COMMIT_TREE", append: false);

            _isCurrentViewIgnored = false;

            await ExecuteRefreshWithAutoExpand(force: true);
        }


        public async Task ExecuteRefreshWithAutoExpand(bool force = false)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            if (IsProcessing && !force) return;
            if (!force) IsProcessing = true;

            try
            {
                if (svnUI != null)
                {
                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Scanning local changes...", "TREE", append: false);

                    if (svnUI.CommitTreeDisplay != null &&
                        svnUI.CommitTreeDisplay.gameObject.activeInHierarchy)
                    {
                        SVNLogBridge.UpdateUIField(
                            svnUI.CommitTreeDisplay,
                            "Refreshing commit list...",
                            "COMMIT_TREE",
                            append: false);
                    }
                }

                string root = svnManager.WorkingDir;

                token.ThrowIfCancellationRequested();

                Task<Dictionary<string, SvnChangeInfo>> statusTask = GetChangesDictionaryAsync(root, token);
                Task<Dictionary<string, string>> locksTask = GetLocksDictionaryAsync(root, token);

                await Task.WhenAll(statusTask, locksTask);

                token.ThrowIfCancellationRequested();

                var statusDict = await statusTask;
                var lockDict = await locksTask;

                if (svnUI != null)
                {
                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);

                    if (svnUI.CommitTreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                }

                if ((statusDict == null || statusDict.Count == 0) &&
                    (lockDict == null || lockDict.Count == 0))
                {
                    ShowEmptyState();
                    return;
                }

                var (processedFlatData, finalBytes) = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();

                    if (lockDict != null && lockDict.Count > 0)
                    {
                        statusDict ??= new Dictionary<string, SvnChangeInfo>();

                        foreach (var l in lockDict)
                        {
                            token.ThrowIfCancellationRequested();

                            if (!statusDict.ContainsKey(l.Key))
                            {
                                statusDict[l.Key] = new SvnChangeInfo
                                {
                                    Status = " ",
                                    Size = "FILE",
                                    Bytes = 0,
                                    Exists = false
                                };
                            }
                        }
                    }

                    token.ThrowIfCancellationRequested();

                    var (treeElements, calculatedBytes) = BuildFlatTreeStructureText(root, statusDict);

                    token.ThrowIfCancellationRequested();

                    ApplyLockColors(treeElements, lockDict);

                    return (treeElements, calculatedBytes);
                }, token);

                token.ThrowIfCancellationRequested();

                _flatTreeData = processedFlatData;
                totalCommitBytes = finalBytes;

                if (svnUI.SvnTreeView != null &&
                    svnUI.SvnTreeView.gameObject.activeInHierarchy)
                {
                    foreach (var e in _flatTreeData)
                        e.IsCommitDelegate = false;

                    svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
                }

                if (svnUI.SVNCommitTreeDisplay != null &&
                    svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy)
                {
                    _commitTreeData = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        return BuildCommitView(_flatTreeData);
                    }, token);

                    token.ThrowIfCancellationRequested();

                    svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);

                    if (svnUI.CommitSizeText != null)
                    {
                        svnUI.CommitSizeText.text =
                            $"Total Commit Size: <color=#FFFF00>{FormatSize(totalCommitBytes)}</color>";
                    }
                }

                UpdateAllStatisticsUI(CalculateStats(statusDict), _isCurrentViewIgnored);
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("SVN Refresh operation canceled safely.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Refresh Error: {ex.Message}");
            }
            finally
            {
                if (!force)
                    IsProcessing = false;
            }
        }

        private void ShowEmptyState()
        {
            ResetTreeView();
            _flatTreeData.Clear();
            _commitTreeData?.Clear();

            if (svnUI.TreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected.</i>", "TREE", append: false);
            if (svnUI.CommitTreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "<i>Nothing to commit.</i>", "COMMIT_TREE", append: false);

            UpdateAllStatisticsUI(new SvnStats(), _isCurrentViewIgnored);
        }

        private void RefreshAllUIComponents()
        {
            if (svnUI.SvnTreeView != null && svnUI.SvnTreeView.gameObject.activeInHierarchy)
            {
                foreach (var e in _flatTreeData) e.IsCommitDelegate = false;
                svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
            }

            bool isCommitActive = (svnUI.SVNCommitTreeDisplay != null && svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy);
            if (isCommitActive)
            {
                _commitTreeData = BuildCommitView(_flatTreeData);
                svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);

                if (svnUI.CommitSizeText != null)
                    svnUI.CommitSizeText.text = $"Total Commit Size: <color=#FFFF00>{FormatSize(totalCommitBytes)}</color>";
            }
        }

        private void ApplyLockColors(List<SvnTreeElement> data, Dictionary<string, string> locks)
        {
            foreach (var e in data)
            {
                if (locks.TryGetValue(e.FullPath, out string lockStatus))
                {
                    string baseColor = "#FFFFFF";
                    if (e.Status.Contains("M")) baseColor = "#FFD700";
                    else if (e.Status.Contains("A")) baseColor = "#00FF00";
                    else if (e.Status.Contains("?")) baseColor = "#00E5FF";
                    else if (e.Status.Contains("D") || e.Status.Contains("!")) baseColor = "#FF4444";

                    string lockColor = lockStatus == "K" ? "#00FF00" : "#FF4444";
                    string cleanBaseStatus = e.Status.Trim();

                    if (string.IsNullOrEmpty(cleanBaseStatus) || cleanBaseStatus == "DIR")
                        e.Status = $"<color={lockColor}>{lockStatus}</color>";
                    else if (!cleanBaseStatus.Contains(lockStatus))
                        e.Status = $"<color={baseColor}>{cleanBaseStatus}</color><color={lockColor}>{lockStatus}</color>";
                }
            }
        }

        public void ClearCurrentData()
        {
            _flatTreeData?.Clear();
            _commitTreeData?.Clear();

            if (svnManager != null && svnManager.CurrentStatusDict != null)
                svnManager.CurrentStatusDict.Clear();

            totalCommitBytes = 0;
        }

        public void ClearSVNTreeView()
        {
            foreach (var svnTreeView in svnUI.SVNTreeViews)
            {
                svnTreeView.ClearView();
            }
        }

        public void ResetTreeView()
        {
            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected. (Everything up to date)</i>", "TREE", append: false);
        }

        private SvnStats CalculateStats(Dictionary<string, SvnChangeInfo> statusDict)
        {
            SvnStats stats = new SvnStats();
            if (statusDict == null) return stats;

            foreach (var item in statusDict.Values)
            {
                string s = item.Status;

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

                if (item.Size == "DIR") stats.FolderCount++;
                else stats.FileCount++;
            }

            return stats;
        }

        private List<SvnTreeElement> BuildCommitView(List<SvnTreeElement> source)
        {
            var visible = new HashSet<string>();

            foreach (var element in source)
            {
                bool isCommitable =
                    !string.IsNullOrWhiteSpace(element.Status) &&
                    element.Status != " " &&
                    element.Status != "DIR";

                if (!isCommitable)
                    continue;

                string current = element.FullPath;

                while (!string.IsNullOrEmpty(current))
                {
                    visible.Add(current);

                    current = GetParentPath(current);
                }
            }

            return source
                .Where(e => visible.Contains(e.FullPath))
                .ToList();
        }

        private void ShowElementAndParents(SvnTreeElement element, Dictionary<string, SvnTreeElement> lookup)
        {
            element.IsVisible = true;

            string parentPath = GetParentPath(element.FullPath);
            if (string.IsNullOrEmpty(parentPath)) return;

            if (lookup.TryGetValue(parentPath, out var parent))
            {
                if (!parent.IsVisible)
                {
                    ShowElementAndParents(parent, lookup);
                }
            }
        }

        private (List<SvnTreeElement> Elements, long TotalBytes) BuildFlatTreeStructureText(
    string root,
    Dictionary<string, SvnChangeInfo> statusDict)
        {
            var previousSelectionStates = new Dictionary<string, bool>();
            if (_flatTreeData != null)
            {
                foreach (var e in _flatTreeData)
                {
                    if (!string.IsNullOrEmpty(e.FullPath))
                        previousSelectionStates[e.FullPath] = e.IsChecked;
                }
            }

            var elements = new List<SvnTreeElement>();
            var existingPaths = new HashSet<string>();

            var sortedPaths = statusDict.Keys.OrderBy(p => p).ToList();

            long localTotalBytes = 0;

            foreach (var relPath in sortedPaths)
            {
                string normalizedPath = relPath.Replace('\\', '/');

                if (normalizedPath.Contains(":/"))
                {
                    int trunkIdx = normalizedPath.LastIndexOf("trunk/");
                    if (trunkIdx != -1)
                        normalizedPath = normalizedPath.Substring(trunkIdx);
                }

                string[] parts = normalizedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                string currentPath = "";

                for (int i = 0; i < parts.Length; i++)
                {
                    string partName = parts[i];
                    currentPath = string.IsNullOrEmpty(currentPath)
                        ? partName
                        : $"{currentPath}/{partName}";

                    if (existingPaths.Contains(currentPath))
                        continue;

                    string physicalPath = Path.Combine(root, currentPath).Replace('\\', '/');
                    bool isLastPart = (i == parts.Length - 1);

                    bool isActuallyFolder = false;

                    if (!isLastPart)
                    {
                        isActuallyFolder = true;
                    }
                    else
                    {
                        if (statusDict.TryGetValue(currentPath, out var info) && info.Size == "DIR")
                            isActuallyFolder = true;
                        else if (Directory.Exists(physicalPath))
                            isActuallyFolder = true;
                    }

                    string displayStatus = " ";

                    if (isLastPart && statusDict.ContainsKey(relPath))
                        displayStatus = statusDict[relPath].Status;
                    else if (isActuallyFolder)
                        displayStatus = statusDict.ContainsKey(currentPath)
                            ? statusDict[currentPath].Status
                            : "DIR";

                    string fileSize = "";

                    if (!isActuallyFolder && isLastPart)
                    {
                        FileInfo fi = new FileInfo(physicalPath);

                        if (fi.Exists)
                        {
                            long bytes = fi.Length;
                            fileSize = FormatSize(bytes);

                            if (displayStatus != " " && displayStatus != "DIR")
                                localTotalBytes += bytes;
                        }
                        else
                        {
                            fileSize = "---";
                        }
                    }

                    bool isChecked =
                        !string.IsNullOrWhiteSpace(displayStatus) &&
                        displayStatus != " " &&
                        displayStatus != "?" &&
                        displayStatus != "I";

                    if (previousSelectionStates.TryGetValue(currentPath, out bool prev))
                        isChecked = prev;

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

                    existingPaths.Add(currentPath);
                }
            }

            var reversedFolders = elements
                .Select((e, idx) => new { Element = e, Index = idx })
                .Where(x => x.Element.IsFolder)
                .OrderByDescending(x => x.Element.Depth)
                .ToList();

            foreach (var folderData in reversedFolders)
            {
                var folder = folderData.Element;
                int startIndex = folderData.Index;
                string prefix = folder.FullPath + "/";

                for (int j = startIndex + 1; j < elements.Count; j++)
                {
                    var child = elements[j];

                    if (!child.FullPath.StartsWith(prefix))
                        break;

                    if (child.IsChecked)
                    {
                        folder.IsChecked = true;
                        break;
                    }
                }
            }

            return (elements, localTotalBytes);
        }

        private string FormatSize(long bytes)
        {
            if (bytes <= 0)
                return "0 B";

            string[] units = { "B", "KB", "MB", "GB", "TB" };

            int digit = (int)Math.Floor(Math.Log(bytes, 1024));

            digit = Mathf.Clamp(digit, 0, units.Length - 1);

            double value = bytes / Math.Pow(1024, digit);

            return value.ToString("F2") + " " + units[digit];
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

        public static async Task<Dictionary<string, SvnChangeInfo>> GetChangesDictionaryAsync(string workingDir, CancellationToken cancellationToken = default)
        {
            const int statusCharIndex = 0;
            const int svnStatusPrefixLength = 8;
            const string allowedSvnStatuses = "MA?!DC~R";
            const string directoryLabel = "DIR";
            const string fileLabel = "FILE";

            workingDir = workingDir.Replace("\\", "/").TrimEnd('/');

            string output = await SvnRunner.RunAsync("status", workingDir, token: cancellationToken);

            var statusDict = new Dictionary<string, SvnChangeInfo>();

            if (string.IsNullOrWhiteSpace(output))
                return statusDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (line.Length < svnStatusPrefixLength)
                    continue;

                string stat = line[statusCharIndex].ToString().ToUpper();

                if (!allowedSvnStatuses.Contains(stat))
                    continue;

                string rawPath = line.Substring(svnStatusPrefixLength).Trim();
                string cleanPath = SvnRunner.CleanSvnPath(rawPath).Replace("\\", "/");

                string fullPath = Path.Combine(workingDir, cleanPath).Replace("\\", "/");

                bool isDir = Directory.Exists(fullPath);
                bool isFile = File.Exists(fullPath);

                statusDict[cleanPath] = new SvnChangeInfo
                {
                    Status = stat,
                    Size = isDir ? directoryLabel : fileLabel,
                    Bytes = isFile ? new FileInfo(fullPath).Length : 0,
                    Exists = isDir || isFile
                };
            }

            return statusDict;
        }

        public struct SvnChangeInfo
        {
            public string Status;
            public string Size;
            public long Bytes;
            public bool Exists;
        }

        public static async Task<Dictionary<string, string>> GetLocksDictionaryAsync(string workingDir, CancellationToken cancellationToken = default)
        {
            const int lockCharIndex = 5;
            const int svnStatusPrefixLength = 8;

            var lockDict = new Dictionary<string, string>();

            try
            {
                string output = await SvnRunner.RunAsync("status -u", workingDir, token: cancellationToken);

                if (string.IsNullOrEmpty(output)) return lockDict;

                string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (line.Length < svnStatusPrefixLength) continue;

                    char lockCode = line[lockCharIndex];
                    if (lockCode == 'K' || lockCode == 'O')
                    {
                        string pathPart = line.Substring(svnStatusPrefixLength).Trim();
                        string cleanPath = SvnRunner.CleanSvnPath(pathPart).Replace("\\", "/");

                        if (cleanPath.Length > 0 && char.IsDigit(cleanPath[0]))
                        {
                            int firstSpace = cleanPath.IndexOf(' ');
                            if (firstSpace != -1) cleanPath = cleanPath.Substring(firstSpace).Trim();
                        }

                        if (!string.IsNullOrEmpty(cleanPath))
                        {
                            lockDict[cleanPath] = lockCode.ToString();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"SVN Lock check failed (probably offline): {ex.Message}");
            }

            return lockDict;
        }

        public void ToggleChildrenSelection(SvnTreeElement parentFolder, bool isChecked)
        {
            UpdateListSelection(_flatTreeData, parentFolder.FullPath, isChecked);

            NotifySelectionChanged();
        }

        private void UpdateListSelection(List<SvnTreeElement> list, string path, bool isChecked)
        {
            if (list == null || list.Count == 0) return;

            int startIndex = list.FindIndex(e => e.FullPath == path);
            if (startIndex == -1) return;

            list[startIndex].IsChecked = isChecked;
            string prefix = path + "/";
            for (int i = startIndex + 1; i < list.Count; i++)
            {
                if (list[i].FullPath.StartsWith(prefix))
                {
                    list[i].IsChecked = isChecked;
                }
                else
                {
                    break;
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

            if (_flatTreeData != null && _flatTreeData.Count > 0)
            {
                _commitTreeData = PrepareCommitTree(_flatTreeData);
            }

            var commitPanel = UnityEngine.Object.FindFirstObjectByType<CommitPanel>(
                UnityEngine.FindObjectsInactive.Include);

            if (commitPanel != null && commitPanel.gameObject.activeInHierarchy)
            {
                if (svnUI.SVNCommitTreeDisplay != null)
                {
                    svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
                }
            }
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

            var lookup = commitTree.ToDictionary(e => e.FullPath);

            foreach (var element in commitTree)
            {
                if (!string.IsNullOrEmpty(element.Status) &&
                    element.Status != " " &&
                    element.Status != "DIR")
                {
                    ShowElementAndParents(element, lookup);
                }
            }

            return commitTree;
        }
    }
}