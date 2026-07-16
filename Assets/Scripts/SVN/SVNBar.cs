using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBar : SVNBase
    {
        private string _svnVersionCached = "";
        private DateTime _lastSizeCalcTime = DateTime.MinValue;
        private string _lastSizeCalcValue = "";

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
            if (svnManager.IsUpdateRunning)
            {
                string projectName = svnProject?.projectName ?? "Project";
                SVNLogBridge.UpdateUIField(
                    svnUI.StatusInfoText,
                    $"<size=150%><color=#FFFF00>●</color></size> " +
                    $"<color=orange><b>{projectName}</b></color> | " +
                    $"<color=#FFFF00>Updating working copy…</color>",
                    "INFO",
                    append: false);
                return;
            }

            var snapshot = await BuildSnapshotAsync(svnProject, path);
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

        public async Task<string> GetFolderSizeAsync(string path)
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
                    foreach (var fi in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        try { bytes += fi.Length; } catch { }
                    }

                    double gigabytes = (double)bytes / (1024 * 1024 * 1024);
                    if (gigabytes >= 1.0)
                        return $"{gigabytes:F2} GB";
                    else
                        return $"{(double)bytes / (1024 * 1024):F2} MB";
                }
                catch
                {
                    return "Size unknown";
                }
            });
        }

        private string ExtractValue(string info, string key)
        {
            if (string.IsNullOrEmpty(info)) return "unknown";
            var match = Regex.Match(info, $@"^{key}\s*(.*)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "unknown";
        }

        private async Task EnsureVersionCached()
        {
            if (string.IsNullOrEmpty(_svnVersionCached))
            {
                try
                {
                    _svnVersionCached = await SvnRunner.RunAsync("--version --quiet", svnManager.WorkingDir);
                    _svnVersionCached = _svnVersionCached.Trim();
                }
                catch { _svnVersionCached = "?.?.?"; }
            }
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

                bool cacheIsValid = (DateTime.UtcNow - _lastSizeCalcTime).TotalSeconds < 10;
                Task<string> sizeTask;

                if (cacheIsValid && !string.IsNullOrEmpty(_lastSizeCalcValue))
                {
                    sizeTask = Task.FromResult(_lastSizeCalcValue);
                }
                else
                {
                    sizeTask = GetFolderSizeAsync(path);
                }

                await Task.WhenAll(infoTask, remoteRevTask, sizeTask);

                string rawInfo = infoTask.Result;

                if (string.IsNullOrWhiteSpace(rawInfo) || rawInfo == "unknown")
                    return snapshot;

                snapshot.ProjectName = projectName;

                _lastSizeCalcValue = sizeTask.Result;
                _lastSizeCalcTime = DateTime.UtcNow;

                snapshot.WorkingCopySize = sizeTask.Result;

                snapshot.Revision = ExtractValue(rawInfo, "Revision:");
                snapshot.Author = ExtractValue(rawInfo, "Last Changed Author:");
                snapshot.Date = ExtractValue(rawInfo, "Last Changed Date:");

                if (int.TryParse(snapshot.Revision, out _))
                {
                    var logTask = GetRealCommitInfoAsync(path, snapshot.Revision);
                    string remoteRevRaw = remoteRevTask.Result;

                    if (!string.IsNullOrWhiteSpace(remoteRevRaw) && !remoteRevRaw.Contains("Error"))
                        snapshot.RemoteRevision = remoteRevRaw.Trim();

                    var (realAuthor, realDate) = await logTask;
                    if (!string.IsNullOrEmpty(realAuthor))
                        snapshot.Author = realAuthor;
                    if (!string.IsNullOrEmpty(realDate))
                        snapshot.Date = realDate;
                }
                else
                {
                    string remoteRevRaw = remoteRevTask.Result;
                    if (!string.IsNullOrWhiteSpace(remoteRevRaw) && !remoteRevRaw.Contains("Error"))
                        snapshot.RemoteRevision = remoteRevRaw.Trim();
                }

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

                if (string.IsNullOrEmpty(_svnVersionCached))
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

            string revDisplay = snapshot.IsOutdated
                ? $"<color=#FF5555>{snapshot.Revision}</color> <color=#FF8888>(HEAD: {snapshot.RemoteRevision})</color>"
                : snapshot.Revision;

            string statusSuffix = "";
            if (isBusy) statusSuffix = " | Updating...";
            else if (isCanceled) statusSuffix = " | Update Canceled";
            else if (isFailed) statusSuffix = $" | Update Interrupted";

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
        }

        public async Task<SVNProjectInfoSnapshot> BuildSnapshot(SVNProject project, string workingDir)
        {
            string info = await SvnRunner.GetInfoAsync(workingDir);
            string revision = SVNAssetLocator.ParseRevision(info);
            string author = ExtractAuthor(info);
            string date = ExtractDate(info);

            if (int.TryParse(revision, out _))
            {
                var realInfo = await GetRealCommitInfoAsync(workingDir, revision);
                if (!string.IsNullOrEmpty(realInfo.author)) author = realInfo.author;
                if (!string.IsNullOrEmpty(realInfo.date)) date = realInfo.date;
            }

            return new SVNProjectInfoSnapshot
            {
                Revision = revision,
                RemoteRevision = "Unknown",
                Author = author,
                Date = date,
                Branch = ExtractBranch(info),
                Server = project.repoUrl
            };
        }

        private string ExtractAuthor(string info)
        {
            var m = Regex.Match(info, @"^Last Changed Author:\s*(.+)$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : "Unknown";
        }

        private string ExtractDate(string info)
        {
            var m = Regex.Match(info, @"^Last Changed Date:\s*(.+)$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : "Unknown";
        }

        private string ExtractBranch(string info)
        {
            var m = Regex.Match(info, @"URL:\s*(.+)$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : "Unknown";
        }

        public void RenderFromSnapshot(SVNProjectInfoSnapshot snapshot)
        {
            if (snapshot == null) return;

            var state = svnManager.OperationInfo;
            bool isBusy = state.State == SVNOperationState.Updating;
            bool isCanceled = state.State == SVNOperationState.Canceled;
            bool isFailed = state.State == SVNOperationState.Failed;

            string statusColor = "#4ca74c";
            if (isBusy) statusColor = "#FFFF00";
            else if (isCanceled) statusColor = "#FFAA00";
            else if (isFailed) statusColor = "#FF1A1A";
            else if (snapshot.IsOutdated) statusColor = "#FF1A1A";

            string shortDate = snapshot.Date != "unknown" ? snapshot.Date.Split('(')[0].Trim() : "no commits";

            string revDisplay = snapshot.IsOutdated
                ? $"<color=#FF5555>{snapshot.Revision}</color> <color=#FF8888>(HEAD: {snapshot.RemoteRevision})</color>"
                : snapshot.Revision;

            string statusSuffix = "";
            if (isBusy) statusSuffix = " | Updating...";
            else if (isCanceled) statusSuffix = $" | Update Canceled";
            else if (isFailed) statusSuffix = $" | Update Failed";

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

            SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, line, "INFO", false);
        }

        public void Dispose()
        {
            if (svnManager != null)
            {
                svnManager.OnSnapshotChanged -= RenderFromSnapshot;
            }
        }

        private async Task<(string author, string date)> GetRealCommitInfoAsync(string path, string revision, CancellationToken token = default)
        {
            try
            {
                string logOutput = await SvnRunner.RunAsync($"log -r {revision} --limit 1", path, token: token);
                if (!string.IsNullOrWhiteSpace(logOutput))
                {
                    var match = Regex.Match(logOutput, @"^r\d+\s*\|\s*([^|]+)\s*\|\s*([^|]+)", RegexOptions.Multiline);
                    if (match.Success)
                    {
                        return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Failed to fetch real author/date from log: {ex.Message}");
            }

            return (null, null);
        }

        public void ShowUpdatingStatus(string projectName)
        {
            SVNLogBridge.UpdateUIField(
                svnUI.StatusInfoText,
                $"<size=150%><color=#FFFF00>●</color></size> " +
                $"<color=orange><b>{projectName}</b></color> | " +
                $"<color=#FFFF00>Updating working copy...</color>",
                "INFO",
                append: false);
        }
    }
}