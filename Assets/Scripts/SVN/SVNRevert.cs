using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNRevert : SVNBase
    {
        private float _lastRevertAllClickTime = -10f;
        private float _lastRevertSingleClickTime = -10f;
        private CancellationTokenSource _revertCts;

        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private const int CleanupTimeoutSeconds = 30;

        private readonly SynchronizationContext _mainThreadContext;

        public SVNRevert(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
        }

        private string TsTag() => $"<color=#9CA3AF>[{DateTime.Now:HH:mm:ss}]</color>";

        private void RunOnMainThread(Action action)
        {
            if (_mainThreadContext != null)
                _mainThreadContext.Post(_ => { try { action(); } catch (Exception ex) { Debug.LogException(ex); } }, null);
        }

        private void LogToConsole(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            SVNLogBridge.LogLine($"{TsTag()} {msg}");
        }

        private void ClearAllUI()
        {
            RunOnMainThread(() =>
            {
                svnUI?.SvnTreeView?.ClearView();
                svnUI?.SVNCommitTreeDisplay?.ClearView();
                if (svnUI?.TreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                if (svnUI?.CommitTreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
            });
        }

        private bool ConfirmAction(ref float lastClickTime, string warningMessage)
        {
            const float ConfirmationWindow = 5f;
            const float MinDoubleClickDelay = 0.30f;
            float now = Time.unscaledTime;
            float elapsed = now - lastClickTime;

            if (elapsed > ConfirmationWindow || lastClickTime < 0f)
            {
                lastClickTime = now;
                LogToConsole(warningMessage);
                return false;
            }

            if (elapsed < MinDoubleClickDelay)
            {
                lastClickTime = now;
                LogToConsole("<color=#FFAA00><b>[Revert]</b></color> Confirmation too fast — press once again.");
                return false;
            }

            lastClickTime = -10f;
            return true;
        }

        private async Task<bool> CleanupWorkingCopyAsync(string root, CancellationToken token)
        {
            LogToConsole("<b>[Cleanup]</b> Checking working copy locks...");
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            try
            {
                await SvnRunner.RunAsync("cleanup", root, false, linkedCts.Token);
                LogToConsole("<color=green>[Cleanup]</color> Working copy is clean.");
                return true;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                LogToConsole("<color=#FFAA00>[Cleanup]</color> Timed out (30s), proceeding...");
                return false;
            }
        }

        private CancellationToken ResetAndGetToken()
        {
            var old = Interlocked.Exchange(ref _revertCts, null);
            old?.Dispose();
            _revertCts = new CancellationTokenSource();
            return _revertCts.Token;
        }

        public async void RevertAll()
        {
            bool hasLock = false;
            try
            {
                hasLock = await _operationLock.WaitAsync(0);
                if (!hasLock || IsProcessing) return;

                await svnManager.CancelBackgroundTasksAsync();

                if (!ConfirmAction(ref _lastRevertAllClickTime,
                    "<color=#FFAA00><b>[Revert All]</b></color> This will discard <b>ALL local changes</b>!\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                {
                    if (hasLock) _operationLock.Release();
                    return;
                }

                string root = svnManager.WorkingDir;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    LogToConsole("<color=#FFAA00>Error:</color> Working directory does not exist.");
                    if (hasLock) _operationLock.Release();
                    return;
                }

                IsProcessing = true;
                var token = ResetAndGetToken();
                var opStart = DateTime.UtcNow;

                LogToConsole("<b>[Revert All]</b> Starting revert of all changes...");
                await CleanupWorkingCopyAsync(root, token);

                LogToConsole("<b>[Revert All]</b> Reverting all local modifications...");
                string result = await SvnRunner.RunAsync("revert -R .", root, false, token);

                svnManager.DiskChangesDetected = true;
                var status = svnManager.GetModule<SVNStatus>();
                status?.ClearCurrentData();
                ClearAllUI();
                await svnManager.RefreshStatus(force: true);

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
                _revertCts?.Dispose();
                _revertCts = null;
                IsProcessing = false;
                if (hasLock) _operationLock.Release();
            }
        }

        public async void RevertSingleItem(SvnTreeElement element)
        {
            bool hasLock = false;
            try
            {
                hasLock = await _operationLock.WaitAsync(0);
                if (!hasLock || IsProcessing) return;

                await svnManager.CancelBackgroundTasksAsync();

                if (!ConfirmAction(ref _lastRevertSingleClickTime,
                    $"<color=#FFAA00><b>[Revert]</b></color> Revert <b>{element.Name}</b>?\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                {
                    if (hasLock) _operationLock.Release();
                    return;
                }

                string root = svnManager.WorkingDir;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    LogToConsole("<color=#FFAA00>Working directory not found.</color>");
                    if (hasLock) _operationLock.Release();
                    return;
                }

                IsProcessing = true;
                var token = ResetAndGetToken();
                var opStart = DateTime.UtcNow;

                await CleanupWorkingCopyAsync(root, token);

                string safePath = SvnRunner.NormalizeRepositoryPath(element.FullPath);
                LogToConsole($"<b>[Revert]</b> Reverting: {safePath}...");
                await SvnRunner.RunAsync($"revert \"{safePath}\"", root, false, token);

                svnManager.DiskChangesDetected = true;
                var status = svnManager.GetModule<SVNStatus>();
                status?.ClearCurrentData();
                ClearAllUI();
                await svnManager.RefreshStatus(force: true);

                var durationMs = (long)(DateTime.UtcNow - opStart).TotalMilliseconds;
                LogToConsole($"<color=green><b>[Revert]</b> Reverted: {element.Name} durationMs={durationMs}</color>");
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
                _revertCts?.Dispose();
                _revertCts = null;
                IsProcessing = false;
                if (hasLock) _operationLock.Release();
            }
        }

        public void CancelRevert()
        {
            var cts = _revertCts;
            if (cts?.IsCancellationRequested == false)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }

                LogToConsole("<color=orange><b>[Revert]</b> Cancel requested...</color>");
            }
        }
    }
}