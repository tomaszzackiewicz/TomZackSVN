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
        private static readonly Regex RevisionRegex = new Regex(@"^Revision:\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex RevisionPrefixRegex = new Regex(@"^\d+\s+", RegexOptions.Compiled);

        private CancellationTokenSource _updateCTS;
        private Task _runningTask;
        private Guid _sessionId = Guid.Empty;

        public SVNUpdate(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void Update()
        {
            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir) || !Directory.Exists(svnManager.WorkingDir))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                return;
            }

            if (_runningTask != null && !_runningTask.IsCompleted)
            {
                SVNLogBridge.LogToOutput("<color=orange>Update already running...</color>");
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
            _runningTask = ExecuteUpdateCoreAsync(null, _sessionId);
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
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                return;
            }

            if (_runningTask != null && !_runningTask.IsCompleted)
            {
                SVNLogBridge.LogLine("<color=orange>Update already running...</color>", false);
                return;
            }

            if (!int.TryParse(revision, out _))
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Invalid revision number: {revision}");
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
            _runningTask = ExecuteUpdateCoreAsync(revision, _sessionId);
        }

        private async Task ExecuteUpdateCoreAsync(string targetRevision, Guid session)
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
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory is empty.");
                return;
            }

            bool isRevisionTarget = !string.IsNullOrEmpty(targetRevision);
            string commandLabel = isRevisionTarget ? $"update to revision {targetRevision}" : "update";

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
                Message = $"Running SVN {commandLabel}...",
                Repo = svnManager.RepositoryUrl
            };

            int uCount = 0, gCount = 0, aCount = 0, dCount = 0, cCount = 0, rCount = 0;
            int processed = 0;

            try
            {
                await SvnRunner.WaitForSemaphoreFreeAsync(token);

                SVNLogBridge.LogToOutput("<b>[SVN]</b> Pre-update cleanup...");
                await SVNClean.CleanupAsync(targetPath, token);
                SVNLogBridge.LogToOutput("<b>[SVN]</b> Cleanup completed.");

                token.ThrowIfCancellationRequested();
                if (session != _sessionId) throw new OperationCanceledException();

                int totalUpdates = 0;
                try
                {
                    string statusOutput = await SvnRunner.RunAsync("status -u", targetPath, token: token);
                    totalUpdates = statusOutput.Split('\n').Count(l => l.Length > 8 && l[8] == '*');
                }
                catch { }

                string svnCommand = isRevisionTarget
                    ? $"update --accept postpone -r {targetRevision}"
                    : "update --accept postpone";

                SVNLogBridge.LogToOutput($"<color=blue><b>[SVN]</b> Running {commandLabel}...</color>");

                string result = await SvnRunner.RunLiveAsync(
                    svnCommand,
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

                        string displayLine;

                        if (trimmed.StartsWith("Updating '.'"))
                        {
                            displayLine = "Scanning repository...";
                        }
                        else if (trimmed.Length > 2 && "UAGDCR ".Contains(trimmed[0]) && char.IsWhiteSpace(trimmed[1]) && char.IsWhiteSpace(trimmed[2]))
                        {
                            char status = trimmed[0];
                            string path = SvnRunner.NormalizeRepositoryPath(trimmed.Substring(1).TrimStart());

                            switch (status)
                            {
                                case 'U': uCount++; break;
                                case 'G': gCount++; break;
                                case 'A': aCount++; break;
                                case 'D': dCount++; break;
                                case 'C': cCount++; break;
                                case 'R': rCount++; break;
                            }

                            string prefix = status switch
                            {
                                'U' => "= Updated",
                                'A' => "+ Added",
                                'D' => "- Deleted",
                                'C' => "x Conflict",
                                'G' => "~ Merged",
                                'R' => "~ Replaced",
                                _ => $"  {status}"
                            };

                            displayLine = $"{prefix}: {path}";
                        }
                        else
                        {
                            displayLine = trimmed;
                        }

                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            if (session != _sessionId) return;

                            SVNLogBridge.LogToOutput(
                                $"<b>[SVN]</b> <color=blue>{displayLine}{progress}</color>");
                        });
                    },
                    token
                );

                token.ThrowIfCancellationRequested();
                if (session != _sessionId || result == "Canceled")
                    throw new OperationCanceledException();

                string newRevision = targetRevision ?? "Unknown";
                try
                {
                    string infoAfter = await SvnRunner.GetInfoAsync(targetPath);
                    token.ThrowIfCancellationRequested();
                    newRevision = ParseRevisionFromInfo(infoAfter);
                }
                catch
                {
                    if (newRevision == "Unknown") newRevision = targetRevision ?? "Unknown";
                }

                stopwatch.Stop();

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Success,
                    Message = $"{commandLabel} completed successfully",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };
                svnManager.LastUpdateSucceeded = true;

                SVNStatus.ClearLockCache();
                svnManager.DiskChangesDetected = true;

                StringBuilder report = new StringBuilder();
                report.AppendLine("\n<color=blue><b>=========================================</b></color>");

                if (isRevisionTarget)
                    report.AppendLine($"<color=blue><b>     UPDATE TO REVISION {targetRevision} REPORT    </b></color>");
                else
                    report.AppendLine("<color=blue><b>          SVN UPDATE REPORT              </b></color>");

                report.AppendLine("<color=blue><b>=========================================</b></color>");

                if (oldRevision == newRevision || oldRevision == "Unknown")
                    report.AppendLine($"  Revision:   <b>{newRevision}</b> (No incoming changes)");
                else
                    report.AppendLine($"  Revision:   <b>{oldRevision}</b> -> <b>{newRevision}</b>");

                report.AppendLine($"  Duration:   <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>");
                report.AppendLine();

                bool hasChanges = uCount > 0 || aCount > 0 || dCount > 0 || cCount > 0 || gCount > 0 || rCount > 0;

                if (!hasChanges)
                {
                    report.AppendLine(isRevisionTarget
                        ? "  <color=green>Working copy was already at this revision.</color>"
                        : "  <color=green>Working copy was already fully up-to-date.</color>");
                }
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
                    Message = $"{commandLabel} canceled by user",
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
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory is empty.");
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

            int uCount = 0, gCount = 0, aCount = 0, dCount = 0, cCount = 0, rCount = 0;
            int processed = 0;

            try
            {
                await SvnRunner.WaitForSemaphoreFreeAsync(token);

                SVNLogBridge.LogToOutput("<b>[SVN]</b> Pre-update cleanup...");
                await SVNClean.CleanupAsync(targetPath, token);
                SVNLogBridge.LogToOutput("<b>[SVN]</b> Cleanup completed.");

                token.ThrowIfCancellationRequested();
                if (session != _sessionId) throw new OperationCanceledException();

                int totalUpdates = 0;
                try
                {
                    string statusOutput = await SvnRunner.RunAsync("status -u", targetPath, token: token);
                    totalUpdates = statusOutput.Split('\n').Count(l => l.Length > 8 && l[8] == '*');
                }
                catch { }

                SVNLogBridge.LogToOutput("<color=blue><b>[SVN]</b> Running update...</color>");

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

                        if (friendlyLine.Length > 2 && "UAGDCR ".Contains(friendlyLine[0]) && char.IsWhiteSpace(friendlyLine[1]) && char.IsWhiteSpace(friendlyLine[2]))
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

                            SVNLogBridge.LogToOutput($"<b>[SVN]</b> <color=blue>{friendlyLine}{progress}</color>");
                        });
                    },
                    token
                );

                token.ThrowIfCancellationRequested();
                if (session != _sessionId || result == "Canceled")
                    throw new OperationCanceledException();

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
                svnManager.DiskChangesDetected = true;

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

            SVNLogBridge.LogToOutput("<color=orange><b>[SVN]</b> Cancel requested...</color>");

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
            if (string.IsNullOrWhiteSpace(infoOutput)) return "Unknown";

            var match = RevisionRegex.Match(infoOutput);
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

                if (string.IsNullOrWhiteSpace(output))
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
                            string pathPart = line.Substring(9).TrimStart();

                            pathPart = RevisionPrefixRegex.Replace(pathPart, "");
                            string cleanPath = SvnRunner.NormalizeRepositoryPath(pathPart.TrimEnd());

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
                SVNLogBridge.LogErrorToOutput($"[SVN] Remote check error: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task<bool> HasLocalModificationsAsync(string workingDir)
        {
            try
            {
                string output = await SvnRunner.RunAsync("status", workingDir);
                if (string.IsNullOrWhiteSpace(output))
                    return false;

                using (var reader = new StringReader(output))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        char status = line[0];

                        if ("MADRC!?".IndexOf(status) >= 0)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Error checking local modifications: {ex.Message}");
                return true;
            }
        }
    }
}