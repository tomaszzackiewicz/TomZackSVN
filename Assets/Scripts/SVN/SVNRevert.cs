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

        private void RunOnMainThread(Action action)
        {
            if (_mainThreadContext != null)
                _mainThreadContext.Post(_ => { try { action(); } catch (Exception ex) { Debug.LogException(ex); } }, null);
        }

        private void LogToConsole(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            SVNLogBridge.LogLine(msg);
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
                LogToConsole("<color=#FFAA00><b>[Revert]</b></color> Confirmation too fast — press the button once again.");
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
                LogToConsole("<color=green>Working copy is clean.</color>");
                return true;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                LogToConsole("<color=#FFAA00>Cleanup timed out (30s). Proceeding anyway...</color>");
                return false;
            }
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
                    "<color=#FFAA00><b>[Revert All]</b></color> Are you sure? This will discard <b>ALL local changes</b>!\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                {
                    if (hasLock) _operationLock.Release();
                    return;
                }

                string root = svnManager.WorkingDir;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    LogToConsole("<color=#FFAA00>Error:</color> Working directory does not exist.");
                    return;
                }

                IsProcessing = true;
                var token = new CancellationTokenSource().Token;

                LogToConsole("<color=#4FC3F7><b>[SVN]</b> Starting revert of all changes...</color>");

                // Zamiast niebezpiecznego WaitForSemaphoreFreeAsync, robimy asynchroniczny cleanup
                await CleanupWorkingCopyAsync(root, token);

                // Usunięto retryOnLock: true, bo cleanup załatwił już blokady
                await SvnRunner.RunAsync("revert -R .", root, false, token);

                svnManager._diskChangesDetected = true;
                var status = svnManager.GetModule<SVNStatus>();
                status?.ClearCurrentData();
                ClearAllUI();
                await svnManager.RefreshStatus(force: true);

                LogToConsole("<color=green><b>[SVN]</b> Revert completed successfully.</color>");
            }
            catch (OperationCanceledException)
            {
                LogToConsole("<color=orange><b>[SVN]</b> Revert cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToConsole($"<color=#FFAA00><b>[SVN]</b> Revert failed:</color>\n{ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                if (hasLock) _operationLock.Release();
            }
        }

        public void CancelRevert()
        {
            if (_revertCts?.IsCancellationRequested == false)
            {
                try { _revertCts?.Cancel(); } catch { }
                LogToConsole("<color=orange><b>[Revert]</b></color> Cancel requested...");
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
                    $"<color=#FFAA00><b>[Revert]</b></color> Are you sure you want to revert <b>{element.Name}</b>?\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                {
                    if (hasLock) _operationLock.Release();
                    return;
                }

                string root = svnManager.WorkingDir;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    LogToConsole("<color=#FFAA00>Working directory not found.</color>");
                    return;
                }

                IsProcessing = true;
                var token = new CancellationTokenSource().Token;

                await CleanupWorkingCopyAsync(root, token);

                string safePath = SvnRunner.NormalizeRepositoryPath(element.FullPath);
                LogToConsole($"<b>Reverting:</b> {safePath}");

                // Usunięto retryOnLock: true
                await SvnRunner.RunAsync($"revert \"{safePath}\"", root, false, token);

                svnManager._diskChangesDetected = true;
                var status = svnManager.GetModule<SVNStatus>();
                status?.ClearCurrentData();
                ClearAllUI();
                await svnManager.RefreshStatus(force: true);

                LogToConsole($"<color=green>Successfully reverted:</color> {element.Name}");
            }
            catch (OperationCanceledException)
            {
                LogToConsole("<color=orange>[Revert]</color> Operation cancelled.");
            }
            catch (Exception ex)
            {
                LogToConsole($"<color=#FFAA00>Revert Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                if (hasLock) _operationLock.Release();
            }
        }
    }
}