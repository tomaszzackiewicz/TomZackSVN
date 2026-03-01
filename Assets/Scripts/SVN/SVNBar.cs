using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBar : SVNBase
    {

        private string _lastKnownProjectName = "";
        private string _svnVersionCached = "";

        public SVNBar(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async Task ShowProjectInfo(SVNProject svnProject, string path, bool forceOutdatedCheck = false, bool isRefreshing = false)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (svnUI == null) return;

            if (svnProject != null && !string.IsNullOrEmpty(svnProject.projectName))
                _lastKnownProjectName = svnProject.projectName;

            string displayName = !string.IsNullOrEmpty(_lastKnownProjectName)
                ? _lastKnownProjectName
                : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            string initialColor = isRefreshing ? "#FFFF00" : "#FFFF00"; // Yellow dot
            SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, $"<size=150%><color={initialColor}>●</color></size> <color=#555555>Initializing {displayName}...</color>", "INFO", append: false);

            string sizeText = "---";
            string rawInfo = "";
            int retryCount = 0;
            int maxRetries = 8;

            while (retryCount < maxRetries)
            {
                try
                {
                    if (!Directory.Exists(Path.Combine(path, ".svn")))
                    {
                        retryCount++;
                        SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, $"<size=150%><color=#FFFF00>●</color></size> <color=#555555>Waiting for .svn metadata... ({retryCount}/{maxRetries})</color>"); //Yellow dot
                        await Task.Delay(1000);
                        continue;
                    }

                    var infoTask = SvnRunner.GetInfoAsync(path);
                    var sizeTask = GetFolderSizeAsync(path);
                    await Task.WhenAll(infoTask, sizeTask);

                    rawInfo = infoTask.Result;
                    sizeText = sizeTask.Result;

                    if (!string.IsNullOrEmpty(rawInfo) && rawInfo != "unknown") break;
                }
                catch (Exception)
                {
                    retryCount++;
                    await Task.Delay(1000);
                }
            }

            if (string.IsNullOrEmpty(rawInfo) || rawInfo == "unknown")
            {
                SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, $"<size=150%><color=black>●</color></size> <b>{displayName}</b> | <color=#FF8888>Not a working copy yet</color>", "INFO", append: false); //Black dot
                return;
            }

            string revision = ExtractValue(rawInfo, "Revision:");
            string author = ExtractValue(rawInfo, "Last Changed Author:");
            string fullDate = ExtractValue(rawInfo, "Last Changed Date:");
            string relUrl = ExtractValue(rawInfo, "Relative URL:");
            string absUrl = ExtractValue(rawInfo, "URL:");
            string repoRootUrl = ExtractValue(rawInfo, "Repository Root:");

            bool isOutdated = false;
            string remoteRevision = revision;

            try
            {
                string remoteRevRaw = await SvnRunner.RunAsync("info -r HEAD --show-item last-changed-revision", path);
                if (!string.IsNullOrEmpty(remoteRevRaw) && !remoteRevRaw.Contains("Error"))
                {
                    remoteRevision = remoteRevRaw.Trim();
                    if (int.TryParse(revision, out int localRev) && int.TryParse(remoteRevision, out int remRev))
                    {
                        isOutdated = remRev > localRev;
                    }
                }
            }
            catch { }

            string statusColor = "#4ca74c"; // Green dot
            if (isRefreshing) statusColor = "#FFFF00"; // Yellow dot
            else if (isOutdated) statusColor = "#FF1A1A"; // red dot

            if (string.IsNullOrEmpty(_lastKnownProjectName) || _lastKnownProjectName == displayName)
            {
                if (repoRootUrl != "unknown")
                {
                    _lastKnownProjectName = repoRootUrl.Split('/').Last(s => !string.IsNullOrEmpty(s));
                    displayName = _lastKnownProjectName;
                }
            }

            string branchName = "trunk";
            string source = (relUrl != "unknown") ? relUrl : absUrl;
            if (source != "unknown")
            {
                branchName = source.Replace("^/", "").Trim();
                if (branchName.Contains("/"))
                    branchName = Path.GetFileName(branchName.TrimEnd('/'));
                if (string.IsNullOrEmpty(branchName) || branchName == "/") branchName = "trunk";
            }

            string serverHost = "local";
            if (absUrl != "unknown")
            {
                try { serverHost = new Uri(absUrl).Host; } catch { }
            }

            string shortDate = (fullDate != "unknown") ? fullDate.Split('(')[0].Trim() : "no commits";
            string appVersion = Application.version;
            if (string.IsNullOrEmpty(_svnVersionCached)) await EnsureVersionCached();

            string currentUser = svnManager.CurrentUserName ?? "Unknown";

            string revDisplay = isOutdated
                ? $"<color=#FF5555>{revision}</color> <color=#FF8888>(HEAD: {remoteRevision})</color>"
                : revision;

            string statusLine = $"<size=150%><color={statusColor}>●</color></size> <color=orange> <b>{displayName}</b> ({sizeText})</color> | " +
                                $"<color=#00E5FF>User:</color> <color=#E6E6E6>{currentUser}</color> | " +
                                $"<color=#00E5FF>Branch:</color> <color=#E6E6E6>{branchName}</color> | " +
                                $"<color=#00E5FF>Rev:</color> <color=#E6E6E6>{revDisplay}</color> | " +
                                $"<color=#00E5FF>By:</color> <color=#E6E6E6>{author}</color> | " +
                                $"<color=#E6E6E6> {shortDate}</color> | " +
                                $"<color=#E6E6E6>Srv: {serverHost}</color> | " +
                                $"<color=#E6E6E6>App: {appVersion}</color> | " +
                                $"<color=#E6E6E6>SVN: {_svnVersionCached}</color>";

            SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, statusLine, "INFO", append: false);
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

                    var files = dir.EnumerateFiles("*", SearchOption.AllDirectories);

                    foreach (var fi in files)
                    {
                        bytes += fi.Length;
                    }

                    double gigabytes = (double)bytes / (1024 * 1024 * 1024);
                    return gigabytes > 1 ? $"{gigabytes:F2} GB" : $"{(double)bytes / (1024 * 1024):F2} MB";
                }
                catch { return "Size unknown"; }
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
    }
}