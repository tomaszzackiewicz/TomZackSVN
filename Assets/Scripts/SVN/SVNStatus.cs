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
        private int _activeRefreshId = 0;
        private const bool ENABLE_FILE_SIZES = true;
        private CancellationTokenSource _projectSwitchDebounceCts;

        // === FIX CACHE: cache locków kluczowany per root — dwie instancje
        // SVNStatus (np. dwa okna edytora) nie nadpisywały sobie nawzajem
        // wpisów innego projektu.
        private static readonly Dictionary<string, (DateTime time, Dictionary<string, SVNLockDetails> data)> _lockCacheMap
            = new Dictionary<string, (DateTime, Dictionary<string, SVNLockDetails>)>(StringComparer.OrdinalIgnoreCase);
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
                // === FIX R/~: "~" rzadkie — pokazywane tylko gdy występuje,
                // żeby nie wydłużać linii zbędnym zerem.
                string obstructedPart = stats.ObstructedCount > 0
                    ? $" | <color=#FF8800>Obs (~): {stats.ObstructedCount}</color>"
                    : "";

                string statsContent = isIgnoredView
                    ? $"<color=#444444><b>VIEW: IGNORED</b></color> | Folders: {stats.IgnoredFolderCount} | Files: {stats.IgnoredFileCount} | Total Ignored: <color=#FFFFFF>{stats.IgnoredCount}</color>"
                    : $"Folders: {stats.FolderCount} | Files: {stats.FileCount} | <color=#FFD700>Mod (M): {stats.ModifiedCount}</color> | <color=#00FF00>Add (A): {stats.AddedCount}</color> | <color=#00E5FF>New (?): {stats.NewFilesCount}</color> | <color=#FF4444>Del (D/!): {stats.DeletedCount}</color> | <color=#A0A0FF>Rep (R): {stats.ReplacedCount}</color> | <color=#FF00FF>Conf (C): {stats.ConflictsCount}</color>{obstructedPart}";

                SVNLogBridge.UpdateUIField(svnUI.StatsText, statsContent, "STATS", append: false);
            }

            if (svnUI.CommitStatsText != null)
            {
                if (isIgnoredView)
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitStatsText,
                        "<color=#FFCC00>Switch to 'Modified' view to see commit details.</color>",
                        "STATS", append: false);
                }
                else
                {
                    // === FIX R: R jest commitowalne → wchodzi do Total.
                    // C i ~ NIE wchodzą (wymagają resolve/naprawy przed committem).
                    int totalToCommit = stats.ModifiedCount + stats.AddedCount +
                                        stats.NewFilesCount + stats.DeletedCount +
                                        stats.ReplacedCount;

                    string conflictPart = stats.ConflictsCount > 0
                        ? $" | <color=#FF0000><b> CONFLICTS (C): {stats.ConflictsCount} (Resolve first!)</b></color>"
                        : "";

                    string obstructedPart = stats.ObstructedCount > 0
                        ? $" | <color=#FF8800><b> OBSTRUCTED (~): {stats.ObstructedCount} (Fix first!)</b></color>"
                        : "";

                    string commitStats =
                        $"<b>Pending Changes:</b> " +
                        $"<color=#FFD700>M: {stats.ModifiedCount}</color> | " +
                        $"<color=#00FF00>A: {stats.AddedCount}</color> | " +
                        $"<color=#00E5FF>?: {stats.NewFilesCount}</color> | " +
                        $"<color=#FF4444>D/!: {stats.DeletedCount}</color> | " +
                        $"<color=#A0A0FF>R: {stats.ReplacedCount}</color> | " +
                        $"<color=#FFFFFF><b>Total: {totalToCommit}</b></color>{conflictPart}{obstructedPart}";

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
            // === FIX LIMIT: 500 tnęło CAŁE poddrzewo w jednym strumieniu
            // rekursywnej enumeracji — duży folder "?" tracił wszystko poza
            // pierwszymi ~500 wpisami (w porządku alfabetycznym).
            // BFS + realny fail-safe per folder "?" (dostosuj wartości do siebie).
            const int maxUnversionedFilesPerRoot = 25_000;
            const int maxUnversionedDirsPerRoot = 10_000;

            workingDir = workingDir.Replace("\\", "/").TrimEnd('/');

            string output = await SvnRunner.RunAsync(
                "status --ignore-externals",
                workingDir,
                token: cancellationToken);

            if (string.IsNullOrWhiteSpace(output))
                return new Dictionary<string, SvnChangeInfo>(2048, StringComparer.OrdinalIgnoreCase);

            var statusDict = await Task.Run(() =>
            {
                var dict = new Dictionary<string, SvnChangeInfo>(2048, StringComparer.OrdinalIgnoreCase);
                string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (line.Length < svnStatusPrefixLength)
                        continue;

                    char itemStatus = line[0];
                    char propStatus = line[1];

                    // === FIX TREE-CONFLICT: svn status pokazuje tree-conflicts jako
                    // 'C' w kolumnie 6 (nie 0). Format: "!      C Models" — parser
                    // widział '!' (missing) i gubił 'C' (conflict) → STATS 0 konfliktów
                    // → Resolve panel pusty → Commit All pada na E155015.
                    char treeConflictStatus = line.Length > 6 ? line[6] : ' ';

                    // Jeżeli tree-conflict: nadpisz status na 'C' (priorytet)
                    if (treeConflictStatus == 'C')
                    {
                        itemStatus = 'C';
                        propStatus = ' ';
                    }

                    char activeChar = itemStatus != ' '
                        ? char.ToUpperInvariant(itemStatus)
                        : char.ToUpperInvariant(propStatus);

                    if (allowedSvnStatuses.IndexOf(activeChar) < 0)
                        continue;

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
                    bool existsOnDisk = false;
                    bool isDeletedOrMissing = (activeChar == 'D' || activeChar == '!');
                    string sizeLabel = fileLabel;
                    long bytes = 0;

                    if (!isDeletedOrMissing)
                    {
                        if (Directory.Exists(fullPathNative))
                        {
                            isDir = true;
                            existsOnDisk = true;
                            sizeLabel = directoryLabel;
                        }
                        else if (File.Exists(fullPathNative))
                        {
                            existsOnDisk = true;
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

                        // === FIX TREE-CONFLICT: tree-conflicted dirs mogą istnieć na dysku
                        if (activeChar == 'C' && Directory.Exists(fullPathNative))
                        {
                            isDir = true;
                            existsOnDisk = true;
                            sizeLabel = directoryLabel;
                        }
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

                        if (!Directory.Exists(fullDirPathNative))
                            continue;

                        // === FIX LIMIT: BFS po katalogach (poziomami) zamiast jednego
                        // rekursywnego strumienia — komplet plików top-level +
                        // podkatalogi przed głębokimi poziomami. Fail-safe chroni
                        // tylko przed patologicznymi drzewami.
                        int countedFiles = 0;
                        int countedDirs = 0;

                        var dirQueue = new Queue<string>();
                        dirQueue.Enqueue(fullDirPathNative);

                        while (dirQueue.Count > 0 &&
                               countedFiles < maxUnversionedFilesPerRoot &&
                               countedDirs < maxUnversionedDirsPerRoot)
                        {
                            string currentDir = dirQueue.Dequeue();

                            try
                            {
                                foreach (var fileFullPath in Directory.EnumerateFiles(currentDir))
                                {
                                    if (++countedFiles > maxUnversionedFilesPerRoot) break;

                                    cancellationToken.ThrowIfCancellationRequested();

                                    if (SvnPathUtils.IsInsideSvnAdminDir(fileFullPath))
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
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { }

                            try
                            {
                                foreach (var dirFullPath in Directory.EnumerateDirectories(currentDir))
                                {
                                    cancellationToken.ThrowIfCancellationRequested();

                                    // .svn pomijamy PRZY ENKOLEJKOWANIU — zero I/O w środku
                                    if (SvnPathUtils.IsInsideSvnAdminDir(dirFullPath))
                                        continue;

                                    string normalizedDirPath = dirFullPath.Replace('\\', '/');
                                    if (!normalizedDirPath.StartsWith(workingDir + "/", StringComparison.OrdinalIgnoreCase))
                                        continue;

                                    string dirRel = normalizedDirPath.Substring(workingDir.Length + 1).Trim('/');
                                    if (string.IsNullOrWhiteSpace(dirRel) || dict.ContainsKey(dirRel))
                                        continue;

                                    if (++countedDirs > maxUnversionedDirsPerRoot) break;

                                    dict[dirRel] = new SvnChangeInfo
                                    {
                                        Status = "?",
                                        Size = directoryLabel,
                                        Bytes = 0,
                                        Exists = true
                                    };

                                    dirQueue.Enqueue(dirFullPath);
                                }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { }
                        }
                    }
                }
                else
                {
                    var toRemove = new List<string>();
                    foreach (var kvp in dict)
                    {
                        if (kvp.Value.Status != "?")
                            continue;

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
            if (list == null || list.Count == 0)
                return;

            int startIndex = list.FindIndex(e => e.FullPath == path);
            if (startIndex == -1)
                return;

            list[startIndex].IsChecked = isChecked;
            string prefix = path + "/";

            for (int i = startIndex + 1; i < list.Count; i++)
            {
                if (list[i].FullPath.StartsWith(prefix))
                    list[i].IsChecked = isChecked;
            }
        }

        public List<SvnTreeElement> GetCurrentData() => _flatTreeData;

        public async Task<Dictionary<string, SVNLockDetails>> GetLocksDictionaryAsync(
            string root, CancellationToken token = default)
        {
            var empty = new Dictionary<string, SVNLockDetails>();
            if (string.IsNullOrEmpty(root)) return empty;

            lock (_cacheLock)
            {
                if (_lockCacheMap.TryGetValue(root, out var cached) &&
                    (DateTime.UtcNow - cached.time) < LockCacheDuration)
                {
                    return cached.data;
                }
            }

            var result = new Dictionary<string, SVNLockDetails>();

            try
            {
                var lockModule = svnManager.GetModule<SVNLock>();
                if (lockModule == null)
                    return result;

                // === FIX: token przekazywany do modułu — cancel nie czeka na
                // proces svn. Wymaga przeciążenia w SVNLock (patrz notka A/C).
                var locks = await lockModule.GetDetailedLocks(root, token);
                token.ThrowIfCancellationRequested();

                foreach (var l in locks)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(l.FullPath))
                        continue;

                    string normalized = NormalizeLockPath(l.FullPath);
                    result[normalized] = l;
                }

                lock (_cacheLock)
                {
                    PruneLockCache();
                    _lockCacheMap[root] = (DateTime.UtcNow, result);
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

        private static void PruneLockCache()
        {
            var expired = _lockCacheMap
                .Where(kvp => (DateTime.UtcNow - kvp.Value.time) > LockCacheDuration)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in expired) _lockCacheMap.Remove(key);
        }

        public static void ClearLockCache()
        {
            lock (_cacheLock) _lockCacheMap.Clear();
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
            if (elements == null || lockDict == null)
                return;

            string currentUser = svnManager.CurrentUserName?.Trim().ToLower();

            foreach (var e in elements)
            {
                e.LockedByMe = false;
                e.LockedByOther = false;

                if (string.IsNullOrEmpty(e.FullPath))
                    continue;

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

        private string NormalizeLockPath(string path) =>
            SvnRunner.NormalizeRepositoryPath(path);

        public void CancelCurrentRefresh()
        {
            _cts?.Cancel();
        }

        public void Dispose()
        {
            if (svnManager != null)
                svnManager.OnProjectChanged -= HandleProjectChanged;

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

            var oldDebounce = _projectSwitchDebounceCts;
            _projectSwitchDebounceCts = new CancellationTokenSource();
            var debounceToken = _projectSwitchDebounceCts.Token;

            oldDebounce?.Cancel();
            _ = Task.Delay(1000).ContinueWith(_ =>
            {
                try { oldDebounce?.Dispose(); } catch { }
            });

            try
            {
                await Task.Delay(250, debounceToken);

                CancelCurrentRefresh();

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
                SVNLogBridge.LogWarning($"[SVN Status] ToggleFolderVisibility: '{folder.Name}' is not a folder");
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
                SVNLogBridge.LogWarning("[SVN Status] ToggleFolderVisibility: _flatTreeData is empty");
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
                if (!e.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    break;

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
                        new HashSet<string>(svnManager?.ExpandedPaths ?? Enumerable.Empty<string>(),
                            StringComparer.OrdinalIgnoreCase));
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
            catch (Exception e)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Błąd podczas odświeżania: {e.Message}");
            }
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
            int myId = Interlocked.Increment(ref _activeRefreshId);

            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            if (oldCts != null)
            {
                oldCts.Cancel();
                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    try { oldCts.Dispose(); } catch { }
                });
            }

            IsProcessing = true;
            string expectedWorkingDir = svnManager.WorkingDir;

            void ResetScanningText(string message = "")
            {
                RunOnMainThread(() =>
                {
                    if (Volatile.Read(ref _activeRefreshId) != myId) return;
                    if (svnUI != null && svnUI.TreeDisplay != null &&
                        svnUI.TreeDisplay.text.Contains("Scanning"))
                    {
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, message, "TREE", append: false);
                    }
                });
            }

            try
            {
                var expandedPaths = CaptureExpandedPaths();
                CommitExpandedPaths(expandedPaths);

                var previousSelectionStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                if (_flatTreeData != null)
                {
                    foreach (var e in _flatTreeData)
                    {
                        if (!string.IsNullOrEmpty(e.FullPath))
                            previousSelectionStates[e.FullPath] = e.IsChecked;
                    }
                }

                RunOnMainThread(() =>
                {
                    if (Volatile.Read(ref _activeRefreshId) != myId) return;
                    if (svnUI != null)
                    {
                        if (svnUI.TreeDisplay != null)
                            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Scanning local changes...", "TREE", append: false);

                        if (svnUI.CommitTreeDisplay != null &&
                            svnUI.CommitTreeDisplay.gameObject.activeInHierarchy)
                        {
                            SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay,
                                "Refreshing commit list...", "COMMIT_TREE", append: false);
                        }
                    }

                    svnUI?.SvnTreeView?.ClearView();
                    svnUI?.SVNCommitTreeDisplay?.ClearView();
                });

                await Task.Yield();
                token.ThrowIfCancellationRequested();

                string root = svnManager.WorkingDir;
                Dictionary<string, SvnChangeInfo> statusDict = null;
                Dictionary<string, SVNLockDetails> lockDict = null;

                // === FIX B: konwersja do formatu czytanego przez managera
                // (PostProcessStatus → auto-open Resolve przy konfliktach).
                Dictionary<string, (string status, string size)> legacyStatusDict = null;

                await Task.Run(async () =>
                {
                    var statusTask = GetChangesDictionaryAsync(root, ShowUnversionedFiles, token);
                    var locksTask = GetLocksDictionaryAsync(root, token);

                    await Task.WhenAll(statusTask, locksTask);
                    token.ThrowIfCancellationRequested();

                    statusDict = statusTask.Result;
                    lockDict = locksTask.Result;

                    var ignoreModule = svnManager.GetModule<SVNIgnore>();
                    if (ignoreModule != null && statusDict != null && statusDict.Count > 0)
                        ignoreModule.FilterOutLocallyIgnored(statusDict);

                    // === FIX B: nikt nie aktualizował svnManager.CurrentStatusDict
                    // w tym refreshu — PostProcessStatus działał na danych
                    // POPRZEDNIEGO projektu (false-positive/negative konfliktów).
                    legacyStatusDict = new Dictionary<string, (string status, string size)>(
                        statusDict.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in statusDict)
                    {
                        string sizeStr = (kvp.Value.Size == "FILE" && kvp.Value.Bytes > 0)
                            ? SvnPathUtils.FormatBytes(kvp.Value.Bytes)
                            : "";
                        legacyStatusDict[kvp.Key] = (kvp.Value.Status, sizeStr);
                    }
                }, token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                if (svnManager.WorkingDir != expectedWorkingDir)
                {
                    SVNLogBridge.LogToOutput("<color=orange>[SVN]</color> Project changed during refresh — discarding results.");
                    ResetScanningText();
                    return;
                }

                if (statusDict == null || statusDict.Count == 0)
                {
                    RunOnMainThread(() =>
                    {
                        if (Volatile.Read(ref _activeRefreshId) != myId) return;
                        ShowEmptyState();
                    });
                    return;
                }

                RunOnMainThread(() =>
                {
                    if (Volatile.Read(ref _activeRefreshId) != myId) return;
                    if (svnUI != null)
                    {
                        if (svnUI.TreeDisplay != null)
                            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);

                        if (svnUI.CommitTreeDisplay != null &&
                            svnUI.CommitTreeDisplay.gameObject.activeInHierarchy)
                        {
                            SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                        }
                    }
                });

                var buildResult = await Task.Run(
                    () => BuildFlatTreeStructureText(root, statusDict, previousSelectionStates), token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                if (svnManager.WorkingDir != expectedWorkingDir) return;

                var newCommitData = await Task.Run(
                    () => BuildCommitView(buildResult.Elements), token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                if (svnManager.WorkingDir != expectedWorkingDir) return;

                RestoreExpandedPaths(buildResult.Elements, expandedPaths);
                RestoreExpandedPaths(newCommitData, expandedPaths);

                if (lockDict != null && lockDict.Count > 0)
                    ApplyLockColors(buildResult.Elements, lockDict);

                var localFlatData = buildResult.Elements;
                var localCommitData = newCommitData;
                var localTotalBytes = buildResult.TotalBytes;

                RunOnMainThread(() =>
                {
                    if (Volatile.Read(ref _activeRefreshId) != myId) return;
                    if (svnManager.WorkingDir != expectedWorkingDir) return;

                    // === FIX B: publikacja PRZED jakimkolwiek czytaniem przez
                    // PostProcessStatus (który biegnie po powrocie z tej metody).
                    if (legacyStatusDict != null)
                        svnManager.SetCurrentStatus(legacyStatusDict);

                    _flatTreeData = localFlatData;
                    _commitTreeData = localCommitData;
                    totalCommitBytes = localTotalBytes;

                    if (svnUI.SvnTreeView != null && svnUI.SvnTreeView.gameObject.activeInHierarchy)
                    {
                        foreach (var e in localFlatData)
                            e.IsCommitDelegate = false;

                        svnUI.SvnTreeView.RefreshUI(localFlatData, this);
                    }

                    if (svnUI.SVNCommitTreeDisplay != null &&
                        svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy)
                    {
                        foreach (var e in localCommitData)
                            e.IsCommitDelegate = true;

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
                RunOnMainThread(() =>
                {
                    if (Volatile.Read(ref _activeRefreshId) != myId) return;
                    if (svnUI != null && svnUI.TreeDisplay != null && svnUI.TreeDisplay.text.Contains("Scanning"))
                    {
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                    }
                });
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"Refresh Error: {ex}");
                ResetScanningText("<color=red>Error during scan. Press Refresh.</color>");
            }
            finally
            {
                if (Volatile.Read(ref _activeRefreshId) == myId)
                {
                    IsProcessing = false;
                }
            }
        }

        private void ShowEmptyState()
        {
            ResetTreeView();
            _flatTreeData.Clear();
            _commitTreeData?.Clear();
            svnUI.SvnTreeView?.ClearView();
            svnUI.SVNCommitTreeDisplay?.ClearView();

            // === FIX B: pusty status ≠ ostatni status — bez tego PostProcessStatus
            // po przełączeniu na czysty projekt widziałby konflikty poprzedniego.
            svnManager.SetCurrentStatus(new Dictionary<string, (string status, string size)>());

            if (svnUI.TreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected.</i>", "TREE", append: false);

            if (svnUI.CommitTreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "<i>Nothing to commit.</i>", "COMMIT_TREE", append: false);

            UpdateAllStatisticsUI(new SvnStats(), _isCurrentViewIgnored);
        }

        public void RefreshVisibleUIOnly()
        {
            svnUI.SvnTreeView?.RefreshUI(_flatTreeData, this);

            bool commitPanelVisible = svnUI.SVNCommitTreeDisplay != null &&
                                      svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy;

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
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay,
                    "<i>No changes detected. (Everything up to date)</i>", "TREE", append: false);
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

                    switch (s)
                    {
                        case "?": stats.NewFilesCount++; break;
                        // === FIX TREE-CONFLICT: konflikt na FOLDERZE też liczony ===
                        case "C": stats.ConflictsCount++; break;
                        // === FIX R/~: wcześniej R i ~ na katalogach ginęły całkowicie ===
                        case "R": stats.ReplacedCount++; break;
                        case "~": stats.ObstructedCount++; break;
                    }
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
                    // === FIX R/~: parser dopuszczał te statusy ("MA?!DC~R"),
                    // więc statystyki muszą je liczyć, żeby zgadzały się z drzewem ===
                    case "R": stats.ReplacedCount++; break;
                    case "~": stats.ObstructedCount++; break;
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
                bool isCommitable = isRoot ||
                    (!string.IsNullOrWhiteSpace(element.Status) &&
                     element.Status != " " &&
                     element.Status != "DIR");

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
                    result.Add(e);
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
            sortedPaths.Sort(StringComparer.OrdinalIgnoreCase);

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

                    currentPath = string.IsNullOrEmpty(currentPath)
                        ? partName
                        : currentPath + "/" + partName;

                    if (!existingPaths.Add(currentPath))
                        continue;

                    bool isLastPart = (i == parts.Length - 1);
                    bool isActuallyFolder = !isLastPart ||
                        (statusDict.TryGetValue(relPath, out var info) && info.Size == "DIR");

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

                    if (ENABLE_FILE_SIZES && !isActuallyFolder && isLastPart &&
                        statusDict.TryGetValue(relPath, out var fileInfo))
                    {
                        bytes = fileInfo.Bytes;
                        fileSize = FormatSize(bytes);
                    }

                    // === FIX R/~: WHITELISTA statusów commitowalnych zamiast
                    // "wszystko poza ' ' i 'I'". Wcześniej zaznaczało to też C i ~
                    // (commit pada: E155015/E200009 — ich bajty szły do Total Commit
                    // Size, choć nie dało się ich zcommitować) oraz status "DIR".
                    // Foldery-rodzice i tak dostają checkbox z pętli na końcu metody.
                    // "I" nie występuje — parser filtrował je już na wejściu.
                    // UWAGA (zmiana zachowania): C nie jest już domyślnie zaznaczone —
                    // zgodnie z komunikatem "(Resolve first!)". Po resolve i refresh
                    // status zmieni się na M/A i plik wróci do zaznaczenia.
                    bool isChecked = displayStatus == "M" ||
                                     displayStatus == "A" ||
                                     displayStatus == "?" ||
                                     displayStatus == "D" ||
                                     displayStatus == "!" ||
                                     displayStatus == "R";

                    if (previousSelectionStates.TryGetValue(currentPath, out bool prev))
                        isChecked = prev;

                    // === FIX R/~: suma spójna z whitelistą zaznaczania (C/~ nie wliczają
                    // się — inaczej Total Commit Size rozjeżdżałby się z "Pending Changes")
                    if (isChecked && !isActuallyFolder && isLastPart)
                        localTotalBytes += bytes;

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
                if (!element.IsChecked || element.IsFolder ||
                    element.Status == "!" || element.Status == "D")
                    continue;

                selectedBytes += element.Bytes;
            }

            totalCommitBytes = selectedBytes;
            svnUI.CommitSizeText.text =
                $"Total Commit Size: <color=#FFFF00>{FormatSize(selectedBytes)}</color>";
        }

        /// <summary>Czysty odczyt — NIE mutuje managera (=== FIX: rozdzielenie capture/commit).</summary>
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

            return expanded;
        }

        /// <summary>Zapis przechwyconych ścieżek do managera.</summary>
        private void CommitExpandedPaths(HashSet<string> expanded)
        {
            if (svnManager == null) return;

            svnManager.ExpandedPaths.Clear();
            foreach (var p in expanded)
                svnManager.ExpandedPaths.Add(p);

            svnManager.ExpandedPaths.Add("");
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

        /// <summary>
        /// === FIX A: publiczne wejście widoku ignored (analog ShowOnlyModified).
        /// Podłącz pod przycisk/tab "Ignored" — do tej pory format "VIEW: IGNORED"
        /// w UpdateAllStatisticsUI był martwy (flaga nigdy nie dostawała true,
        /// a CalculateStats nie liczył pól Ignored*).
        /// </summary>
        public async void ShowOnlyIgnored()
        {
            try { await RefreshIgnoredInternal(); }
            catch (Exception e)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Błąd podczas odświeżania widoku ignored: {e.Message}");
            }
        }

        /// <summary>
        /// === FIX A: pełny flow widoku ignored: GetIgnoredOnlyAsync (svn I + reguły
        /// lokalne z .svnignore i properties) → drzewo → statystyki z polami Ignored*.
        /// Powrót do widoku zmian: ShowOnlyModified() (resetuje flagę).
        /// </summary>
        public async Task RefreshIgnoredInternal()
        {
            int myId = Interlocked.Increment(ref _activeRefreshId);

            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            if (oldCts != null)
            {
                oldCts.Cancel();
                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    try { oldCts.Dispose(); } catch { }
                });
            }

            IsProcessing = true;
            string expectedWorkingDir = svnManager.WorkingDir;

            ClearSVNTreeView();

            RunOnMainThread(() =>
            {
                if (Volatile.Read(ref _activeRefreshId) != myId) return;
                if (svnUI != null && svnUI.TreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Scanning ignored files...", "TREE", append: false);
            });

            try
            {
                string root = svnManager.WorkingDir;

                var ignoreModule = svnManager.GetModule<SVNIgnore>();
                if (ignoreModule == null)
                {
                    SVNLogBridge.LogErrorToOutput("[SVNStatus] SVNIgnore module not available — ignored view unavailable.");
                    return;
                }

                var ignoredDict = await ignoreModule.GetIgnoredOnlyAsync(root, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (svnManager.WorkingDir != expectedWorkingDir) return;

                if (ignoredDict == null || ignoredDict.Count == 0)
                {
                    RunOnMainThread(() =>
                    {
                        if (Volatile.Read(ref _activeRefreshId) != myId) return;

                        _flatTreeData.Clear();
                        _commitTreeData = null;
                        _isCurrentViewIgnored = true;

                        svnUI.SvnTreeView?.ClearView();
                        svnUI.SVNCommitTreeDisplay?.ClearView();

                        if (svnUI.TreeDisplay != null)
                            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No ignored files.</i>", "TREE", append: false);

                        UpdateAllStatisticsUI(new SvnStats(), isIgnoredView: true);
                    });
                    return;
                }

                // Konwersja (status, size) → SvnChangeInfo + drzewo — off-thread.
                // "I" nie jest na whiteliście zaznaczania (BuildFlatTreeStructureText),
                // więc ignored pliki nie dostają checkboxa do commitu — poprawnie.
                var localFlatData = await Task.Run(() =>
                {
                    var statusDict = new Dictionary<string, SvnChangeInfo>(
                        ignoredDict.Count, StringComparer.OrdinalIgnoreCase);

                    foreach (var kvp in ignoredDict)
                    {
                        statusDict[kvp.Key] = new SvnChangeInfo
                        {
                            Status = kvp.Value.status,   // "I"
                            Size = kvp.Value.size,       // "DIR" / "FILE"
                            Bytes = 0,
                            Exists = true
                        };
                    }

                    return BuildFlatTreeStructureText(
                        root, statusDict, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase))
                        .Elements;
                }, token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                if (svnManager.WorkingDir != expectedWorkingDir) return;

                var stats = CalculateIgnoredStats(ignoredDict);

                RunOnMainThread(() =>
                {
                    if (Volatile.Read(ref _activeRefreshId) != myId) return;
                    if (svnManager.WorkingDir != expectedWorkingDir) return;

                    _flatTreeData = localFlatData;
                    _commitTreeData = null;          // brak panelu commit dla ignored
                    totalCommitBytes = 0;
                    _isCurrentViewIgnored = true;

                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);

                    svnUI.SvnTreeView?.RefreshUI(localFlatData, this);
                    svnUI.SVNCommitTreeDisplay?.ClearView();

                    UpdateAllStatisticsUI(stats, isIgnoredView: true);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"Ignored view refresh error: {ex}");
            }
            finally
            {
                if (Volatile.Read(ref _activeRefreshId) == myId)
                {
                    IsProcessing = false;
                }
            }
        }

        /// <summary>=== FIX A: statystyki widoku ignored — pola, których CalculateStats nie rusza.</summary>
        private static SvnStats CalculateIgnoredStats(Dictionary<string, (string status, string size)> ignoredDict)
        {
            var stats = new SvnStats();
            if (ignoredDict == null) return stats;

            foreach (var item in ignoredDict.Values)
            {
                if (item.size == "DIR")
                    stats.IgnoredFolderCount++;
                else
                    stats.IgnoredFileCount++;

                stats.IgnoredCount++;
            }

            return stats;
        }
    }
}