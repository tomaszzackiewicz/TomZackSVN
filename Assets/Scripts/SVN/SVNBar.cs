using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBar : SVNBase
    {
        private string _svnVersionCached = "";

        private Task<string> _svnVersionTask;

        private readonly Dictionary<string, (DateTime time, string value)> _sizeCache = new();
        private readonly object _sizeCacheLock = new();

        private int _snapshotGeneration = 0;

        public SVNBar(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            manager.OnSnapshotChanged += RenderFromSnapshot;
        }

        public async Task ShowProjectInfo(
            SVNProject svnProject,
            string path,
            bool forceOutdatedCheck = false,
            bool isRefreshing = false)
        {
            int currentGeneration = Interlocked.Increment(ref _snapshotGeneration);

            var snapshot = await BuildSnapshotAsync(svnProject, path);

            if (currentGeneration != _snapshotGeneration)
                return;

            svnManager.CurrentSnapshot = snapshot;
            RenderSnapshot(snapshot, isRefreshing);
        }

        public async Task StartLightSizeMonitor(string path, CancellationToken token)
        {
            await Task.CompletedTask;
        }

        public async Task StartLiveSizeMonitor(string path, CancellationToken token)
        {
            await Task.CompletedTask;
        }

        public async Task<string> GetFolderSizeAsync(string path, CancellationToken token = default)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string targetPath = Path.Combine(path, "Assets");
                    if (!Directory.Exists(targetPath)) targetPath = path;

                    DirectoryInfo dir = new DirectoryInfo(targetPath);
                    if (!dir.Exists) return "0 MB";

                    long bytes = 0;
                    int fileCount = 0;
                    foreach (var fi in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        try { bytes += fi.Length; } catch { }

                        if ((++fileCount & 0x3FF) == 0) // every 1024 files
                            token.ThrowIfCancellationRequested();
                    }

                    double gigabytes = (double)bytes / (1024 * 1024 * 1024);
                    if (gigabytes >= 1.0)
                        return $"{gigabytes:F2} GB";
                    else
                        return $"{(double)bytes / (1024 * 1024):F2} MB";
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    return "Size unknown";
                }
            }, token);
        }

        private string ExtractValue(string info, string key)
        {
            if (string.IsNullOrEmpty(info)) return "unknown";
            var match = Regex.Match(info, $@"^{key}\s*(.*)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "unknown";
        }

        private async Task EnsureVersionCached()
        {
            if (_svnVersionTask == null)
            {
                _svnVersionTask = SvnRunner.RunAsync("--version --quiet", svnManager.WorkingDir)
                    .ContinueWith(t =>
                        t.IsFaulted ? "?.?.?" : (t.Result ?? "?.?.?").Trim(),
                        TaskContinuationOptions.ExecuteSynchronously);
            }
            _svnVersionCached = await _svnVersionTask;
        }

        public async Task<SVNProjectInfoSnapshot> BuildSnapshotAsync(
            SVNProject svnProject,
            string path)
        {
            var snapshot = new SVNProjectInfoSnapshot();

            try
            {
                if (string.IsNullOrEmpty(path))
                    return snapshot;

                if (!Directory.Exists(Path.Combine(path, ".svn")))
                    return snapshot;

                string projectName =
                    svnProject != null && !string.IsNullOrEmpty(svnProject.projectName)
                        ? svnProject.projectName
                        : Path.GetFileName(path);

                var infoTask = SvnRunner.GetInfoAsync(path);
                var remoteRevTask = SvnRunner.RunAsync("info -r HEAD --show-item last-changed-revision", path);

                string cacheKey = path.Replace("\\", "/").TrimEnd('/');
                string cachedSize = null;

                lock (_sizeCacheLock)
                {
                    var now = DateTime.UtcNow;
                    var expiredKeys = _sizeCache.Where(kvp => (now - kvp.Value.time).TotalSeconds > 10).Select(kvp => kvp.Key).ToList();
                    foreach (var key in expiredKeys)
                        _sizeCache.Remove(key);

                    if (_sizeCache.TryGetValue(cacheKey, out var entry) && (now - entry.time).TotalSeconds < 10)
                        cachedSize = entry.value;
                }

                Task<string> sizeTask = cachedSize != null
                    ? Task.FromResult(cachedSize)
                    : GetFolderSizeAsync(path);

                string rawInfo = await infoTask;
                string sizeStr = await sizeTask;

                string remoteRevRaw = null;
                try { remoteRevRaw = await remoteRevTask; }
                catch (Exception ex)
                {
                    SVNLogBridge.LogError($"Remote revision check failed: {ex.Message}");
                }

                if (!string.IsNullOrEmpty(sizeStr) && cachedSize == null)
                {
                    lock (_sizeCacheLock)
                    {
                        _sizeCache[cacheKey] = (DateTime.UtcNow, sizeStr);
                    }
                }

                if (string.IsNullOrWhiteSpace(rawInfo) || rawInfo == "unknown")
                    return snapshot;

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
                if (int.TryParse(snapshot.Revision, out int localRev) &&
                    int.TryParse(snapshot.RemoteRevision, out int remoteRev))
                {
                    snapshot.IsOutdated = remoteRev > localRev;
                }

                string source = snapshot.RelativeUrl != "unknown"
                    ? snapshot.RelativeUrl
                    : snapshot.Url;

                snapshot.Branch = "trunk";
                if (!string.IsNullOrEmpty(source) && source != "unknown")
                {
                    string branch = source.Replace("^/", "").Trim();
                    if (branch.Contains("/"))
                        branch = Path.GetFileName(branch.TrimEnd('/'));
                    if (!string.IsNullOrEmpty(branch))
                        snapshot.Branch = branch;
                }

                snapshot.Server = "local";
                if (!string.IsNullOrEmpty(snapshot.Url) && snapshot.Url != "unknown")
                {
                    try { snapshot.Server = new Uri(snapshot.Url).Host; }
                    catch { }
                }

                snapshot.AppVersion = Application.version;
                await EnsureVersionCached();
                snapshot.SvnVersion = _svnVersionCached;
                snapshot.CurrentUser = svnManager.CurrentUserName ?? "Unknown";
                snapshot.IsValid = true;

                return snapshot;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"BuildSnapshotAsync failed: {ex.Message}");
                return snapshot;
            }
        }

        public void RenderSnapshot(SVNProjectInfoSnapshot snapshot, bool isRefreshing = false)
        {
            PostToMainThread(() =>
            {
                if (snapshot == null || !snapshot.IsValid)
                {
                    SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, "<size=150%><color=black>●</color></size> Invalid working copy", "INFO", append: false);
                    return;
                }

                var state = svnManager.OperationInfo;
                bool isBusy = state.State == SVNOperationState.Updating;
                bool isCanceled = state.State == SVNOperationState.Canceled;
                bool isFailed = state.State == SVNOperationState.Failed;

                string statusColor = "#4ca74c";
                if (isRefreshing || isBusy) statusColor = "#FFFF00";
                else if (isCanceled) statusColor = "#FFAA00";
                else if (isFailed) statusColor = "#FF1A1A";
                else if (snapshot.IsOutdated) statusColor = "#FF1A1A";

                string shortDate = snapshot.Date != "unknown" ? snapshot.Date.Split('(')[0].Trim() : "no commits";

                string revDisplay = snapshot.IsOutdated ? $"<color=#FF4444>{snapshot.Revision}</color> <color=#FF8888>(HEAD: {snapshot.RemoteRevision})</color>" : snapshot.Revision;

                string statusSuffix = "";
                if (isBusy) statusSuffix = " | Updating...";
                else if (isCanceled) statusSuffix = " | Update Canceled";
                else if (isFailed) statusSuffix = " | Update Interrupted";

                string line =
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

                SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, line, "INFO", append: false);
            });
        }

        public void RenderFromSnapshot(SVNProjectInfoSnapshot snapshot)
        {
            RenderSnapshot(snapshot);
        }

        public void Dispose()
        {
            if (svnManager != null)
            {
                svnManager.OnSnapshotChanged -= RenderFromSnapshot;
            }
        }

        public void ShowUpdatingStatus(string projectName)
        {
            PostToMainThread(() =>
            {
                SVNLogBridge.UpdateUIField(
                    svnUI.StatusInfoText,
                    $"<size=150%><color=#FFFF00>●</color></size> " +
                    $"<color=orange><b>{projectName}</b></color> | " +
                    $"<color=#FFFF00>Updating working copy...</color>",
                    "INFO",
                    append: false);
            });
        }
    }
}