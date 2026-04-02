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
            if (showDebugLogs) SVNLogBridge.LogLine("<color=blue>[SVN Polling]</color> Started.");
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
                    // 1. POBIERANIE DANYCH Z UNITY MUSI BYĆ BEZPIECZNE
                    // Najlepiej pobrać WorkingDir raz lub przez bezpieczną właściwość
                    string wd = "";

                    // Używamy Dispatchera lub sprawdzamy, czy możemy bezpiecznie pobrać ścieżkę
                    // Jeśli SVNManager.Instance jest statyczny i nie dotyka MonoBehaviour w getterze, 
                    // to zadziała, ale bezpieczniej jest to zrobić tak:
                    wd = SVNManager.MainThreadWorkingDir; // Dodaj taką statyczną zmienną w SVNManager

                    if (string.IsNullOrEmpty(wd))
                    {
                        await Task.Delay(5000, token);
                        continue;
                    }

                    // 2. OPERACJA SIECIOWA (SVN) - To może być w tle
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
                            // Pobierz nazwę użytkownika z cache'u, nie z obiektu Unity bezpośrednio w tym wątku
                            string localUser = SVNManager.CachedUserName;

                            if (!string.Equals(author.Trim(), localUser?.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                string commitMsg = await FetchCleanCommitMessage(wd, remoteRev);

                                // 3. WSZYSTKO CO DOTYKA UI LUB KOMPONENTÓW UNITY WRACA DO GŁÓWNEGO WĄTKU
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
                                if (showDebugLogs) SVNLogBridge.LogLine($"<color=green>[SVN Polling]</color> Local commit detected (Rev {remoteRev}).");

                                UnityMainThreadDispatcher.Enqueue(async () =>
                                {
                                    if (SVNManager.Instance != null)
                                        await SVNManager.Instance.RefreshStatus();
                                });
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { /* Ignorujemy przy zamykaniu */ }
                catch (Exception e)
                {
                    // Tutaj nie używamy Debug.Log bezpośrednio, jeśli SVNLogBridge dotyka MonoBehaviour
                    // Ale zwykły Debug.Log w nowszych wersjach Unity jest thread-safe.
                    Debug.LogError($"[SVN Polling Error] {e.Message}");
                }

                // Czekamy przed następnym sprawdzeniem
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