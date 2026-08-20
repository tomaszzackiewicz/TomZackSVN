using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNStatus : SVNBase, IDisposable
    {
        public event Action OnSelectionChanged;

        private List<SvnTreeElement> _flatTreeData = new List<SvnTreeElement>();
        private List<SvnTreeElement> _commitTreeData;
        private bool _isCurrentViewIgnored = false;
        private long totalCommitBytes = 0;
        private CancellationTokenSource _cts;
        private const bool ENABLE_FILE_SIZES = true;

        private CancellationTokenSource _projectSwitchDebounceCts;

        private static (DateTime time, string root, Dictionary<string, SVNLockDetails> data) _lockCache;
        private static readonly TimeSpan LockCacheDuration = TimeSpan.FromMinutes(2);
        private static readonly object _cacheLock = new object();
        public bool ShowUnversionedFiles { get; set; } = true;

        public SVNStatus(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
            manager.OnProjectChanged += HandleProjectChanged;
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
    bool expandUnversioned = true,
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
                return new Dictionary<string, SvnChangeInfo>(2048, StringComparer.OrdinalIgnoreCase);

            var statusDict = await Task.Run(() =>
            {
                var dict = new Dictionary<string, SvnChangeInfo>(2048, StringComparer.OrdinalIgnoreCase);
                string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line.Length < svnStatusPrefixLength) continue;

                    char itemStatus = line[0];
                    char propStatus = line[1];
                    char activeChar = itemStatus != ' '
                        ? char.ToUpperInvariant(itemStatus)
                        : char.ToUpperInvariant(propStatus);

                    if (allowedSvnStatuses.IndexOf(activeChar) < 0) continue;

                    string stat = activeChar.ToString();
                    string rawPath = line.Substring(svnStatusPrefixLength).TrimStart();
                    string cleanPath = SvnRunner.NormalizeRepositoryPath(rawPath);

                    if (!string.IsNullOrEmpty(cleanPath))
                        cleanPath = cleanPath.Replace('\\', '/').Trim().Trim('/');

                    if (string.IsNullOrEmpty(cleanPath) || cleanPath == ".")
                    {
                        if (cleanPath == ".")
                        {
                            dict["."] = new SvnChangeInfo
                            {
                                Status = stat,
                                Size = directoryLabel,
                                Bytes = 0,
                                Exists = true
                            };
                        }
                        continue;
                    }

                    string fullPathNative = (workingDir + "/" + cleanPath)
                        .Replace('\\', '/')
                        .Replace('/', Path.DirectorySeparatorChar);

                    bool isDir = false;
                    bool isFile = false;
                    bool existsOnDisk = false;
                    bool isDeletedOrMissing = (activeChar == 'D' || activeChar == '!');

                    if (!isDeletedOrMissing)
                    {
                        try
                        {
                            var attr = File.GetAttributes(fullPathNative);
                            isDir = (attr & FileAttributes.Directory) == FileAttributes.Directory;
                            isFile = !isDir;
                            existsOnDisk = true;
                        }
                        catch
                        {
                            if (Directory.Exists(fullPathNative))
                            {
                                isDir = true;
                                existsOnDisk = true;
                            }
                            else if (File.Exists(fullPathNative))
                            {
                                isFile = true;
                                existsOnDisk = true;
                            }
                        }
                    }

                    if (existsOnDisk && Directory.Exists(fullPathNative))
                    {
                        isDir = true;
                        isFile = false;
                    }

                    string sizeLabel;
                    long bytes = 0;

                    if (existsOnDisk)
                    {
                        sizeLabel = isDir ? directoryLabel : fileLabel;
                        if (isFile)
                        {
                            try { bytes = new FileInfo(fullPathNative).Length; }
                            catch { bytes = 0; }
                        }
                    }
                    else
                    {
                        string nameOnly = cleanPath.Contains('/')
                            ? cleanPath.Substring(cleanPath.LastIndexOf('/') + 1)
                            : cleanPath;
                        bool hasExtension = nameOnly.LastIndexOf('.') > 0;
                        sizeLabel = hasExtension ? fileLabel : directoryLabel;
                    }

                    dict[cleanPath] = new SvnChangeInfo
                    {
                        Status = stat,
                        Size = sizeLabel,
                        Bytes = bytes,
                        Exists = existsOnDisk
                    };
                }

                if (expandUnversioned)
                {
                    var unversionedDirs = dict
                        .Where(kvp => kvp.Value.Status == "?" && kvp.Value.Size == directoryLabel)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var dirRelPath in unversionedDirs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string fullDirPathNative = (workingDir + "/" + dirRelPath)
                            .Replace('\\', '/')
                            .Replace('/', Path.DirectorySeparatorChar);

                        if (!Directory.Exists(fullDirPathNative)) continue;

                        string[] filesInDir;
                        try
                        {
                            filesInDir = Directory.GetFiles(fullDirPathNative, "*", SearchOption.AllDirectories);
                        }
                        catch { continue; }

                        foreach (var fileFullPath in filesInDir)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var pathParts = fileFullPath.Split(Path.DirectorySeparatorChar, '/');
                            if (pathParts.Any(p => p.Equals(".svn", StringComparison.OrdinalIgnoreCase)))
                                continue;

                            string normalizedFullPath = fileFullPath.Replace('\\', '/');
                            if (!normalizedFullPath.StartsWith(workingDir + "/", StringComparison.OrdinalIgnoreCase))
                                continue;

                            string fileRelPath = normalizedFullPath.Substring(workingDir.Length + 1).Trim('/');
                            if (string.IsNullOrWhiteSpace(fileRelPath) || dict.ContainsKey(fileRelPath))
                                continue;

                            long fileBytes = 0;
                            try { fileBytes = new FileInfo(fileFullPath).Length; }
                            catch { }

                            dict[fileRelPath] = new SvnChangeInfo
                            {
                                Status = "?",
                                Size = fileLabel,
                                Bytes = fileBytes,
                                Exists = true
                            };
                        }

                        string[] dirsInDir;
                        try
                        {
                            dirsInDir = Directory.GetDirectories(fullDirPathNative, "*", SearchOption.AllDirectories);
                        }
                        catch { continue; }

                        foreach (var dirFullPath in dirsInDir)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var pathParts = dirFullPath.Split(Path.DirectorySeparatorChar, '/');
                            if (pathParts.Any(p => p.Equals(".svn", StringComparison.OrdinalIgnoreCase)))
                                continue;

                            string normalizedDirPath = dirFullPath.Replace('\\', '/');
                            if (!normalizedDirPath.StartsWith(workingDir + "/", StringComparison.OrdinalIgnoreCase))
                                continue;

                            string dirRel = normalizedDirPath.Substring(workingDir.Length + 1).Trim('/');
                            if (string.IsNullOrWhiteSpace(dirRel) || dict.ContainsKey(dirRel))
                                continue;

                            dict[dirRel] = new SvnChangeInfo
                            {
                                Status = "?",
                                Size = directoryLabel,
                                Bytes = 0,
                                Exists = true
                            };
                        }
                    }
                }
                else
                {
                    var toRemove = new List<string>();

                    foreach (var kvp in dict)
                    {
                        if (kvp.Value.Status != "?") continue;

                        string native = (workingDir + "/" + kvp.Key)
                            .Replace('\\', '/')
                            .Replace('/', Path.DirectorySeparatorChar);

                        if (Directory.Exists(native))
                            continue;

                        if (kvp.Value.Size == directoryLabel && kvp.Key.IndexOf('/') < 0)
                            continue;

                        toRemove.Add(kvp.Key);
                    }

                    foreach (var key in toRemove)
                        dict.Remove(key);
                }

                int qTotal = dict.Count(k => k.Value.Status == "?");
                int qDir = dict.Count(k => k.Value.Status == "?" && k.Value.Size == directoryLabel);
                Debug.Log($"[SVN] expandUnversioned={expandUnversioned} | ? total={qTotal} | ? DIR={qDir} | " +
                          string.Join(", ", dict.Where(k => k.Value.Status == "?").Select(k => $"{k.Key}[{k.Value.Size}]")));

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
                UpdateListSelection(_commitTreeData, parentFolder.FullPath, isChecked);

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
                    list[i].IsChecked = isChecked;
            }
        }

        public List<SvnTreeElement> GetCurrentData() => _flatTreeData;

        public async Task<Dictionary<string, SVNLockDetails>> GetLocksDictionaryAsync(string root, CancellationToken token = default)
        {
            lock (_cacheLock)
            {
                if (_lockCache.data != null &&
                    string.Equals(_lockCache.root, root, StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.UtcNow - _lockCache.time) < LockCacheDuration)
                {
                    return _lockCache.data;
                }
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
                    _lockCache = (DateTime.UtcNow, root, result);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
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

        public void NotifySelectionChanged()
        {
            if (svnUI.SvnTreeView != null)
                svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);

            bool commitPanelVisible =
                svnUI.SVNCommitTreeDisplay != null &&
                svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy;

            if (commitPanelVisible && _commitTreeData != null)
                svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);

            UpdateSelectedSizeDisplay();
            OnSelectionChanged?.Invoke();
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

        private string NormalizeLockPath(string path) => SvnRunner.NormalizeRepositoryPath(path);

        public void CancelCurrentRefresh()
        {
            _cts?.Cancel();
        }

        public void Dispose()
        {
            if (svnManager != null)
            {
                svnManager.OnProjectChanged -= HandleProjectChanged;
            }
            _cts?.Cancel();
            _cts?.Dispose();
            _projectSwitchDebounceCts?.Cancel();
            _projectSwitchDebounceCts?.Dispose();
        }

        private void RunOnMainThread(Action action)
        {
            if (action == null) return;
            UnityMainThreadDispatcher.Enqueue(action);
        }

        private async void HandleProjectChanged(SVNProject project)
        {
            if (project == null) return;

            _projectSwitchDebounceCts?.Cancel();
            _projectSwitchDebounceCts?.Dispose();
            _projectSwitchDebounceCts = new CancellationTokenSource();
            var debounceToken = _projectSwitchDebounceCts.Token;

            try
            {
                await Task.Delay(250, debounceToken);

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                IsProcessing = false;

                if (svnManager != null)
                {
                    svnManager.ExpandedPaths.Clear();
                    svnManager.ExpandedPaths.Add("");
                }

                ClearCurrentData();
                ClearSVNTreeView();

                svnManager.WorkingDir = project.workingDir;
                svnManager.RepositoryUrl = project.repoUrl;
                svnManager.CurrentKey = project.privateKeyPath;

                await Task.Delay(50, debounceToken);
                await RefreshModifiedInternal();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVNStatus] Project switch failed: {ex}");
            }
        }

        public void ToggleFolderVisibility(SvnTreeElement folder)
        {
            if (folder == null)
            {
                Debug.LogError("[SVN Status] ToggleFolderVisibility: folder is NULL");
                return;
            }
            if (!folder.IsFolder)
            {
                Debug.LogWarning($"[SVN Status] ToggleFolderVisibility: '{folder.Name}' is not a folder");
                return;
            }

            folder.IsExpanded = !folder.IsExpanded;

            if (svnManager != null)
            {
                if (folder.IsExpanded)
                    svnManager.ExpandedPaths.Add(folder.FullPath);
                else
                    svnManager.ExpandedPaths.Remove(folder.FullPath);
            }

            if (_flatTreeData == null || _flatTreeData.Count == 0)
            {
                Debug.LogWarning("[SVN Status] ToggleFolderVisibility: _flatTreeData is empty");
                return;
            }

            int startIndex = _flatTreeData.FindIndex(e => e == folder);
            if (startIndex == -1)
            {
                Debug.LogError($"[SVN Status] ToggleFolderVisibility: folder '{folder.FullPath}' not found in current list");
                return;
            }

            string folderPath = folder.FullPath;
            string prefix = folderPath.EndsWith("/") ? folderPath : folderPath + "/";
            var localLookup = new Dictionary<string, SvnTreeElement>(32, StringComparer.OrdinalIgnoreCase)
            {
                [folderPath] = folder
            };

            for (int i = startIndex + 1; i < _flatTreeData.Count; i++)
            {
                var e = _flatTreeData[i];
                if (!e.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) break;

                localLookup[e.FullPath] = e;
                string parentPath = GetParentPath(e.FullPath);
                if (string.IsNullOrEmpty(parentPath)) continue;

                if (localLookup.TryGetValue(parentPath, out var parent))
                    e.IsVisible = parent.IsVisible && parent.IsExpanded;
            }

            if (_commitTreeData != null)
            {
                var commitFolder = _commitTreeData.FirstOrDefault(e =>
                    e.IsFolder && string.Equals(e.FullPath, folder.FullPath, StringComparison.OrdinalIgnoreCase));
                if (commitFolder != null)
                {
                    commitFolder.IsExpanded = folder.IsExpanded;
                    RestoreExpandedPaths(_commitTreeData,
                        new HashSet<string>(svnManager?.ExpandedPaths ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase));
                }
            }

            svnUI.SvnTreeView?.RefreshUI(_flatTreeData, this);

            bool commitPanelVisible = svnUI.SVNCommitTreeDisplay != null &&
                                      svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy;
            if (commitPanelVisible && _commitTreeData != null)
                svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
        }

        public void ExpandAll()
        {
            if (_flatTreeData != null)
            {
                foreach (var item in _flatTreeData)
                {
                    if (item.IsFolder) item.IsExpanded = true;
                    item.IsVisible = true;
                }
                svnUI.SvnTreeView?.RefreshUI(_flatTreeData, this);
            }

            if (_commitTreeData != null)
            {
                foreach (var item in _commitTreeData)
                {
                    if (item.IsFolder) item.IsExpanded = true;
                    item.IsVisible = true;
                }
                svnUI.SVNCommitTreeDisplay?.RefreshUI(_commitTreeData, this);
            }

            SyncExpandedPathsFromTree();
        }

        public void CollapseAll()
        {
            if (_flatTreeData != null)
            {
                foreach (var item in _flatTreeData)
                {
                    if (item.IsFolder) item.IsExpanded = false;
                    item.IsVisible = (item.Depth <= 1);
                }
                svnUI.SvnTreeView?.RefreshUI(_flatTreeData, this);
            }

            if (_commitTreeData != null)
            {
                foreach (var item in _commitTreeData)
                {
                    if (item.IsFolder) item.IsExpanded = false;
                    item.IsVisible = (item.Depth <= 1);
                }
                svnUI.SVNCommitTreeDisplay?.RefreshUI(_commitTreeData, this);
            }

            if (svnManager != null)
            {
                svnManager.ExpandedPaths.Clear();
                svnManager.ExpandedPaths.Add("");
            }
        }

        private string GetParentPath(string path)
        {
            int lastSlash = path.LastIndexOf('/');
            return lastSlash > 0 ? path.Substring(0, lastSlash) : "";
        }

        public async Task RefreshAfterAction()
        {
            ClearSVNTreeView();
            await ExecuteRefreshWithAutoExpand(force: true);
        }

        public async void ShowOnlyModified()
        {
            try { await RefreshModifiedInternal(); }
            catch (Exception e) { SVNLogBridge.LogErrorToOutput($"[SVN] Błąd podczas odświeżania: {e.Message}"); }
        }

        public async Task RefreshModifiedInternal()
        {
            ClearSVNTreeView();
            RunOnMainThread(() =>
            {
                if (svnUI.TreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Refreshing...", "TREE", append: false);
                if (svnUI.CommitTreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "Refreshing...", "COMMIT_TREE", append: false);
            });
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
            string expectedWorkingDir = svnManager.WorkingDir;
            void ResetScanningText(string message = "")
            {
                RunOnMainThread(() =>
                {
                    if (svnUI != null && svnUI.TreeDisplay != null && svnUI.TreeDisplay.text.Contains("Scanning"))
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, message, "TREE", append: false);
                });
            }
            try
            {
                var expandedPaths = CaptureExpandedPaths();
                RunOnMainThread(() =>
                {
                    if (svnUI != null)
                    {
                        if (svnUI.TreeDisplay != null)
                            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Scanning local changes...", "TREE", append: false);
                        if (svnUI.CommitTreeDisplay != null && svnUI.CommitTreeDisplay.gameObject.activeInHierarchy)
                            SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "Refreshing commit list...", "COMMIT_TREE", append: false);
                    }
                    svnUI?.SvnTreeView?.ClearView();
                    svnUI?.SVNCommitTreeDisplay?.ClearView();
                });
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                string root = svnManager.WorkingDir;
                Dictionary<string, SvnChangeInfo> statusDict = null;
                Dictionary<string, SVNLockDetails> lockDict = null;
                await Task.Run(async () =>
                {
                    var statusTask = GetChangesDictionaryAsync(root, ShowUnversionedFiles, token);
                    var locksTask = GetLocksDictionaryAsync(root, token);
                    await Task.WhenAll(statusTask, locksTask);
                    token.ThrowIfCancellationRequested();
                    statusDict = statusTask.Result;
                    lockDict = locksTask.Result;

                    // === OPCJA B: Filtracja lokalnych reguł z .svnignore ===
                    var ignoreModule = svnManager.GetModule<SVNIgnore>();
                    if (ignoreModule != null && statusDict != null && statusDict.Count > 0)
                    {
                        ignoreModule.FilterOutLocallyIgnored(statusDict);
                    }
                    // ======================================================

                }, token).ConfigureAwait(false);
                await Task.Yield();
                if (svnManager.WorkingDir != expectedWorkingDir)
                {
                    SVNLogBridge.LogToOutput("<color=orange>[SVN]</color> Project changed during refresh — discarding results.");
                    ResetScanningText();
                    return;
                }
                if (statusDict == null || statusDict.Count == 0)
                {
                    RunOnMainThread(ShowEmptyState);
                    return;
                }
                RunOnMainThread(() =>
                {
                    if (svnUI != null)
                    {
                        if (svnUI.TreeDisplay != null)
                            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                        if (svnUI.CommitTreeDisplay != null && svnUI.CommitTreeDisplay.gameObject.activeInHierarchy)
                            SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                    }
                });
                var previousSelectionStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in _flatTreeData)
                {
                    if (!string.IsNullOrEmpty(e.FullPath))
                        previousSelectionStates[e.FullPath] = e.IsChecked;
                }
                var buildResult = await Task.Run(() => BuildFlatTreeStructureText(root, statusDict, previousSelectionStates), token);
                token.ThrowIfCancellationRequested();
                if (svnManager.WorkingDir != expectedWorkingDir) return;
                var newCommitData = await Task.Run(() => BuildCommitView(buildResult.Elements), token);
                token.ThrowIfCancellationRequested();
                if (svnManager.WorkingDir != expectedWorkingDir) return;
                _flatTreeData = buildResult.Elements;
                _commitTreeData = newCommitData;
                totalCommitBytes = buildResult.TotalBytes;
                RestoreExpandedPaths(_flatTreeData, expandedPaths);
                RestoreExpandedPaths(_commitTreeData, expandedPaths);
                if (lockDict != null && lockDict.Count > 0)
                    ApplyLockColors(_flatTreeData, lockDict);
                var localFlatData = _flatTreeData;
                var localCommitData = _commitTreeData;
                RunOnMainThread(() =>
                {
                    if (svnManager.WorkingDir != expectedWorkingDir) return;
                    if (svnUI.SvnTreeView != null && svnUI.SvnTreeView.gameObject.activeInHierarchy)
                    {
                        foreach (var e in localFlatData) e.IsCommitDelegate = false;
                        svnUI.SvnTreeView.RefreshUI(localFlatData, this);
                    }
                    if (svnUI.SVNCommitTreeDisplay != null && svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy)
                    {
                        foreach (var e in localCommitData) e.IsCommitDelegate = true;
                        if (svnUI.CommitTreeDisplay != null)
                            SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                        svnUI.SVNCommitTreeDisplay.RefreshUI(localCommitData, this);
                        UpdateSelectedSizeDisplay();
                    }
                    UpdateAllStatisticsUI(CalculateStats(statusDict), _isCurrentViewIgnored);
                });
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogToOutput("<color=orange>[SVN]</color> Refresh canceled.");
                ResetScanningText("<i>Refresh canceled.</i>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"Refresh Error: {ex}");
                ResetScanningText("<color=red>Error during scan. Press Refresh.</color>");
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
            RunOnMainThread(() =>
            {
                _flatTreeData?.Clear();
                _commitTreeData?.Clear();
                if (svnManager != null && svnManager.CurrentStatusDict != null)
                    svnManager.CurrentStatusDict.Clear();
                totalCommitBytes = 0;
            });
        }

        public void ClearSVNTreeView()
        {
            RunOnMainThread(() =>
            {
                foreach (var svnTreeView in svnUI.SVNTreeViews)
                    svnTreeView.ClearView();
            });
        }

        public void ResetTreeView()
        {
            RunOnMainThread(() =>
            {
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected. (Everything up to date)</i>", "TREE", append: false);
            });
        }

        private SvnStats CalculateStats(Dictionary<string, SvnChangeInfo> statusDict)
        {
            SvnStats stats = new SvnStats();
            if (statusDict == null) return stats;

            foreach (var item in statusDict.Values)
            {
                bool isFolder = item.Size == "DIR";
                string s = item.Status;

                if (isFolder)
                {
                    stats.FolderCount++;
                    if (s == "?") stats.NewFilesCount++;
                    continue;
                }

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
                    result.Add(e);
                }
            }
            return result;
        }

        private (List<SvnTreeElement> Elements, long TotalBytes) BuildFlatTreeStructureText(
            string root,
            Dictionary<string, SvnChangeInfo> statusDict,
            Dictionary<string, bool> previousSelectionStates)
        {
            bool hasRootChange = statusDict.ContainsKey(".");

            int estimatedCount = statusDict.Count * 2;
            var elements = new List<SvnTreeElement>(estimatedCount);
            var existingPaths = new HashSet<string>(estimatedCount, StringComparer.Ordinal);
            var pathToIndex = new Dictionary<string, int>(estimatedCount, StringComparer.Ordinal);

            var sortedPaths = new List<string>(statusDict.Keys);
            sortedPaths.Sort(StringComparer.Ordinal);

            long localTotalBytes = 0;

            if (hasRootChange)
            {
                var rootInfo = statusDict["."];
                pathToIndex[".svn-root"] = 0;
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
                if (relPath == ".") continue;

                string normalizedPath = SvnRunner.NormalizeRepositoryPath(relPath);
                if (!string.IsNullOrEmpty(normalizedPath))
                    normalizedPath = normalizedPath.Replace('\\', '/').Trim();
                string[] parts = normalizedPath.Split('/');
                string currentPath = "";

                for (int i = 0; i < parts.Length; i++)
                {
                    string partName = parts[i];
                    if (string.IsNullOrEmpty(partName)) continue;

                    currentPath = string.IsNullOrEmpty(currentPath) ? partName : currentPath + "/" + partName;

                    if (!existingPaths.Add(currentPath))
                        continue;

                    bool isLastPart = (i == parts.Length - 1);
                    bool isActuallyFolder = !isLastPart || (statusDict.TryGetValue(relPath, out var info) && info.Size == "DIR");

                    string displayStatus = " ";

                    if (isLastPart)
                    {
                        if (statusDict.TryGetValue(relPath, out var finalInfo))
                            displayStatus = finalInfo.Status;
                    }
                    else if (isActuallyFolder)
                    {
                        if (statusDict.TryGetValue(currentPath, out var folderInfo))
                            displayStatus = folderInfo.Status;
                        else
                            displayStatus = "DIR";
                    }

                    string fileSize = "";
                    long bytes = 0;

                    if (ENABLE_FILE_SIZES && !isActuallyFolder && isLastPart && statusDict.TryGetValue(relPath, out var fileInfo))
                    {
                        bytes = fileInfo.Bytes;
                        fileSize = FormatSize(bytes);

                        if (displayStatus != " " && displayStatus != "DIR" && displayStatus != "!" && displayStatus != "D")
                            localTotalBytes += bytes;
                    }

                    bool isChecked = !string.IsNullOrWhiteSpace(displayStatus) && displayStatus != " " && displayStatus != "I";
                    if (previousSelectionStates.TryGetValue(currentPath, out bool prev))
                        isChecked = prev;

                    pathToIndex[currentPath] = elements.Count;
                    elements.Add(new SvnTreeElement
                    {
                        FullPath = currentPath,
                        Name = partName,
                        Depth = i + 1,
                        Status = displayStatus,
                        IsFolder = isActuallyFolder,
                        IsChecked = isChecked,
                        IsExpanded = false,
                        IsVisible = true,
                        Size = fileSize,
                        LockedByMe = false,
                        LockedByOther = false,
                        Bytes = (isActuallyFolder || !ENABLE_FILE_SIZES || !isLastPart) ? 0 : bytes
                    });
                }
            }

            for (int i = elements.Count - 1; i >= 0; i--)
            {
                var el = elements[i];
                if (el.IsFolder || !el.IsChecked) continue;

                string parentPath = GetParentPath(el.FullPath);
                while (!string.IsNullOrEmpty(parentPath))
                {
                    if (pathToIndex.TryGetValue(parentPath, out int parentIdx))
                    {
                        var parent = elements[parentIdx];
                        if (parent.IsChecked) break;
                        parent.IsChecked = true;
                        parentPath = GetParentPath(parentPath);
                    }
                    else break;
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
                if (!element.IsChecked || element.IsFolder || element.Status == "!" || element.Status == "D")
                    continue;
                selectedBytes += element.Bytes;
            }

            totalCommitBytes = selectedBytes;
            svnUI.CommitSizeText.text = $"Total Commit Size: <color=#FFFF00>{FormatSize(selectedBytes)}</color>";
        }

        private HashSet<string> CaptureExpandedPaths()
        {
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_flatTreeData != null)
            {
                foreach (var e in _flatTreeData)
                {
                    if (e.IsFolder && e.IsExpanded && !string.IsNullOrEmpty(e.FullPath))
                        expanded.Add(e.FullPath);
                }
            }

            if (expanded.Count == 0 && svnManager?.ExpandedPaths != null)
            {
                foreach (var p in svnManager.ExpandedPaths)
                {
                    if (!string.IsNullOrEmpty(p))
                        expanded.Add(p);
                }
            }

            if (svnManager != null)
            {
                svnManager.ExpandedPaths.Clear();
                foreach (var p in expanded)
                    svnManager.ExpandedPaths.Add(p);
                svnManager.ExpandedPaths.Add("");
            }

            return expanded;
        }

        private void RestoreExpandedPaths(List<SvnTreeElement> elements, HashSet<string> expanded)
        {
            if (elements == null || elements.Count == 0) return;

            expanded ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in elements)
            {
                if (!e.IsFolder) continue;
                e.IsExpanded = !string.IsNullOrEmpty(e.FullPath) && expanded.Contains(e.FullPath);
            }

            var byPath = new Dictionary<string, SvnTreeElement>(elements.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var e in elements)
            {
                if (!string.IsNullOrEmpty(e.FullPath))
                    byPath[e.FullPath] = e;
            }

            foreach (var e in elements)
            {
                if (e.Depth <= 1)
                {
                    e.IsVisible = true;
                    continue;
                }

                string parentPath = GetParentPath(e.FullPath);
                bool visible = true;

                while (!string.IsNullOrEmpty(parentPath))
                {
                    if (!byPath.TryGetValue(parentPath, out var parent) || !parent.IsExpanded)
                    {
                        visible = false;
                        break;
                    }
                    parentPath = GetParentPath(parentPath);
                }

                e.IsVisible = visible;
            }
        }

        private void SyncExpandedPathsFromTree()
        {
            if (svnManager == null) return;

            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");

            if (_flatTreeData == null) return;

            foreach (var e in _flatTreeData)
            {
                if (e.IsFolder && e.IsExpanded && !string.IsNullOrEmpty(e.FullPath))
                    svnManager.ExpandedPaths.Add(e.FullPath);
            }
        }
    }
}