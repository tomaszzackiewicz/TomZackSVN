using System;
using UnityEngine;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace SVN.Core
{
    public class SVNUpdate : SVNBase
    {
        private string _lastLiveLine = string.Empty;
        private bool _hasNewLine = false;
        private CancellationTokenSource _updateCTS;
        private Task _runningTask;
        private Guid _sessionId = Guid.Empty;

        public SVNUpdate(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void Update()
        {
            if (_runningTask != null &&
                !_runningTask.IsCompleted)
            {
                SVNLogBridge.LogLine(
                    "<color=orange>Update already running...</color>",
                    false
                );

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
            if (session != _sessionId)
                return;

            try
            {
                _updateCTS?.Dispose();
            }
            catch { }

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

            // Zapamiętujemy rewizję PRZED aktualizacją, żeby pokazać dokładny stan "jak było przedtem"
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

            _lastLiveLine = "Connecting to repository...";
            _hasNewLine = true;

            var svnBar = svnManager.GetModule<SVNBar>();

            _ = svnBar?.StartLiveSizeMonitor(targetPath, token);

            if (svnBar != null)
            {
                await svnBar.ShowProjectInfo(
                    svnManager.CurrentProject,
                    targetPath,
                    isRefreshing: true
                );
            }

            var stopwatch = Stopwatch.StartNew();

            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Updating,
                Message = "Running SVN update...",
                Repo = svnManager.RepositoryUrl
            };

            try
            {
                SVNLogBridge.LogLine("<b>[SVN]</b> Pre-update cleanup...", false);

                await SVNClean.CleanupAsync(targetPath, token);

                token.ThrowIfCancellationRequested();
                if (session != _sessionId)
                    throw new OperationCanceledException();

                SVNLogBridge.LogLine("<color=blue><b>[SVN]</b> Running update...</color>", false);

                StringBuilder liveOutput = new StringBuilder();

                string result = await SvnRunner.RunLiveAsync(
                    "update --accept postpone",
                    targetPath,
                    (line) =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        if (token.IsCancellationRequested) return;
                        if (session != _sessionId) return;

                        liveOutput.AppendLine(line);

                        _lastLiveLine = line;
                        _hasNewLine = true;

                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            if (session != _sessionId) return;

                            SVNLogBridge.LogLine(
                                $"<b>[SVN]</b> Updating: <color=blue>{line}</color>",
                                false
                            );
                        });
                    },
                    token
                );

                token.ThrowIfCancellationRequested();

                if (session != _sessionId || result == "Canceled")
                    throw new OperationCanceledException();

                string finalOutput = liveOutput.ToString();

                int updatedCount = 0;
                string revision = "Unknown";

                int uCount = 0, gCount = 0, aCount = 0, dCount = 0, cCount = 0, rCount = 0;

                await Task.Run(() =>
                {
                    updatedCount = Regex.Matches(finalOutput, @"^[UGADR]\s", RegexOptions.Multiline).Count;
                    revision = svnManager.ParseRevision(finalOutput);

                    var matches = Regex.Matches(finalOutput, @"^([UGADCR])\s", RegexOptions.Multiline);

                    foreach (Match m in matches)
                    {
                        switch (m.Groups[1].Value)
                        {
                            case "U": uCount++; break;
                            case "G": gCount++; break;
                            case "A": aCount++; break;
                            case "D": dCount++; break;
                            case "C": cCount++; break;
                            case "R": rCount++; break;
                        }
                    }
                }, token);

                token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(revision) || revision == "Unknown")
                {
                    string infoOutput = await SvnRunner.GetInfoAsync(targetPath);
                    token.ThrowIfCancellationRequested();

                    revision = ParseRevisionFromInfo(infoOutput);
                }

                stopwatch.Stop();

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Success,
                    Message = "Update completed successfully",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };

                svnManager.LastUpdateSucceeded = true;

                // 🔥 ROZBUDOWANY, CZYSTY RAPORT KOŃCOWY (BEZ ZNAKÓW PIPE)
                StringBuilder report = new StringBuilder();
                report.AppendLine("\n<color=blue><b>=========================================</b></color>");
                report.AppendLine("<color=blue><b>          SVN UPDATE REPORT              </b></color>");
                report.AppendLine("<color=blue><b>=========================================</b></color>");

                if (oldRevision == revision || oldRevision == "Unknown")
                {
                    report.AppendLine($"  Revision:   <b>{revision}</b> (No incoming changes)");
                }
                else
                {
                    report.AppendLine($"  Revision:   <b>{oldRevision}</b> ➔ <b>{revision}</b>");
                }

                report.AppendLine($"  Duration:   <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>");
                report.AppendLine();

                if (uCount == 0 && aCount == 0 && dCount == 0 && cCount == 0 && gCount == 0 && rCount == 0)
                {
                    report.AppendLine("  <color=green>Working copy was already fully up-to-date.</color>");
                }
                else
                {
                    report.AppendLine("  <b>[File Modifications]</b>");
                    if (uCount > 0) report.AppendLine($"    Updated:   <b>{uCount}</b>");
                    if (aCount > 0) report.AppendLine($"    Added:     <b>{aCount}</b>");
                    if (dCount > 0) report.AppendLine($"    Deleted:   <b><color=#B22222>{dCount}</color></b>"); // Wiśniowy kontrast
                    if (gCount > 0) report.AppendLine($"    Merged:    <b>{gCount}</b>");
                    if (rCount > 0) report.AppendLine($"    Replaced:  <b>{rCount}</b>");

                    if (cCount > 0)
                    {
                        report.AppendLine();
                        report.AppendLine("  <color=red><b>CRITICAL WARNING: CONFLICTS DETECTED</b></color>");
                        report.AppendLine($"    Conflicts: <b><color=red>{cCount}</color></b>");
                        report.AppendLine("    Please resolve conflicts in working copy before compiling.");

                        await svnManager.GetModule<SVNResolve>().RefreshConflictUI();
                    }
                }
                report.AppendLine("<color=yellow><b>=========================================</b></color>");

                SVNLogBridge.LogLine(report.ToString(), false);

                var statusModule = svnManager.GetModule<SVNStatus>();

                if (!svnManager.WasUpdateCanceled && statusModule != null)
                {
                    await statusModule.RefreshModifiedInternal();
                }

                if (!svnManager.WasUpdateCanceled && svnBar != null)
                {
                    var newSnapshot = await svnBar.BuildSnapshotAsync(
                        svnManager.CurrentProject,
                        svnManager.WorkingDir
                    );

                    string newAuthor = await GetAuthorForRevision(svnManager.WorkingDir, revision);

                    newSnapshot.Revision = revision;

                    if (!string.IsNullOrEmpty(newAuthor))
                    {
                        newSnapshot.Author = newAuthor;
                        newSnapshot.CurrentUser = newAuthor;
                    }

                    svnManager.CurrentSnapshot = newSnapshot;

                    await svnBar.ShowProjectInfo(
                        svnManager.CurrentProject,
                        svnManager.WorkingDir,
                        forceOutdatedCheck: true,
                        isRefreshing: false
                    );
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();

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

                _lastLiveLine = "";
                _hasNewLine = false;

                _runningTask = null;
            }
        }

        private async Task<string> GetAuthorForRevision(string targetPath, string revision)
        {
            try
            {
                // svn info -r <rewizja> pobiera informacje z repozytorium dla konkretnej rewizji
                string infoOutput = await SvnRunner.GetInfoAsync($"-r {revision} \"{targetPath}\"");
                var match = Regex.Match(infoOutput, @"^Last Changed Author:\s*(.+)$", RegexOptions.Multiline);
                return match.Success ? match.Groups[1].Value.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        public void CancelUpdate()
        {
            if (_updateCTS == null || !IsProcessing)
                return;

            SVNLogBridge.LogLine(
                "<color=orange><b>[SVN]</b> Cancel requested...</color>",
                false
            );

            svnManager.WasUpdateCanceled = true;
            svnManager.IsUpdateRunning = false;
            svnManager.LastUpdateSucceeded = false;

            _updateCTS.Cancel();
            _sessionId = Guid.NewGuid();

            _lastLiveLine = "";
            _hasNewLine = false;

            // 🔥 STATE
            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Canceled,
                //Message = "Update canceled by user",
                Duration = 0,
                Repo = svnManager.RepositoryUrl
            };

            var snapshot = svnManager.CurrentSnapshot;

            string statusColor = "#FFAA00";

            string projectName =
                snapshot?.ProjectName ??
                (string.IsNullOrEmpty(svnManager.WorkingDir)
                    ? "Unknown project"
                    : Path.GetFileName(svnManager.WorkingDir.TrimEnd('/', '\\')));

            string user =
                snapshot?.CurrentUser ??
                svnManager.CurrentUserName ??
                "Unknown";

            string branch =
                snapshot?.Branch ?? "unknown";

            string revision =
                snapshot?.Revision ?? "unknown";

            string repo =
                Uri.TryCreate(svnManager.RepositoryUrl, UriKind.Absolute, out var uri)
                    ? uri.Host
                    : "Unknown repo";

            string message =
                svnManager.OperationInfo.Message;

            // 🔥 EXACT SAME FORMAT AS RenderSnapshot
            string line =
     $"<size=150%><color={statusColor}>●</color></size> " +
     $"<color=orange><b>{projectName}</b> ({snapshot?.WorkingCopySize ?? "?"})</color> | " +
     $"<color=#00E5FF>User:</color> <color=#E6E6E6>{user}</color> | " +
     $"<color=#00E5FF>Branch:</color> <color=#E6E6E6>{branch}</color> | " +
     $"<color=#00E5FF>Rev:</color> <color=#E6E6E6>{revision}</color> | " +
     $"<color=#00E5FF>Status:</color> <color=#E6E6E6>Canceled</color> | " +
     $"<color=#E6E6E6>Srv:{repo}</color> | " +
     $"<color=#E6E6E6>Update Interrupted</color>";

            SVNLogBridge.UpdateUIField(
                svnUI.StatusInfoText,
                line,
                "INFO",
                append: false
            );
        }

        public string ParseRevisionFromInfo(string infoOutput)
        {
            var match = System.Text.RegularExpressions.Regex.Match(infoOutput, @"^Revision:\s+(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value : "Unknown";
        }

        public async void CheckRemoteModificationsButton()
        {
            await CheckRemoteModifications();
        }

        public async Task CheckRemoteModifications()
        {
            var updateModule = svnManager.GetModule<SVNUpdate>();

            await updateModule.UpdateRemoteModifications();
        }

        public async Task UpdateRemoteModifications()
        {
            if (IsProcessing) return;

            string root = svnManager.WorkingDir;
            IsProcessing = true;

            try
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "", "REMOTE", false);

                string startMsg = "<b>[SVN]</b> Checking repository for remote changes...";
                SVNLogBridge.LogLine(startMsg);
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, startMsg + "\n", "REMOTE", true);

                string output = await SvnRunner.RunAsync("status -u", root);

                if (string.IsNullOrEmpty(output))
                {
                    string noChangesMsg = "<color=green>No changes found.</color>";
                    SVNLogBridge.LogLine(noChangesMsg);
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, noChangesMsg, "REMOTE", true);
                    return;
                }

                using (var stringReader = new System.IO.StringReader(output))
                {
                    string line;
                    int remoteChangesCount = 0;
                    System.Text.StringBuilder remoteLogs = new System.Text.StringBuilder();

                    while ((line = stringReader.ReadLine()) != null)
                    {
                        if (line.Length > 8)
                        {
                            char remoteStatus = line[8];

                            if (remoteStatus == '*')
                            {
                                remoteChangesCount++;
                                string pathPart = line.Substring(9).Trim();
                                string cleanPath = System.Text.RegularExpressions.Regex.Replace(pathPart, @"^\d+\s+", "");

                                string changeMsg = $"<color=orange>Update available:</color> {cleanPath}";
                                SVNLogBridge.LogLine(changeMsg);
                                remoteLogs.AppendLine(changeMsg);
                            }
                        }
                    }

                    if (remoteChangesCount > 0)
                    {
                        SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, remoteLogs.ToString(), "REMOTE", true);

                        string summary = $"\n<b>Summary:</b> Found <color=red>{remoteChangesCount}</color> items to update.";
                        SVNLogBridge.LogLine(summary);
                        SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, summary, "REMOTE", true);
                    }
                    else
                    {
                        string upToDateMsg = "<color=green>Your working copy is up to date.</color>";
                        SVNLogBridge.LogLine(upToDateMsg);
                        SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, upToDateMsg, "REMOTE", true);
                    }
                }
            }
            catch (System.Exception ex)
            {
                string errorMsg = $"<color=red>Remote Check Error:</color> {ex.Message}";
                SVNLogBridge.LogError($"[SVN] {errorMsg}");
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, errorMsg, "REMOTE", true);
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }
}