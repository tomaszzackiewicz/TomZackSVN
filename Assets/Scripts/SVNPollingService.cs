using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNPollingService : MonoBehaviour
    {
        private int _lastKnownRemoteRevision = -1;
        private string _lastValidWorkingDir = "";
        private int _isCheckingFlag;
        private CancellationTokenSource _lifetimeCts;

        public bool IsPaused { get; set; } = false;

        [Header("Focus Settings")]
        public float focusCheckCooldownSeconds = 180f;
        private float _lastFocusCheckTime = -100f;

        [Header("Logging")]
        [Tooltip("Log debug info to file (not UI console)")]
        public bool showDebugLogs = false;

        private void Awake()
        {
            _lifetimeCts = new CancellationTokenSource();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus || IsPaused) return;

            float currentTime = Time.realtimeSinceStartup;
            if (currentTime - _lastFocusCheckTime < focusCheckCooldownSeconds)
                return;

            if (Interlocked.Exchange(ref _isCheckingFlag, 1) == 1)
                return;

            _lastFocusCheckTime = currentTime;
            _ = CheckForRemoteCommitsAsync(_lifetimeCts.Token).ContinueWith(_ =>
                Interlocked.Exchange(ref _isCheckingFlag, 0));
        }

        private void OnDestroy()
        {
            _lifetimeCts?.Cancel();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
        }

        public void ResetRevisionTracking()
        {
            _lastKnownRemoteRevision = -1;
            _lastValidWorkingDir = "";
        }

        public void CancelCurrentCheck()
        {
            if (_lifetimeCts != null)
            {
                _lifetimeCts.Cancel();
                _lifetimeCts.Dispose();
            }

            _lifetimeCts = new CancellationTokenSource();

            Interlocked.Exchange(ref _isCheckingFlag, 0);
        }

        public async Task CheckForRemoteCommitsAsync(CancellationToken token = default)
        {
            try
            {
                SVNManager manager = SVNManager.Instance;
                if (manager == null) return;

                string wd = manager.WorkingDir;
                if (string.IsNullOrEmpty(wd)) return;

                if (!Directory.Exists(wd) || !Directory.Exists(Path.Combine(wd, ".svn")))
                {
                    if (showDebugLogs)
                        SVNLogBridge.LogToFile("[Polling] Skipped – no valid working copy.", "POLLING");
                    return;
                }

                if (!string.Equals(wd, _lastValidWorkingDir, StringComparison.OrdinalIgnoreCase))
                {
                    _lastKnownRemoteRevision = -1;
                    _lastValidWorkingDir = wd;
                }

                await manager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string revOutput = await SvnRunner.RunAsync(
                    "info -r HEAD --show-item last-changed-revision", wd, false, token).ConfigureAwait(false);

                if (!int.TryParse(revOutput.Trim(), out int remoteRev))
                    return;

                if (_lastKnownRemoteRevision == -1)
                {
                    _lastKnownRemoteRevision = remoteRev;
                    return;
                }

                if (remoteRev <= _lastKnownRemoteRevision)
                    return;

                _lastKnownRemoteRevision = remoteRev;

                string author = await FetchAuthor(wd, token).ConfigureAwait(false);
                string localUser = manager.CurrentUserName;

                if (this == null) return;

                if (!string.Equals(author.Trim(), localUser?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    string commitMsg = await FetchCleanCommitMessage(wd, remoteRev, token).ConfigureAwait(false);

                    if (this == null) return;

                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        if (this == null) return;

                        if (SVNNotificationAudio.Instance != null)
                            SVNNotificationAudio.Instance.PlayCommitSound();

                        SVNLogBridge.ShowNotification(
                            $"<b>{author}</b> committed changes!\n" +
                            $"Revision: <color=yellow>{remoteRev}</color>\n" +
                            $"<i>\"{commitMsg}\"</i>");

                        if (SVNManager.Instance != null)
                            _ = SVNManager.Instance.RefreshStatus();
                    });
                }
                else
                {
                    if (showDebugLogs)
                        SVNLogBridge.LogToFile($"[Polling] Local commit detected (Rev {remoteRev}).", "POLLING");

                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        if (this == null) return;
                        if (SVNManager.Instance != null)
                            _ = SVNManager.Instance.RefreshStatus();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                if (showDebugLogs)
                    SVNLogBridge.LogToFile("[Polling] Cancelled.", "POLLING");
            }
            catch (Exception e)
            {
                SVNLogBridge.LogToFile($"[SVN Polling Error] {e.Message}", "ERROR");
            }
        }

        private async Task<string> FetchAuthor(string wd, CancellationToken token)
        {
            try
            {
                string output = await SvnRunner.RunAsync("info -r HEAD --show-item last-changed-author", wd, false, token).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(output) ? "Someone" : output.Trim();
            }
            catch { return "Someone"; }
        }

        private async Task<string> FetchCleanCommitMessage(string wd, int rev, CancellationToken token)
        {
            try
            {
                string logOutput = await SvnRunner.RunAsync($"log -r {rev} --incremental", wd, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(logOutput)) return "No message.";

                string[] lines = logOutput.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
                string cleanMsg = (lines.Length > 1) ? string.Join(" ", lines, 1, lines.Length - 1).Trim() : lines[0].Trim();
                return cleanMsg.Length > 120 ? cleanMsg.Substring(0, 117) + "..." : cleanMsg;
            }
            catch { return "No message."; }
        }
    }
}