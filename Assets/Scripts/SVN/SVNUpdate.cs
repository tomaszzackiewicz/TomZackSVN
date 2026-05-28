using System;
using UnityEngine;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Text;
using System.Diagnostics;

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
                SVNLogBridge.LogError(
                    "[SVN] Working directory is empty."
                );

                return;
            }

            _lastLiveLine = "Connecting to repository...";
            _hasNewLine = true;

            var svnBar = svnManager.GetModule<SVNBar>();

            if (svnBar != null)
            {
                await svnBar.ShowProjectInfo(
                    svnManager.CurrentProject,
                    targetPath,
                    isRefreshing: true
                );
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                SVNLogBridge.LogLine(
                    "<b>[SVN]</b> Pre-update cleanup...",
                    false
                );

                await SVNClean.CleanupAsync(
                    targetPath,
                    token
                );

                token.ThrowIfCancellationRequested();

                if (session != _sessionId)
                    throw new OperationCanceledException();

                SVNLogBridge.LogLine(
                    "<color=blue><b>[SVN]</b> Running update...</color>",
                    false
                );

                StringBuilder liveOutput = new StringBuilder();

                string result = await SvnRunner.RunLiveAsync(
                    "update --accept postpone",
                    targetPath,
                    (line) =>
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            return;

                        if (token.IsCancellationRequested)
                            return;

                        if (session != _sessionId)
                            return;

                        liveOutput.AppendLine(line);

                        _lastLiveLine = line;
                        _hasNewLine = true;

                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            if (token.IsCancellationRequested)
                                return;

                            if (session != _sessionId)
                                return;

                            SVNLogBridge.LogLine(
                                $"<b>[SVN]</b> Updating: <color=#4FC3F7>{line}</color>",
                                false
                            );
                        });
                    },
                    token
                );

                token.ThrowIfCancellationRequested();

                if (session != _sessionId)
                    throw new OperationCanceledException();

                if (result == "Canceled")
                    throw new OperationCanceledException();

                string finalOutput = liveOutput.ToString();

                int updatedCount = 0;

                string revision = "Unknown";

                int uCount = 0;
                int gCount = 0;
                int aCount = 0;
                int dCount = 0;
                int cCount = 0;
                int rCount = 0;

                await Task.Run(() =>
                {
                    updatedCount = Regex.Matches(
                        finalOutput,
                        @"^[UGADR]\s",
                        RegexOptions.Multiline
                    ).Count;

                    revision = svnManager.ParseRevision(finalOutput);

                    var detailedMatches = Regex.Matches(
                        finalOutput,
                        @"^([UGADCR])\s",
                        RegexOptions.Multiline
                    );

                    foreach (Match match in detailedMatches)
                    {
                        switch (match.Groups[1].Value)
                        {
                            case "U":
                                uCount++;
                                break;

                            case "G":
                                gCount++;
                                break;

                            case "A":
                                aCount++;
                                break;

                            case "D":
                                dCount++;
                                break;

                            case "C":
                                cCount++;
                                break;

                            case "R":
                                rCount++;
                                break;
                        }
                    }
                }, token);

                token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(revision) ||
                    revision == "Unknown")
                {
                    string infoOutput =
                        await SvnRunner.GetInfoAsync(targetPath);

                    token.ThrowIfCancellationRequested();

                    revision =
                        ParseRevisionFromInfo(infoOutput);
                }

                stopwatch.Stop();

                string summary = "<b>[SVN UPDATE COMPLETED]</b>\n";

                if (updatedCount > 0)
                {
                    summary +=
                        $"<color=green>Success!</color> Updated <b>{updatedCount}</b> items.\n";
                }
                else
                {
                    summary +=
                        "<color=blue>No changes found.</color> Project is up to date.\n";
                }

                if (uCount > 0)
                    summary += $"Updated (U): <b>{uCount}</b>\n";

                if (gCount > 0)
                    summary += $"Merged (G): <b>{gCount}</b>\n";

                if (aCount > 0)
                    summary += $"Added (A): <b>{aCount}</b>\n";

                if (dCount > 0)
                    summary += $"Deleted (D): <b>{dCount}</b>\n";

                if (rCount > 0)
                    summary += $"Replaced (R): <b>{rCount}</b>\n";

                if (cCount > 0)
                {
                    summary +=
                        $"<color=#FF5252><b>CONFLICTS (C): {cCount}</b></color>\n";
                }

                summary +=
                    $"Current Revision: <color=#FFD700><b>{revision}</b></color>\n";

                string formattedTime =
                    stopwatch.Elapsed.TotalSeconds >= 60
                        ? $"{(int)stopwatch.Elapsed.TotalMinutes}m {stopwatch.Elapsed.Seconds}s"
                        : $"{stopwatch.Elapsed.TotalSeconds:F1}s";

                summary += $"Time Elapsed: <b>{formattedTime}</b>\n";

                SVNLogBridge.LogLine(summary, false);

                svnManager.LastUpdateSucceeded = true;

                var statusModule =
                    svnManager.GetModule<SVNStatus>();

                var svnBarModule =
                    svnManager.GetModule<SVNBar>();

                if (!svnManager.WasUpdateCanceled &&
                    statusModule != null)
                {
                    await statusModule.RefreshModifiedInternal();
                }

                if (!svnManager.WasUpdateCanceled &&
                    svnBarModule != null)
                {
                    svnManager.CurrentSnapshot =
                        await svnBarModule.BuildSnapshot(
                            svnManager.CurrentProject,
                            targetPath
                        );

                    await svnBarModule.ShowProjectInfo(
                        svnManager.CurrentProject,
                        targetPath,
                        forceOutdatedCheck: true,
                        isRefreshing: false
                    );
                }
            }
            catch (OperationCanceledException)
            {
                if (stopwatch.IsRunning)
                    stopwatch.Stop();

                SVNLogBridge.LogLine(
                    $"\n<color=orange><b>[ABORTED]</b></color> Update cancelled after {stopwatch.Elapsed.TotalSeconds:F1}s.",
                    false
                );
            }
            catch (Exception ex)
            {
                if (stopwatch.IsRunning)
                    stopwatch.Stop();

                if (!token.IsCancellationRequested &&
                    session == _sessionId)
                {
                    SVNLogBridge.LogError(
                        $"[SVN] Update Error:\n{ex}"
                    );
                }
            }
            finally
            {
                if (stopwatch.IsRunning)
                    stopwatch.Stop();

                svnManager.IsUpdateRunning = false;

                if (!token.IsCancellationRequested)
                {
                    svnManager.WasUpdateCanceled = false;
                }

                _lastLiveLine = "";
                _hasNewLine = false;

                try
                {
                    _updateCTS?.Dispose();
                }
                catch { }

                _updateCTS = null;

                _runningTask = null;

                IsProcessing = false;

                SVNLogBridge.LogLine(
                    "<color=#888888>[SVN]</color> Update session finished.",
                    false
                );
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

            // 🔥 invalidacja starej sesji
            _sessionId = Guid.NewGuid();

            _lastLiveLine = "";
            _hasNewLine = false;

            SVNLogBridge.UpdateUIField(
                svnUI.StatusInfoText,
                "<size=150%><color=#FFAA00>●</color></size> " +
                "<color=#FFAA00>Update canceled</color>",
                "INFO",
                append: false
            );
        }

        public string ParseRevisionFromInfo(string infoOutput)
        {
            var match = System.Text.RegularExpressions.Regex.Match(infoOutput, @"^Revision:\s+(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value : "Unknown";
        }

        private async Task UpdateUILive()
        {
            while (IsProcessing)
            {
                if (_hasNewLine)
                {
                    SVNLogBridge.LogLine($"<b>[SVN]</b> Updating: <color=orange>{_lastLiveLine}</color>", false);
                    _hasNewLine = false;
                }
                await Task.Yield();
            }
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