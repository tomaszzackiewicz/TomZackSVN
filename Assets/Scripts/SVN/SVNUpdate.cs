using System;
using UnityEngine;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;

namespace SVN.Core
{
    public class SVNUpdate : SVNBase
    {
        private string _lastLiveLine = string.Empty;
        private bool _hasNewLine = false;
        private CancellationTokenSource _updateCTS;

        public SVNUpdate(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void Update()
        {
            _ = ExecuteUpdateAsync();
        }

        public async Task ExecuteUpdateAsync()
        {
            if (IsProcessing) return;

            string targetPath = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(targetPath))
            {
                SVNLogBridge.LogLine("<color=red>Error:</color> Working Directory not set.", false);
                return;
            }

            IsProcessing = true;
            _lastLiveLine = "Connecting to repository...";
            _hasNewLine = true;

            _updateCTS = new CancellationTokenSource();
            var uiUpdateTask = UpdateUILive(_updateCTS.Token);

            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                SVNLogBridge.LogLine("<b>[SVN]</b> Pre-update cleanup...", false);
                await SVNClean.CleanupAsync(targetPath);

                _updateCTS.Token.ThrowIfCancellationRequested();

                string output = await SvnRunner.RunLiveAsync(
                    "update --accept postpone",
                    targetPath,
                    (line) =>
                    {
                        _lastLiveLine = line;
                        _hasNewLine = true;
                    },
                    _updateCTS.Token
                );

                _hasNewLine = false;

                int updatedCount = Regex.Matches(output, @"^[UGADR]\s", RegexOptions.Multiline).Count;
                string revision = svnManager.ParseRevision(output);

                if (string.IsNullOrEmpty(revision) || revision == "Unknown")
                {
                    string infoOutput = await SvnRunner.GetInfoAsync(targetPath);
                    revision = ParseRevisionFromInfo(infoOutput);
                }

                stopwatch.Stop();

                int uCount = 0; int gCount = 0; int aCount = 0; int dCount = 0; int cCount = 0; int rCount = 0;
                var detailedMatches = Regex.Matches(output, @"^([UGADCR])\s", RegexOptions.Multiline);
                foreach (Match match in detailedMatches)
                {
                    switch (match.Groups[1].Value)
                    {
                        case "U": uCount++; break;
                        case "G": gCount++; break;
                        case "A": aCount++; break;
                        case "D": dCount++; break;
                        case "C": cCount++; break;
                        case "R": rCount++; break;
                    }
                }

                string summary = "<b>[SVN UPDATE COMPLETED]</b>\n";

                if (updatedCount > 0)
                {
                    summary += $"<color=green>Success!</color> Updated <b>{updatedCount}</b> items.\n";

                    summary += "-----------------------------------\n";
                    if (uCount > 0) summary += $"  <color=#4FC3F7>• Updated (U):</color> <b>{uCount}</b>\n";
                    if (gCount > 0) summary += $"  <color=#81C784>• Merged (G):</color> <b>{gCount}</b>\n";
                    if (aCount > 0) summary += $"  <color=#AED581>• Added (A):</color> <b>{aCount}</b>\n";
                    if (dCount > 0) summary += $"  <color=#E57373>• Deleted (D):</color> <b>{dCount}</b>\n";
                    if (rCount > 0) summary += $"  <color=#FFB74D>• Replaced (R):</color> <b>{rCount}</b>\n";
                }
                else
                {
                    summary += "<color=blue>No changes found.</color> Project is up to date.\n";
                }

                if (cCount > 0)
                {
                    summary += "-----------------------------------\n";
                    summary += $"  <color=#FF5252><b>⚠ CONFLICTS (C): {cCount}</b></color>\n";
                }

                summary += "-----------------------------------\n";
                summary += $"Current Revision: <color=#FFD700><b>{revision}</b></color>\n";

                string formattedTime = stopwatch.Elapsed.TotalSeconds >= 60
                    ? $"{(int)stopwatch.Elapsed.TotalMinutes}m {stopwatch.Elapsed.Seconds}s"
                    : $"{stopwatch.Elapsed.TotalSeconds:F1}s";

                summary += $"Time Elapsed: <b>{formattedTime}</b>\n";
                summary += "-----------------------------------";

                SVNLogBridge.LogLine(summary, false);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                SVNLogBridge.LogLine($"\n<color=orange><b>[ABORTED]</b></color> Update cancelled by user after <b>{stopwatch.Elapsed.TotalSeconds:F1}s</b>. Next update will auto-cleanup.", false);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                SVNLogBridge.LogError($"[SVN] Update Error after {stopwatch.Elapsed.TotalSeconds:F1}s: {ex}");
                SVNLogBridge.LogLine($"\n<color=red><b>Update Failed:</b></color> {ex.Message} (Time: <b>{stopwatch.Elapsed.TotalSeconds:F1}s</b>)");
            }
            finally
            {
                _hasNewLine = false;

                _updateCTS?.Dispose();
                _updateCTS = null;

                await svnManager.RefreshStatus();

                IsProcessing = false;
            }
        }

