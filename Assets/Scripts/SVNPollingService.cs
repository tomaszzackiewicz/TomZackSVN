using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNPollingService : MonoBehaviour
    {
        private int _lastKnownRemoteRevision = -1;
        private string _lastValidWorkingDir = "";

        [Header("Focus Settings")]
        public float focusCheckCooldownSeconds = 180f;
        private float _lastFocusCheckTime = -100f;

        [Header("Logging")]
        public bool showDebugLogs = false;

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                float currentTime = Time.realtimeSinceStartup;
                if (currentTime - _lastFocusCheckTime >= focusCheckCooldownSeconds)
                {
                    _lastFocusCheckTime = currentTime;
                    _ = CheckForRemoteCommitsAsync();
                }
            }
        }

        public void ResetRevisionTracking()
        {
            _lastKnownRemoteRevision = -1;
            _lastValidWorkingDir = "";
        }

        public async Task CheckForRemoteCommitsAsync()
        {
            try
            {
                SVNManager manager = SVNManager.Instance;
                if (manager == null)
                    return;

                string wd = manager.WorkingDir;
                if (string.IsNullOrEmpty(wd))
                    return;

                if (!Directory.Exists(wd) || !Directory.Exists(Path.Combine(wd, ".svn")))
                {
                    if (showDebugLogs)
                        SVNLogBridge.LogLine("<color=grey>[Polling]</color> Skipped – no valid working copy.");
                    return;
                }

                if (!string.Equals(wd, _lastValidWorkingDir, StringComparison.OrdinalIgnoreCase))
                {
                    _lastKnownRemoteRevision = -1;
                    _lastValidWorkingDir = wd;
                }

                await manager.CancelBackgroundTasksAsync();

                string revOutput = await SvnRunner.RunAsync(
                    "info -r HEAD --show-item last-changed-revision", wd);

                if (!int.TryParse(revOutput.Trim(), out int remoteRev))
                    return;

                if (_lastKnownRemoteRevision == -1)
                {
                    _lastKnownRemoteRevision = remoteRev;
                    return;
                }

                if (remoteRev > _lastKnownRemoteRevision)
                {
                    _lastKnownRemoteRevision = remoteRev;

                    string author = await FetchAuthor(wd);
                    string localUser = manager.CurrentUserName;

                    if (!string.Equals(author.Trim(), localUser?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        string commitMsg = await FetchCleanCommitMessage(wd, remoteRev);

                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            if (SVNNotificationAudio.Instance != null)
                                SVNNotificationAudio.Instance.PlayCommitSound();

                            SVNLogBridge.ShowNotification(
                                $"<b>{author}</b> committed changes!\n" +
                                $"Revision: <color=yellow>{remoteRev}</color>\n" +
                                $"<i>\"{commitMsg}\"</i>");

                            _ = manager.RefreshStatus();
                        });
                    }
                    else
                    {
                        if (showDebugLogs)
                            SVNLogBridge.LogLine($"<color=green>[Polling]</color> Local commit detected (Rev {remoteRev}).");

                        UnityMainThreadDispatcher.Enqueue(() => _ = manager.RefreshStatus());
                    }
                }
            }
            catch (Exception e)
            {
                SVNLogBridge.LogError($"[SVN Polling Error] {e.Message}");
            }
        }

        private async Task<string> FetchAuthor(string wd)
        {
            try
            {
                string output = await SvnRunner.RunAsync("info -r HEAD --show-item last-changed-author", wd);
                return string.IsNullOrWhiteSpace(output) ? "Someone" : output.Trim();
            }
            catch { return "Someone"; }
        }

        private async Task<string> FetchCleanCommitMessage(string wd, int rev)
        {
            try
            {
                string logOutput = await SvnRunner.RunAsync($"log -r {rev} --incremental", wd);
                if (string.IsNullOrWhiteSpace(logOutput)) return "No message.";

                string[] lines = logOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                string cleanMsg = (lines.Length > 1) ? string.Join(" ", lines, 1, lines.Length - 1).Trim() : lines[0].Trim();
                return cleanMsg.Length > 120 ? cleanMsg.Substring(0, 117) + "..." : cleanMsg;
            }
            catch { return "No message."; }
        }
    }
}