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

    public class SVNBar : SVNBase
    {
        private string _svnVersionCached = "";
        private Task<string> _svnVersionTask;
        private readonly Dictionary<string, (DateTime time, string value)> _sizeCache = new();
        private readonly object _sizeCacheLock = new object();

        private CancellationTokenSource _snapshotCts;

        private volatile BarState _state = BarState.Idle;
        private volatile string _desiredContent = "";
        private volatile string _lastRenderedContent = "";
        private readonly object _contentLock = new object();
        private CancellationTokenSource _monitorCts;

        public BarState CurrentState => _state;

        public SVNBar(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
        }

        public void Tick()
        {
            if (svnUI?.StatusInfoText == null) return;

            string content;
            lock (_contentLock)
            {
                content = _desiredContent;
            }

            if (content != _lastRenderedContent)
            {
                svnUI.StatusInfoText.text = content;
                _lastRenderedContent = content;
            }
        }

        private void SetContent(string content, string source)
        {
            lock (_contentLock)
            {
                if (_desiredContent != content)
                {
                    _desiredContent = content;
                }
            }
        }

        public void SetLoadingContent(string projectName)
        {
            string content =
                $"<size=150%><color=#FFFF00>●</color></size> " +
                $"<color=orange><b>{projectName}</b></color> | " +
                $"<color=#FFFF00>Loading project...</color>";

            SetContent(content, "SetLoadingContent");
        }

        public void ShowNoWorkingCopy(string projectName = null)
        {
            string name = string.IsNullOrWhiteSpace(projectName)
                ? ""
                : $"<color=#888888><b>{projectName}</b></color> | ";
            string content =
                $"<size=150%><color=black>●</color></size> " +
                $"{name}<color=#888888>No working copy</color>";

            SetContent(content, "ShowNoWorkingCopy");
        }

        public async Task ShowProjectInfo(
    SVNProject svnProject,
    string path,
    bool forceOutdatedCheck = false,
    bool isRefreshing = false)
        {
            if (_state != BarState.Idle) return;

            _snapshotCts?.Cancel();
            _snapshotCts?.Dispose();
            _snapshotCts = new CancellationTokenSource();
            var localToken = _snapshotCts.Token;

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
                    IsValid = true
                };
            }

            string size = svnManager.CurrentSnapshot?.WorkingCopySize ?? "...";
            SetContent(BuildUpdatingContent(projectName, size), "BeginUpdate");

            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = new CancellationTokenSource();

            _ = RunSizeMonitor(svnManager.WorkingDir, _monitorCts.Token);
        }

        public async Task EndUpdate(SVNProjectInfoSnapshot fallbackSnapshot)
        {
            _monitorCts?.Cancel();

            _state = BarState.Idle;

            bool renderedFresh = false;
            try
            {
                var freshSnapshot = await BuildSnapshotAsync(null, svnManager.WorkingDir);

                if (freshSnapshot != null && freshSnapshot.IsValid)
                {
                    if (string.IsNullOrEmpty(freshSnapshot.ProjectName) && fallbackSnapshot != null)
                        freshSnapshot.ProjectName = fallbackSnapshot.ProjectName;

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
                svnManager.CurrentSnapshot = fallbackSnapshot;
                try { fallbackSnapshot.WorkingCopySize = await GetFolderSizeAsync(svnManager.WorkingDir); } catch { }

                _lastRenderedContent = "";
                SetContent(BuildNormalContent(fallbackSnapshot, isRefreshing: false), "EndUpdate-Fallback");
            }
        }

        public void EndUpdateFailed(SVNProjectInfoSnapshot oldSnapshot)
        {
            _monitorCts?.Cancel();
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

                while (!token.IsCancellationRequested && _state == BarState.Updating)
                {
                    try
                    {
                        string newSize = await GetFolderSizeAsync(path, token);

                        if (token.IsCancellationRequested || _state != BarState.Updating) break;

                        var snapshot = svnManager.CurrentSnapshot;
                        if (snapshot != null && snapshot.IsValid)
                        {
                            bool changed = snapshot.WorkingCopySize != newSize;
                            snapshot.WorkingCopySize = newSize;

                            if (changed)
                            {
                                SetContent(BuildUpdatingContent(snapshot.ProjectName, newSize), "Monitor");
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

        private string BuildUpdatingContent(string projectName, string size)
        {
            var snapshot = svnManager.CurrentSnapshot;
            string user = snapshot?.CurrentUser ?? "...";
            string branch = snapshot?.Branch ?? "...";
            string rev = snapshot?.Revision ?? "...";
            string svnVer = snapshot?.SvnVersion ?? "...";

            return
                $"<size=150%><color=#FFFF00>●</color></size> " +
                $"<color=orange><b>{projectName}</b> ({size})</color> | " +
                $"<color=#00E5FF>User:</color> <color=#E6E6E6>{user}</color> | " +
                $"<color=#00E5FF>Branch:</color> <color=#E6E6E6>{branch}</color> | " +
                $"<color=#00E5FF>Rev:</color> <color=#E6E6E6>{rev}</color> | " +
                $"<color=#E6E6E6>SVN: {svnVer}</color>" +
                " | <color=#FFFF00>Updating...</color>";
        }

        private string BuildNormalContent(SVNProjectInfoSnapshot snapshot, bool isRefreshing)
        {
            var state = svnManager.OperationInfo;
            bool isCanceled = state.State == SVNOperationState.Canceled;
            bool isFailed = state.State == SVNOperationState.Failed;

            string statusColor = "#4ca74c";
            if (isRefreshing) statusColor = "#FFFF00";
            else if (isCanceled) statusColor = "#FFAA00";
            else if (isFailed) statusColor = "#FF1A1A";
            else if (snapshot.IsOutdated) statusColor = "#FF1A1A";

            string shortDate = snapshot.Date != "unknown"
                ? snapshot.Date.Split('(')[0].Trim()
                : "no dates";

            string revDisplay = snapshot.IsOutdated
                ? $"<color=#FF4444>{snapshot.Revision}</color> <color=#FF8888>(HEAD: {snapshot.RemoteRevision})</color>"
                : snapshot.Revision;

            string statusSuffix = "";
            if (isCanceled) statusSuffix = " | Update Canceled";
            else if (isFailed) statusSuffix = " | Update Interrupted";

            return
                $"<size=150%><color={statusColor}>●</color></size> " +
                $"<color=orange><b>{snapshot.ProjectName}</b> ({snapshot.WorkingCopySize})</color> | " +
                $"<color=#00E5FF>User:</color> <color=#E6E6E6>{snapshot.CurrentUser}</color> | " +
                $"<color=#00E5FF>Branch:</color> <color=#E6E6E6>{snapshot.Branch}</color> | " +
                $"<color=#00E5FF>Rev:</color> <color=#E6E6E6>{revDisplay}</color> | " +
                $"<color=#00E5FF>By:</color> <color=#E6E6E6>{snapshot.Author}</color> | " +
                $"<color=#E6E6E6>{shortDate}</color> | " +
                $"<color=#E6E6E6>Srv: {snapshot.Server}</color> | " +
                $"<color=#E6E6E6>App: {snapshot.AppVersion}</color> | " +
                $"<color=#E6E6E6>SVN: {snapshot.SvnVersion}</color>" +
                statusSuffix;
        }

        public async Task StartLightSizeMonitor(string path, CancellationToken token)
        {
            await Task.CompletedTask;
        }

        public async Task StartLiveSizeMonitor(string path, CancellationToken token)
        {
            await Task.CompletedTask;
        }

        public void ShowUpdatingStatus(string projectName)
        {
        }

        public void ResetMonitorState()
        {
        }

        public void ForceRenderAfterUpdate(SVNProjectInfoSnapshot snapshot)
        {
        }

        public void ClearSizeCache()
        {
            lock (_sizeCacheLock) _sizeCache.Clear();
        }

        public async Task<string> GetFolderSizeAsync(string path, CancellationToken token = default, bool includeSvnTemp = false)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(path)) return "0 MB";

                    long bytes = 0;
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
                            if (fi.FullName.Contains(".svn"))
                                continue;
                            bytes += fi.Length;
                        }
                        catch { }

                        if ((++fileCount & 0x1F) == 0)
                            token.ThrowIfCancellationRequested();
                    }

                    double gigabytes = (double)bytes / (1024 * 1024 * 1024);
                    return gigabytes >= 1.0 ? $"{gigabytes:F2} GB" : $"{(double)bytes / (1024 * 1024):F2} MB";
                }
                catch (OperationCanceledException) { throw; }
                catch { return "Size unknown"; }
            }, token);
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

        public async Task<SVNProjectInfoSnapshot> BuildSnapshotAsync(SVNProject svnProject, string path, CancellationToken token = default)
        {
            var snapshot = new SVNProjectInfoSnapshot();
            try
            {
                if (string.IsNullOrEmpty(path)) return snapshot;
                if (!Directory.Exists(Path.Combine(path, ".svn"))) return snapshot;

                string projectName = svnProject != null && !string.IsNullOrEmpty(svnProject.projectName)
                    ? svnProject.projectName : Path.GetFileName(path);

                var infoTask = SvnRunner.GetInfoAsync(path, token);
                var remoteRevTask = SvnRunner.RunAsync("info -r HEAD --show-item last-changed-revision", path, token: token);

                string cacheKey = path.Replace("\\", "/").TrimEnd('/');
                string cachedSize = null;

                lock (_sizeCacheLock)
                {
                    var now = DateTime.UtcNow;
                    var expiredKeys = _sizeCache.Where(kvp => (now - kvp.Value.time).TotalSeconds > 10)
                        .Select(kvp => kvp.Key).ToList();
                    foreach (var key in expiredKeys) _sizeCache.Remove(key);
                    if (_sizeCache.TryGetValue(cacheKey, out var entry) && (now - entry.time).TotalSeconds < 10)
                        cachedSize = entry.value;
                }

                Task<string> sizeTask = cachedSize != null ? Task.FromResult(cachedSize) : GetFolderSizeAsync(path, token);

                string rawInfo = await infoTask;
                string sizeStr = await sizeTask;

                string remoteRevRaw = null;
                try { remoteRevRaw = await remoteRevTask; } catch { }

                if (!string.IsNullOrEmpty(sizeStr) && cachedSize == null)
                    lock (_sizeCacheLock) { _sizeCache[cacheKey] = (DateTime.UtcNow, sizeStr); }

                if (string.IsNullOrWhiteSpace(rawInfo) || rawInfo == "unknown") return snapshot;

                snapshot.ProjectName = projectName;
                snapshot.WorkingCopySize = sizeStr;
                snapshot.Revision = ExtractValue(rawInfo, "Revision:");
                snapshot.Author = ExtractValue(rawInfo, "Last Changed Author:");
                snapshot.Date = ExtractValue(rawInfo, "Last Changed Date:");

                if (!string.IsNullOrWhiteSpace(remoteRevRaw) && !remoteRevRaw.Contains("Error"))
                    snapshot.RemoteRevision = remoteRevRaw.Trim();

                snapshot.RelativeUrl = ExtractValue(rawInfo, "Relative URL:");
                snapshot.Url = ExtractValue(rawInfo, "URL:");
                snapshot.RepoRoot = ExtractValue(rawInfo, "Repository Root:");

                snapshot.IsOutdated = false;
                if (int.TryParse(snapshot.Revision, out int localRev) && int.TryParse(snapshot.RemoteRevision, out int remoteRev))
                {
                    snapshot.IsOutdated = remoteRev > localRev;
                }

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
                await EnsureVersionCached();
                snapshot.SvnVersion = _svnVersionCached;
                snapshot.CurrentUser = svnManager.CurrentUserName ?? "Unknown";
                snapshot.IsValid = true;
                return snapshot;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogError($"[SVNBar] BuildSnapshotAsync failed: {ex.Message}");
                return snapshot;
            }
        }

        public void Dispose()
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _snapshotCts?.Cancel();
            _snapshotCts?.Dispose();
        }
    }
}