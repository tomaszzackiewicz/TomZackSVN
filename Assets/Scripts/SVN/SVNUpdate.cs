using System;
using System.Collections.Generic;
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

            _runningTask = ExecuteUpdateCoreAsync(svnManager.WorkingDir, null, _sessionId);
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
            _runningTask = ExecuteUpdateCoreAsync(svnManager.WorkingDir, revision, _sessionId);
        }

        private async Task ExecuteUpdateCoreAsync(string targetPath, string targetRevision, Guid session)
        {
            if (session != _sessionId) return;

            var statusModule = svnManager.GetModule<SVNStatus>();
            statusModule?.CancelCurrentRefresh();

            CancellationTokenSource localCts = new CancellationTokenSource();
            CancellationToken token = localCts.Token;

            int waitCount = 0;
            while (statusModule != null && statusModule.IsProcessing && waitCount < 50)
            {
                await Task.Delay(20, token);
                waitCount++;
            }

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                svnUI?.SvnTreeView?.ClearView();
                if (svnUI?.TreeDisplay != null && svnUI.TreeDisplay.text.Contains("Scanning"))
                {
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                }
            });

            SVNBar svnBar = null;
            var stopwatch = Stopwatch.StartNew();
            var oldSnapshot = svnManager.CurrentSnapshot;
            string oldRevision = oldSnapshot?.Revision ?? "Unknown";

            if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                localCts.Dispose();
                return;
            }

            try
            {
                // Przypisujemy zainicjalizowany token do globalnego CTS modułu
                _updateCTS = localCts;

                svnManager.IsUpdateRunning = true;
                svnManager.LastUpdateSucceeded = false;
                IsProcessing = true;

                if (session != _sessionId) throw new OperationCanceledException(token);

                bool isRevisionTarget = !string.IsNullOrEmpty(targetRevision);
                string commandLabel = isRevisionTarget ? $"update to revision {targetRevision}" : "update";

                if (oldRevision == "Unknown")
                {
                    try
                    {
                        string infoBefore = await SvnRunner.GetInfoAsync(targetPath, token);
                        token.ThrowIfCancellationRequested();
                        oldRevision = ParseRevisionFromInfo(infoBefore);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Could not determine current revision: {ex.Message}</color>");
                    }
                }

                svnBar = svnManager.GetModule<SVNBar>();
                string projectName = svnManager.CurrentProject?.projectName ?? Path.GetFileName(targetPath);
                svnBar?.BeginUpdate(projectName);

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Updating,
                    Message = $"Running SVN {commandLabel}...",
                    Duration = 0,
                    Repo = svnManager.RepositoryUrl
                };

                int uCount = 0, gCount = 0, aCount = 0, dCount = 0, cCount = 0, rCount = 0;
                int processed = 0;
                int totalUpdates = 0;

                await SvnRunner.WaitForSemaphoreFreeAsync(token);
                token.ThrowIfCancellationRequested();
                if (session != _sessionId) throw new OperationCanceledException(token);

                SVNLogBridge.LogToOutput("<b>[SVN]</b> Pre-update cleanup...");
                await SVNClean.CleanupAsync(targetPath, token);
                SVNLogBridge.LogToOutput("<b>[SVN]</b> Cleanup completed.");

                try
                {
                    string statusOutput = await SvnRunner.RunAsync("status -u", targetPath, token: token);
                    token.ThrowIfCancellationRequested();

                    if (!string.IsNullOrWhiteSpace(statusOutput))
                    {
                        using (var reader = new StringReader(statusOutput))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.Length > 8 && line[8] == '*') totalUpdates++;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Progress estimation unavailable: {ex.Message}</color>");
                }

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

                        string displayLine;
                        char contentStatus = trimmed.Length > 0 ? trimmed[0] : ' ';
                        char propStatus = trimmed.Length > 1 ? trimmed[1] : ' ';
                        char activeStatus = contentStatus != ' ' ? contentStatus : propStatus;

                        if (trimmed.StartsWith("Updating '.'"))
                        {
                            displayLine = "Scanning repository...";
                        }
                        else if (trimmed.StartsWith("At revision") || trimmed.StartsWith("Checked out revision"))
                        {
                            return;
                        }
                        else if (trimmed.Length > 2 && "UAGDCR ".Contains(activeStatus))
                        {
                            char status = activeStatus;
                            string path = SvnRunner.NormalizeRepositoryPath(trimmed.Substring(2).TrimStart());

                            switch (status)
                            {
                                case 'U': uCount++; break;
                                case 'G': gCount++; break;
                                case 'A': aCount++; break;
                                case 'D': dCount++; break;
                                case 'C': cCount++; break;
                                case 'R': rCount++; break;
                            }

                            string prefix;
                            switch (status)
                            {
                                case 'U': prefix = "= Updated"; break;
                                case 'A': prefix = "+ Added"; break;
                                case 'D': prefix = "- Deleted"; break;
                                case 'C': prefix = "x Conflict"; break;
                                case 'G': prefix = "~ Merged"; break;
                                case 'R': prefix = "~ Replaced"; break;
                                default: prefix = $"  {status}"; break;
                            }

                            displayLine = $"{prefix}: {path}";
                        }
                        else
                        {
                            displayLine = trimmed;
                        }

                        string progressStr = totalUpdates > 0 ? $" ({processed}/{totalUpdates})" : "";

                        bool shouldLog = !token.IsCancellationRequested && session == _sessionId;
                        string logMessage = $"<b>[SVN]</b> <color=blue>{displayLine}{progressStr}</color>";

                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            if (shouldLog)
                                SVNLogBridge.LogToOutput(logMessage);
                        });
                    },
                    token
                );

                token.ThrowIfCancellationRequested();
                if (session != _sessionId || result == "Canceled")
                    throw new OperationCanceledException(token);

                string newRevision = targetRevision ?? "Unknown";
                try
                {
                    string infoAfter = await SvnRunner.GetInfoAsync(targetPath, token);
                    token.ThrowIfCancellationRequested();
                    newRevision = ParseRevisionFromInfo(infoAfter);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Could not determine resulting revision: {ex.Message}</color>");
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

                var report = new StringBuilder();
                report.AppendLine("\n<color=blue><b>=========================================</b></color>");
                report.AppendLine(isRevisionTarget
                    ? $"<color=blue><b>     UPDATE TO REVISION {targetRevision} REPORT    </b></color>"
                    : "<color=blue><b>          SVN UPDATE REPORT             </b></color>");
                report.AppendLine("<color=blue><b>=========================================</b></color>");
                report.AppendLine(oldRevision == newRevision || oldRevision == "Unknown"
                    ? $"  Revision:   <b>{newRevision}</b> (No incoming changes)"
                    : $"  Revision:   <b>{oldRevision}</b> -> <b>{newRevision}</b>");
                report.AppendLine($"  Duration:   <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>\n");

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
                        report.AppendLine("\n  <color=#FFAA00><b>CRITICAL WARNING: CONFLICTS DETECTED</b></color>");
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
                    var snap = svnManager.CurrentSnapshot ?? new SVNProjectInfoSnapshot();
                    snap.Revision = newRevision;

                    string newAuthor = await GetAuthorForRevision(svnManager.WorkingDir, newRevision, token);
                    if (!string.IsNullOrEmpty(newAuthor))
                    {
                        snap.Author = newAuthor;
                    }

                    snap.IsValid = true;
                    svnManager.CurrentSnapshot = snap;

                    await svnBar.EndUpdate(snap);
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                svnManager.LastUpdateSucceeded = false;
                svnManager.CurrentSnapshot = oldSnapshot;

                svnBar?.EndUpdateFailed(oldSnapshot);

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Canceled,
                    Message = "Update canceled by user",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };

                var cancelReport = new StringBuilder();
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

                if (ex.Message.Contains("locked") || ex.Message.Contains("E155004"))
                {
                    try
                    {
                        if (localCts != null && !localCts.IsCancellationRequested)
                            await SVNClean.CleanupAsync(targetPath, localCts.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                }

                svnManager.LastUpdateSucceeded = false;
                svnManager.CurrentSnapshot = oldSnapshot;

                svnBar?.EndUpdateFailed(oldSnapshot);

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Failed,
                    Message = ex.Message,
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };

                var failureReport = new StringBuilder();
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

                if (_updateCTS == localCts)
                    _updateCTS = null;

                localCts?.Dispose();
                _runningTask = null;
            }
        }

        private async Task<string> GetAuthorForRevision(string targetPath, string revision, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(revision) || revision == "Unknown")
                return string.Empty;

            try
            {
                token.ThrowIfCancellationRequested();
                string logOutput = await SvnRunner.RunAsync($"log -r {revision} -l 1", targetPath, token: token);
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(logOutput)) return string.Empty;

                using (var reader = new StringReader(logOutput))
                {
                    reader.ReadLine();
                    string revisionLine = reader.ReadLine();

                    if (string.IsNullOrWhiteSpace(revisionLine)) return string.Empty;

                    string[] parts = revisionLine.Split('|');
                    if (parts.Length >= 2)
                        return parts[1].Trim();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Could not determine revision author: {ex.Message}</color>");
            }
            return string.Empty;
        }

        public void CancelUpdate()
        {
            if (_updateCTS == null || !svnManager.IsUpdateRunning) return;

            SVNLogBridge.LogToOutput("<color=orange><b>[SVN]</b> Cancel requested...</color>");

            svnManager.WasUpdateCanceled = true;
            svnManager.LastUpdateSucceeded = false;

            try { _updateCTS.Cancel(); }
            catch (ObjectDisposedException) { }

            _sessionId = Guid.NewGuid();

            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Canceled,
                Message = "Cancel requested...",
                Duration = 0,
                Repo = svnManager.RepositoryUrl
            };
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

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                return;
            }

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                CancellationToken token = cts.Token;
                IsProcessing = true;
                var remoteFiles = new List<string>();
                var conflictFiles = new List<string>();

                try
                {
                    SVNLogBridge.LogLine("<i>Checking remote changes...</i>");
                    string output = await SvnRunner.RunAsync("status -u", root, token: token);
                    token.ThrowIfCancellationRequested();

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
                            token.ThrowIfCancellationRequested();

                            if (line.Length > 8 && line[8] == '*')
                            {
                                remoteChangesCount++;
                                string pathPart = line.Substring(9).TrimStart();
                                pathPart = RevisionPrefixRegex.Replace(pathPart, "");
                                string cleanPath = SvnRunner.NormalizeRepositoryPath(pathPart.TrimEnd());
                                remoteFiles.Add(cleanPath);
                            }

                            if (line.Length > 1 && (line[0] == 'C' || line[1] == 'C'))
                            {
                                string rawPath = line.Length > 8 ? line.Substring(8).Trim() : line.Trim();
                                string cleanPath = SvnRunner.NormalizeRepositoryPath(SvnRunner.CleanSvnPath(rawPath));
                                if (!conflictFiles.Contains(cleanPath))
                                    conflictFiles.Add(cleanPath);
                            }
                        }
                    }

                    if (conflictFiles.Count > 0)
                    {
                        SVNLogBridge.LogLine($"<color=#FF4444><b>WARNING: {conflictFiles.Count} local conflict(s) detected!</b></color>");
                        SVNLogBridge.LogLine("<color=#FF4444>Resolve before updating or merge will fail.</color>");
                        foreach (var c in conflictFiles.Take(10))
                            SVNLogBridge.LogLine($"<color=#FF4444>  • {c}</color>");
                        if (conflictFiles.Count > 10)
                            SVNLogBridge.LogLine($"<color=#FF4444>  ... and {conflictFiles.Count - 10} more</color>");
                        SVNLogBridge.LogLine("");
                    }

                    if (remoteChangesCount > 0)
                    {
                        string tempFile = await WriteRemoteChangesToTempFileAsync(remoteFiles, root, token);
                        OpenInEditor(tempFile);

                        SVNLogBridge.LogLine($"<b>Summary:</b> Found <color=#FFAA00>{remoteChangesCount}</color> items to update.");
                        SVNLogBridge.LogLine("<color=yellow>Full list opened in external text editor.</color>");
                    }
                    else
                    {
                        SVNLogBridge.LogLine("<color=green>Your working copy is up to date.</color>");
                    }
                }
                catch (OperationCanceledException)
                {
                    SVNLogBridge.LogLine("<color=yellow>Remote update check canceled or timed out.</color>");
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
        }

        private async Task<string> WriteRemoteChangesToTempFileAsync(List<string> files, string root, CancellationToken token)
        {
            CleanupOldTempFiles("svn_remote_changes_*.txt");

            string tempFilePath = Path.Combine(Path.GetTempPath(), $"svn_remote_changes_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"# SVN Remote Changes Report");
            sb.AppendLine($"# Root: {root}");
            sb.AppendLine($"# Generated: {DateTime.Now:G}");
            sb.AppendLine($"# Total items to update: {files.Count}");
            sb.AppendLine(new string('-', 60));

            foreach (var path in files)
            {
                sb.AppendLine(path);
            }

            await File.WriteAllTextAsync(tempFilePath, sb.ToString(), new UTF8Encoding(false), token).ConfigureAwait(false);
            return tempFilePath;
        }

        private void OpenInEditor(string filePath)
        {
            try
            {
                string editorPath = svnManager?.MergeToolPath;
                if (!string.IsNullOrWhiteSpace(editorPath) && File.Exists(editorPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = editorPath,
                        Arguments = $"\"{filePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Could not open editor: {ex.Message}");
            }
        }

        private static void CleanupOldTempFiles(string pattern)
        {
            try
            {
                string temp = Path.GetTempPath();
                foreach (var file in Directory.GetFiles(temp, pattern))
                {
                    if (File.Exists(file))
                    {
                        var fi = new FileInfo(file);
                        if (fi.CreationTime < DateTime.Now.AddHours(-24))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public async Task<bool> HasLocalModificationsAsync(string workingDir, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
                return false;

            try
            {
                token.ThrowIfCancellationRequested();
                string output = await SvnRunner.RunAsync("status", workingDir, token: token);
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(output))
                    return false;

                using (var reader = new StringReader(output))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        char status = line[0];
                        if ("MADRC!?".IndexOf(status) >= 0)
                            return true;
                    }
                }
                return false;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Error checking local modifications: {ex.Message}");
                return true;
            }
        }
    }
}