using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using System.Threading;

namespace SVN.Core
{
    public class SVNBar : SVNBase
    {
        private string _svnVersionCached = "";

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
            var snapshot = await BuildSnapshotAsync(svnProject, path);
            svnManager.CurrentSnapshot = snapshot;
            RenderSnapshot(snapshot, isRefreshing);
        }

        public async Task<string> GetFolderSizeAsync(string path)
        {
            return await Task.Run(() =>
            {
                try
                {
                    DirectoryInfo dir = new DirectoryInfo(path);
                    if (!dir.Exists) return "0 GB";

                    long bytes = 0;

                    foreach (var fi in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        bytes += fi.Length;
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

                string rawInfo = await SvnRunner.GetInfoAsync(path);

                if (string.IsNullOrWhiteSpace(rawInfo) || rawInfo == "unknown")
                    return snapshot;

                snapshot.ProjectName = projectName;

                if (!string.IsNullOrEmpty(svnManager.CurrentSnapshot?.WorkingCopySize) &&
                    svnManager.CurrentSnapshot.WorkingCopySize != "?" &&
                    svnManager.CurrentSnapshot.WorkingCopySize != "calculating…")
                {
                    snapshot.WorkingCopySize = svnManager.CurrentSnapshot.WorkingCopySize;
                }
                else
                {
                    snapshot.WorkingCopySize = await GetFolderSizeAsync(path);
                }

                snapshot.Revision = ExtractValue(rawInfo, "Revision:");
                snapshot.Author = ExtractValue(rawInfo, "Last Changed Author:");
                snapshot.Date = ExtractValue(rawInfo, "Last Changed Date:");

                if (int.TryParse(snapshot.Revision, out _))
                {
                    var realInfo = await GetRealCommitInfoAsync(path, snapshot.Revision);
                    if (!string.IsNullOrEmpty(realInfo.author))
                        snapshot.Author = realInfo.author;
                    if (!string.IsNullOrEmpty(realInfo.date))
                        snapshot.Date = realInfo.date;
                }

                snapshot.RelativeUrl = ExtractValue(rawInfo, "Relative URL:");
                snapshot.Url = ExtractValue(rawInfo, "URL:");
                snapshot.RepoRoot = ExtractValue(rawInfo, "Repository Root:");
                snapshot.RemoteRevision = snapshot.Revision;

                try
                {
                    string remoteRevRaw = await SvnRunner.RunAsync("info -r HEAD --show-item last-changed-revision", path);
                    if (!string.IsNullOrWhiteSpace(remoteRevRaw) && !remoteRevRaw.Contains("Error"))
                    {
                        snapshot.RemoteRevision = remoteRevRaw.Trim();
                        if (int.TryParse(snapshot.Revision, out int localRev) &&
                            int.TryParse(snapshot.RemoteRevision, out int remoteRev))
                        {
                            snapshot.IsOutdated = remoteRev > localRev;
                        }
                    }
                }
                catch { }

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

        public void RenderSnapshot(
    SVNProjectInfoSnapshot snapshot,
    bool isRefreshing = false)
        {
            if (snapshot == null || !snapshot.IsValid)
            {
                SVNLogBridge.UpdateUIField(
                    svnUI.StatusInfoText,
                    "<size=150%><color=black>●</color></size> Invalid working copy",
                    "INFO",
                    append: false);

                return;
            }

            var state = svnManager.OperationInfo;

            bool isBusy =
                state.State == SVNOperationState.Updating;

            bool isCanceled =
                state.State == SVNOperationState.Canceled;

            bool isFailed =
                state.State == SVNOperationState.Failed;

            string statusColor = "#4ca74c";

            if (isRefreshing || isBusy)
                statusColor = "#FFFF00";
            else if (isCanceled)
                statusColor = "#FFAA00";
            else if (isFailed)
                statusColor = "#FF1A1A";
            else if (snapshot.IsOutdated)
                statusColor = "#FF1A1A";

            string shortDate =
                snapshot.Date != "unknown"
                    ? snapshot.Date.Split('(')[0].Trim()
                    : "no commits";

            string revDisplay =
                snapshot.IsOutdated
                    ? $"<color=#FF5555>{snapshot.Revision}</color> <color=#FF8888>(HEAD: {snapshot.RemoteRevision})</color>"
                    : snapshot.Revision;

            string statusSuffix = "";

            if (isBusy)
                statusSuffix = " | Updating...";
            else if (isCanceled)
                statusSuffix = " | Update Canceled";
            else if (isFailed)
                statusSuffix = $" | Update Interrupted";

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

            SVNLogBridge.UpdateUIField(
                svnUI.StatusInfoText,
                line,
                "INFO",
                append: false);
        }

        public async Task<SVNProjectInfoSnapshot> BuildSnapshot(
    SVNProject project,
    string workingDir)
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
            if (snapshot == null)
                return;

            var state = svnManager.OperationInfo;

            bool isBusy =
                state.State == SVNOperationState.Updating;

            bool isCanceled =
                state.State == SVNOperationState.Canceled;

            bool isFailed =
                state.State == SVNOperationState.Failed;

            string statusColor = "#4ca74c";

            if (isBusy)
                statusColor = "#FFFF00";
            else if (isCanceled)
                statusColor = "#FFAA00";
            else if (isFailed)
                statusColor = "#FF1A1A";
            else if (snapshot.IsOutdated)
                statusColor = "#FF1A1A";

            string shortDate =
                snapshot.Date != "unknown"
                    ? snapshot.Date.Split('(')[0].Trim()
                    : "no commits";

            string revDisplay =
                snapshot.IsOutdated
                    ? $"<color=#FF5555>{snapshot.Revision}</color> <color=#FF8888>(HEAD: {snapshot.RemoteRevision})</color>"
                    : snapshot.Revision;

            string statusSuffix = "";

            if (isBusy)
                statusSuffix = " | Updating...";
            else if (isCanceled)
                statusSuffix = $" | Update Canceled";
            else if (isFailed)
                statusSuffix = $" | Update Failed";

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

            SVNLogBridge.UpdateUIField(
                svnUI.StatusInfoText,
                line,
                "INFO",
                false
            );
        }

        public void Dispose()
        {
            if (svnManager != null)
            {
                svnManager.OnSnapshotChanged -= RenderFromSnapshot;
            }
        }

        public async Task StartLiveSizeMonitor(string path, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                long bytes = await CalculateWorkingCopySizeBytes(path, token);

                double gb = bytes / (1024d * 1024d * 1024d);

                string formatted =
                    gb > 1
                        ? $"{gb:F2} GB"
                        : $"{bytes / (1024d * 1024d):F2} MB";

                if (svnManager.CurrentSnapshot != null)
                {
                    svnManager.CurrentSnapshot.WorkingCopySize = formatted;

                    svnManager.RaiseSnapshotChanged(svnManager.CurrentSnapshot);
                }

                await Task.Delay(1000, token);
            }
        }

        public async Task<long> CalculateWorkingCopySizeBytes(string path, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(path))
                        return 0;

                    var dir = new DirectoryInfo(path);
                    if (!dir.Exists)
                        return 0;

                    long bytes = 0;

                    foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        if (token.IsCancellationRequested)
                            break;

                        try
                        {
                            bytes += file.Length;
                        }
                        catch { }
                    }

                    return bytes;
                }
                catch
                {
                    return 0;
                }
            }, token);
        }

        private async Task<(string author, string date)> GetRealCommitInfoAsync(string path, string revision)
        {
            try
            {
                string logOutput = await SvnRunner.RunAsync($"log -r {revision} --limit 1", path);
                if (!string.IsNullOrWhiteSpace(logOutput))
                {
                    var match = Regex.Match(logOutput, @"^r\d+\s*\|\s*([^|]+)\s*\|\s*([^|]+)", RegexOptions.Multiline);
                    if (match.Success)
                    {
                        return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
                    }
                }
            }
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