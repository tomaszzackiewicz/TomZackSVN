using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNPollingService : MonoBehaviour
    {
        private CancellationTokenSource _cts;
        private bool _isPolling = false;
        private int _lastKnownRemoteRevision = -1;

        [Header("Settings")]
        public float pollIntervalMinutes = 1f;
        public bool showDebugLogs = true;

        public void StartPolling(SVNStatus statusModule)
        {
            if (_isPolling) return;
            _cts = new CancellationTokenSource();
            _isPolling = true;
            Task.Run(() => PollLoop(_cts.Token));
            if (showDebugLogs) Debug.Log("<color=blue>[SVN Polling]</color> Started.");
        }

        public void StopPolling()
        {
            if (!_isPolling) return;
            _isPolling = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private async Task PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isPolling)
            {
                try
                {
                    string wd = SVNManager.Instance != null ? SVNManager.Instance.WorkingDir : string.Empty;
                    if (string.IsNullOrEmpty(wd)) { await Task.Delay(5000, token); continue; }

                    string revOutput = await SvnRunner.RunAsync("info -r HEAD --show-item last-changed-revision", wd);

                    if (int.TryParse(revOutput.Trim(), out int remoteRev))
                    {
                        if (_lastKnownRemoteRevision == -1)
                        {
                            _lastKnownRemoteRevision = remoteRev;
                        }
                        else if (remoteRev > _lastKnownRemoteRevision)
                        {
                            _lastKnownRemoteRevision = remoteRev;

                            string author = await FetchAuthor(wd);

                            string localUser = SVNManager.Instance != null ? SVNManager.Instance.CurrentUserName : string.Empty;

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
                                if (showDebugLogs) Debug.Log($"<color=green>[SVN Polling]</color> Local commit detected (Rev {remoteRev}). Skipping notification.");

                                UnityMainThreadDispatcher.Enqueue(async () =>
                                {
                                    if (SVNManager.Instance != null)
                                        await SVNManager.Instance.RefreshStatus();
                                });
                            }
                        }
                    }
                }
                catch (Exception e) { if (showDebugLogs) Debug.LogWarning($"[SVN Polling] {e.Message}"); }

                await Task.Delay(TimeSpan.FromMinutes(pollIntervalMinutes), token);
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

        private void OnDestroy() => StopPolling();
    }
}