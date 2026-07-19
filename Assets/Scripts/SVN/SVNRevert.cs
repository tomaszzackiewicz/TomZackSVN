using System;
using System.IO;
using System.Threading;
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
            if (string.IsNullOrWhiteSpace(msg))
                return;

            SVNLogBridge.LogLine(msg);

            if (svnUI?.CommitConsoleContent != null)
            {
                SVNLogBridge.UpdateUIField(
                    svnUI.CommitConsoleContent,
                    msg,
                    "REVERT",
                    append: true);
            }
        }

        private void ClearAllUI()
        {
            svnUI?.SvnTreeView?.ClearView();
            svnUI?.SVNCommitTreeDisplay?.ClearView();

            if (svnUI?.TreeDisplay != null)
            {
                SVNLogBridge.UpdateUIField(
                    svnUI.TreeDisplay,
                    "",
                    "TREE",
                    append: false);
            }

            if (svnUI?.CommitTreeDisplay != null)
            {
                SVNLogBridge.UpdateUIField(
                    svnUI.CommitTreeDisplay,
                    "",
                    "COMMIT_TREE",
                    append: false);
            }
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

                LogToConsole(
                    "<color=#FFAA00><b>[Revert]</b></color> Confirmation too fast — press the button once again.");

                return false;
            }

            lastClickTime = -10f;
            return true;
        }

        public async void RevertAll()
        {
            if (IsProcessing)
                return;

            await svnManager.CancelBackgroundTasksAsync();

            if (!ConfirmAction(
                    ref _lastRevertAllClickTime,
                    "<color=#FFAA00><b>[Revert All]</b></color> Are you sure? This will discard <b>ALL local changes</b>!\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            string root = svnManager.WorkingDir;

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                LogToConsole("<color=red>Error:</color> Working directory does not exist.");
                return;
            }

            try
            {
                _revertCts?.Cancel();
            }
            catch { }

            try
            {
                _revertCts?.Dispose();
            }
            catch { }

            _revertCts = new CancellationTokenSource();
            CancellationToken token = _revertCts.Token;

            IsProcessing = true;

            try
            {
                LogToConsole("<color=#4FC3F7><b>[SVN]</b> Preparing revert...</color>");

                token.ThrowIfCancellationRequested();

                await SvnRunner.WaitForSemaphoreFreeAsync(token);

                token.ThrowIfCancellationRequested();

                await SvnRunner.RunAsync(
                    "revert -R .",
                    root,
                    retryOnLock: true,
                    token: token);

                token.ThrowIfCancellationRequested();

                svnManager._diskChangesDetected = true;

                var status = svnManager.GetModule<SVNStatus>();

                status?.ClearCurrentData();
                status?.ClearSVNTreeView();

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
                LogToConsole($"<color=red><b>[SVN]</b> Revert failed:</color>\n{ex.Message}");
            }
            finally
            {
                IsProcessing = false;

                _revertCts?.Dispose();
                _revertCts = null;
            }
        }

        public void CancelRevert()
        {
            if (!IsProcessing)
                return;

            var cts = _revertCts;

            if (cts == null || cts.IsCancellationRequested)
                return;

            LogToConsole("<color=orange><b>[Revert]</b></color> Cancel requested...");

            try
            {
                cts.Cancel();
            }
            catch
            {
            }
        }

        public async void RevertSingleItem(SvnTreeElement element)
        {
            if (IsProcessing || element == null)
                return;

            await svnManager.CancelBackgroundTasksAsync();

            if (!ConfirmAction(
                    ref _lastRevertSingleClickTime,
                    $"<color=#FFAA00><b>[Revert]</b></color> Are you sure you want to revert <b>{element.Name}</b>?\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            string root = svnManager.WorkingDir;

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                LogToConsole("<color=red>Working directory not found.</color>");
                return;
            }

            try
            {
                _revertCts?.Cancel();
            }
            catch { }

            try
            {
                _revertCts?.Dispose();
            }
            catch { }

            _revertCts = new CancellationTokenSource();
            CancellationToken token = _revertCts.Token;

            IsProcessing = true;

            try
            {
                await SvnRunner.WaitForSemaphoreFreeAsync(token);

                token.ThrowIfCancellationRequested();

                string safePath = SvnRunner.NormalizeRepositoryPath(element.FullPath);

                LogToConsole($"<b>Reverting:</b> {safePath}");

                await SvnRunner.RunAsync(
                    $"revert \"{safePath}\"",
                    root,
                    retryOnLock: true,
                    token: token);

                token.ThrowIfCancellationRequested();

                svnManager._diskChangesDetected = true;

                var status = svnManager.GetModule<SVNStatus>();

                status?.ClearCurrentData();
                status?.ClearSVNTreeView();

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
                LogToConsole($"<color=red>Revert Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;

                _revertCts?.Dispose();
                _revertCts = null;
            }
        }
    }
}