using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNStatus : SVNBase
    {
        public event Action OnSelectionChanged;

        private List<SvnTreeElement> _flatTreeData = new List<SvnTreeElement>();
        private List<SvnTreeElement> _commitTreeData;
        private bool _isCurrentViewIgnored = false;
        private long totalCommitBytes = 0;
        private CancellationTokenSource _cts;
        private const bool ENABLE_FILE_SIZES = true;

        private static (DateTime time, Dictionary<string, SVNLockDetails> data) _lockCache;
        private static readonly TimeSpan LockCacheDuration = TimeSpan.FromMinutes(2);
        private static readonly object _cacheLock = new object();

        private static readonly string[] SIZE_UNITS = { "B", "KB", "MB", "GB", "TB" };

        public SVNStatus(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            manager.OnProjectChanged += HandleProjectChanged;
        }

        private async void HandleProjectChanged(SVNProject project)
        {
            if (project == null) return;
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                IsProcessing = false;

                ClearCurrentData();
                ClearSVNTreeView();

                svnManager.WorkingDir = project.workingDir;
                svnManager.RepositoryUrl = project.repoUrl;
                svnManager.CurrentKey = project.privateKeyPath;

                await Task.Delay(50);
                await RefreshModifiedInternal();
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVNStatus] Project switch failed: {ex}");
            }
        }

        public void ToggleFolderVisibility(SvnTreeElement folder)
        {
            List<SvnTreeElement> targetData = folder.IsCommitDelegate ? _commitTreeData : _flatTreeData;
            if (targetData == null || targetData.Count == 0) return;

            int startIndex = targetData.IndexOf(folder);
            if (startIndex == -1) return;

            string folderPath = folder.FullPath;
            string prefix = folderPath.EndsWith("/") ? folderPath : folderPath + "/";

            var localLookup = new Dictionary<string, SvnTreeElement>(32) { [folderPath] = folder };

            for (int i = startIndex + 1; i < targetData.Count; i++)
            {
                var e = targetData[i];
                if (!e.FullPath.StartsWith(prefix)) break;

                localLookup[e.FullPath] = e;
                string parentPath = GetParentPath(e.FullPath);
                if (string.IsNullOrEmpty(parentPath)) continue;

                if (localLookup.TryGetValue(parentPath, out var parent))
                {
                    e.IsVisible = parent.IsVisible && parent.IsExpanded;
                }
            }

            if (folder.IsCommitDelegate)
                svnUI.SVNCommitTreeDisplay?.RefreshUI(targetData, this);
            else
                svnUI.SvnTreeView?.RefreshUI(targetData, this);
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
            try { await RefreshModifiedInternal(); }
            catch (Exception e) { SVNLogBridge.LogError($"[SVN] Błąd podczas odświeżania: {e.Message}"); }
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
            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            if (oldCts != null)
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }

            IsProcessing = true;

            try
            {
                if (svnUI != null)
                {
                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Scanning local changes...", "TREE", append: false);
                    if (svnUI.CommitTreeDisplay != null && svnUI.CommitTreeDisplay.gameObject.activeInHierarchy)
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "Refreshing commit list...", "COMMIT_TREE", append: false);
                }

                await Task.Yield();
                token.ThrowIfCancellationRequested();

                svnUI?.SvnTreeView?.ClearView();
                svnUI?.SVNCommitTreeDisplay?.ClearView();

                string root = svnManager.WorkingDir;

                var statusTask = GetChangesDictionaryAsync(root, token);
                var locksTask = GetLocksDictionaryAsync(root, token);
                await Task.WhenAll(statusTask, locksTask);
                token.ThrowIfCancellationRequested();

                var statusDict = statusTask.Result;
                var lockDict = locksTask.Result;

                if (statusDict == null || statusDict.Count == 0)
                {
                    ShowEmptyState();
                    return;
                }

                if (svnUI != null)
                {
                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                    if (svnUI.CommitTreeDisplay != null && svnUI.CommitTreeDisplay.gameObject.activeInHierarchy)
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                }

                var buildResult = await Task.Run(() => BuildFlatTreeStructureText(root, statusDict), token);
                token.ThrowIfCancellationRequested();

                _flatTreeData = buildResult.Elements;
                totalCommitBytes = buildResult.TotalBytes;

                if (lockDict != null && lockDict.Count > 0)
                    ApplyLockColors(_flatTreeData, lockDict);

                var localData = _flatTreeData;

                if (svnUI.SvnTreeView != null && svnUI.SvnTreeView.gameObject.activeInHierarchy)
                {
                    foreach (var e in localData)
                        e.IsCommitDelegate = false;
                    svnUI.SvnTreeView.RefreshUI(localData, this);
                }

                token.ThrowIfCancellationRequested();

                if (svnUI.SVNCommitTreeDisplay != null && svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy)
                {
                    _commitTreeData = await Task.Run(() => BuildCommitView(localData), token);
                    token.ThrowIfCancellationRequested();

                    if (svnUI.CommitTreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);

                    svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
                    UpdateSelectedSizeDisplay();
                }

                UpdateAllStatisticsUI(CalculateStats(statusDict), _isCurrentViewIgnored);
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>[SVN]</color> Refresh canceled.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Refresh Error: {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void ShowEmptyState()
        {
            ResetTreeView();
            _flatTreeData.Clear();
            _commitTreeData?.Clear();

            svnUI.SvnTreeView?.ClearView();
            svnUI.SVNCommitTreeDisplay?.ClearView();

            if (svnUI.TreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected.</i>", "TREE", append: false);
            if (svnUI.CommitTreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "<i>Nothing to commit.</i>", "COMMIT_TREE", append: false);

            UpdateAllStatisticsUI(new SvnStats(), _isCurrentViewIgnored);
        }

        public void RefreshVisibleUIOnly()
        {
            svnUI.SvnTreeView?.RefreshUI(_flatTreeData, this);

            bool commitPanelVisible = svnUI.SVNCommitTreeDisplay != null && svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy;
            if (commitPanelVisible)
                svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
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
                svnTreeView.ClearView();
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
                bool isFolder = item.Size == "DIR";
                string s = item.Status;

                if (isFolder) { stats.FolderCount++; continue; }
                stats.FileCount++;

                switch (s)
                {
                    case "M": stats.ModifiedCount++; break;
                    case "A": stats.AddedCount++; break;
                    case "?": stats.NewFilesCount++; break;
                    case "D":
                    case "!": stats.DeletedCount++; break;
                    case "C": stats.ConflictsCount++; break;
                }
            }
            return stats;
        }

        private List<SvnTreeElement> BuildCommitView(List<SvnTreeElement> source)
        {
            var visible = new HashSet<string>();
            foreach (var element in source)
            {
                bool isRoot = element.FullPath == ".svn-root" || element.FullPath == ".";
                bool isCommitable = isRoot || (!string.IsNullOrWhiteSpace(element.Status) && element.Status != " " && element.Status != "DIR");
                if (!isCommitable) continue;

                string current = element.FullPath;
                while (!string.IsNullOrEmpty(current))
                {
                    visible.Add(current);
                    current = GetParentPath(current);
                }
            }

            var result = new List<SvnTreeElement>(visible.Count);
            foreach (var e in source)
            {
                if (visible.Contains(e.FullPath))
                {
                    e.IsCommitDelegate = true;
                    result.Add(e);
                }
            }
            return result;
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

            bool hasRootChange = statusDict.ContainsKey(".");

            if (_flatTreeData != null)
            {
                foreach (var e in _flatTreeData)
                {
                    if (!string.IsNullOrEmpty(e.FullPath))
                    {
                        previousSelectionStates[e.FullPath] = e.IsChecked;
                    }
                }
            }

            var elements = new List<SvnTreeElement>(statusDict.Count * 2);
            var existingPaths = new HashSet<string>(statusDict.Count * 2);

            var sortedPaths = new List<string>(statusDict.Keys);
            sortedPaths.Sort(StringComparer.Ordinal);

            long localTotalBytes = 0;

            if (hasRootChange)
            {
                var rootInfo = statusDict["."];

                elements.Add(new SvnTreeElement
                {
                    FullPath = ".svn-root",
                    Name = "[Repository Root Change]",
                    Depth = 0,
                    Status = rootInfo.Status,
                    IsFolder = true,
                    IsCommitDelegate = true,
                    IsChecked = true,
                    IsExpanded = true,
                    IsVisible = true,
                    Size = "",
                    Bytes = 0
                });

                existingPaths.Add(".");
            }

            foreach (var relPath in sortedPaths)
            {
                if (relPath == ".")
                    continue;

                string normalizedPath = SvnRunner.NormalizeRepositoryPath(relPath);

                string[] parts = normalizedPath.Split('/');
                string currentPath = "";

                for (int i = 0; i < parts.Length; i++)
                {
                    string partName = parts[i];

                    if (string.IsNullOrEmpty(partName))
                        continue;

                    currentPath = string.IsNullOrEmpty(currentPath)
                        ? partName
                        : currentPath + "/" + partName;

                    if (!existingPaths.Add(currentPath))
                        continue;

                    bool isLastPart = (i == parts.Length - 1);

                    bool isActuallyFolder =
                        !isLastPart ||
                        (statusDict.TryGetValue(relPath, out var info) && info.Size == "DIR");

                    string displayStatus = " ";

                    if (isLastPart)
                    {
                        if (statusDict.TryGetValue(relPath, out var finalInfo))
                        {
                            displayStatus = finalInfo.Status;
                        }
                    }
                    else if (isActuallyFolder)
                    {
                        if (statusDict.TryGetValue(currentPath, out var folderInfo))
                        {
                            displayStatus = folderInfo.Status;
                        }
                        else
                        {
                            displayStatus = "DIR";
                        }
                    }

                    string fileSize = "";
                    long bytes = 0;

                    if (
                        ENABLE_FILE_SIZES &&
                        !isActuallyFolder &&
                        isLastPart &&
                        statusDict.TryGetValue(relPath, out var fileInfo)
                    )
                    {
                        bytes = fileInfo.Bytes;
                        fileSize = FormatSize(bytes);

                        if (
                            displayStatus != " " &&
                            displayStatus != "DIR" &&
                            displayStatus != "!" &&
                            displayStatus != "D"
                        )
                        {
                            localTotalBytes += bytes;
                        }
                    }

                    bool isChecked =
                        !string.IsNullOrWhiteSpace(displayStatus) &&
                        displayStatus != " " &&
                        displayStatus != "?" &&
                        displayStatus != "I";

                    if (previousSelectionStates.TryGetValue(currentPath, out bool prev))
                    {
                        isChecked = prev;
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
                        Size = fileSize,
                        LockedByMe = false,
                        LockedByOther = false,
                        Bytes = (isActuallyFolder || !ENABLE_FILE_SIZES || !isLastPart) ? 0 : bytes
                    });
                }
            }

            var reversedFolders = new List<(SvnTreeElement Element, int Index)>();

            for (int i = 0; i < elements.Count; i++)
            {
                if (elements[i].IsFolder)
                {
                    reversedFolders.Add((elements[i], i));
                }
            }

            reversedFolders.Sort((a, b) =>
                b.Element.Depth.CompareTo(a.Element.Depth));

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

        public void UpdateSelectedSizeDisplay()
        {
            if (svnUI == null || svnUI.CommitSizeText == null)
                return;

            if (_flatTreeData == null || _flatTreeData.Count == 0)
            {
                svnUI.CommitSizeText.text = "Total Commit Size: <color=#FFFF00>0 B</color>";
                return;
            }

            long selectedBytes = 0;

            foreach (var element in _flatTreeData)
            {
                if (!element.IsChecked)
                    continue;

                if (element.IsFolder)
                    continue;

                if (element.Status == "!" || element.Status == "D")
                    continue;

                selectedBytes += element.Bytes;
            }

            totalCommitBytes = selectedBytes;

            svnUI.CommitSizeText.text =
                $"Total Commit Size: <color=#FFFF00>{FormatSize(selectedBytes)}</color>";
        }

        private string FormatSize(long bytes)
        {
            if (bytes <= 0)
                return "0 B";

            var units = SIZE_UNITS;

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

        public static async Task<Dictionary<string, SvnChangeInfo>> GetChangesDictionaryAsync(
    string workingDir,
    CancellationToken cancellationToken = default)
        {
            const int svnStatusPrefixLength = 8;
            const string allowedSvnStatuses = "MA?!DC~R";
            const string directoryLabel = "DIR";
            const string fileLabel = "FILE";

            workingDir = workingDir.Replace("\\", "/").TrimEnd('/');

            string output = await SvnRunner.RunAsync(
                "status --ignore-externals",
                workingDir,
                token: cancellationToken
            );

            if (string.IsNullOrWhiteSpace(output))
                return new Dictionary<string, SvnChangeInfo>(2048);

            var statusDict = await Task.Run(() =>
            {
                var dict = new Dictionary<string, SvnChangeInfo>(2048);
                string[] lines = output.Split(
                    new[] { '\n', '\r' },
                    StringSplitOptions.RemoveEmptyEntries
                );

                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (line.Length < svnStatusPrefixLength)
                        continue;

                    char itemStatus = line[0];
                    char propStatus = line[1];

                    string stat = itemStatus != ' ' ? itemStatus.ToString().ToUpper() : propStatus.ToString().ToUpper();

                    if (!allowedSvnStatuses.Contains(stat))
                        continue;

                    string rawPath = line.Substring(svnStatusPrefixLength);

                    string cleanPath = SvnRunner.NormalizeRepositoryPath(rawPath);

                    bool isRootChange = cleanPath == ".";

                    if (isRootChange)
                    {
                        dict["."] = new SvnChangeInfo
                        {
                            Status = stat,
                            Size = "DIR",
                            Bytes = 0,
                            Exists = true
                        };
                        continue;
                    }

                    string fullPath = Path.Combine(workingDir, cleanPath).Replace("\\", "/");

                    bool isActuallyFile = File.Exists(fullPath);
                    bool isActuallyDir = !isActuallyFile && Directory.Exists(fullPath);
                    bool existsOnDisk = isActuallyFile || isActuallyDir;

                    string sizeLabel;
                    long bytes = 0;

                    if (existsOnDisk)
                    {
                        sizeLabel = isActuallyFile ? fileLabel : directoryLabel;

                        if (isActuallyFile)
                        {
                            try
                            {
                                var fileInfo = new FileInfo(fullPath);
                                bytes = fileInfo.Length;
                            }
                            catch { bytes = 0; }
                        }
                    }
                    else
                    {
                        sizeLabel = Path.HasExtension(cleanPath) ? fileLabel : directoryLabel;
                    }

                    dict[cleanPath] = new SvnChangeInfo
                    {
                        Status = stat,
                        Size = sizeLabel,
                        Bytes = bytes,
                        Exists = existsOnDisk
                    };
                }
                return dict;
            }, cancellationToken);

            return statusDict;
        }

        public struct SvnChangeInfo
        {
            public string Status;
            public string Size;
            public long Bytes;
            public bool Exists;
        }

        public void ToggleChildrenSelection(SvnTreeElement parentFolder, bool isChecked)
        {
            UpdateListSelection(_flatTreeData, parentFolder.FullPath, isChecked);

            if (_commitTreeData != null)
            {
                UpdateListSelection(_commitTreeData, parentFolder.FullPath, isChecked);
            }

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

        public async Task<Dictionary<string, SVNLockDetails>> GetLocksDictionaryAsync(string root, CancellationToken token = default)
        {
            lock (_cacheLock)
            {
                if (_lockCache.data != null && (DateTime.UtcNow - _lockCache.time) < LockCacheDuration)
                    return _lockCache.data;
            }

            var result = new Dictionary<string, SVNLockDetails>();
            try
            {
                var lockModule = svnManager.GetModule<SVNLock>();
                if (lockModule == null) return result;

                var locks = await lockModule.GetDetailedLocks(root);
                token.ThrowIfCancellationRequested();

                foreach (var l in locks)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(l.FullPath)) continue;
                    string normalized = NormalizeLockPath(l.FullPath);
                    result[normalized] = l;
                }

                lock (_cacheLock)
                {
                    _lockCache = (DateTime.UtcNow, result);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Lock dictionary fetch failed: {ex.Message}");
            }
            return result;
        }

        public static void ClearLockCache()
        {
            lock (_cacheLock)
            {
                _lockCache = default;
            }
        }

        public void ApplyLockColors(List<SvnTreeElement> elements, Dictionary<string, SVNLockDetails> lockDict)
        {
            if (elements == null || lockDict == null) return;

            string currentUser = svnManager.CurrentUserName?.Trim().ToLower();

            foreach (var e in elements)
            {
                e.LockedByMe = false;
                e.LockedByOther = false;
                if (string.IsNullOrEmpty(e.FullPath)) continue;

                string normalized = NormalizeLockPath(e.FullPath);
                if (lockDict.TryGetValue(normalized, out var lockInfo))
                {
                    bool isMine = !string.IsNullOrEmpty(lockInfo.Owner) &&
                                  lockInfo.Owner.Trim().ToLower() == currentUser;
                    e.LockedByMe = isMine;
                    e.LockedByOther = !isMine;
                }
            }
        }

        private string NormalizeLockPath(string path)
        {
            return SvnRunner.NormalizeRepositoryPath(path);
        }

        public void NotifySelectionChanged()
        {
            if (svnUI.SvnTreeView != null)
                svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);

            bool commitPanelVisible =
                svnUI.SVNCommitTreeDisplay != null &&
                svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy;

            if (commitPanelVisible && _commitTreeData != null)
            {
                svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
            }

            UpdateSelectedSizeDisplay();
            OnSelectionChanged?.Invoke();
        }

        public void CancelCurrentRefresh()
        {
            _cts?.Cancel();
        }
    }
}