using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNRevision : SVNBase
    {
        private const float DoubleClickThreshold = 5.0f;

        private float _lastUpdateToRevClickTime;
        private string _pendingRevision;

        private float _lastRevertClickTime;
        private string _pendingRevertInput;

        private int _processingFlag;

        public SVNRevision(SVNUI svnUI, SVNManager svnManager) : base(svnUI, svnManager) { }

        private bool TryEnterProcessing()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1) return false;
            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        private void LogToRevisionPanel(string message, bool append = true)
        {
            if (svnUI?.RevisionDisplayArea != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.RevisionDisplayArea, message);
            }
            else
            {
                SVNLogBridge.LogLine(message, append);
            }
        }

        public async void UpdateToRevisionButton()
        {
            if (IsProcessing) return;

            if (svnUI?.UpdateRevisionInput == null)
            {
                LogToRevisionPanel("<color=red>Error: UpdateRevisionInput is not assigned in SVNUI Inspector!</color>");
                return;
            }

            string rev = svnUI.UpdateRevisionInput.text?.Trim();

            if (string.IsNullOrWhiteSpace(rev))
            {
                LogToRevisionPanel("<color=yellow>No revision specified. Executing standard update to HEAD...</color>", append: false);
                svnManager.GetModule<SVNUpdate>()?.Update();
                return;
            }

            rev = rev.TrimStart('r', 'R');

            if (!int.TryParse(rev, out int _))
            {
                LogToRevisionPanel("<color=red>Error: Invalid format. Please enter just numbers (e.g. 150).</color>");
                return;
            }

            var updateModule = svnManager.GetModule<SVNUpdate>();
            if (updateModule == null)
            {
                LogToRevisionPanel("<color=red>Error: SVNUpdate module is not available.</color>");
                return;
            }

            bool hasModifications = await updateModule.HasLocalModificationsAsync(svnManager.WorkingDir).ConfigureAwait(false);
            if (hasModifications)
            {
                LogToRevisionPanel(
                    "<color=#FFAA00>Cannot update to a specific revision while you have uncommitted local changes. " +
                    "Please commit or revert them first.</color>");
                return;
            }

            float timeSinceLastClick = Time.time - _lastUpdateToRevClickTime;

            if (timeSinceLastClick < DoubleClickThreshold && _pendingRevision == rev)
            {
                _pendingRevision = null;

                LogToRevisionPanel($"<color=green>Executing update to revision {rev}...</color>", append: false);
                updateModule.UpdateToRevision(rev);
            }
            else
            {
                _lastUpdateToRevClickTime = Time.time;
                _pendingRevision = rev;

                LogToRevisionPanel(
                    $"<color=#FFAA00><b>ATTENTION:</b> Click <b>ONE MORE TIME</b> within 5 seconds to confirm update to revision {rev}.\n" +
                    "This will overwrite local files!</color>", append: false);
            }
        }

        public async void RevertCommitsButton() => await RevertCommitsFromInputAsync();

        private async Task RevertCommitsFromInputAsync()
        {
            if (svnUI?.UpdateRevisionInput == null)
            {
                LogToRevisionPanel("<color=#FFAA00>[Revert] Revision input field not assigned in UI.</color>");
                return;
            }

            string inputText = svnUI.UpdateRevisionInput.text?.Trim();
            if (string.IsNullOrWhiteSpace(inputText))
            {
                LogToRevisionPanel("<color=#FFAA00>[Revert] Please enter revision numbers (e.g. 150, 148:150, 155).</color>");
                return;
            }

            var revisionItems = SvnRevisionRangeParser.Parse(inputText);

            if (revisionItems.Count == 0)
            {
                LogToRevisionPanel("<color=#FFAA00>[Revert] No valid revision numbers entered.</color>");
                return;
            }

            var cleanRevs = new List<string>();
            foreach (var item in revisionItems)
            {
                if (item.IsRange)
                    cleanRevs.Add($"r{item.Start}:r{item.End}");
                else
                    cleanRevs.Add($"r{item.Start}");
            }

            string revListString = cleanRevs.Count == 1
                ? $"revision {cleanRevs[0]}"
                : $"revisions: {string.Join(", ", cleanRevs)}";

            float timeSinceLastClick = Time.time - _lastRevertClickTime;

            if (timeSinceLastClick < DoubleClickThreshold && _pendingRevertInput == inputText)
            {
                _pendingRevertInput = null;
            }
            else
            {
                _lastRevertClickTime = Time.time;
                _pendingRevertInput = inputText;

                LogToRevisionPanel(
                    $"<color=#FFAA00><b>ATTENTION:</b> Click <b>ONE MORE TIME</b> within {DoubleClickThreshold} seconds to confirm undoing changes from {revListString}.\n" +
                    "This will modify your working copy!</color>", append: false);
                return;
            }

            if (!TryEnterProcessing()) return;

            try
            {
                string workingDir = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(workingDir))
                {
                    LogToRevisionPanel("<color=#FFAA00>[Revert] Working directory not set.</color>");
                    return;
                }

                var revArgs = new StringBuilder();
                foreach (var item in revisionItems)
                {
                    if (item.IsRange)
                    {
                        for (long rev = item.Start; rev <= item.End; rev++)
                        {
                            revArgs.Append($"-c -{rev} ");
                        }
                    }
                    else
                    {
                        revArgs.Append($"-c -{item.Start} ");
                    }
                }

                LogToRevisionPanel($"[REVERT COMMITS] Undoing changes from {revListString}...", append: false);
                LogToRevisionPanel("[Revert] Bringing working copy to a uniform revision...");

                try
                {
                    await SvnRunner.RunAsync("update", workingDir, true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogToRevisionPanel($"<color=yellow>[Revert] Update failed (non-fatal): {ex.Message}</color>");
                }

                string args = $"merge {revArgs}. --non-interactive --accept postpone";
                LogToRevisionPanel($"[Revert] Executing: svn {args}");

                string output = await SvnRunner.RunAsync(args, workingDir, true, CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(output) || output.Contains("No changes") || output.Contains("Already merged"))
                {
                    LogToRevisionPanel($"<color=yellow>[Revert] {revListString} has no effect on current working copy.</color>");
                }
                else if (output.Contains("conflict") || output.IndexOf("C ") >= 0)
                {
                    LogToRevisionPanel("<color=yellow>[REVERT CONFLICTS] Reverting caused conflicts! Please use the Resolve panel to fix them before committing.</color>");
                }
                else
                {
                    LogToRevisionPanel($"<color=green>[Revert] Successfully reverted {revListString}.</color>");
                    LogToRevisionPanel("<color=#FFFF00>IMPORTANT: You MUST commit these changes now to finalize the undo.</color>");
                }

                await svnManager.RefreshStatus().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogToRevisionPanel("<color=yellow>[Revert] Cancelled by user.</color>");
            }
            catch (Exception ex)
            {
                LogToRevisionPanel($"<color=#FFAA00>[Revert Error] {ex.Message}</color>");
            }
            finally
            {
                ExitProcessing();
            }
        }

        public async void ExportRevisionButton() => await ExportRevisionFromInputAsync();

        private async Task ExportRevisionFromInputAsync()
        {
            if (svnUI?.UpdateRevisionInput == null)
            {
                LogToRevisionPanel("<color=red>Error: UpdateRevisionInput is not assigned in SVNUI Inspector!</color>");
                return;
            }

            string rev = svnUI.UpdateRevisionInput.text?.Trim()?.TrimStart('r', 'R');
            if (string.IsNullOrWhiteSpace(rev))
            {
                LogToRevisionPanel("<color=#FFAA00>[Export] Please enter a valid revision number to export.</color>");
                return;
            }

            if (!int.TryParse(rev, out int _))
            {
                LogToRevisionPanel("<color=red>[Export] Error: Invalid revision format. Please enter numbers only (e.g. 150).</color>");
                return;
            }

            if (!TryEnterProcessing()) return;

            try
            {
                LogToRevisionPanel($"<color=green>[Export] Initiating export for revision r{rev}...</color>");

                var externalModule = svnManager.GetModule<SVNExternal>();

                if (externalModule != null)
                {
                    externalModule.ExportRevision(rev);
                }
                else
                {
                    LogToRevisionPanel("<color=red>[Export Error] SVNExternal module was not found in SVNManager!</color>");
                }
            }
            catch (Exception ex)
            {
                LogToRevisionPanel($"<color=#FFAA00>[Export Error] {ex.Message}</color>");
            }
            finally
            {
                ExitProcessing();
            }
        }

        public async Task RestoreSingleFileAsync(string relativeFilePath, string revision)
        {
            if (IsProcessing) return;
            if (!TryEnterProcessing()) return;

            try
            {
                string cleanPath = relativeFilePath.Replace('\\', '/').TrimStart('/');

                LogToRevisionPanel($"<color=green>[Restore File] Fetching {cleanPath} at r{revision}...</color>");

                string args = $"cat -r {revision} {SvnMergeUrlResolver.EscapeSvnArg(cleanPath)}";

                string fileContent = await SvnRunner.RunAsync(args, svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(fileContent))
                {
                    LogToRevisionPanel("<color=yellow>[Restore File] File is empty or does not exist in this revision.</color>");
                    return;
                }

                string fullDiskPath = Path.Combine(svnManager.WorkingDir, cleanPath.Replace('/', Path.DirectorySeparatorChar));

                File.WriteAllText(fullDiskPath, fileContent, new UTF8Encoding(false));

                LogToRevisionPanel($"<color=green>[Restore File] Successfully restored: {cleanPath}</color>");

                await svnManager.RefreshStatus().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogToRevisionPanel($"<color=red>[Restore File Error] {ex.Message}</color>");
            }
            finally
            {
                ExitProcessing();
            }
        }

        public async Task ExtractSingleFileToAsync(string relativeFilePath, string revision, string destinationPath)
        {
            if (IsProcessing) return;
            if (!TryEnterProcessing()) return;

            try
            {
                string cleanPath = relativeFilePath.Replace('\\', '/').TrimStart('/');

                LogToRevisionPanel($"<color=green>[Extract File] Fetching {cleanPath} at r{revision}...</color>");

                string args = $"cat -r {revision} {SvnMergeUrlResolver.EscapeSvnArg(cleanPath)}";
                string fileContent = await SvnRunner.RunAsync(args, svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(fileContent))
                {
                    LogToRevisionPanel("<color=yellow>[Extract File] File is empty or does not exist in this revision.</color>");
                    return;
                }

                string destDir = Path.GetDirectoryName(destinationPath);
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                File.WriteAllText(destinationPath, fileContent, new UTF8Encoding(false));

                LogToRevisionPanel($"<color=green>[Extract File] Saved to: {destinationPath}</color>");
            }
            catch (Exception ex)
            {
                LogToRevisionPanel($"<color=red>[Extract File Error] {ex.Message}</color>");
            }
            finally
            {
                ExitProcessing();
            }
        }
    }
}