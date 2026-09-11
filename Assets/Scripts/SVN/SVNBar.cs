using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public enum BarState
    {
        Idle,
        Updating,
        PostUpdate
    }

    public class SVNBar : SVNBase, IDisposable
    {
        private string _svnVersionCached = "";
        private Task<string> _svnVersionTask;
        private readonly Dictionary<string, (DateTime time, string working, string total)> _sizeCache = new();
        private readonly object _sizeCacheLock = new object();

        private (string path, string root) _wcRootCache;
        private volatile bool _checkoutIncomplete;

        private CancellationTokenSource _snapshotCts;

        private volatile BarState _state = BarState.Idle;
        private volatile string _desiredContent = "";
        private volatile string _lastRenderedContent = "";

        private CancellationTokenSource _monitorCts;

        public BarState CurrentState => _state;

        public SVNBar(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
        }

        public void Tick()
        {
            if (svnUI?.StatusInfoText == null) return;

            string content = _desiredContent;

            if (content != _lastRenderedContent)
            {
                svnUI.StatusInfoText.text = content;
                _lastRenderedContent = content;
            }
        }

        private void SetContent(string content, string source)
        {
            if (_desiredContent != content)
            {
                _desiredContent = content;
            }
        }

        public void SetLoadingContent(string projectName)
        {
            string content =
                $"<size=150%><color={SVNColors.StatusModified}>●</color></size> " +
                $"<color=orange><b>{projectName}</b></color> | " +
                $"<color={SVNColors.StatusModified}>Loading project...</color>";

            SetContent(content, "SetLoadingContent");
        }

        public void ShowNoWorkingCopy(string projectName = null)
        {
            string name = string.IsNullOrWhiteSpace(projectName)
                ? ""
                : $"<color={SVNColors.Dim}><b>{projectName}</b></color> | ";
            string content =
                $"<size=150%><color=black>●</color></size> " +
                $"{name}<color={SVNColors.Dim}>No working copy</color>";

            SetContent(content, "ShowNoWorkingCopy");
        }

        public async Task ShowProjectInfo(
            SVNProject svnProject,
            string path,
            bool forceOutdatedCheck = false,
            bool isRefreshing = false)
        {
            if (_state != BarState.Idle) return;

            // === FIX (cache wersji SVN per projekt): jeśli poprzedni projekt
            // miał sfaultowane wykrycie wersji ("?.?.?"), zostawało na stałe.
            // Reset przy zmianie ścieżki projektu.
            if (!string.Equals(_wcRootCache.path, path, StringComparison.OrdinalIgnoreCase))
            {
                _svnVersionTask = null;
                _svnVersionCached = "";
            }

            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _snapshotCts, null));
            var newCts = new CancellationTokenSource();
            _snapshotCts = newCts;
            var localToken = newCts.Token;

            try
            {
                var snapshot = await BuildSnapshotAsync(svnProject, path, localToken);
                if (localToken.IsCancellationRequested) return;
                if (_state != BarState.Idle) return;

                if (snapshot == null || !snapshot.IsValid)
                {
                    ShowNoWorkingCopy(svnProject?.projectName ?? Path.GetFileName(path));
                    return;
                }

                svnManager.CurrentSnapshot = snapshot;
                SetContent(BuildNormalContent(snapshot, isRefreshing), "ShowProjectInfo");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.LogError($"[SVNBar] ShowProjectInfo failed: {ex.Message}");
                ShowNoWorkingCopy(svnProject?.projectName ?? Path.GetFileName(path));
            }
        }

        public void RenderSnapshot(SVNProjectInfoSnapshot snapshot, bool isRefreshing = false)
        {
            if (_state != BarState.Idle) return;
            if (snapshot == null || !snapshot.IsValid) return;

            var current = svnManager.CurrentSnapshot;
            if (current != null
                && !string.IsNullOrEmpty(current.RemoteRevision)
                && string.IsNullOrEmpty(snapshot.RemoteRevision)
                && string.Equals(current.ProjectName, snapshot.ProjectName, StringComparison.OrdinalIgnoreCase))
            {
                snapshot.RemoteRevision = current.RemoteRevision;
                snapshot.IsOutdated = current.IsOutdated;
                snapshot.CheckoutIncomplete = current.CheckoutIncomplete;
            }

            SetContent(BuildNormalContent(snapshot, isRefreshing), "RenderSnapshot");
        }

        public void RenderFromSnapshot(SVNProjectInfoSnapshot snapshot)
        {
            RenderSnapshot(snapshot);
        }

        public void BeginUpdate(string projectName)
        {
            _state = BarState.Updating;

            if (svnManager.CurrentSnapshot == null)
            {
                svnManager.CurrentSnapshot = new SVNProjectInfoSnapshot
                {
                    ProjectName = projectName,
                    WorkingCopySize = "...",
                    RepoTotalSize = "...",
                    IsValid = true
                };
            }

            string size = svnManager.CurrentSnapshot?.WorkingCopySize ?? "...";
            string total = svnManager.CurrentSnapshot?.RepoTotalSize ?? "...";
            SetContent(BuildUpdatingContent(projectName, size, total), "BeginUpdate");

            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _monitorCts, null));
            var newMonitorCts = new CancellationTokenSource();
            _monitorCts = newMonitorCts;
            _ = RunSizeMonitor(svnManager.WorkingDir, newMonitorCts.Token);
        }

        public async Task EndUpdate(SVNProjectInfoSnapshot fallbackSnapshot)
        {
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _monitorCts, null));
            _state = BarState.Idle;

            ClearSizeCache();
            _checkoutIncomplete = false;

            bool renderedFresh = false;
            try
            {
                var freshSnapshot = await BuildSnapshotAsync(null, svnManager.WorkingDir);
                if (freshSnapshot != null && freshSnapshot.IsValid)
                {
                    if (fallbackSnapshot != null && !string.IsNullOrEmpty(fallbackSnapshot.ProjectName))
                        freshSnapshot.ProjectName = fallbackSnapshot.ProjectName;

                    if (fallbackSnapshot != null && !string.IsNullOrEmpty(fallbackSnapshot.Revision))
                    {
                        if (string.IsNullOrEmpty(freshSnapshot.Revision) || freshSnapshot.Revision == "unknown")
                            freshSnapshot.Revision = fallbackSnapshot.Revision;

                        freshSnapshot.IsOutdated = false;
                        if (string.IsNullOrEmpty(freshSnapshot.RemoteRevision))
                            freshSnapshot.RemoteRevision = freshSnapshot.Revision;
                    }

                    svnManager.CurrentSnapshot = freshSnapshot;
                    _lastRenderedContent = "";
                    SetContent(BuildNormalContent(freshSnapshot, isRefreshing: false), "EndUpdate-Fresh");
                    renderedFresh = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SVNBar] Pobranie świeżych danych po update nie powiodło się (używam fallbacku): {ex.Message}");
            }

            if (!renderedFresh && fallbackSnapshot != null)
            {
                fallbackSnapshot.IsValid = true;

                if (!string.IsNullOrEmpty(fallbackSnapshot.Revision))
                {
                    fallbackSnapshot.IsOutdated = false;
                    if (string.IsNullOrEmpty(fallbackSnapshot.RemoteRevision))
                        fallbackSnapshot.RemoteRevision = fallbackSnapshot.Revision;
                }

                svnManager.CurrentSnapshot = fallbackSnapshot;
                try
                {
                    string wcRoot = await GetWorkingCopyRootAsync(svnManager.WorkingDir);
                    var sizes = await GetSizesForNestedAsync(svnManager.WorkingDir, wcRoot);
                    fallbackSnapshot.WorkingCopySize = sizes.WorkingSize;
                    fallbackSnapshot.RepoTotalSize = sizes.TotalSize;
                }
                catch { }

                _lastRenderedContent = "";
                SetContent(BuildNormalContent(fallbackSnapshot, isRefreshing: false), "EndUpdate-Fallback");
            }
        }

        public void EndUpdateFailed(SVNProjectInfoSnapshot oldSnapshot)
        {
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _monitorCts, null));
            _state = BarState.Idle;

            if (oldSnapshot != null)
            {
                svnManager.CurrentSnapshot = oldSnapshot;
                _lastRenderedContent = "";
                SetContent(BuildNormalContent(oldSnapshot, isRefreshing: false), "EndUpdateFailed");
            }
        }

        private async Task RunSizeMonitor(string path, CancellationToken token)
        {
            try
            {
                await Task.Delay(400, token);

                string wcRoot = await GetWorkingCopyRootAsync(path, token);

                while (!token.IsCancellationRequested && _state == BarState.Updating)
                {
                    try
                    {
                        var sizes = await GetSizesForNestedAsync(path, wcRoot, token);
                        if (token.IsCancellationRequested || _state != BarState.Updating) break;

                        var snapshot = svnManager.CurrentSnapshot;
                        if (snapshot != null && snapshot.IsValid)
                        {
                            bool changed = snapshot.WorkingCopySize != sizes.WorkingSize ||
                                           snapshot.RepoTotalSize != sizes.TotalSize;
                            snapshot.WorkingCopySize = sizes.WorkingSize;
                            snapshot.RepoTotalSize = sizes.TotalSize;

                            if (changed)
                            {
                                SetContent(BuildUpdatingContent(snapshot.ProjectName, sizes.WorkingSize, sizes.TotalSize), "Monitor");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    try { await Task.Delay(5000, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private string BuildUpdatingContent(string projectName, string workingSize, string totalSize)
        {
            var snapshot = svnManager.CurrentSnapshot;
            string user = snapshot?.CurrentUser ?? "...";
            string branch = snapshot?.Branch ?? "...";
            string rev = snapshot?.Revision ?? "...";
            string svnVer = snapshot?.SvnVersion ?? "...";

            return
                $"<size=150%><color={SVNColors.StatusModified}>●</color></size> " +
                $"<color=orange><b>{projectName}</b> ({FormatSizeDisplay(workingSize, totalSize)})</color> | " +
                $"<color={SVNColors.Header}>User:</color> <color={SVNColors.Info}>{user}</color> | " +
                $"<color={SVNColors.Header}>Branch:</color> <color={SVNColors.Info}>{branch}</color> | " +
                $"<color={SVNColors.Header}>Rev:</color> <color={SVNColors.Info}>{rev}</color> | " +
                $"<color={SVNColors.Info}>SVN: {svnVer}</color>" +
                $" | <color={SVNColors.StatusModified}>Updating...</color>";
        }

        private string BuildNormalContent(SVNProjectInfoSnapshot snapshot, bool isRefreshing)
        {
            var state = svnManager.OperationInfo;
            bool isCanceled = state.State == SVNOperationState.Canceled;
            bool isFailed = state.State == SVNOperationState.Failed;

            string statusColor = "#4ca74c";
            if (isRefreshing) statusColor = SVNColors.StatusModified;
            else if (isCanceled) statusColor = SVNColors.Warning;
            else if (isFailed) statusColor = SVNColors.Error;
            else if (snapshot.IsOutdated) statusColor = SVNColors.Error;

            string shortDate = snapshot.Date != "unknown"
                ? snapshot.Date.Split('(')[0].Trim()
                : "no dates";

            string revDisplay = snapshot.IsOutdated
                ? $"<color={SVNColors.Error}>{snapshot.Revision}</color> <color={SVNColors.Warning}>(HEAD: {snapshot.RemoteRevision})</color>"
                : snapshot.Revision;

            string statusSuffix = "";
            if (isCanceled) statusSuffix = " | Update Canceled";
            else if (isFailed) statusSuffix = " | Update Interrupted";

            return
                $"<size=150%><color={statusColor}>●</color></size> " +
                $"<color=orange><b>{snapshot.ProjectName}</b> ({FormatSizeDisplay(snapshot.WorkingCopySize, snapshot.RepoTotalSize)})</color> | " +
                $"<color={SVNColors.Header}>User:</color> <color={SVNColors.Info}>{snapshot.CurrentUser}</color> | " +
                $"<color={SVNColors.Header}>Branch:</color> <color={SVNColors.Info}>{snapshot.Branch}</color> | " +
                $"<color={SVNColors.Header}>Rev:</color> <color={SVNColors.Info}>{revDisplay}</color> | " +
                $"<color={SVNColors.Header}>By:</color> <color={SVNColors.Info}>{snapshot.Author}</color> | " +
                $"<color={SVNColors.Info}>{shortDate}</color> | " +
                $"<color={SVNColors.Info}>Srv: {snapshot.Server}</color> | " +
                $"<color={SVNColors.Info}>App: {snapshot.AppVersion}</color> | " +
                $"<color={SVNColors.Info}>SVN: {snapshot.SvnVersion}</color>" +
                statusSuffix;
        }

        public void ClearSizeCache()
        {
            lock (_sizeCacheLock) _sizeCache.Clear();
        }

        // ==================== ROZMIARY ====================

        public async Task<(string WorkingSize, string TotalSize)> GetFolderSizesAsync(
            string path, CancellationToken token = default)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(path)) return ("0 MB", "0 MB");

                    long workingBytes = 0;
                    long totalBytes = 0;
                    int fileCount = 0;

                    var options = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.System
                    };

                    foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", options))
                    {
                        try
                        {
                            totalBytes += fi.Length;
                            if (!SvnPathUtils.IsInsideSvnAdminDir(fi.FullName))
                                workingBytes += fi.Length;
                        }
                        catch { }

                        if ((++fileCount & 0x1F) == 0)
                            token.ThrowIfCancellationRequested();
                    }

                    return (SvnPathUtils.FormatBytes(workingBytes), SvnPathUtils.FormatBytes(totalBytes));
                }
                catch (OperationCanceledException) { throw; }
                catch { return ("Size unknown", "Size unknown"); }
            }, token);
        }

        public async Task<string> GetFolderSizeAsync(string path, CancellationToken token = default, bool includeSvnTemp = false)
        {
            var sizes = await GetFolderSizesAsync(path, token);
            return includeSvnTemp ? sizes.TotalSize : sizes.WorkingSize;
        }

        public async Task<string> GetWorkingCopyRootAsync(string path, CancellationToken token = default)
        {
            if (!string.IsNullOrEmpty(_wcRootCache.path) &&
                string.Equals(_wcRootCache.path, path, StringComparison.OrdinalIgnoreCase) &&
                _wcRootCache.root != null)
                return _wcRootCache.root;

            string root = null;
            try
            {
                string raw = await SvnRunner.RunAsync("info --show-item wc-root", path, token: token);
                if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("Error"))
                    root = raw.Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            if (root == null)
                root = SvnPathUtils.FindWorkingCopyRoot(path) ?? path;

            _wcRootCache = (path, root);
            return root;
        }

        public async Task<(string WorkingSize, string TotalSize)> GetSizesForNestedAsync(
            string projectPath, string wcRoot, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(wcRoot) ||
                string.Equals(projectPath, wcRoot, StringComparison.OrdinalIgnoreCase))
            {
                return await GetFolderSizesAsync(projectPath, token);
            }

            var workingTask = GetFolderSizesAsync(projectPath, token);
            var totalTask = GetFolderSizesAsync(wcRoot, token);

            try { await Task.WhenAll(workingTask, totalTask); }
            catch { }

            string working = workingTask.Status == TaskStatus.RanToCompletion
                ? workingTask.Result.WorkingSize : "Size unknown";
            string total = totalTask.Status == TaskStatus.RanToCompletion
                ? totalTask.Result.TotalSize : "Size unknown";

            return (working, total);
        }

        public async Task<(string WorkingSize, string TotalSize)> GetSizesWithCacheAsync(
            string path, CancellationToken token = default)
        {
            string cacheKey = path.Replace("\\", "/").TrimEnd('/');

            lock (_sizeCacheLock)
            {
                if (_sizeCache.TryGetValue(cacheKey, out var entry) &&
                    (DateTime.UtcNow - entry.time).TotalSeconds < 10)
                {
                    return (entry.working, entry.total);
                }
            }

            string wcRoot = await GetWorkingCopyRootAsync(path, token);
            var sizes = await GetSizesForNestedAsync(path, wcRoot, token);

            if (sizes.WorkingSize != "Size unknown")
            {
                lock (_sizeCacheLock)
                {
                    _sizeCache[cacheKey] = (DateTime.UtcNow, sizes.WorkingSize, sizes.TotalSize);
                }

                string persistKey = svnManager?.CurrentProject?.workingDir ?? cacheKey;
                try
                {
                    ProjectSettings.UpdateProject(persistKey, p =>
                    {
                        p.cachedWorkingSize = sizes.WorkingSize;
                        p.cachedTotalSize = sizes.TotalSize;
                    });
                }
                catch { }
            }

            return sizes;
        }

        private static string FormatSizeDisplay(string workingSize, string totalSize)
        {
            string working = string.IsNullOrWhiteSpace(workingSize) ? "..." : workingSize;

            if (string.IsNullOrWhiteSpace(totalSize) ||
                totalSize == "unknown" || totalSize == "Size unknown")
                return $"<color={SVNColors.Header}>Project:</color> {working}";

            return $"<color={SVNColors.Header}>Project:</color> {working} " +
                   $"/ <color={SVNColors.Header}>Repo:</color> {totalSize}";
        }

        private async Task<bool> CheckCheckoutIncompleteAsync(string path, CancellationToken token = default)
        {
            try
            {
                string status = await SvnRunner.RunAsync("status", path, token: token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(status)) return false;

                foreach (var rawLine in status.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (string.IsNullOrEmpty(line)) continue;

                    if (line.Length > 0 && line[0] == '!') return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            return false;
        }

        private string ExtractValue(string info, string key)
        {
            if (string.IsNullOrEmpty(info)) return "unknown";

            int keyIndex = info.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (keyIndex == -1) return "unknown";

            int valueStart = keyIndex + key.Length;
            while (valueStart < info.Length && char.IsWhiteSpace(info[valueStart])) valueStart++;

            int valueEnd = info.IndexOf('\n', valueStart);
            if (valueEnd == -1) valueEnd = info.Length;

            return info.Substring(valueStart, valueEnd - valueStart).Trim();
        }

        private async Task EnsureVersionCached()
        {
            if (_svnVersionTask == null)
            {
                _svnVersionTask = SvnRunner.RunAsync("--version --quiet", svnManager.WorkingDir)
                    .ContinueWith(t => t.IsFaulted ? "?.?.?" : (t.Result ?? "?.?.?").Trim(),
                        TaskContinuationOptions.ExecuteSynchronously);
            }
            _svnVersionCached = await _svnVersionTask;
        }

        // ==================== SNAPSHOT ====================

        public async Task<SVNProjectInfoSnapshot> BuildSnapshotAsync(SVNProject svnProject, string path, CancellationToken token = default)
        {
            var snapshot = new SVNProjectInfoSnapshot();
            try
            {
                if (string.IsNullOrEmpty(path)) return snapshot;
                if (!SvnPathUtils.IsInsideWorkingCopy(path)) return snapshot;

                string projectName = svnProject != null && !string.IsNullOrEmpty(svnProject.projectName)
                    ? svnProject.projectName : Path.GetFileName(path);

                string cacheKey = path.Replace("\\", "/").TrimEnd('/');
                string cachedWorking = null, cachedTotal = null;
                string sizesSource = "none";

                lock (_sizeCacheLock)
                {
                    var now = DateTime.UtcNow;
                    var expiredKeys = _sizeCache.Where(kvp => (now - kvp.Value.time).TotalSeconds > 10)
                        .Select(kvp => kvp.Key).ToList();
                    foreach (var key in expiredKeys) _sizeCache.Remove(key);
                    if (_sizeCache.TryGetValue(cacheKey, out var entry) && (now - entry.time).TotalSeconds < 10)
                    {
                        cachedWorking = entry.working;
                        cachedTotal = entry.total;
                        sizesSource = "mem-cache";
                    }
                }

                if (cachedWorking == null &&
                    svnProject != null &&
                    !string.IsNullOrWhiteSpace(svnProject.cachedWorkingSize))
                {
                    cachedWorking = svnProject.cachedWorkingSize;
                    cachedTotal = string.IsNullOrWhiteSpace(svnProject.cachedTotalSize)
                        ? "..." : svnProject.cachedTotalSize;
                    sizesSource = "project-object";
                }

                if (cachedWorking == null)
                {
                    try
                    {
                        var persisted = ProjectSettings.LoadProjects().FirstOrDefault(p =>
                            !string.IsNullOrEmpty(p.workingDir) &&
                            string.Equals(p.workingDir.Replace("\\", "/").TrimEnd('/'),
                                cacheKey, StringComparison.OrdinalIgnoreCase));

                        if (persisted != null && !string.IsNullOrWhiteSpace(persisted.cachedWorkingSize))
                        {
                            cachedWorking = persisted.cachedWorkingSize;
                            cachedTotal = string.IsNullOrWhiteSpace(persisted.cachedTotalSize)
                                ? "..." : persisted.cachedTotalSize;
                            sizesSource = "persisted-lookup";
                        }
                        else
                        {
                            sizesSource = persisted == null ? "no-project-entry" : "fields-empty";
                        }
                    }
                    catch { sizesSource = "lookup-threw"; }
                }

                Debug.Log($"[SVNBar] Sizes: source={sizesSource}, key='{cacheKey}', " +
                          $"working='{cachedWorking ?? "null"}', total='{cachedTotal ?? "null"}'");

                bool fromMemCache = sizesSource == "mem-cache";

                string rawInfo = await SvnRunner.GetInfoAsync(path, token);
                await EnsureVersionCached();

                if (string.IsNullOrWhiteSpace(rawInfo) || rawInfo == "unknown")
                    return snapshot;

                snapshot.ProjectName = projectName;
                snapshot.WorkingCopySize = cachedWorking ?? "...";
                snapshot.RepoTotalSize = cachedTotal ?? "...";
                snapshot.Revision = ExtractValue(rawInfo, "Revision:");
                snapshot.Author = ExtractValue(rawInfo, "Last Changed Author:");
                snapshot.Date = ExtractValue(rawInfo, "Last Changed Date:");
                snapshot.RelativeUrl = ExtractValue(rawInfo, "Relative URL:");
                snapshot.Url = ExtractValue(rawInfo, "URL:");
                snapshot.RepoRoot = ExtractValue(rawInfo, "Repository Root:");

                string source = snapshot.RelativeUrl != "unknown" ? snapshot.RelativeUrl : snapshot.Url;
                snapshot.Branch = "trunk";
                if (!string.IsNullOrEmpty(source) && source != "unknown")
                {
                    string branch = source.Replace("^/", "").Trim();
                    if (branch.Contains("/")) branch = Path.GetFileName(branch.TrimEnd('/'));
                    if (!string.IsNullOrEmpty(branch)) snapshot.Branch = branch;
                }

                snapshot.Server = "local";
                if (!string.IsNullOrEmpty(snapshot.Url) && snapshot.Url != "unknown")
                { try { snapshot.Server = new Uri(snapshot.Url).Host; } catch { } }

                snapshot.AppVersion = Application.version;
                snapshot.SvnVersion = _svnVersionCached;
                snapshot.CurrentUser = svnManager.CurrentUserName ?? "Unknown";

                var prev = svnManager.CurrentSnapshot;
                if (prev != null && prev.IsValid
                    && !string.IsNullOrEmpty(prev.RemoteRevision)
                    && string.Equals(prev.ProjectName, snapshot.ProjectName, StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.RemoteRevision = prev.RemoteRevision;
                    snapshot.IsOutdated = prev.IsOutdated;
                    snapshot.CheckoutIncomplete = prev.CheckoutIncomplete;
                }

                snapshot.IsValid = true;

                if (!fromMemCache)
                    _ = FillHeavySnapshotDataAsync(snapshot, path, cacheKey);

                return snapshot;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogError($"[SVNBar] BuildSnapshotAsync failed: {ex.Message}");
                return snapshot;
            }
        }

        /// <summary>
        /// Background "ciężkie dane" — best effort, bez timeoutu. Jeśli svn
        /// utknie, SvnProcessTracker killuje proces na shutdown.
        /// </summary>
        private async Task FillHeavySnapshotDataAsync(
            SVNProjectInfoSnapshot snapshot, string path, string cacheKey)
        {
            try
            {
                var remoteTask = SvnRunner.RunAsync(
                    "info -r HEAD --show-item last-changed-revision",
                    path);

                string wcRoot = await GetWorkingCopyRootAsync(path).ConfigureAwait(false);
                var sizeTask = GetSizesForNestedAsync(path, wcRoot);
                var incompleteTask = CheckCheckoutIncompleteAsync(path);

                string remoteRevRaw = null;
                try { remoteRevRaw = await remoteTask.ConfigureAwait(false); }
                catch { }

                var sizes = await sizeTask.ConfigureAwait(false);
                bool incomplete = await incompleteTask.ConfigureAwait(false);

                bool changed = false;

                if (!string.IsNullOrEmpty(sizes.WorkingSize) && sizes.WorkingSize != "Size unknown")
                {
                    snapshot.WorkingCopySize = sizes.WorkingSize;
                    snapshot.RepoTotalSize = sizes.TotalSize;
                    lock (_sizeCacheLock)
                        _sizeCache[cacheKey] = (DateTime.UtcNow, sizes.WorkingSize, sizes.TotalSize);
                    changed = true;

                    string persistKey = svnManager?.CurrentProject?.workingDir ?? cacheKey;
                    try
                    {
                        ProjectSettings.UpdateProject(persistKey, p =>
                        {
                            p.cachedWorkingSize = sizes.WorkingSize;
                            p.cachedTotalSize = sizes.TotalSize;
                        });
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(remoteRevRaw) && !remoteRevRaw.Contains("Error"))
                {
                    snapshot.RemoteRevision = remoteRevRaw.Trim();
                    if (int.TryParse(snapshot.Revision, out int localRev) &&
                        int.TryParse(snapshot.RemoteRevision, out int remoteRev))
                        snapshot.IsOutdated = remoteRev > localRev;
                    changed = true;
                }

                _checkoutIncomplete = incomplete;
                snapshot.CheckoutIncomplete = incomplete;
                if (incomplete) changed = true;

                if (changed && _state == BarState.Idle &&
                    ReferenceEquals(svnManager.CurrentSnapshot, snapshot))
                {
                    SetContent(BuildNormalContent(snapshot, isRefreshing: false), "FillHeavy");
                }
            }
            catch { }
        }

        // ==================== CHECKOUT ====================

        public void BeginCheckout(string projectName, string workingDir)
        {
            if (_state != BarState.Idle) return;

            if (svnManager.CurrentSnapshot == null ||
                !string.Equals(svnManager.CurrentSnapshot.ProjectName, projectName, StringComparison.Ordinal))
            {
                svnManager.CurrentSnapshot = new SVNProjectInfoSnapshot
                {
                    ProjectName = projectName,
                    WorkingCopySize = "...",
                    RepoTotalSize = "...",
                    IsValid = true
                };
            }

            _state = BarState.Updating;
            SetContent(BuildUpdatingContent(projectName,
                svnManager.CurrentSnapshot.WorkingCopySize,
                svnManager.CurrentSnapshot.RepoTotalSize), "BeginCheckout");

            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _monitorCts, null));
            var newMonitorCts = new CancellationTokenSource();
            _monitorCts = newMonitorCts;

            _ = RunSizeMonitor(workingDir, newMonitorCts.Token);
        }

        public async Task EndCheckout(SVNProjectInfoSnapshot fallbackSnapshot)
        {
            await EndUpdate(fallbackSnapshot).ConfigureAwait(false);
        }

        public void Dispose()
        {
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _monitorCts, null));
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _snapshotCts, null));
        }
    }
}