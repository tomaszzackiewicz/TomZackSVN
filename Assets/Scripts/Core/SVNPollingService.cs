using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    /// <summary>
    /// Polling SVN — FOCUS + ON-DEMAND (commity/rewizje).
    ///
    /// === ARCHITEKTURA:
    ///  - Zero timerów/pętli: w spoczynku zero CPU i zero alokacji — budzi
    ///    się wyłącznie na focus (cooldown) i na żądanie (manager/Repair).
    ///  - Akcje lokalne są event-driven; locki/protection przy focusie
    ///    odświeża SVNManager.FocusRefreshAsync → PollRepositoryStateAsync
    ///    (poll rewizji jest na locki ślepy — lock nie bumpuje HEAD).
    ///
    /// === FIX (double focus handler): usunięty OnApplicationFocus z tej klasy.
    /// Wcześniej DWA MonoBehaviour'y reagowały na powrót do okna:
    ///   - SVNManager.OnApplicationFocus → FocusRefreshAsync → CheckForRemoteCommitsAsync
    ///   - SVNPollingService.OnApplicationFocus → CheckForRemoteCommitsAsync (bezpośrednio)
    /// Oba odpalały w tej samej klatce, każdy z własnym cooldownem (10s vs 180s).
    /// Single-flight w CheckForRemoteCommitsAsync ratował przed duplikatem, ale drugie
    /// wywołanie CICHO się kończyło — jeśli pierwsze padło na IsPaused, drugie też.
    /// Teraz jedyna ścieżka: SVNManager.OnApplicationFocus → FocusRefreshAsync.
    ///
    /// === PUBLIC API (callery: SVNCheckout, SVNTerminal, SVNRepoRepair):
    ///  - ResetRevisionTracking() — po nowym checkoucie/reparacji.
    ///  - CancelCurrentCheck() — anuluje biegnący check.
    ///  - IsPaused — pauza usługi.
    /// </summary>
    public class SVNPollingService : MonoBehaviour
    {
        private int _lastKnownRemoteRevision = -1;
        private string _lastValidWorkingDir = "";
        private int _isCheckingFlag;
        private CancellationTokenSource _lifetimeCts;
        private CancellationTokenSource _currentCheckCts;

        public bool IsPaused { get; set; } = false;

        [Header("Logging")]
        [Tooltip("Log debug info to file (not UI console)")]
        public bool showDebugLogs = false;

        private void Awake()
        {
            _lifetimeCts = new CancellationTokenSource();
        }

        private void OnDestroy()
        {
            _lifetimeCts?.Cancel();
            var lifetime = _lifetimeCts;
            _lifetimeCts = null;
            if (lifetime != null)
                _ = Task.Delay(1000).ContinueWith(_ => { try { lifetime.Dispose(); } catch { } });

            var current = Interlocked.Exchange(ref _currentCheckCts, null);
            if (current != null)
            {
                try { current.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { current.Dispose(); } catch { } });
            }
        }

        // ================= PUBLIC API (dla modułów) =================

        public void ResetRevisionTracking()
        {
            _lastKnownRemoteRevision = -1;
            _lastValidWorkingDir = "";
        }

        public void CancelCurrentCheck()
        {
            var current = Volatile.Read(ref _currentCheckCts);
            if (current != null)
            {
                try { current.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        // === single-flight + lifetime + token zewnętrzny + per-check CTS ===

        public async Task CheckForRemoteCommitsAsync(CancellationToken token = default)
        {
            if (IsPaused) return;

            if (Interlocked.Exchange(ref _isCheckingFlag, 1) == 1)
                return;

            var localCts = new CancellationTokenSource();

            var prevCts = Interlocked.Exchange(ref _currentCheckCts, localCts);
            if (prevCts != null)
            {
                try { prevCts.Cancel(); } catch { }
                try { prevCts.Dispose(); } catch { }
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    token, _lifetimeCts?.Token ?? CancellationToken.None, localCts.Token);

                await CheckForRemoteCommitsCoreAsync(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.CompareExchange(ref _currentCheckCts, null, localCts);
                Interlocked.Exchange(ref _isCheckingFlag, 0);

                try { localCts.Dispose(); } catch { }
            }
        }

        // ================= CORE =================

        private async Task CheckForRemoteCommitsCoreAsync(CancellationToken token)
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

                if (!int.TryParse((revOutput ?? "").Trim(), out int remoteRev))
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

        // ================= POMOCNICZE =================

        private async Task<string> FetchAuthor(string wd, CancellationToken token)
        {
            try
            {
                string output = await SvnRunner.RunAsync(
                    "info -r HEAD --show-item last-changed-author", wd, false, token).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(output) ? "Someone" : output.Trim();
            }
            catch { return "Someone"; }
        }

        private async Task<string> FetchCleanCommitMessage(string wd, int rev, CancellationToken token)
        {
            try
            {
                string logOutput = await SvnRunner.RunAsync(
                    $"log -r {rev} --incremental", wd, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(logOutput)) return "No message.";

                string[] lines = logOutput.Split(
                    new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
                string cleanMsg = (lines.Length > 1)
                    ? string.Join(" ", lines, 1, lines.Length - 1).Trim()
                    : lines[0].Trim();
                return cleanMsg.Length > 120 ? cleanMsg.Substring(0, 117) + "..." : cleanMsg;
            }
            catch { return "No message."; }
        }
    }
}