        public void CancelUpdate()
        {
            if (IsProcessing && _updateCTS != null)
            {
                _updateCTS.Cancel();
            }
        }

        private async Task UpdateUILive(CancellationToken token)
        {
            while (IsProcessing && !token.IsCancellationRequested)
            {
                if (_hasNewLine)
                {
                    string line = _lastLiveLine;

                    _hasNewLine = false;

                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        SVNLogBridge.LogLine(
                            $"<b>[SVN]</b> Updating: <color=blue>{line}</color>",
                            false
                        );
                    });
                }

                try
                {
                    await Task.Delay(33, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        // public async void Update()
        // {
        //     if (IsProcessing) return;

        //     string targetPath = svnManager.WorkingDir;
        //     if (string.IsNullOrEmpty(targetPath))
        //     {
        //         SVNLogBridge.LogLine("<color=red>Error:</color> Working Directory not set.", false);
        //         return;
        //     }

        //     IsProcessing = true;
        //     _lastLiveLine = "Connecting to repository...";
        //     _hasNewLine = true;

        //     var uiUpdateTask = UpdateUILive();

        //     try
        //     {
        //         SVNLogBridge.LogLine("<b>[SVN]</b> Pre-update cleanup...", false);
        //         await SVNClean.CleanupAsync(targetPath);

        //         string output = await SvnRunner.RunLiveAsync(
        //             "update --accept postpone",
        //             targetPath,
        //             (line) =>
        //             {
        //                 _lastLiveLine = line;
        //                 _hasNewLine = true;
        //             }
        //         );

        //         _hasNewLine = false;

        //         int updatedCount = Regex.Matches(output, @"^[UGADR]\s", RegexOptions.Multiline).Count;

        //         string revision = svnManager.ParseRevision(output);

        //         if (string.IsNullOrEmpty(revision) || revision == "Unknown")
        //         {
        //             string infoOutput = await SvnRunner.GetInfoAsync(targetPath);
        //             revision = ParseRevisionFromInfo(infoOutput);
        //         }

        //         string summary = "<b>[SVN UPDATE COMPLETED]</b>\n";
        //         if (updatedCount > 0)
        //             summary += $"<color=green>Success!</color> Updated <b>{updatedCount}</b> items.\n";
        //         else
        //             summary += "<color=blue>No changes found.</color> Project is up to date.\n";

        //         summary += $"Current Revision: <color=#FFD700><b>{revision}</b></color>\n";
        //         summary += "-----------------------------------";
        //         SVNLogBridge.LogLine(summary, false);

        //         IsProcessing = false;
        //         await svnManager.RefreshStatus();
        //     }
        //     catch (Exception ex)
        //     {
        //         SVNLogBridge.LogError($"[SVN] Update Error: {ex}");
        //         SVNLogBridge.LogLine($"\n<color=red><b>Update Failed:</b></color> {ex.Message}");
        //     }
        //     finally
        //     {
        //         IsProcessing = false;
        //         _hasNewLine = false;
        //     }
        // }

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
                await svnManager.RefreshStatus();
                IsProcessing = false;
            }
        }
    }
}