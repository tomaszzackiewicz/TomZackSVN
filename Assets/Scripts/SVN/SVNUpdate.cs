using System;
using UnityEngine;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNUpdate : SVNBase
    {
        private string _lastLiveLine = string.Empty;
        private bool _hasNewLine = false;

        public SVNUpdate(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void Update()
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

            var uiUpdateTask = UpdateUILive();

            try
            {
                SVNLogBridge.LogLine("<b>[SVN]</b> Pre-update cleanup...", false);
                await SVNClean.CleanupAsync(targetPath);

                string output = await SvnRunner.RunLiveAsync(
                    "update --accept postpone",
                    targetPath,
                    (line) =>
                    {
                        _lastLiveLine = line;
                        _hasNewLine = true;
                    }
                );

                _hasNewLine = false;

                int updatedCount = Regex.Matches(output, @"^[UGADR]\s", RegexOptions.Multiline).Count;

                string revision = svnManager.ParseRevision(output);

                if (string.IsNullOrEmpty(revision) || revision == "Unknown")
                {
                    string infoOutput = await SvnRunner.GetInfoAsync(targetPath);
                    revision = ParseRevisionFromInfo(infoOutput);
                }

                string summary = "<b>[SVN UPDATE COMPLETED]</b>\n";
                if (updatedCount > 0)
                    summary += $"<color=green>Success!</color> Updated <b>{updatedCount}</b> items.\n";
                else
                    summary += "<color=blue>No changes found.</color> Project is up to date.\n";

                summary += $"Current Revision: <color=#FFD700><b>{revision}</b></color>\n";
                summary += "-----------------------------------";
                SVNLogBridge.LogLine(summary, false);

                IsProcessing = false;
                await Task.Delay(500);
                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Update Error: {ex}");
                SVNLogBridge.LogLine($"\n<color=red><b>Update Failed:</b></color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _hasNewLine = false;
            }
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

        public void CheckRemoteModifications()
        {
            var updateModule = svnManager.GetModule<SVNUpdate>();

            _ = updateModule.UpdateRemoteModifications();
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
                Debug.LogError($"[SVN] {errorMsg}");
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, errorMsg, "REMOTE", true);
            }
            finally
            {
                IsProcessing = false;
                await svnManager.RefreshStatus();
            }
        }
    }
}