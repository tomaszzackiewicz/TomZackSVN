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

        // === FIX (miganie): fingerprint ostatnio wyrenderowanego drzewa.
        // Reprezentuje DANE Z SERWERA (status, bytes, locki, ochronę), NIE
        // stan UI (IsChecked/IsExpanded). Zapisywany WYŁĄCZNIE na main thread.
        private string _lastFlatTreeFingerprint;

        // === FIX (perf): cache ścieżek svn:needs-lock — `propget -R` na dużym WC
        // trwa kilka sekund, a był wołany przy KAŻDYM refreshu (commit, edit, focus).
        // TTL 2 min, tak samo jak lock cache; oba unieważniane razem przy ClearLockCache.
        private static readonly Dictionary<string, (DateTime time, HashSet<string> data)> _protectionCacheMap
            = new Dictionary<string, (DateTime, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan ProtectionCacheDuration = TimeSpan.FromMinutes(2);
        private static readonly object _protectionCacheLock = new object();

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

        // ===================================================================
        //  INVARIANT TEKSTÓW STATUSU: "Scanning/Refreshing/Loading" + wypełnione
        //  drzewo = NIGDY. Wcześniej czyszczenie tekstu i render drzewa były
        //  OSOBNYMI akcjami w kolejce dispatchera (a UpdateUIField dokładał
        //  drugi hop) — każde opóźnienie/zgubienie jednej z nich dawało oba
        //  naraz (objaw po mergu). Teraz: atomowo.
        // ===================================================================

        private static bool IsTransientStatus(string text) =>
            text != null && (text.Contains("Scanning") ||
                             text.Contains("Refreshing") ||
                             text.Contains("Loading"));

        private void ClearTransientTreeStatusTexts()
        {
            if (svnUI == null) return;

            if (svnUI.TreeDisplay != null && IsTransientStatus(svnUI.TreeDisplay.text))
                svnUI.TreeDisplay.text = "";

            if (svnUI.CommitTreeDisplay != null && IsTransientStatus(svnUI.CommitTreeDisplay.text))
                svnUI.CommitTreeDisplay.text = "";
        }

        private void HealTreeStatusTextsOnRender()
        {
            if (_flatTreeData == null || _flatTreeData.Count == 0)
                return;
            ClearTransientTreeStatusTexts();
        }

        public void ClearStaleTreeStatusTexts()
        {
            if (IsProcessing) return;
            RunOnMainThread(() =>
            {
                if (IsProcessing) return;
                ClearTransientTreeStatusTexts();
            });
        }

        private void WriteTreeStatus(int refreshId, string treeText, string commitText, bool clearTreeViews = false)
        {
            RunOnMainThread(() =>
            {
                if (Volatile.Read(ref _activeRefreshId) != refreshId) return;
                if (svnUI == null) return;

                if (svnUI.TreeDisplay != null && treeText != null)
                    svnUI.TreeDisplay.text = treeText;

                if (svnUI.CommitTreeDisplay != null && commitText != null)
                    svnUI.CommitTreeDisplay.text = commitText;

                if (clearTreeViews)
                {
                    svnUI.SvnTreeView?.ClearView();
                    svnUI.SVNCommitTreeDisplay?.ClearView();

                    // === FIX (miganie): po ClearView fingerprint jest nieaktualny
                    // — UI jest puste, więc następny refresh MUSI re-renderować.
                    _lastFlatTreeFingerprint = null;
                }
            });
        }

        // ===================================================================
        //  FINGERPRINT — reprezentuje DANE Z SERWERA, nie stan UI.
        // ===================================================================
        private static string BuildFlatTreeFingerprint(List<SvnTreeElement> elements)
        {
            if (elements == null) return "null";
            if (elements.Count == 0) return "empty";

            var sb = new System.Text.StringBuilder(elements.Count * 40);
            sb.Append(elements.Count).Append('\n');
            foreach (var e in elements)
            {
                sb.Append(e.FullPath ?? "").Append('|');
                sb.Append(e.Status ?? "").Append('|');
                sb.Append(e.Size ?? "").Append('|');
                sb.Append(e.Bytes).Append('|');
                sb.Append(e.IsFolder ? 'D' : 'F').Append('|');
                sb.Append(e.LockedByMe ? 'M' : '-').Append('|');
                sb.Append(e.LockedByOther ? 'O' : '-').Append('|');
                sb.Append(e.IsProtected ? 'P' : '-').Append('\n');
            }
            return sb.ToString();
        }

        // ===================================================================
        //  STATYSTYKI UI
        // ===================================================================

        public void UpdateAllStatisticsUI(SvnStats stats, bool isIgnoredView)
        {
            if (svnUI == null) return;

            if (svnUI.StatsText != null)
            {
                string obstructedPart = stats.ObstructedCount > 0
                    ? $" | <color={SVNColors.StatusObstructed}>Obs (~): {stats.ObstructedCount}</color>"
                    : "";

                string statsContent = isIgnoredView
                    ? $"<color=#444444><b>VIEW: IGNORED</b></color> | " +
                      $"<color={SVNColors.DirHex}>Folders: {stats.IgnoredFolderCount}</color> | " +
                      $"<color={SVNColors.FileHex}>Files: {stats.IgnoredFileCount}</color> | " +
                      $"Total Ignored: <color={SVNColors.StatusDefault}>{stats.IgnoredCount}</color>"
                    : $"<color={SVNColors.DirHex}>Folders: {stats.FolderCount}</color> | " +
                      $"<color={SVNColors.FileHex}>Files: {stats.FileCount}</color> | " +
                      $"<color={SVNColors.StatusModified}>Mod (M): {stats.ModifiedCount}</color> | " +
                      $"<color={SVNColors.StatusAdded}>Add (A): {stats.AddedCount}</color> | " +
                      $"<color={SVNColors.StatusUnversioned}>New (?): {stats.NewFilesCount}</color> | " +
                      $"<color={SVNColors.StatusDeleted}>Del (D/!): {stats.DeletedCount}</color> | " +
                      $"<color={SVNColors.StatusReplaced}>Rep (R): {stats.ReplacedCount}</color> | " +
                      $"<color={SVNColors.StatusConflict}>Conf (C): {stats.ConflictsCount}</color>{obstructedPart}";

                SVNLogBridge.UpdateUIField(svnUI.StatsText, statsContent, "STATS", append: false);
            }

            if (svnUI.CommitStatsText != null)
            {
                if (isIgnoredView)
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitStatsText,
                        $"<color={SVNColors.Warning}>Switch to 'Modified' view to see commit details.</color>",
                        "STATS", append: false);
                }
                else
                {
                    int totalToCommit = stats.ModifiedCount + stats.AddedCount +
                                        stats.NewFilesCount + stats.DeletedCount +
                                        stats.ReplacedCount;

                    string conflictPart = stats.ConflictsCount > 0
                        ? $" | <color={SVNColors.Error}><b> CONFLICTS (C): {stats.ConflictsCount} (Resolve first!)</b></color>"
                        : "";

                    string obstructedPart = stats.ObstructedCount > 0
                        ? $" | <color={SVNColors.StatusObstructed}><b> OBSTRUCTED (~): {stats.ObstructedCount} (Fix first!)</b></color>"
                        : "";

                    string commitStats =
                        $"<b>Pending Changes:</b> " +
                        $"<color={SVNColors.StatusModified}>M: {stats.ModifiedCount}</color> | " +
                        $"<color={SVNColors.StatusAdded}>A: {stats.AddedCount}</color> | " +
                        $"<color={SVNColors.StatusUnversioned}>?: {stats.NewFilesCount}</color> | " +
                        $"<color={SVNColors.StatusDeleted}>D/!: {stats.DeletedCount}</color> | " +
                        $"<color={SVNColors.StatusReplaced}>R: {stats.ReplacedCount}</color> | " +
                        $"<color={SVNColors.StatusDefault}><b>Total: {totalToCommit}</b></color>{conflictPart}{obstructedPart}";

                    SVNLogBridge.UpdateUIField(svnUI.CommitStatsText, commitStats, "STATS", append: false);
                }
            }
        }

        // ===================================================================
        //  POBIERANIE STATUSU
        // ===================================================================

        public static async Task<Dictionary<string, SvnChangeInfo>> GetChangesDictionaryAsync(
            string workingDir,
            bool expandUnversioned = true,
            CancellationToken cancellationToken = default)
        {
            const int svnStatusPrefixLength = 8;
            const string allowedSvnStatuses = "MA?!DC~R";
            const string directoryLabel = "DIR";
            const string fileLabel = "FILE";
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

                    char treeConflictStatus = line.Length > 6 ? line[6] : ' ';
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
                    string cleanPath = SvnRunner.SanitizeSvnPath(rawPath);

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

        private static async Task<HashSet<string>> SafeGetProtectedPathsAsync(
            string root, CancellationToken token)
        {
            if (string.IsNullOrEmpty(root))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_protectionCacheLock)
            {
                if (_protectionCacheMap.TryGetValue(root, out var cached) &&
                    (DateTime.UtcNow - cached.time) < ProtectionCacheDuration)
                {
                    return cached.data;
                }
            }

            try
            {
                var result = await SvnRunner.GetProtectedPathsAsync(root, token).ConfigureAwait(false);

                lock (_protectionCacheLock)
                {
                    var expired = _protectionCacheMap
                        .Where(kv => (DateTime.UtcNow - kv.Value.time) > ProtectionCacheDuration)
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var k in expired) _protectionCacheMap.Remove(k);

                    _protectionCacheMap[root] = (DateTime.UtcNow, result);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVNStatus] Protected fetch failed (flags disabled): {ex.Message}");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void ApplyProtectionFlags(List<SvnTreeElement> elements, HashSet<string> protectedPaths)
        {
            if (elements == null || protectedPaths == null)
                return;

            string root = svnManager?.WorkingDir?.Replace("\\", "/").TrimEnd('/');
            bool hasRoot = !string.IsNullOrEmpty(root);

            foreach (var e in elements)
            {
                if (e.IsFolder || string.IsNullOrEmpty(e.FullPath))
                    continue;

                string p = e.FullPath.Replace('\\', '/').Trim().TrimStart('/');
                if (p.StartsWith("./")) p = p.Substring(2);

                if (hasRoot && p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                    p = p.Substring(root.Length + 1);

                e.IsProtected = protectedPaths.Contains(p);
            }
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
            lock (_protectionCacheLock) _protectionCacheMap.Clear();

            SVNIgnore.InvalidateSvnRulesCache();
        }

        public void NotifySelectionChanged()
        {
            HealTreeStatusTextsOnRender();

            if (svnUI.SvnTreeView != null &&
                svnUI.SvnTreeView.gameObject.activeInHierarchy)
            {
                svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
            }

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
            SvnRunner.SanitizeSvnPath(path);

        public void CancelCurrentRefresh()
        {
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (svnManager != null)
                svnManager.OnProjectChanged -= HandleProjectChanged;

            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _cts, null));
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _projectSwitchDebounceCts, null));
        }

        private void RunOnMainThread(Action action)
        {
            if (action == null) return;
            UnityMainThreadDispatcher.Enqueue(action);
        }

        // ===================================================================
        //  HANDLE PROJECT CHANGED
        // ===================================================================
        private async void HandleProjectChanged(SVNProject project)
        {
            if (project == null) return;

            string normalizedNew = (project.workingDir ?? "").Replace('\\', '/').TrimEnd('/');
            string normalizedCurrent = (svnManager?.WorkingDir ?? "").Replace('\\', '/').TrimEnd('/');

            bool sameProjectAlreadyLoaded =
                !string.IsNullOrEmpty(normalizedNew) &&
                string.Equals(normalizedNew, normalizedCurrent, StringComparison.OrdinalIgnoreCase) &&
                _flatTreeData != null && _flatTreeData.Count > 0 &&
                _lastFlatTreeFingerprint != null;

            if (sameProjectAlreadyLoaded)
            {
                if (svnManager != null)
                {
                    svnManager.ExpandedPaths.Clear();
                    svnManager.ExpandedPaths.Add("");
                }

                SVNLogBridge.LogLine(
                    $"<color={SVNColors.Dim}>[SVNStatus] Skipped redundant project-change refresh (already loaded).</color>");
                return;
            }

            var oldDebounce = _projectSwitchDebounceCts;
            _projectSwitchDebounceCts = new CancellationTokenSource();
            var debounceToken = _projectSwitchDebounceCts.Token;

            SVNShared.CancelAndDisposeLater(oldDebounce);

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

        /// <summary>
        /// Przełącza rozwinięcie folderu. Identyfikacja po FullPath (nie po referencji),
        /// dzięki czemu działa niezależnie z którego widoku (główny/commit) przyszło kliknięcie.
        /// Zawsze synchronizuje oba widoki i oba drzewa danych.
        /// </summary>
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

            ToggleFolderVisibilityByPath(folder.FullPath);
        }

        public void ToggleFolderVisibilityByPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;

            HealTreeStatusTextsOnRender();

            var flatFolder = FindFolderByPath(_flatTreeData, fullPath);
            var commitFolder = FindFolderByPath(_commitTreeData, fullPath);

            var reference = flatFolder ?? commitFolder;
            if (reference == null)
            {
                SVNLogBridge.LogWarning($"[SVN Status] Toggle: folder '{fullPath}' not found in any view");
                return;
            }

            bool newExpanded = !reference.IsExpanded;

            if (flatFolder != null) flatFolder.IsExpanded = newExpanded;
            if (commitFolder != null) commitFolder.IsExpanded = newExpanded;

            if (svnManager != null)
            {
                if (newExpanded) svnManager.ExpandedPaths.Add(fullPath);
                else svnManager.ExpandedPaths.Remove(fullPath);
            }

            RecomputeDescendantVisibility(_flatTreeData, fullPath, newExpanded);
            RecomputeDescendantVisibility(_commitTreeData, fullPath, newExpanded);

            RefreshTreesIfVisible();
        }

        private static SvnTreeElement FindFolderByPath(List<SvnTreeElement> list, string fullPath)
        {
            if (list == null || list.Count == 0 || string.IsNullOrEmpty(fullPath))
                return null;

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.IsFolder && !string.IsNullOrEmpty(e.FullPath) &&
                    string.Equals(e.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        private void RecomputeDescendantVisibility(List<SvnTreeElement> list, string folderPath, bool expanded)
        {
            if (list == null || list.Count == 0 || string.IsNullOrEmpty(folderPath))
                return;
            var folder = FindFolderByPath(list, folderPath);
            if (folder != null) folder.IsExpanded = expanded;

            string prefix = folderPath.EndsWith("/") ? folderPath : folderPath + "/";

            var lookup = new Dictionary<string, SvnTreeElement>(StringComparer.OrdinalIgnoreCase);
            if (folder != null) lookup[folderPath] = folder;

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (string.IsNullOrEmpty(e.FullPath)) continue;
                if (!e.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                lookup[e.FullPath] = e;
            }

            if (lookup.Count == 0) return;

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (string.IsNullOrEmpty(e.FullPath)) continue;
                if (!e.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                bool visible = expanded;

                string parentPath = GetParentPath(e.FullPath);
                while (!string.IsNullOrEmpty(parentPath) &&
                       parentPath.Length >= folderPath.Length)
                {
                    if (lookup.TryGetValue(parentPath, out var parent))
                    {
                        if (parent.IsFolder && !parent.IsExpanded)
                        {
                            visible = false;
                            break;
                        }
                    }
                    parentPath = GetParentPath(parentPath);
                }

                e.IsVisible = visible;
            }
        }

        private void RefreshTreesIfVisible()
        {
            if (svnUI?.SvnTreeView != null &&
                svnUI.SvnTreeView.gameObject.activeInHierarchy)
            {
                svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
            }

            if (svnUI?.SVNCommitTreeDisplay != null &&
                svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy &&
                _commitTreeData != null)
            {
                svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
            }
        }

        public void ExpandAll()
        {
            HealTreeStatusTextsOnRender();

            if (_flatTreeData != null)
            {
                foreach (var item in _flatTreeData)
                {
                    if (item.IsFolder) item.IsExpanded = true;
                    item.IsVisible = true;
                }

                if (svnUI.SvnTreeView != null &&
                    svnUI.SvnTreeView.gameObject.activeInHierarchy)
                {
                    svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
                }
            }

            if (_commitTreeData != null)
            {
                foreach (var item in _commitTreeData)
                {
                    if (item.IsFolder) item.IsExpanded = true;
                    item.IsVisible = true;
                }

                if (svnUI.SVNCommitTreeDisplay != null &&
                    svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy)
                {
                    svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
                }
            }

            SyncExpandedPathsFromTree();
        }

        public void CollapseAll()
        {
            HealTreeStatusTextsOnRender();

            if (_flatTreeData != null)
            {
                foreach (var item in _flatTreeData)
                {
                    if (item.IsFolder) item.IsExpanded = false;
                    item.IsVisible = (item.Depth <= 1);
                }

                if (svnUI.SvnTreeView != null &&
                    svnUI.SvnTreeView.gameObject.activeInHierarchy)
                {
                    svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
                }
            }

            if (_commitTreeData != null)
            {
                foreach (var item in _commitTreeData)
                {
                    if (item.IsFolder) item.IsExpanded = false;
                    item.IsVisible = (item.Depth <= 1);
                }

                if (svnUI.SVNCommitTreeDisplay != null &&
                    svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy)
                {
                    svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
                }
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

        // ===================================================================
        //  REFRESH
        // ===================================================================

        public async Task RefreshAfterAction()
        {
            SVNIgnore.InvalidateSvnRulesCache();

            await ExecuteRefreshWithAutoExpand(force: true);
        }

        public async void ShowOnlyModified()
        {
            try { await RefreshModifiedInternal(); }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Błąd podczas odświeżania: {e.Message}");
            }
        }

        public async Task RefreshModifiedInternal()
        {
            // === FIX (miganie tekstu): pokaż "Refreshing..." TYLKO gdy drzewo
            // jest puste (brak cache). Gdy drzewo już jest widoczne, tekst
            // "Refreshing..." renderowałby się NA WIERZCHU plików (TreeDisplay
            // i drzewo dzielą obszar UI). ExecuteRefreshWithAutoExpand ma ten
            // sam guard dla "Scanning..." — tutaj brakowało go dla "Refreshing...".
            bool hasCache = _flatTreeData != null && _flatTreeData.Count > 0;

            if (!hasCache)
            {
                RunOnMainThread(() =>
                {
                    if (svnUI.TreeDisplay != null)
                        svnUI.TreeDisplay.text = "Refreshing...";
                    if (svnUI.CommitTreeDisplay != null)
                        svnUI.CommitTreeDisplay.text = "Refreshing...";
                });
            }

            _isCurrentViewIgnored = false;
            await ExecuteRefreshWithAutoExpand(force: true);
        }

        public async Task ExecuteRefreshWithAutoExpand(bool force = false)
        {
            int myId = Interlocked.Increment(ref _activeRefreshId);

            var newCts = new CancellationTokenSource();
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _cts, newCts));
            CancellationToken token = newCts.Token;

            IsProcessing = true;
            string expectedWorkingDir = svnManager.WorkingDir;

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

                // === FIX (miganie): jeśli mamy już drzewo (hasCache), nie
                // czyścimy widoku i nie pokazujemy "Scanning..." — user nie
                // widzi migotania. Tekst pojawia się tylko przy pierwszym
                // załadowaniu.
                bool hasCache = _flatTreeData != null && _flatTreeData.Count > 0;
                WriteTreeStatus(
                    myId,
                    hasCache ? null : "Scanning local changes...",
                    hasCache ? null : "Refreshing commit list...",
                    clearTreeViews: !hasCache);

                await Task.Yield();
                token.ThrowIfCancellationRequested();

                string root = svnManager.WorkingDir;
                Dictionary<string, SvnChangeInfo> statusDict = null;
                Dictionary<string, SVNLockDetails> lockDict = null;
                HashSet<string> protectedPaths = null;

                Dictionary<string, (string status, string size)> legacyStatusDict = null;

                await Task.Run(async () =>
                {
                    var statusTask = GetChangesDictionaryAsync(root, ShowUnversionedFiles, token);
                    var locksTask = GetLocksDictionaryAsync(root, token);
                    var protTask = SafeGetProtectedPathsAsync(root, token);

                    await Task.WhenAll(statusTask, locksTask, protTask);
                    token.ThrowIfCancellationRequested();

                    statusDict = statusTask.Result;
                    lockDict = locksTask.Result;
                    protectedPaths = protTask.Result;

                    var ignoreModule = svnManager.GetModule<SVNIgnore>();
                    if (ignoreModule != null && statusDict != null && statusDict.Count > 0)
                        ignoreModule.FilterOutLocallyIgnored(statusDict);

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
                    SVNLogBridge.LogToOutput("<color=#8B0000>>[SVN]</color> Project changed during refresh — discarding results.");
                    WriteTreeStatus(myId, "", "");
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

                WriteTreeStatus(myId, "", "");

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

                ApplyProtectionFlags(buildResult.Elements, protectedPaths);

                var localFlatData = buildResult.Elements;
                var localCommitData = newCommitData;
                var localTotalBytes = buildResult.TotalBytes;

                // === FIX (miganie): fingerprint liczony POZA main thread.
                string newFingerprint = BuildFlatTreeFingerprint(localFlatData);

                RunOnMainThread(() =>
                {
                    if (Volatile.Read(ref _activeRefreshId) != myId) return;
                    if (svnManager.WorkingDir != expectedWorkingDir) return;

                    if (svnUI.TreeDisplay != null) svnUI.TreeDisplay.text = "";
                    if (svnUI.CommitTreeDisplay != null) svnUI.CommitTreeDisplay.text = "";

                    if (legacyStatusDict != null)
                        svnManager.SetCurrentStatus(legacyStatusDict);

                    // === FIX (miganie): pomiń re-render gdy dane logicznie identyczne.
                    if (newFingerprint == _lastFlatTreeFingerprint &&
                        _flatTreeData != null && _flatTreeData.Count > 0)
                    {
                        _flatTreeData = localFlatData;
                        _commitTreeData = localCommitData;
                        totalCommitBytes = localTotalBytes;

                        UpdateAllStatisticsUI(CalculateStats(statusDict), _isCurrentViewIgnored);
                        return;
                    }

                    _lastFlatTreeFingerprint = newFingerprint;

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

                        svnUI.SVNCommitTreeDisplay.RefreshUI(localCommitData, this);
                        UpdateSelectedSizeDisplay();
                    }

                    UpdateAllStatisticsUI(CalculateStats(statusDict), _isCurrentViewIgnored);
                });
            }
            catch (OperationCanceledException)
            {
                WriteTreeStatus(myId, "", "");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"Refresh Error: {ex}");
                WriteTreeStatus(myId, "<color=red>Error during scan. Press Refresh.</color>", "");
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
            _flatTreeData.Clear();
            _commitTreeData?.Clear();

            svnUI.SvnTreeView?.ClearView();
            svnUI.SVNCommitTreeDisplay?.ClearView();

            svnManager.SetCurrentStatus(new Dictionary<string, (string status, string size)>());

            if (svnUI.TreeDisplay != null)
                svnUI.TreeDisplay.text = "<i>No changes detected.</i>";
            if (svnUI.CommitTreeDisplay != null)
                svnUI.CommitTreeDisplay.text = "<i>Nothing to commit.</i>";

            _lastFlatTreeFingerprint = null;

            UpdateAllStatisticsUI(new SvnStats(), _isCurrentViewIgnored);

            SVNLogBridge.LogToOutput(
                $"<color={SVNColors.Dim}>[SVN] Empty state at {DateTime.Now:HH:mm:ss.fff} (refreshId={Volatile.Read(ref _activeRefreshId)})</color>");
        }

        public void RefreshVisibleUIOnly()
        {
            HealTreeStatusTextsOnRender();

            if (svnUI.SvnTreeView != null &&
                svnUI.SvnTreeView.gameObject.activeInHierarchy)
            {
                svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
            }

            bool commitPanelVisible = svnUI.SVNCommitTreeDisplay != null &&
                                      svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy;

            if (commitPanelVisible && _commitTreeData != null)
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

                _lastFlatTreeFingerprint = null;
            });
        }

        public void ClearSVNTreeView()
        {
            RunOnMainThread(() =>
            {
                foreach (var svnTreeView in svnUI.SVNTreeViews)
                    svnTreeView.ClearView();

                _lastFlatTreeFingerprint = null;
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
                        case "C": stats.ConflictsCount++; break;
                        case "R": stats.ReplacedCount++; break;
                        case "~": stats.ObstructedCount++; break;
                        case "M": stats.ModifiedCount++; break;
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

                string normalizedPath = SvnRunner.SanitizeSvnPath(relPath);
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

                    bool isCommitableStatus = displayStatus == "M" ||
                                              displayStatus == "A" ||
                                              displayStatus == "?" ||
                                              displayStatus == "D" ||
                                              displayStatus == "!" ||
                                              displayStatus == "R";

                    bool isChecked = isCommitableStatus;

                    if (isCommitableStatus && previousSelectionStates.TryGetValue(currentPath, out bool prev))
                        isChecked = prev;

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

        // ===================================================================
        //  WIDOK IGNORED
        // ===================================================================

        public async void ShowOnlyIgnored()
        {
            try { await RefreshIgnoredInternal(); }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Błąd podczas odświeżania widoku ignored: {e.Message}");
            }
        }

        public async Task RefreshIgnoredInternal()
        {
            int myId = Interlocked.Increment(ref _activeRefreshId);

            var newCts = new CancellationTokenSource();
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _cts, newCts));
            CancellationToken token = newCts.Token;

            IsProcessing = true;
            string expectedWorkingDir = svnManager.WorkingDir;

            ClearSVNTreeView();
            WriteTreeStatus(myId, "Scanning ignored files...", null, clearTreeViews: true);

            try
            {
                string root = svnManager.WorkingDir;

                var ignoreModule = svnManager.GetModule<SVNIgnore>();
                if (ignoreModule == null)
                {
                    SVNLogBridge.LogErrorToOutput("[SVNStatus] SVNIgnore module not available — ignored view unavailable.");
                    WriteTreeStatus(myId, "<color=red>Ignored view unavailable.</color>", null);
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

                        _lastFlatTreeFingerprint = null;

                        svnUI.SvnTreeView?.ClearView();
                        svnUI.SVNCommitTreeDisplay?.ClearView();

                        if (svnUI.TreeDisplay != null)
                            svnUI.TreeDisplay.text = "<i>No ignored files.</i>";

                        UpdateAllStatisticsUI(new SvnStats(), isIgnoredView: true);
                    });
                    return;
                }

                var localFlatData = await Task.Run(() =>
                {
                    var statusDict = new Dictionary<string, SvnChangeInfo>(
                        ignoredDict.Count, StringComparer.OrdinalIgnoreCase);

                    foreach (var kvp in ignoredDict)
                    {
                        statusDict[kvp.Key] = new SvnChangeInfo
                        {
                            Status = kvp.Value.status,
                            Size = kvp.Value.size,
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

                string newFingerprint = BuildFlatTreeFingerprint(localFlatData);

                RunOnMainThread(() =>
                {
                    if (Volatile.Read(ref _activeRefreshId) != myId) return;
                    if (svnManager.WorkingDir != expectedWorkingDir) return;

                    if (svnUI.TreeDisplay != null)
                        svnUI.TreeDisplay.text = "";

                    if (newFingerprint == _lastFlatTreeFingerprint &&
                        _flatTreeData != null && _flatTreeData.Count > 0)
                    {
                        _flatTreeData = localFlatData;
                        _commitTreeData = null;
                        totalCommitBytes = 0;
                        _isCurrentViewIgnored = true;
                        UpdateAllStatisticsUI(stats, isIgnoredView: true);
                        return;
                    }

                    _lastFlatTreeFingerprint = newFingerprint;

                    _flatTreeData = localFlatData;
                    _commitTreeData = null;
                    totalCommitBytes = 0;
                    _isCurrentViewIgnored = true;

                    if (svnUI.SvnTreeView != null &&
                        svnUI.SvnTreeView.gameObject.activeInHierarchy)
                    {
                        svnUI.SvnTreeView.RefreshUI(localFlatData, this);
                    }

                    svnUI.SVNCommitTreeDisplay?.ClearView();

                    UpdateAllStatisticsUI(stats, isIgnoredView: true);
                });
            }
            catch (OperationCanceledException)
            {
                WriteTreeStatus(myId, "", null);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"Ignored view refresh error: {ex}");
                WriteTreeStatus(myId, "<color=red>Ignored view error. Press Refresh.</color>", null);
            }
            finally
            {
                if (Volatile.Read(ref _activeRefreshId) == myId)
                {
                    IsProcessing = false;
                }
            }
        }

        public void RenderCommitTreeIfNeeded()
        {
            if (svnUI?.SVNCommitTreeDisplay == null) return;

            RunOnMainThread(() =>
            {
                if (!svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy) return;

                if (_isCurrentViewIgnored)
                {
                    svnUI.SVNCommitTreeDisplay.ClearView();
                    UpdateSelectedSizeDisplay();
                    return;
                }

                if (_commitTreeData == null || _commitTreeData.Count == 0)
                {
                    svnUI.SVNCommitTreeDisplay.ClearView();
                    UpdateSelectedSizeDisplay();
                    return;
                }

                foreach (var e in _commitTreeData)
                    e.IsCommitDelegate = true;

                svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
                UpdateSelectedSizeDisplay();
            });
        }

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