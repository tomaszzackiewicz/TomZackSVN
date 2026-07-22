using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNUpdate : SVNBase
    {
        private CancellationTokenSource _updateCTS;
        private Task _runningTask;
        private Guid _sessionId = Guid.Empty;

        public SVNUpdate(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void Update()
        {
            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir) || !Directory.Exists(svnManager.WorkingDir))
            {
                SVNLogBridge.LogError("[SVN] Working directory does not exist.");
                return;
            }

            if (_runningTask != null && !_runningTask.IsCompleted)
            {
                SVNLogBridge.LogLine("<color=orange>Update already running...</color>", false);
                return;
            }

            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Updating,
                Message = "Starting update...",
                Duration = 0,
                Repo = svnManager.RepositoryUrl
            };

            svnManager.WasUpdateCanceled = false;
            _sessionId = Guid.NewGuid();
            _runningTask = ExecuteUpdateAsync(_sessionId);
        }

        public async Task ExecuteUpdateAsync(Guid session)
        {
            if (session != _sessionId) return;

            var statusModule = svnManager.GetModule<SVNStatus>();
            statusModule?.CancelCurrentRefresh();

            try { _updateCTS?.Dispose(); } catch { }

            _updateCTS = new CancellationTokenSource();
            CancellationToken token = _updateCTS.Token;

            svnManager.IsUpdateRunning = true;
            svnManager.LastUpdateSucceeded = false;
            IsProcessing = true;

            string targetPath = svnManager.WorkingDir;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                SVNLogBridge.LogError("[SVN] Working directory is empty.");
                return;
            }

            string oldRevision = svnManager.CurrentSnapshot?.Revision ?? "Unknown";
            if (oldRevision == "Unknown")
            {
                try
                {
                    string infoBefore = await SvnRunner.GetInfoAsync(targetPath);
                    oldRevision = ParseRevisionFromInfo(infoBefore);
                }
                catch { oldRevision = "Unknown"; }
            }

            var oldSnapshot = svnManager.CurrentSnapshot;

            SVNBar svnBar = svnManager.GetModule<SVNBar>();
            svnBar?.ShowUpdatingStatus(svnManager.CurrentProject?.projectName ?? Path.GetFileName(targetPath));

            var stopwatch = Stopwatch.StartNew();
            _ = svnBar?.StartLightSizeMonitor(targetPath, token);

            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Updating,
                Message = "Running SVN update...",
                Repo = svnManager.RepositoryUrl
            };

            // OPT: Zliczanie na bieżąco zamiast regexu na całym outputcie
            int uCount = 0, gCount = 0, aCount = 0, dCount = 0, cCount = 0, rCount = 0;
            int processed = 0;

            try
            {
                await SvnRunner.WaitForSemaphoreFreeAsync(token);

                SVNLogBridge.LogLine("<b>[SVN]</b> Pre-update cleanup...");
                await SVNClean.CleanupAsync(targetPath, token);
                SVNLogBridge.LogLine("<b>[SVN]</b> Cleanup completed.");

                token.ThrowIfCancellationRequested();
                if (session != _sessionId) throw new OperationCanceledException();

                int totalUpdates = 0;
                try
                {
                    string statusOutput = await SvnRunner.RunAsync("status -u", targetPath, token: token);
                    totalUpdates = statusOutput.Split('\n').Count(l => l.Length > 8 && l[8] == '*');
                }
                catch { }

                SVNLogBridge.LogLine("<color=blue><b>[SVN]</b> Running update...</color>");

                string result = await SvnRunner.RunLiveAsync(
                    "update --accept postpone",
                    targetPath,
                    (line) =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;

                        string trimmed = line.Trim();
                        if (trimmed.Length > 0 && trimmed.All(c => c == '@' || c == '*' || c == ' ')) return;
                        if (trimmed.StartsWith("*****") || trimmed.StartsWith("@@@@@")) return;

                        string cleanLine = trimmed.Replace("[SVN ERROR]", "").Trim();
                        if (cleanLine.Length > 0 && cleanLine.All(c => c == '@' || c == '*') ||
                            cleanLine.StartsWith("*****") || cleanLine.StartsWith("@@@@@")) return;

                        if (token.IsCancellationRequested) return;
                        if (session != _sessionId) return;

                        processed++;
                        string progress = totalUpdates > 0 ? $" ({processed}/{totalUpdates})" : "";

                        string friendlyLine = line;

                        if (friendlyLine.Length > 1 && "UAGDCR ".Contains(friendlyLine[0]))
                        {
                            char status = friendlyLine[0];
                            string path = SvnRunner.NormalizeRepositoryPath(friendlyLine.Substring(1).TrimStart());
                            friendlyLine = $"{status} {path}";

                            // OPT: Zliczanie na bieżąco — zero alokacji, zero regexu
                            switch (status)
                            {
                                case 'U': uCount++; break;
                                case 'G': gCount++; break;
                                case 'A': aCount++; break;
                                case 'D': dCount++; break;
                                case 'C': cCount++; break;
                                case 'R': rCount++; break;
                            }
                        }

                        friendlyLine = friendlyLine
                            .Replace("Updating '.'", "Scanning repository...")
                            .Replace("U ", "= Updated: ")
                            .Replace("A ", "+ Added: ")
                            .Replace("D ", "− Deleted: ")
                            .Replace("C ", "x Conflict: ")
                            .Replace("G ", "~ Merged: ")
                            .Replace("R ", "↻ Replaced: ");

                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            if (session != _sessionId) return;

                            SVNLogBridge.LogLine(
                                $"<b>[SVN]</b> <color=blue>{friendlyLine}{progress}</color>",
                                false
                            );
                        });
                    },
                    token
                );

                token.ThrowIfCancellationRequested();
                if (session != _sessionId || result == "Canceled")
                    throw new OperationCanceledException();

                // OPT: Zamiast parsować output, bierzemy revision z svn info (jedno wywołanie, niezawodne)
                string revision = "Unknown";
                try
                {
                    string infoAfter = await SvnRunner.GetInfoAsync(targetPath);
                    token.ThrowIfCancellationRequested();
                    revision = ParseRevisionFromInfo(infoAfter);
                }
                catch { revision = "Unknown"; }

                stopwatch.Stop();

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Success,
                    Message = "Update completed successfully",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };
                svnManager.LastUpdateSucceeded = true;

                SVNStatus.ClearLockCache();
                svnManager._diskChangesDetected = true;

                StringBuilder report = new StringBuilder();
                report.AppendLine("\n<color=blue><b>=========================================</b></color>");
                report.AppendLine("<color=blue><b>          SVN UPDATE REPORT              </b></color>");
                report.AppendLine("<color=blue><b>=========================================</b></color>");

                if (oldRevision == revision || oldRevision == "Unknown")
                    report.AppendLine($"  Revision:   <b>{revision}</b> (No incoming changes)");
                else
                    report.AppendLine($"  Revision:   <b>{oldRevision}</b> ➔ <b>{revision}</b>");

                report.AppendLine($"  Duration:   <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>");
                report.AppendLine();

                if (uCount == 0 && aCount == 0 && dCount == 0 && cCount == 0 && gCount == 0 && rCount == 0)
                    report.AppendLine("  <color=green>Working copy was already fully up-to-date.</color>");
                else
                {
                    report.AppendLine("  <b>[File Modifications]</b>");
                    if (uCount > 0) report.AppendLine($"    Updated:   <b>{uCount}</b>");
                    if (aCount > 0) report.AppendLine($"    Added:     <b>{aCount}</b>");
                    if (dCount > 0) report.AppendLine($"    Deleted:   <b><color=#B22222>{dCount}</color></b>");
                    if (gCount > 0) report.AppendLine($"    Merged:    <b>{gCount}</b>");
                    if (rCount > 0) report.AppendLine($"    Replaced:  <b>{rCount}</b>");

                    if (cCount > 0)
                    {
                        report.AppendLine();
                        report.AppendLine("  <color=#FFAA00><b>CRITICAL WARNING: CONFLICTS DETECTED</b></color>");
                        report.AppendLine($"    Conflicts: <b><color=#FFAA00>{cCount}</color></b>");
                        report.AppendLine("    Please resolve conflicts in working copy before compiling.");
                        await svnManager.GetModule<SVNResolve>().RefreshConflictUI();
                    }
                }
                report.AppendLine("<color=yellow><b>=========================================</b></color>");
                SVNLogBridge.LogLine(report.ToString(), false);

                if (!svnManager.WasUpdateCanceled && statusModule != null)
                    await statusModule.RefreshModifiedInternal();

                if (!svnManager.WasUpdateCanceled && svnBar != null)
                {
                    svnManager.IsUpdateRunning = false;
                    var newSnapshot = await svnBar.BuildSnapshotAsync(svnManager.CurrentProject, svnManager.WorkingDir);

                    string newAuthor = await GetAuthorForRevision(svnManager.WorkingDir, revision);

                    newSnapshot.Revision = revision;
                    if (!string.IsNullOrEmpty(newAuthor))
                    {
                        newSnapshot.Author = newAuthor;
                        newSnapshot.CurrentUser = newAuthor;
                    }
                    svnManager.CurrentSnapshot = newSnapshot;
                    await svnBar.ShowProjectInfo(svnManager.CurrentProject, svnManager.WorkingDir,
                        forceOutdatedCheck: true, isRefreshing: false);
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                svnManager.IsUpdateRunning = false;
                svnManager.CurrentSnapshot = oldSnapshot;
                if (svnBar != null)
                    await svnBar.ShowProjectInfo(svnManager.CurrentProject, svnManager.WorkingDir,
                        forceOutdatedCheck: false, isRefreshing: false);

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Canceled,
                    Message = "Update canceled by user",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };

                StringBuilder cancelReport = new StringBuilder();
                cancelReport.AppendLine("\n<color=#FFAA00><b>=========================================</b></color>");
                cancelReport.AppendLine("<color=#FFAA00><b>          UPDATE INTERRUPTED             </b></color>");
                cancelReport.AppendLine("<color=#FFAA00><b>=========================================</b></color>");
                cancelReport.AppendLine($"  Process aborted after <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>.");
                cancelReport.AppendLine("  Working copy state might be incomplete.");
                cancelReport.AppendLine("<color=#FFAA00><b>=========================================</b></color>");
                SVNLogBridge.LogLine(cancelReport.ToString(), false);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                svnManager.IsUpdateRunning = false;
                svnManager.CurrentSnapshot = oldSnapshot;
                if (svnBar != null)
                    await svnBar.ShowProjectInfo(svnManager.CurrentProject, svnManager.WorkingDir,
                        forceOutdatedCheck: false, isRefreshing: false);

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Failed,
                    Message = ex.Message,
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };

                StringBuilder failureReport = new StringBuilder();
                failureReport.AppendLine("\n<color=#B22222><b>=========================================</b></color>");
                failureReport.AppendLine("<color=#B22222><b>            UPDATE FAILED                </b></color>");
                failureReport.AppendLine("<color=#B22222><b>=========================================</b></color>");
                failureReport.AppendLine($"  Execution crashed after <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>.");
                failureReport.AppendLine($"  Error message: <color=#E6E6E6>{ex.Message}</color>");
                failureReport.AppendLine("<color=#B22222><b>=========================================</b></color>");
                SVNLogBridge.LogLine(failureReport.ToString(), false);
            }
            finally
            {
                svnManager.IsUpdateRunning = false;
                IsProcessing = false;
                _runningTask = null;
            }
        }

        private async Task<string> GetAuthorForRevision(string targetPath, string revision)
        {
            try
            {
                string logOutput = await SvnRunner.RunAsync($"log -r {revision} -q", targetPath);
                var match = Regex.Match(logOutput, @"^r\d+\s*\|\s*([^|]+)\s*\|", RegexOptions.Multiline);
                return match.Success ? match.Groups[1].Value.Trim() : null;
            }
            catch { return null; }
        }

        public void CancelUpdate()
        {
            if (_updateCTS == null || !svnManager.IsUpdateRunning) return;

            SVNLogBridge.LogLine("<color=orange><b>[SVN]</b> Cancel requested...</color>", false);

            svnManager.WasUpdateCanceled = true;
            svnManager.IsUpdateRunning = false;
            svnManager.LastUpdateSucceeded = false;

            try { _updateCTS?.Cancel(); } catch { }

            _sessionId = Guid.NewGuid();

            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Canceled,
                Duration = 0,
                Repo = svnManager.RepositoryUrl
            };

            var snapshot = svnManager.CurrentSnapshot;
            string statusColor = "#FFAA00";

            string projectName = snapshot?.ProjectName ??
                (string.IsNullOrEmpty(svnManager.WorkingDir)
                    ? "Unknown project"
                    : Path.GetFileName(svnManager.WorkingDir.TrimEnd('/', '\\')));

            string user = snapshot?.CurrentUser ?? svnManager.CurrentUserName ?? "Unknown";
            string branch = snapshot?.Branch ?? "unknown";
            string revision = snapshot?.Revision ?? "unknown";
            string repo = Uri.TryCreate(svnManager.RepositoryUrl, UriKind.Absolute, out var uri) ? uri.Host : "Unknown repo";

            string line =
                $"<size=150%><color={statusColor}>●</color></size> " +
                $"<color=orange><b>{projectName}</b> ({snapshot?.WorkingCopySize ?? "?"})</color> | " +
                $"<color=#00E5FF>User:</color> <color=#E6E6E6>{user}</color> | " +
                $"<color=#00E5FF>Branch:</color> <color=#E6E6E6>{branch}</color> | " +
                $"<color=#00E5FF>Rev:</color> <color=#E6E6E6>{revision}</color> | " +
                $"<color=#00E5FF>Status:</color> <color=#E6E6E6>Canceled</color> | " +
                $"<color=#E6E6E6>Srv:{repo}</color> | " +
                $"<color=#E6E6E6>Update Interrupted</color>";

            SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, line, "INFO", append: false);
        }

        public string ParseRevisionFromInfo(string infoOutput)
        {
            var match = Regex.Match(infoOutput, @"^Revision:\s+(\d+)", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value : "Unknown";
        }

        public async void CheckRemoteModificationsButton() => await ShowRemoteUpdatesInline();

        public async Task ShowRemoteUpdatesInline()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            IsProcessing = true;

            try
            {
                SVNLogBridge.LogLine("<i>Checking remote changes...</i>");
                string output = await SvnRunner.RunAsync("status -u", root);

                if (string.IsNullOrEmpty(output))
                {
                    SVNLogBridge.LogLine("<color=green>No remote changes found.</color>");
                    return;
                }

                int remoteChangesCount = 0;
                using (var reader = new StringReader(output))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length > 8 && line[8] == '*')
                        {
                            remoteChangesCount++;
                            string pathPart = line.Substring(9).Trim();
                            pathPart = Regex.Replace(pathPart, @"^\d+\s+", "");
                            string cleanPath = SvnRunner.NormalizeRepositoryPath(pathPart);
                            SVNLogBridge.LogLine($"<color=orange>Update available:</color> {cleanPath}");
                        }
                    }
                }

                if (remoteChangesCount > 0)
                    SVNLogBridge.LogLine($"\n<b>Summary:</b> Found <color=#FFAA00>{remoteChangesCount}</color> items to update.");
                else
                    SVNLogBridge.LogLine("<color=green>Your working copy is up to date.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN] Remote check error: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void UpdateToRevision(string revision)
        {
            if (string.IsNullOrWhiteSpace(revision))
            {
                Update();
                return;
            }

            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir) || !Directory.Exists(svnManager.WorkingDir))
            {
                SVNLogBridge.LogError("[SVN] Working directory does not exist.");
                return;
            }

            if (_runningTask != null && !_runningTask.IsCompleted)
            {
                SVNLogBridge.LogLine("<color=orange>Update already running...</color>", false);
                return;
            }

            if (!IsValidRevision(revision))
            {
                SVNLogBridge.LogError($"[SVN] Invalid revision number: {revision}");
                return;
            }

            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Updating,
                Message = $"Starting update to revision {revision}...",
                Duration = 0,
                Repo = svnManager.RepositoryUrl
            };

            svnManager.WasUpdateCanceled = false;
            _sessionId = Guid.NewGuid();
            _runningTask = ExecuteUpdateToRevisionAsync(revision, _sessionId);
        }

        private bool IsValidRevision(string rev)
        {
            return int.TryParse(rev, out _);
        }

        private async Task ExecuteUpdateToRevisionAsync(string revision, Guid session)
        {
            if (session != _sessionId) return;

            var statusModule = svnManager.GetModule<SVNStatus>();
            statusModule?.CancelCurrentRefresh();

            try { _updateCTS?.Dispose(); } catch { }

            _updateCTS = new CancellationTokenSource();
            CancellationToken token = _updateCTS.Token;

            svnManager.IsUpdateRunning = true;
            svnManager.LastUpdateSucceeded = false;
            IsProcessing = true;

            string targetPath = svnManager.WorkingDir;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                SVNLogBridge.LogError("[SVN] Working directory is empty.");
                return;
            }

            string oldRevision = svnManager.CurrentSnapshot?.Revision ?? "Unknown";
            if (oldRevision == "Unknown")
            {
                try
                {
                    string infoBefore = await SvnRunner.GetInfoAsync(targetPath);
                    oldRevision = ParseRevisionFromInfo(infoBefore);
                }
                catch { oldRevision = "Unknown"; }
            }

            var oldSnapshot = svnManager.CurrentSnapshot;

            SVNBar svnBar = svnManager.GetModule<SVNBar>();
            svnBar?.ShowUpdatingStatus(svnManager.CurrentProject?.projectName ?? Path.GetFileName(targetPath));

            var stopwatch = Stopwatch.StartNew();
            _ = svnBar?.StartLightSizeMonitor(targetPath, token);

            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Updating,
                Message = $"Running SVN update to revision {revision}...",
                Repo = svnManager.RepositoryUrl
            };

            int uCount = 0, gCount = 0, aCount = 0, dCount = 0, cCount = 0, rCount = 0;
            int processed = 0;

            try
            {
                await SvnRunner.WaitForSemaphoreFreeAsync(token);

                SVNLogBridge.LogLine("<b>[SVN]</b> Pre-update cleanup...");
                await SVNClean.CleanupAsync(targetPath, token);
                SVNLogBridge.LogLine("<b>[SVN]</b> Cleanup completed.");

                token.ThrowIfCancellationRequested();
                if (session != _sessionId) throw new OperationCanceledException();

                int totalUpdates = 0;
                try
                {
                    string statusOutput = await SvnRunner.RunAsync("status -u", targetPath, token: token);
                    totalUpdates = statusOutput.Split('\n').Count(l => l.Length > 8 && l[8] == '*');
                }
                catch { }

                SVNLogBridge.LogLine($"<color=blue><b>[SVN]</b> Running update to revision {revision}...</color>");

                string result = await SvnRunner.RunLiveAsync(
                    $"update --accept postpone -r {revision}",
                    targetPath,
                    (line) =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;

                        string trimmed = line.Trim();
                        if (trimmed.Length > 0 && trimmed.All(c => c == '@' || c == '*' || c == ' ')) return;
                        if (trimmed.StartsWith("*****") || trimmed.StartsWith("@@@@@")) return;

                        string cleanLine = trimmed.Replace("[SVN ERROR]", "").Trim();
                        if (cleanLine.Length > 0 && cleanLine.All(c => c == '@' || c == '*') ||
                            cleanLine.StartsWith("*****") || cleanLine.StartsWith("@@@@@")) return;

                        if (token.IsCancellationRequested) return;
                        if (session != _sessionId) return;

                        processed++;
                        string progress = totalUpdates > 0 ? $" ({processed}/{totalUpdates})" : "";

                        string friendlyLine = line;

                        if (friendlyLine.Length > 1 && "UAGDCR ".Contains(friendlyLine[0]))
                        {
                            char status = friendlyLine[0];
                            string path = SvnRunner.NormalizeRepositoryPath(friendlyLine.Substring(1).TrimStart());
                            friendlyLine = $"{status} {path}";

                            switch (status)
                            {
                                case 'U': uCount++; break;
                                case 'G': gCount++; break;
                                case 'A': aCount++; break;
                                case 'D': dCount++; break;
                                case 'C': cCount++; break;
                                case 'R': rCount++; break;
                            }
                        }

                        friendlyLine = friendlyLine
                            .Replace("Updating '.'", "Scanning repository...")
                            .Replace("U ", "= Updated: ")
                            .Replace("A ", "+ Added: ")
                            .Replace("D ", "− Deleted: ")
                            .Replace("C ", "x Conflict: ")
                            .Replace("G ", "~ Merged: ")
                            .Replace("R ", "↻ Replaced: ");

                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            if (session != _sessionId) return;

                            SVNLogBridge.LogLine(
                                $"<b>[SVN]</b> <color=blue>{friendlyLine}{progress}</color>",
                                false
                            );
                        });
                    },
                    token
                );

                token.ThrowIfCancellationRequested();
                if (session != _sessionId || result == "Canceled")
                    throw new OperationCanceledException();

                string newRevision = revision;
                try
                {
                    string infoAfter = await SvnRunner.GetInfoAsync(targetPath);
                    token.ThrowIfCancellationRequested();
                    newRevision = ParseRevisionFromInfo(infoAfter);
                }
                catch { newRevision = revision; }

                stopwatch.Stop();

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Success,
                    Message = $"Update to revision {revision} completed",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };
                svnManager.LastUpdateSucceeded = true;

                SVNStatus.ClearLockCache();
                svnManager._diskChangesDetected = true;

                StringBuilder report = new StringBuilder();
                report.AppendLine("\n<color=blue><b>=========================================</b></color>");
                report.AppendLine($"<color=blue><b>          UPDATE TO REVISION {revision} REPORT</b></color>");
                report.AppendLine("<color=blue><b>=========================================</b></color>");

                if (oldRevision == newRevision)
                    report.AppendLine($"  Revision:   <b>{newRevision}</b> (No incoming changes)");
                else
                    report.AppendLine($"  Revision:   <b>{oldRevision}</b> ➔ <b>{newRevision}</b>");

                report.AppendLine($"  Duration:   <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>");
                report.AppendLine();

                if (uCount == 0 && aCount == 0 && dCount == 0 && cCount == 0 && gCount == 0 && rCount == 0)
                    report.AppendLine("  <color=green>Working copy was already at this revision.</color>");
                else
                {
                    report.AppendLine("  <b>[File Modifications]</b>");
                    if (uCount > 0) report.AppendLine($"    Updated:   <b>{uCount}</b>");
                    if (aCount > 0) report.AppendLine($"    Added:     <b>{aCount}</b>");
                    if (dCount > 0) report.AppendLine($"    Deleted:   <b><color=#B22222>{dCount}</color></b>");
                    if (gCount > 0) report.AppendLine($"    Merged:    <b>{gCount}</b>");
                    if (rCount > 0) report.AppendLine($"    Replaced:  <b>{rCount}</b>");

                    if (cCount > 0)
                    {
                        report.AppendLine();
                        report.AppendLine("  <color=#FFAA00><b>CRITICAL WARNING: CONFLICTS DETECTED</b></color>");
                        report.AppendLine($"    Conflicts: <b><color=#FFAA00>{cCount}</color></b>");
                        report.AppendLine("    Please resolve conflicts in working copy before compiling.");
                        await svnManager.GetModule<SVNResolve>().RefreshConflictUI();
                    }
                }
                report.AppendLine("<color=yellow><b>=========================================</b></color>");
                SVNLogBridge.LogLine(report.ToString(), false);

                if (!svnManager.WasUpdateCanceled && statusModule != null)
                    await statusModule.RefreshModifiedInternal();

                if (!svnManager.WasUpdateCanceled && svnBar != null)
                {
                    svnManager.IsUpdateRunning = false;
                    var newSnapshot = await svnBar.BuildSnapshotAsync(svnManager.CurrentProject, svnManager.WorkingDir);

                    string newAuthor = await GetAuthorForRevision(svnManager.WorkingDir, newRevision);

                    newSnapshot.Revision = newRevision;
                    if (!string.IsNullOrEmpty(newAuthor))
                    {
                        newSnapshot.Author = newAuthor;
                        newSnapshot.CurrentUser = newAuthor;
                    }
                    svnManager.CurrentSnapshot = newSnapshot;
                    await svnBar.ShowProjectInfo(svnManager.CurrentProject, svnManager.WorkingDir,
                        forceOutdatedCheck: true, isRefreshing: false);
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                svnManager.IsUpdateRunning = false;
                svnManager.CurrentSnapshot = oldSnapshot;
                if (svnBar != null)
                    await svnBar.ShowProjectInfo(svnManager.CurrentProject, svnManager.WorkingDir,
                        forceOutdatedCheck: false, isRefreshing: false);

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Canceled,
                    Message = $"Update to revision {revision} canceled by user",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };

                StringBuilder cancelReport = new StringBuilder();
                cancelReport.AppendLine("\n<color=#FFAA00><b>=========================================</b></color>");
                cancelReport.AppendLine("<color=#FFAA00><b>          UPDATE INTERRUPTED             </b></color>");
                cancelReport.AppendLine("<color=#FFAA00><b>=========================================</b></color>");
                cancelReport.AppendLine($"  Process aborted after <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>.");
                cancelReport.AppendLine("  Working copy state might be incomplete.");
                cancelReport.AppendLine("<color=#FFAA00><b>=========================================</b></color>");
                SVNLogBridge.LogLine(cancelReport.ToString(), false);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                svnManager.IsUpdateRunning = false;
                svnManager.CurrentSnapshot = oldSnapshot;
                if (svnBar != null)
                    await svnBar.ShowProjectInfo(svnManager.CurrentProject, svnManager.WorkingDir,
                        forceOutdatedCheck: false, isRefreshing: false);

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Failed,
                    Message = ex.Message,
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };

                StringBuilder failureReport = new StringBuilder();
                failureReport.AppendLine("\n<color=#B22222><b>=========================================</b></color>");
                failureReport.AppendLine($"<color=#B22222><b>            UPDATE TO REV {revision} FAILED</b></color>");
                failureReport.AppendLine("<color=#B22222><b>=========================================</b></color>");
                failureReport.AppendLine($"  Execution crashed after <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>.");
                failureReport.AppendLine($"  Error message: <color=#E6E6E6>{ex.Message}</color>");
                failureReport.AppendLine("<color=#B22222><b>=========================================</b></color>");
                SVNLogBridge.LogLine(failureReport.ToString(), false);
            }
            finally
            {
                svnManager.IsUpdateRunning = false;
                IsProcessing = false;
                _runningTask = null;
            }
        }

        public async Task<bool> HasLocalModificationsAsync(string workingDir)
        {
            try
            {
                string output = await SvnRunner.RunAsync("status", workingDir);
                if (string.IsNullOrWhiteSpace(output))
                    return false;

                foreach (string line in output.Split('\n'))
                {
                    if (line.Length > 0 && "MADRC!".Contains(line[0]))
                        return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}