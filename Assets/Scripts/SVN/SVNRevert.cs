using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNRevert : SVNBase, IDisposable
    {
        private float _lastRevertAllClickTime = -10f;
        private float _lastRevertSingleClickTime = -10f;
        private CancellationTokenSource _revertCts;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private const int CleanupTimeoutSeconds = 30;
        private int _disposed;

        public SVNRevert(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        private void LogToConsole(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            PostToMainThread(() => SVNLogBridge.LogLine(msg));
        }

        private bool ConfirmAction(float currentTime, ref float lastClickTime, string warningMessage)
        {
            const float ConfirmationWindow = 5f;
            const float MinDoubleClickDelay = 0.30f;

            float elapsed = currentTime - lastClickTime;

            if (elapsed > ConfirmationWindow || lastClickTime < 0f)
            {
                lastClickTime = currentTime;
                LogToConsole(warningMessage);
                return false;
            }

            if (elapsed < MinDoubleClickDelay)
            {
                lastClickTime = currentTime;
                LogToConsole("<color=#FFAA00><b>[Revert]</b></color> Confirmation too fast — press once again.");
                return false;
            }

            lastClickTime = -10f;
            return true;
        }

        // === FIX D2: porażka cleanupu z dowolnego powodu = czytelny komunikat
        // "cleanup blocked" (wcześniej: ogólne "[Revert All] Failed: svn: E155004..."
        // — użytkownik nie wiedział, że to pre-check zablokował, nie revert, padł).
        private async Task<bool> CleanupWorkingCopyAsync(string root, CancellationToken token)
        {
            LogToConsole("<b>[Cleanup]</b> Checking working copy locks...");
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            try
            {
                await SvnRunner.RunAsync("cleanup", root, false, linkedCts.Token).ConfigureAwait(false);
                LogToConsole("<color=green>[Cleanup]</color> Working copy is clean.");
                return true;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                LogToConsole("<color=#FF4444>[Cleanup]</color> Timed out (30s). Aborting operation for safety.");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw; // cancel użytkownika — standardowa ścieżka "Cancelled"
            }
            catch (Exception ex)
            {
                LogToConsole($"<color=#FF4444>[Cleanup]</color> Failed: {ex.Message}\nOperation aborted for safety (nothing was reverted).");
                return false;
            }
        }

        private bool TryGetRelativePath(string root, string path, out string relativePath)
        {
            relativePath = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;

            string normRoot = Path.GetFullPath(root.Trim()).Replace('\\', '/').TrimEnd('/');
            string normInput = path.Replace('\\', '/').Trim();

            try
            {
                string absolutePath = Path.IsPathRooted(normInput)
                    ? Path.GetFullPath(normInput)
                    : Path.GetFullPath(Path.Combine(normRoot, normInput));
                absolutePath = absolutePath.Replace('\\', '/').TrimEnd('/');

                string prefix = normRoot + "/";
                if (!absolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;

                relativePath = absolutePath.Substring(prefix.Length).Replace('\\', '/').Trim('/');
                return !string.IsNullOrWhiteSpace(relativePath);
            }
            catch
            {
                return false;
            }
        }

        // === FIX D1: delayed dispose — pole zdejmowane pod CompareExchange (jak było,
        // poprawnie odcina Cancel), a fizyczny dispose po sekundzie: token bywa jeszcze
        // zarejestrowany w umierającym RefreshStatus/SvnRunner i natychmiastowy
        // dispose dawał tam ODE (nie-OCE!) → fałszywy "[Revert] Failed: safe handle...".
        private void ClearOperationToken(CancellationTokenSource localCts)
        {
            if (localCts == null) return;
            Interlocked.CompareExchange(ref _revertCts, null, localCts);
            _ = Task.Delay(1000).ContinueWith(_ => { try { localCts.Dispose(); } catch { } });
        }

        public async void RevertAll()
        {
            float clickTime = Time.unscaledTime;
            if (!ConfirmAction(clickTime, ref _lastRevertAllClickTime,
                "<color=#FFAA00><b>[Revert All]</b></color> This will discard <b>ALL local changes</b>!\n" +
                "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            bool hasLock = false;
            bool ownsProcessing = false;
            CancellationTokenSource localCts = null;

            try
            {
                hasLock = await _operationLock.WaitAsync(0).ConfigureAwait(false);
                if (!hasLock) return;

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string root = svnManager.WorkingDir;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    LogToConsole("<color=#FFAA00>Error:</color> Working directory does not exist.");
                    return;
                }

                ownsProcessing = true;
                IsProcessing = true;

                localCts = new CancellationTokenSource();
                Volatile.Write(ref _revertCts, localCts);
                CancellationToken token = localCts.Token;

                var opStart = DateTime.UtcNow;
                LogToConsole("<b>[Revert All]</b> Starting revert of all changes...");

                bool cleanupOk = await CleanupWorkingCopyAsync(root, token).ConfigureAwait(false);
                if (!cleanupOk) return;

                token.ThrowIfCancellationRequested();
                LogToConsole("<b>[Revert All]</b> Reverting all local modifications...");

                await SvnRunner.RunAsync(new[] { "revert", "-R", "." }, root, false, token).ConfigureAwait(false);
                svnManager.DiskChangesDetected = true;

                await svnManager.RefreshStatus(force: true).ConfigureAwait(false);

                var durationMs = (long)(DateTime.UtcNow - opStart).TotalMilliseconds;
                LogToConsole($"<color=green><b>[Revert All]</b> Completed. durationMs={durationMs}</color>");
            }
            catch (OperationCanceledException)
            {
                LogToConsole("<color=orange><b>[Revert All]</b> Cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToConsole($"<color=#FFAA00><b>[Revert All]</b> Failed:</color> {ex.Message}");
            }
            finally
            {
                ClearOperationToken(localCts);
                if (ownsProcessing) IsProcessing = false;
                if (hasLock)
                {
                    try { _operationLock.Release(); }
                    catch (SemaphoreFullException) { }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        public async void RevertSingleItem(SvnTreeElement element)
        {
            if (element == null)
            {
                LogToConsole("<color=#FFAA00>Cannot revert: item is null.</color>");
                return;
            }

            // Snapshot pól na main thread (tu jesteśmy — klik z drzewa UI).
            string elementName = element.Name;
            string elementFullPath = element.FullPath;

            float clickTime = Time.unscaledTime;
            if (!ConfirmAction(clickTime, ref _lastRevertSingleClickTime,
                $"<color=#FFAA00><b>[Revert]</b></color> Revert <b>{elementName}</b>?\n" +
                "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            bool hasLock = false;
            bool ownsProcessing = false;
            CancellationTokenSource localCts = null;

            try
            {
                hasLock = await _operationLock.WaitAsync(0).ConfigureAwait(false);
                if (!hasLock) return;

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string root = svnManager.WorkingDir;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    LogToConsole("<color=#FFAA00>Working directory not found.</color>");
                    return;
                }

                ownsProcessing = true;
                IsProcessing = true;

                localCts = new CancellationTokenSource();
                Volatile.Write(ref _revertCts, localCts);
                CancellationToken token = localCts.Token;

                var opStart = DateTime.UtcNow;

                bool cleanupOk = await CleanupWorkingCopyAsync(root, token).ConfigureAwait(false);
                if (!cleanupOk) return;

                if (!TryGetRelativePath(root, elementFullPath, out string safePath))
                {
                    LogToConsole("<color=#FF4444>Invalid path or path outside working copy.</color>");
                    return;
                }

                token.ThrowIfCancellationRequested();
                LogToConsole($"<b>[Revert]</b> Reverting: {safePath}...");

                await SvnRunner.RunAsync(new[] { "revert", "-R", safePath }, root, false, token).ConfigureAwait(false);
                svnManager.DiskChangesDetected = true;

                await svnManager.RefreshStatus(force: true).ConfigureAwait(false);

                var durationMs = (long)(DateTime.UtcNow - opStart).TotalMilliseconds;
                LogToConsole($"<color=green><b>[Revert]</b> Reverted: {elementName} durationMs={durationMs}</color>");
            }
            catch (OperationCanceledException)
            {
                LogToConsole("<color=orange><b>[Revert]</b> Cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToConsole($"<color=#FFAA00><b>[Revert]</b> Failed:</color> {ex.Message}");
            }
            finally
            {
                ClearOperationToken(localCts);
                if (ownsProcessing) IsProcessing = false;
                if (hasLock)
                {
                    try { _operationLock.Release(); }
                    catch (SemaphoreFullException) { }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        public void CancelRevert()
        {
            try
            {
                var cts = Volatile.Read(ref _revertCts);
                if (cts == null || cts.IsCancellationRequested) return;
                cts.Cancel();
                LogToConsole("<color=orange><b>[Revert]</b> Cancel requested...</color>");
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                LogToConsole($"<color=#FFAA00>[Revert] Error during cancel:</color> {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            CancelRevert();

            // === FIX D3: grace period na dojście operacji do finally (Release/
            // RefreshStatus) po Cancel — dopiero potem dispose semafora
            // (wzorzec z SVNMissing).
            _ = Task.Delay(1500).ContinueWith(_ =>
            {
                try { _operationLock.Dispose(); } catch { }
            });

            GC.SuppressFinalize(this);
        }
    }
}