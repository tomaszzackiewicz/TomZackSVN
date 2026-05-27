using System;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNPollingService : MonoBehaviour
    {
        private int _lastKnownRemoteRevision = -1;

        [Header("Focus Settings")]
        public float focusCheckCooldownSeconds = 3f;
        private float _lastFocusCheckTime = -100f;

        [Header("Logging")]
        public bool showDebugLogs = true;

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

        private async Task CheckForRemoteCommitsAsync()
        {
            try
            {
                string wd = SVNManager.MainThreadWorkingDir;

                if (string.IsNullOrEmpty(wd)) return;

                string revOutput = await SvnRunner.RunAsync("info -r HEAD --show-item last-changed-revision", wd);

                if (int.TryParse(revOutput.Trim(), out int remoteRev))
                {
                    if (_lastKnownRemoteRevision == -1)
                    {
                        _lastKnownRemoteRevision = remoteRev;
                        return;
                    }

                    if (remoteRev > _lastKnownRemoteRevision)
                    {
                        _lastKnownRemoteRevision = remoteRev;

                        string author = await FetchAuthor(wd);
                        string localUser = SVNManager.CachedUserName;

                        if (!string.Equals(author.Trim(), localUser?.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            string commitMsg = await FetchCleanCommitMessage(wd, remoteRev);

                            UnityMainThreadDispatcher.Enqueue(async () =>
                            {
                                if (SVNNotificationAudio.Instance != null)
                                    SVNNotificationAudio.Instance.PlayCommitSound();

                                string message = $"<b>{author}</b> committed changes!\n" +
                                                 $"Revision: <color=yellow>{remoteRev}</color>\n" +
                                                 $"<i>\"{commitMsg}\"</i>";

                                SVNLogBridge.ShowNotification(message);

                                if (SVNManager.Instance != null)
                                    await SVNManager.Instance.RefreshStatus();
                            });
                        }
                        else
                        {
                            if (showDebugLogs) SVNLogBridge.LogLine($"<color=green>[SVN Focus Check]</color> Local commit detected (Rev {remoteRev}).");

                            UnityMainThreadDispatcher.Enqueue(async () =>
                            {
                                if (SVNManager.Instance != null)
                                    await SVNManager.Instance.RefreshStatus();
                            });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SVN Focus Check Error] {e.Message}");
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