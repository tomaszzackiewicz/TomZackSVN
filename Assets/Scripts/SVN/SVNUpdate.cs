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
                if (svnUI.LogText != null)
                    svnUI.LogText.text = "<color=red>Error:</color> Working Directory not set.";
                return;
            }

            IsProcessing = true;
            _lastLiveLine = "Connecting to repository...";
            _hasNewLine = true;

            var uiUpdateTask = UpdateUILive();

            try
            {
                Debug.Log($"[SVN] Starting update for: {targetPath}");

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
                    revision = svnManager.ParseRevisionFromInfo(infoOutput);
                }

                if (svnUI.LogText != null)
                {
                    string summary = "<b>[SVN UPDATE COMPLETED]</b>\n";
                    if (updatedCount > 0)
                        summary += $"<color=green>Success!</color> Updated <b>{updatedCount}</b> items.\n";
                    else
                        summary += "<color=blue>No changes found.</color> Project is up to date.\n";

                    summary += $"Current Revision: <color=#FFD700><b>{revision}</b></color>\n";
                    summary += "-----------------------------------";
                    svnUI.LogText.text = summary;
                }
                IsProcessing = false;
                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Update Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text += $"\n<color=red><b>Update Failed:</b></color> {ex.Message}";
            }
            finally
            {
                IsProcessing = false;
                _hasNewLine = false;
            }
        }

        private async Task UpdateUILive()
        {
            while (IsProcessing)
            {
                if (_hasNewLine && svnUI.LogText != null)
                {
                    svnUI.LogText.text = $"<b>[SVN]</b> Updating: <color=orange>{_lastLiveLine}</color>";
                    _hasNewLine = false;
                }
                await Task.Yield();
            }
        }
    }
}