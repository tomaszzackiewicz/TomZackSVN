using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using UnityEngine;

namespace SVN.Core
{
    public class SVNRevert : SVNBase
    {
        private float _lastRevertAllClickTime = -10f;
        private float _lastRevertSingleClickTime = -10f;
        private CancellationTokenSource _revertCts;

        public SVNRevert(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private void LogToConsole(string msg)
        {
            SVNLogBridge.LogLine(msg);
            if (svnUI.CommitConsoleContent != null)
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, msg, "REVERT", append: true);
        }

        private void ClearAllUI()
        {
            if (svnUI.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
            if (svnUI.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();
            if (svnUI.TreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
            if (svnUI.CommitTreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
        }

        private bool ConfirmAction(ref float lastClickTime, string warningMessage)
        {
            float timeSinceLastClick = Time.time - lastClickTime;

            if (timeSinceLastClick > 5f)
            {
                lastClickTime = Time.time;
                LogToConsole(warningMessage);
                return false;
            }

            if (timeSinceLastClick < 0.3f)
            {
                lastClickTime = Time.time;
                LogToConsole("<color=#FFAA00><b>[Revert]</b></color> Confirmation too fast – press again to confirm.");
                return false;
            }

            lastClickTime = -10f;
            return true;
        }

        public async void RevertAll()
        {
            if (IsProcessing) return;

            await svnManager.CancelBackgroundTasksAsync();

            if (!ConfirmAction(ref _lastRevertAllClickTime,
                    "<color=#FFAA00><b>[Revert All]</b></color> Are you sure? This will discard <b>ALL local changes</b>!\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root))
            {
                LogToConsole("<color=red>Error:</color> Working directory not set.");
                return;
            }

            IsProcessing = true;
            _revertCts = new CancellationTokenSource();
            var token = _revertCts.Token;

            LogToConsole("<b>Starting Revert process...</b>");

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                token.ThrowIfCancellationRequested();

                var filesToRevert = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && "MADRC".Contains(x.Value.status))
                    .Select(x => Path.GetFullPath(Path.Combine(root, x.Key).Replace("/", "\\")))
                    .ToArray();

                if (filesToRevert.Length == 0)
                {
                    LogToConsole("<color=yellow>No local changes detected to revert.</color>");
                    return;
                }

                await RevertWorkingCopyAsync(root, msg => LogToConsole($"<color=green>[Progress]</color> {msg}"), token);

                svnManager._diskChangesDetected = true;

                var statusModule = svnManager.GetModule<SVNStatus>();
                statusModule?.ClearSVNTreeView();
                statusModule?.ClearCurrentData();
                ClearAllUI();

                LogToConsole($"<color=green><b>SUCCESS!</b></color> Reverted <b>{filesToRevert.Length}</b> files.");
                LogToConsole("<color=#4FC3F7>Refreshing SVN status...</color>");

                await svnManager.RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                LogToConsole("<color=orange>[Revert]</color> Operation cancelled by user.");
            }
            catch (Exception ex)
            {
                LogToConsole($"<color=red>Revert Error:</color> {ex.Message}");
            }
            finally
            {
                _revertCts?.Dispose();
                _revertCts = null;
                IsProcessing = false;
            }
        }

        public void CancelRevert()
        {
            if (_revertCts != null && IsProcessing)
            {
                LogToConsole("<color=orange><b>[Revert]</b></color> Cancel requested...");
                _revertCts.Cancel();
            }
        }

        public async void RevertSingleItem(SvnTreeElement element)
        {
            if (IsProcessing || element == null) return;

            await svnManager.CancelBackgroundTasksAsync();

            if (!ConfirmAction(ref _lastRevertSingleClickTime,
                    $"<color=#FFAA00><b>[Revert]</b></color> Are you sure you want to revert <b>{element.Name}</b>?\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root)) return;

            IsProcessing = true;

            _revertCts = new CancellationTokenSource();
            var token = _revertCts.Token;

            SVNLogBridge.LogLine($"<b>Reverting:</b> {element.Name}...");

            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                await SvnRunner.RunAsync($"revert \"{element.FullPath}\"", root, token: token);

                SVNLogBridge.LogLine($"<color=green>Successfully reverted:</color> {element.Name}");

                svnManager._diskChangesDetected = true;

                var statusModule = svnManager.GetModule<SVNStatus>();
                statusModule?.ClearSVNTreeView();
                statusModule?.ClearCurrentData();
                ClearAllUI();

                await svnManager.RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>[Revert]</color> Operation cancelled by user.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Revert Error:</color> {ex.Message}");
            }
            finally
            {
                _revertCts?.Dispose();
                _revertCts = null;
                IsProcessing = false;
            }
        }

        private static async Task RevertWorkingCopyAsync(string workingDir, Action<string> onProgress, CancellationToken token)
        {
            string cleanWorkingDir = Path.GetFullPath(workingDir.Trim()).Replace('\\', '/');

            try
            {
                onProgress?.Invoke("Performing recursive revert on working directory...");

                string result = await SvnRunner.RunAsync("revert -R .", cleanWorkingDir, token: token);
                token.ThrowIfCancellationRequested();

                if (result.Contains("svn: E"))
                {
                    onProgress?.Invoke("Revert failed, attempting cleanup...");
                    await SvnRunner.RunAsync("cleanup", cleanWorkingDir, token: token);
                    token.ThrowIfCancellationRequested();

                    onProgress?.Invoke("Retrying recursive revert...");
                    result = await SvnRunner.RunAsync("revert -R .", cleanWorkingDir, token: token);
                }

                SVNLogBridge.LogLine("<color=green>[SVN]</color> Recursive revert completed successfully.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN Revert Error] Recursive revert failed: {ex.Message}");
                throw;
            }
        }
    }
}