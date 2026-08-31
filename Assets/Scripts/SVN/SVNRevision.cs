using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using SFB;

namespace SVN.Core
{
    public class SVNRevision : SVNBase
    {
        private const float DoubleClickThreshold = 5.0f;
        private const int MaxLogLines = 300;

        private float _lastUpdateToRevClickTime;
        private string _pendingRevision;

        private float _lastRevertClickTime;
        private string _pendingRevertInput;

        // Double-click confirmation for REVERT PATH in rollback mode (with a revision)
        private float _lastRevertPathClickTime;
        private string _pendingRevertPathKey;

        private int _processingFlag;
        private Stopwatch _operationStopwatch;

        public SVNRevision(SVNUI svnUI, SVNManager svnManager) : base(svnUI, svnManager)
        {
            _operationStopwatch = new Stopwatch();
        }

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

        // ========== LOGGING HELPERS ==========
        private void LogToRevisionPanel(string message, bool clearBefore = false)
        {
            if (svnUI?.RevisionDisplayArea != null)
            {
                if (clearBefore)
                    svnUI.RevisionDisplayArea.text = string.Empty;

                string current = svnUI.RevisionDisplayArea.text;
                var lines = new List<string>(current.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
                lines.Add(message);

                if (lines.Count > MaxLogLines)
                    lines = lines.Skip(lines.Count - MaxLogLines).ToList();

                svnUI.RevisionDisplayArea.text = string.Join("\n", lines) + "\n";
            }
            else
            {
                SVNLogBridge.LogLine(message, true);
            }
        }

        private void ClearLogPanel()
        {
            LogToRevisionPanel(string.Empty, clearBefore: true);
        }

        private void StartOperationTimer()
        {
            _operationStopwatch.Reset();
            _operationStopwatch.Start();
        }

        private void StopOperationTimer()
        {
            _operationStopwatch.Stop();
        }

        private string GetElapsedTime()
        {
            return _operationStopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        }

        private void LogHeader(string title)
        {
            LogToRevisionPanel("");
            LogToRevisionPanel("---------------------------------------------");
            LogToRevisionPanel($"  {title}");
            LogToRevisionPanel("---------------------------------------------");
        }

        private void LogStep(int step, int total, string msg)
        {
            LogToRevisionPanel($"[{step}/{total}] {msg}");
        }

        private void LogDetail(string msg)
        {
            LogToRevisionPanel($"    >> {msg}");
        }

        private void LogSuccess(string msg)
        {
            LogToRevisionPanel($"[OK] {msg}");
        }

        private void LogWarning(string msg)
        {
            LogToRevisionPanel($"[WARN] {msg}");
        }

        private void LogError(string msg)
        {
            LogToRevisionPanel($"[ERROR] {msg}");
        }

        private void LogInfoBox(string[] lines)
        {
            LogToRevisionPanel("---------------------------------------------");
            foreach (var line in lines)
                LogToRevisionPanel($"  {line}");
            LogToRevisionPanel("---------------------------------------------");
        }

        private void LogCmd(string cmd)
        {
            LogToRevisionPanel($"    $ svn {cmd}");
        }

        private void LogEnd(bool success = true)
        {
            LogToRevisionPanel("");
            LogToRevisionPanel("---------------------------------------------");
            if (success)
                LogToRevisionPanel("  OPERATION COMPLETED SUCCESSFULLY");
            else
                LogToRevisionPanel("  OPERATION FAILED");
            LogToRevisionPanel("---------------------------------------------");
            LogToRevisionPanel("");
        }

        // ========== BROWSE (file OR folder - one button) ==========
        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>
        /// A single Browse button handling both target types:
        /// 1) the FILE dialog (the title informs: Cancel = pick a folder),
        /// 2) after cancelling - the FOLDER dialog.
        /// Always stores a RELATIVE path into RevisionFilePathInput.
        /// Selecting the working copy root stores ".".
        /// </summary>
        public void BrowsePath()
        {
            // Stage 1: FILE dialog. Cancelling it falls through to the FOLDER dialog.
            if (TryBrowseRelative(folder: false, "Select File (Cancel to pick a Folder)", out string fileRel))
            {
                if (fileRel != null)
                    SetRevisionPathInput(fileRel, "file");
                // fileRel == null => path outside WorkingDir - error already logged in TryBrowseRelative
                return;
            }

            // File dialog cancelled -> open the Folder dialog
            LogToRevisionPanel("[Browse] File dialog cancelled - opening Folder dialog...");

            if (TryBrowseRelative(folder: true, "Select Folder", out string folderRel))
            {
                if (folderRel != null)
                    SetRevisionPathInput(folderRel, "folder");
            }
            else
            {
                LogToRevisionPanel("[Browse] Both dialogs cancelled - nothing selected.");
            }
        }

        /// <summary>
        /// Returns false ONLY when the dialog was cancelled (the caller can then try a folder).
        /// Returns true + relPath == null when the path is outside the WorkingDir (error already logged).
        /// </summary>
        private bool TryBrowseRelative(bool folder, string title, out string relPath)
        {
            relPath = null;

            string root = svnManager.WorkingDir ?? "";

            string[] paths = folder
                ? StandaloneFileBrowser.OpenFolderPanel(title, root, false)
                : StandaloneFileBrowser.OpenFilePanel(title, root, new[] { new ExtensionFilter("All Files", "*") }, false);

            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
                return false; // cancelled

            string sel = NormalizePath(paths[0]);
            string normRoot = NormalizePath(root);

            if (string.IsNullOrEmpty(normRoot))
            {
                LogWarning("Working Directory is not set - storing absolute path.");
                relPath = sel;
                return true;
            }

            if (sel.Equals(normRoot, StringComparison.OrdinalIgnoreCase))
            {
                // The working copy ROOT was selected: store "." (visible in the input,
                // semantically correct: Revert = whole WC, Extract Folder = repo root)
                relPath = ".";
                return true;
            }

            if (sel.StartsWith(normRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                relPath = sel.Substring(normRoot.Length + 1);
                return true;
            }

            LogError("Selected path is outside the Working Directory - path NOT stored.");
            return true; // handled (error) - do not chain further
        }

        private void SetRevisionPathInput(string relPath, string type)
        {
            if (svnUI?.RevisionFilePathInput == null)
            {
                LogError("RevisionFilePathInput is not assigned in SVNUI.");
                return;
            }

            svnUI.RevisionFilePathInput.text = relPath;
            svnUI.RevisionFilePathInput.ForceLabelUpdate();
            LogSuccess($"Selected {type}: {relPath}");
        }

        // ========== PATH RESOLUTION ==========
        /// <summary>
        /// Converts input (absolute or relative) into a path RELATIVE to WorkingDir ('/' as separator).
        /// Returns "" when the input points at the working copy root ("." also maps to "").
        /// Returns null when the path is outside the working copy or invalid.
        /// </summary>
        private string ResolveRelativeInsideWorkingDir(string pathFromInput)
        {
            if (string.IsNullOrWhiteSpace(pathFromInput)) return null;

            string workingDir = svnManager?.WorkingDir;
            if (string.IsNullOrWhiteSpace(workingDir)) return null;

            string p = pathFromInput.Replace('\\', '/').Trim().TrimEnd('/');
            if (p.Length == 0) return null;
            if (p == ".") return "";

            if (!Path.IsPathRooted(p))
                return p.TrimStart('/');

            // Absolute path - must point inside the working copy
            string root = Path.GetFullPath(workingDir).Replace('\\', '/').TrimEnd('/');
            string abs = Path.GetFullPath(p).Replace('\\', '/').TrimEnd('/');

            if (abs.Equals(root, StringComparison.OrdinalIgnoreCase))
                return ""; // working copy root

            if (abs.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return abs.Substring(root.Length + 1);

            return null; // outside the working copy
        }

        /// <summary>
        /// Checks whether an 'svn status' line refers to the specific target.
        /// SVN may print the path as absolute (when we passed an absolute one) or as
        /// relative (relative to the process cwd = workingDir) - both forms are compared.
        /// </summary>
        private static bool StatusLineRefersToPath(string line, string absPath, string relPath)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length < 9) return false;

            string p = line.Substring(8).Trim().Replace('\\', '/').TrimEnd('/');
            string abs = (absPath ?? "").Replace('\\', '/').TrimEnd('/');
            string rel = (relPath ?? "").Replace('\\', '/').TrimEnd('/');

            return (!string.IsNullOrEmpty(abs) && p.Equals(abs, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(rel) && p.Equals(rel, StringComparison.OrdinalIgnoreCase));
        }

        // ========== HELPERS ==========
        private async Task<string> GetBranchUrlAsync(string workingDir, CancellationToken token)
        {
            // RunDetailedAsync - full result; an error (e.g. E155007) lands in the exception
            // message instead of being lost in a generic "SVN Error (Code 1)".
            var (output, error, exitCode) = await SvnRunner.RunDetailedAsync(
                "info --show-item url", workingDir, retryOnLock: true, throwOnError: false, token);

            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
                throw new Exception($"Cannot resolve branch URL (svn info, exit {exitCode}): {(error ?? "").Trim()}");

            return output.Trim();
        }

        private static long CalculateFolderSize(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;

            long size = 0;
            try
            {
                var dirInfo = new DirectoryInfo(path);
                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { size += file.Length; }
                    catch { }
                }
            }
            catch { }

            return size;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes >= 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F2} KB";
            return $"{bytes} B";
        }

        // ========== UPDATE TO REVISION ==========
        public async void UpdateToRevisionButton()
        {
            if (IsProcessing)
            {
                LogWarning("Another operation is currently in progress. Please wait...");
                return;
            }

            if (svnUI?.UpdateRevisionInput == null)
            {
                LogError("UpdateRevisionInput is not assigned in the SVNUI Inspector.");
                return;
            }

            string rev = svnUI.UpdateRevisionInput.text?.Trim();

            if (string.IsNullOrWhiteSpace(rev))
            {
                ClearLogPanel();
                LogHeader("UPDATE TO HEAD");
                LogStep(1, 1, "No revision specified. Executing standard update to HEAD...");
                var updateModule = svnManager.GetModule<SVNUpdate>();
                if (updateModule != null)
                {
                    updateModule.Update();
                    LogSuccess("Update to HEAD initiated. Monitoring progress...");
                    _ = MonitorUpdateCompletionAsync("HEAD", updateModule);
                }
                else
                {
                    LogError("SVNUpdate module is not available.");
                    LogEnd(false);
                }
                return;
            }

            rev = rev.TrimStart('r', 'R');

            if (!int.TryParse(rev, out _))
            {
                ClearLogPanel();
                LogHeader("INVALID REVISION");
                LogError($"Invalid format: \"{rev}\". Enter numbers only (e.g. 150).");
                LogEnd(false);
                return;
            }

            var updateModule2 = svnManager.GetModule<SVNUpdate>();
            if (updateModule2 == null)
            {
                ClearLogPanel();
                LogHeader("UPDATE TO REVISION ERROR");
                LogError("SVNUpdate module is not available.");
                LogEnd(false);
                return;
            }

            float now = Time.time;

            var dirty = await svnManager.GetWorkingCopyDirtyStateAsync(svnManager.WorkingDir);

            if (dirty.IsBlockingDirty)
            {
                ClearLogPanel();
                LogHeader($"UPDATE TO REVISION r{rev} ABORTED");
                LogWarning("Cannot update - uncommitted versioned changes present.");
                if (dirty.ConflictedCount > 0)
                    LogDetail($"Conflicts: {dirty.ConflictedCount}");
                LogInfoBox(new[]
                {
                    "Options:",
                    "  1. Commit your changes first",
                    "  2. Use Revert All to discard them"
                });
                LogEnd(false);
                return;
            }

            float timeSinceLastClick = now - _lastUpdateToRevClickTime;

            if (timeSinceLastClick < DoubleClickThreshold && _pendingRevision == rev)
            {
                _pendingRevision = null;
                ClearLogPanel();
                LogHeader($"UPDATE TO REVISION r{rev}");
                if (dirty.UnversionedCount > 0)
                    LogDetail($"{dirty.UnversionedCount} unversioned file(s) will be left untouched.");

                LogStep(1, 2, $"Starting update to r{rev}...");
                LogDetail("Delegating task to SVNUpdate engine...");
                updateModule2.UpdateToRevision(rev);

                LogStep(2, 2, "Task handed over. Background monitor running...");
                _ = MonitorUpdateCompletionAsync(rev, updateModule2);
            }
            else
            {
                _lastUpdateToRevClickTime = now;
                _pendingRevision = rev;
                ClearLogPanel();
                LogHeader($"CONFIRMATION REQUIRED - UPDATE r{rev}");
                LogWarning($"Files will be overwritten to match revision r{rev}.");
                LogDetail("Click UPDATE button ONE MORE TIME within 5 seconds to execute.");
            }
        }

        private async Task MonitorUpdateCompletionAsync(string revision, SVNUpdate updateModule)
        {
            try
            {
                await Task.Delay(500);

                int waited = 0;

                while (SvnRunner.ActiveOperationsCount > 0 || (updateModule?.IsProcessing ?? false))
                {
                    await Task.Delay(500);
                    waited += 500;

                    if (waited % 10000 == 0)
                    {
                        LogToRevisionPanel($"Update still in progress... {waited / 1000}s elapsed");
                    }
                }

                LogToRevisionPanel("");
                LogToRevisionPanel($"UPDATE TO {revision} COMPLETE");
                LogToRevisionPanel("");
                LogToRevisionPanel("  Working copy is synchronized.");
                LogToRevisionPanel("  Check the Update Report above for details.");
                LogToRevisionPanel("");
                LogToRevisionPanel("REVISION PANEL OPERATION FINISHED");
                LogToRevisionPanel("");

                LogEnd(true);

                if (svnManager != null)
                {
                    LogToRevisionPanel("  Refreshing working copy status...");
                    try
                    {
                        await svnManager.RefreshStatus();
                        LogToRevisionPanel("  Status refreshed.");
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Status refresh failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Update monitor error: {ex.Message}");
                LogEnd(false);
            }
        }

        // ========== REVERT COMMITS ==========
        public async void RevertCommitsButton() => await RevertCommitsFromInputAsync();

        private async Task RevertCommitsFromInputAsync()
        {
            if (svnUI?.UpdateRevisionInput == null)
            {
                LogError("[Revert] Revision input field is not assigned in the UI.");
                return;
            }

            string inputText = svnUI.UpdateRevisionInput.text?.Trim();
            if (string.IsNullOrWhiteSpace(inputText))
            {
                ClearLogPanel();
                LogHeader("REVERT COMMITS - NO INPUT");
                LogWarning("Please enter revision numbers (e.g. 150, 148:150, 155).");
                LogEnd(false);
                return;
            }

            var revisionItems = SvnRevisionRangeParser.Parse(inputText);
            if (revisionItems.Count == 0)
            {
                ClearLogPanel();
                LogHeader("REVERT COMMITS - INVALID INPUT");
                LogWarning("No valid revision numbers entered.");
                LogEnd(false);
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

                ClearLogPanel();
                LogHeader("CONFIRMATION REQUIRED - REVERT COMMITS");
                LogWarning($"About to undo: {revListString}");
                LogDetail("This will modify your working copy (reverse merge).");
                LogDetail("Click REVERT COMMITS button ONE MORE TIME within 5 seconds to confirm.");
                return;
            }

            if (!TryEnterProcessing())
            {
                LogWarning("Another operation is running. Please wait...");
                return;
            }

            ClearLogPanel();
            LogHeader($"REVERT COMMITS - {revListString}");
            StartOperationTimer();

            try
            {
                string workingDir = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(workingDir))
                {
                    LogError("[Revert] Working directory path is missing or invalid.");
                    LogEnd(false);
                    return;
                }

                LogDetail("Executing reverse merge operation on working directory.");

                LogStep(1, 4, "Resolving repository URL...");
                string repoUrl = await GetBranchUrlAsync(workingDir, CancellationToken.None);
                LogDetail($"Source Target: {repoUrl}");

                var revArgs = new StringBuilder();
                foreach (var item in revisionItems)
                {
                    if (item.IsRange)
                        revArgs.Append($"-r {item.End}:{item.Start - 1} ");
                    else
                        revArgs.Append($"-c -{item.Start} ");
                }

                // RunDetailedAsync - no exception, explicit exit code + stderr.
                LogStep(2, 4, "Bringing working copy to a uniform revision (svn update)...");
                var (updOut, updErr, updExit) = await SvnRunner.RunDetailedAsync(
                    "update", workingDir, retryOnLock: true);
                if (updExit != 0)
                    LogWarning($"Pre-update step non-fatal warning (exit {updExit}): {(updErr ?? "").Trim()}");
                else
                    LogDetail("Working copy updated to uniform revision.");

                LogStep(3, 4, "Executing reverse merge command...");
                string args = $"merge {revArgs}\"{repoUrl}\" . --non-interactive --accept postpone";
                LogCmd(args);

                var (mergeOut, mergeErr, mergeExit) = await SvnRunner.RunDetailedAsync(
                    args, workingDir, retryOnLock: true);

                if (mergeExit != 0)
                {
                    LogError($"Reverse merge failed (exit code {mergeExit}).");
                    if (!string.IsNullOrWhiteSpace(mergeErr)) LogDetail(ExtractSvnError(mergeErr));
                    if ((mergeErr ?? "").Contains("E1950") || (mergeErr ?? "").Contains("ancestry"))
                        LogWarning("Tip: Target revision may not share ancestry. Try running SVN Update first.");
                    LogEnd(false);
                    return;
                }

                string output = mergeOut ?? "";
                bool hasConflicts = output.IndexOf("conflict", StringComparison.OrdinalIgnoreCase) >= 0
                                 || (mergeErr ?? "").IndexOf("conflict", StringComparison.OrdinalIgnoreCase) >= 0;

                if (string.IsNullOrWhiteSpace(output) || output.Contains("No changes") || output.Contains("Already merged"))
                {
                    LogWarning($"{revListString} has no effect on the current working copy.");
                    LogDetail("Changes were already reverted or do not apply here.");
                }
                else if (hasConflicts)
                {
                    LogError("Revert action produced merge conflicts!");
                    LogDetail("Use the Resolve panel to fix conflicts manually, then commit.");
                }
                else
                {
                    LogSuccess($"Reverse merge successful for {revListString}.");
                }

                LogStep(4, 4, "Refreshing working copy status...");
                await svnManager.RefreshStatus();
                LogDetail("Status refresh complete.");

                if (!hasConflicts)
                {
                    var statusModule = svnManager.GetModule<SVNStatus>();
                    var data = statusModule?.GetCurrentData();
                    if (data != null)
                    {
                        int deletedCount = data.Count(e => e.Status == "D" || e.Status == "!");
                        int modifiedCount = data.Count(e => e.Status == "M");
                        int addedCount = data.Count(e => e.Status == "A");

                        if (deletedCount > 0 || modifiedCount > 0 || addedCount > 0)
                        {
                            LogDetail("Working copy file status after revert:");
                            if (deletedCount > 0) LogDetail($"   Deleted:  {deletedCount} file(s)");
                            if (modifiedCount > 0) LogDetail($"   Modified: {modifiedCount} file(s)");
                            if (addedCount > 0) LogDetail($"   Added:    {addedCount} file(s)");
                        }
                    }

                    LogInfoBox(new[]
                    {
                        "NEXT STEP REQUIRED: Commit changes to finalize revert",
                        "Do NOT click \"Revert All\"!",
                        "It will undo this reverse merge and bring bad code back.",
                        "Use 'Commit All' to submit the undone state to repo."
                    });
                }
                StopOperationTimer();
                LogDetail($"Elapsed time: {GetElapsedTime()}");
                LogEnd(true);
            }
            catch (OperationCanceledException)
            {
                StopOperationTimer();
                LogWarning("[Revert] Task was cancelled by user.");
                LogEnd(false);
            }
            catch (Exception ex)
            {
                StopOperationTimer();
                LogError($"[Revert Error] {ex.Message}");
                if (ex.Message.Contains("E1950") || ex.Message.Contains("ancestry"))
                    LogWarning("Tip: Target revision may not share ancestry. Try running SVN Update first.");
                LogEnd(false);
            }
            finally
            {
                ExitProcessing();
            }
        }

        // ========== EXPORT REVISION ==========
        public async void ExportRevisionButton() => await ExportRevisionFromInputAsync();

        private async Task ExportRevisionFromInputAsync()
        {
            if (IsProcessing)
            {
                LogWarning("Another operation is running. Please wait...");
                return;
            }

            if (svnUI?.UpdateRevisionInput == null)
            {
                LogError("UpdateRevisionInput is not assigned in the SVNUI Inspector.");
                return;
            }

            string rev = svnUI.UpdateRevisionInput.text?.Trim()?.TrimStart('r', 'R');
            if (string.IsNullOrWhiteSpace(rev))
            {
                ClearLogPanel();
                LogHeader("EXPORT REVISION - NO INPUT");
                LogWarning("Please enter a valid revision number to export.");
                LogEnd(false);
                return;
            }

            if (!int.TryParse(rev, out _))
            {
                ClearLogPanel();
                LogHeader("EXPORT REVISION - INVALID FORMAT");
                LogError("Invalid revision format. Enter numbers only (e.g. 150).");
                LogEnd(false);
                return;
            }

            var externalModule = svnManager.GetModule<SVNExternal>();
            if (externalModule == null)
            {
                ClearLogPanel();
                LogHeader("EXPORT REVISION ERROR");
                LogError("SVNExternal module missing from SVNManager setup.");
                LogEnd(false);
                return;
            }

            if (!TryEnterProcessing())
            {
                LogWarning("Another operation is running. Please wait...");
                return;
            }

            ClearLogPanel();
            LogHeader($"EXPORT REVISION r{rev}");
            LogStep(1, 2, "Initializing export task...");
            LogDetail("Handing execution over to SVNExternal module.");
            StartOperationTimer();

            try
            {
                externalModule.ExportRevision(rev);

                await Task.Delay(500);
                while (SvnRunner.ActiveOperationsCount > 0 || (externalModule?.IsProcessing ?? false))
                {
                    await Task.Delay(500);
                }

                LogStep(2, 2, "Export process finished.");
                LogSuccess($"Export operation for r{rev} completed.");
                LogDetail("Check export destination folder for output.");
                StopOperationTimer();
                LogDetail($"Elapsed time: {GetElapsedTime()}");
                LogEnd(true);
            }
            catch (Exception ex)
            {
                StopOperationTimer();
                LogError($"[Export Error] {ex.Message}");
                LogEnd(false);
            }
            finally
            {
                ExitProcessing();
            }
        }

        // ========== RESTORE SINGLE FILE ==========
        public async Task RestoreSingleFileAsync(string relativeFilePath, string revision)
        {
            if (IsProcessing)
            {
                LogWarning("Processing busy. Cannot restore file right now.");
                return;
            }

            // Path resolution (absolute or relative input, "." = root)
            string cleanPath = ResolveRelativeInsideWorkingDir(relativeFilePath);
            if (cleanPath == null)
            {
                ClearLogPanel();
                LogHeader("RESTORE FILE - INVALID PATH");
                LogError("Path is outside the working copy or invalid.");
                LogDetail($"WorkingDir: {svnManager?.WorkingDir}");
                LogDetail($"Input:      {relativeFilePath}");
                LogEnd(false);
                return;
            }
            if (cleanPath.Length == 0)
            {
                ClearLogPanel();
                LogHeader("RESTORE FILE - INVALID PATH");
                LogError("Path points to the working copy root - a FILE path is required.");
                LogEnd(false);
                return;
            }

            // Type guard: svn cat works on files only
            string wdEarly = svnManager?.WorkingDir;
            if (!string.IsNullOrEmpty(wdEarly))
            {
                string absTarget = Path.GetFullPath(Path.Combine(wdEarly, cleanPath.Replace('/', Path.DirectorySeparatorChar)));
                if (Directory.Exists(absTarget) && !File.Exists(absTarget))
                {
                    ClearLogPanel();
                    LogHeader("RESTORE FILE - TARGET IS A FOLDER");
                    LogError("RESTORE FILE works on FILES only. The selected path is a folder.");
                    LogInfoBox(new[]
                    {
                        "For folders use:",
                        "  - EXTRACT FOLDER      (standalone snapshot of a revision)",
                        "  - UPDATE TO REVISION  (whole working copy)"
                    });
                    LogEnd(false);
                    return;
                }
            }

            if (!TryEnterProcessing()) return;

            string rev = (revision ?? "").Trim().TrimStart('r', 'R');
            ClearLogPanel();
            LogHeader($"RESTORE FILE - {cleanPath} @ r{rev}");
            StartOperationTimer();

            try
            {
                string workingDir = svnManager.WorkingDir;

                LogStep(1, 3, "Resolving target branch URL...");
                string branchUrl = await GetBranchUrlAsync(workingDir, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(branchUrl))
                {
                    LogError("Cannot determine branch URL. Is this working copy valid?");
                    LogEnd(false);
                    return;
                }

                string fullUrl = $"{branchUrl.TrimEnd('/')}/{cleanPath.TrimStart('/')}";
                LogDetail($"URL: {fullUrl}");

                string fullDiskPath = Path.Combine(workingDir, cleanPath.Replace('/', Path.DirectorySeparatorChar));
                bool existedBefore = File.Exists(fullDiskPath);
                LogDetail($"Local path: {fullDiskPath}");
                LogDetail($"Target state: {(existedBefore ? "Will overwrite existing local file" : "Will create new file")}");

                string destDir = Path.GetDirectoryName(fullDiskPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                LogStep(2, 3, "Fetching file content from revision (svn cat)...");
                string args = $"cat -r {rev} \"{fullUrl}\"";
                LogCmd(args);
                var (exitCode, error) = await SvnRunner.RunToFileAsync(args, workingDir, fullDiskPath);

                if (exitCode != 0)
                {
                    LogError($"Restore action failed (Exit Code {exitCode}).");
                    if (!string.IsNullOrWhiteSpace(error))
                        LogDetail(ExtractSvnError(error));
                    if (error?.Contains("E200009") == true)
                        LogWarning("File path may not exist in specified revision r" + rev);
                    try { if (File.Exists(fullDiskPath)) File.Delete(fullDiskPath); } catch { }
                    LogEnd(false);
                    return;
                }

                long fileSize = 0;
                try { fileSize = new FileInfo(fullDiskPath).Length; } catch { }

                LogStep(3, 3, "Synchronizing SVN workspace status...");
                await svnManager.RefreshStatus();

                LogSuccess($"File successfully restored: {cleanPath} (r{rev})");
                LogDetail($"Extracted file size: {FormatFileSize(fileSize)}");
                LogInfoBox(new[]
                {
                    "ACTION REQUIRED: Use Commit to write this restored version into the repository."
                });
                StopOperationTimer();
                LogDetail($"Elapsed time: {GetElapsedTime()}");
                LogEnd(true);
            }
            catch (Exception ex)
            {
                StopOperationTimer();
                LogError($"[Restore File Error] {ex.Message}");
                LogEnd(false);
            }
            finally
            {
                ExitProcessing();
            }
        }

        // ========== EXTRACT SINGLE FILE ==========
        public async Task ExtractSingleFileToAsync(string relativeFilePath, string revision, string destinationPath)
        {
            if (IsProcessing)
            {
                LogWarning("Processing busy. Cannot extract file right now.");
                return;
            }

            // Path resolution (absolute or relative input, "." = root)
            string cleanPath = ResolveRelativeInsideWorkingDir(relativeFilePath);
            if (cleanPath == null)
            {
                ClearLogPanel();
                LogHeader("EXTRACT FILE - INVALID PATH");
                LogError("Path is outside the working copy or invalid.");
                LogDetail($"WorkingDir: {svnManager?.WorkingDir}");
                LogDetail($"Input:      {relativeFilePath}");
                LogEnd(false);
                return;
            }
            if (cleanPath.Length == 0)
            {
                ClearLogPanel();
                LogHeader("EXTRACT FILE - INVALID PATH");
                LogError("Path points to the working copy root - a FILE path is required.");
                LogEnd(false);
                return;
            }

            // Type guard: svn cat works on files only
            string wdEarly = svnManager?.WorkingDir;
            if (!string.IsNullOrEmpty(wdEarly))
            {
                string absTarget = Path.GetFullPath(Path.Combine(wdEarly, cleanPath.Replace('/', Path.DirectorySeparatorChar)));
                if (Directory.Exists(absTarget) && !File.Exists(absTarget))
                {
                    ClearLogPanel();
                    LogHeader("EXTRACT FILE - TARGET IS A FOLDER");
                    LogError("EXTRACT FILE works on FILES only. The selected path is a folder.");
                    LogDetail("Use EXTRACT FOLDER instead.");
                    LogEnd(false);
                    return;
                }
            }

            if (!TryEnterProcessing()) return;

            string rev = (revision ?? "").Trim().TrimStart('r', 'R');
            ClearLogPanel();
            LogHeader($"EXTRACT FILE - {cleanPath} @ r{rev}");
            StartOperationTimer();

            try
            {
                string workingDir = svnManager.WorkingDir;

                LogStep(1, 3, "Resolving target branch URL...");
                string branchUrl = await GetBranchUrlAsync(workingDir, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(branchUrl))
                {
                    LogError("Cannot determine branch URL. Is this working copy valid?");
                    LogEnd(false);
                    return;
                }

                string fullUrl = $"{branchUrl.TrimEnd('/')}/{cleanPath.TrimStart('/')}";
                LogDetail($"URL: {fullUrl}");
                LogDetail($"Export Dest: {destinationPath}");

                string destDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                LogStep(2, 3, "Downloading requested file version (svn cat)...");
                string args = $"cat -r {rev} \"{fullUrl}\"";
                LogCmd(args);
                var (exitCode, error) = await SvnRunner.RunToFileAsync(args, workingDir, destinationPath);

                if (exitCode != 0)
                {
                    LogError($"Extract action failed (Exit code {exitCode}).");
                    if (!string.IsNullOrWhiteSpace(error))
                        LogDetail(ExtractSvnError(error));
                    if (error?.Contains("E200009") == true)
                        LogWarning("File path may not exist in specified revision r" + rev);
                    LogEnd(false);
                    return;
                }

                long fileSize = 0;
                try { fileSize = new FileInfo(destinationPath).Length; } catch { }

                LogStep(3, 3, "Extraction finished.");
                LogSuccess($"File successfully extracted to: {destinationPath}");
                LogDetail($"Saved Size: {FormatFileSize(fileSize)}");
                LogDetail("Note: Standalone copy extracted. SVN status and local working copy are unmodified.");
                StopOperationTimer();
                LogDetail($"Elapsed time: {GetElapsedTime()}");
                LogEnd(true);
            }
            catch (Exception ex)
            {
                StopOperationTimer();
                LogError($"[Extract File Error] {ex.Message}");
                LogEnd(false);
            }
            finally
            {
                ExitProcessing();
            }
        }

        // ========== EXTRACT FOLDER ==========
        public async Task ExtractFolderToAsync(string relativeFolderPath, string revision, string targetLocalPath)
        {
            if (string.IsNullOrWhiteSpace(relativeFolderPath) || string.IsNullOrWhiteSpace(targetLocalPath))
                throw new ArgumentException("Folder path and target path cannot be empty.");
            if (string.IsNullOrWhiteSpace(revision))
                throw new ArgumentException("Revision cannot be empty.", nameof(revision));

            if (IsProcessing)
            {
                LogWarning("Processing busy. Cannot extract folder right now.");
                return;
            }

            // Path resolution (absolute or relative input; "" = root - allowed)
            string cleanPath = ResolveRelativeInsideWorkingDir(relativeFolderPath);
            if (cleanPath == null)
            {
                ClearLogPanel();
                LogHeader("EXTRACT FOLDER - INVALID PATH");
                LogError("Path is outside the working copy or invalid.");
                LogDetail($"WorkingDir: {svnManager?.WorkingDir}");
                LogDetail($"Input:      {relativeFolderPath}");
                LogEnd(false);
                return;
            }

            // Type guard: svn export -r on a file would yield a single file, not a folder
            string wdEarly = svnManager?.WorkingDir;
            if (!string.IsNullOrEmpty(wdEarly) && cleanPath.Length > 0)
            {
                string absTarget = Path.GetFullPath(Path.Combine(wdEarly, cleanPath.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(absTarget))
                {
                    ClearLogPanel();
                    LogHeader("EXTRACT FOLDER - TARGET IS A FILE");
                    LogError("EXTRACT FOLDER works on FOLDERS only. The selected path is a file.");
                    LogDetail("Use EXTRACT FILE instead.");
                    LogEnd(false);
                    return;
                }
            }

            if (!TryEnterProcessing()) return;

            string normalizedPath = cleanPath.Length == 0 ? "." : SvnRunner.NormalizeRepositoryPath(cleanPath);
            string rev = revision.Trim().TrimStart('r', 'R');
            string revDisplay = rev.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ? "HEAD" : $"r{rev}";

            ClearLogPanel();
            LogHeader($"EXTRACT FOLDER - {(cleanPath.Length == 0 ? "<repository root>" : normalizedPath)} @ {revDisplay}");
            StartOperationTimer();

            try
            {
                // STEP 1: folder URL
                LogStep(1, 3, "Resolving remote folder URL...");

                string folderUrl = null;

                if (cleanPath.Length > 0)
                {
                    var (infoOut, infoErr, infoExit) = await SvnRunner.RunDetailedAsync(
                        $"info --show-item url \"{normalizedPath}\"",
                        svnManager.WorkingDir, retryOnLock: false, throwOnError: false);
                    if (infoExit == 0 && !string.IsNullOrWhiteSpace(infoOut))
                        folderUrl = infoOut.Trim();
                    else
                        LogDetail("Local info check missed path - attempting manual URL construction...");
                }

                if (string.IsNullOrWhiteSpace(folderUrl))
                {
                    var (rootOut, rootErr, rootExit) = await SvnRunner.RunDetailedAsync(
                        "info --show-item url", svnManager.WorkingDir, retryOnLock: true, throwOnError: false);
                    if (rootExit != 0 || string.IsNullOrWhiteSpace(rootOut))
                    {
                        LogError($"Cannot determine repository URL (exit {rootExit}): {ExtractSvnError(rootErr)}");
                        LogEnd(false);
                        return;
                    }
                    string root = rootOut.Trim().TrimEnd('/');
                    folderUrl = cleanPath.Length == 0 ? root : $"{root}/{normalizedPath.TrimStart('/')}";
                }

                LogDetail($"Source URL: {folderUrl}");
                LogDetail($"Target Disk Path: {targetLocalPath}");

                // STEP 2 (pre-check): does the folder exist at this revision?
                LogStep(2, 3, $"Verifying folder exists at {revDisplay}...");
                var (lsOut, lsErr, lsExit) = await SvnRunner.RunDetailedAsync(
                    $"ls -r {rev} \"{folderUrl}\"",
                    svnManager.WorkingDir, retryOnLock: false, throwOnError: false);

                if (lsExit != 0)
                {
                    string lsError = ExtractSvnError(lsErr);

                    if (lsError.Contains("E195012"))
                    {
                        LogError($"Folder does NOT exist at {revDisplay} under this path.");
                        LogDetail("SVN URLs are not stable over time - the folder may have been");
                        LogDetail("added later, or moved/renamed after this revision.");

                        // Find the earliest revision in which this path has history:
                        long? earliest = null;
                        try
                        {
                            var (logOut, logErr2, logExit2) = await SvnRunner.RunDetailedAsync(
                                $"log -r 1:HEAD --limit 1 --stop-on-copy \"{folderUrl}\"",
                                svnManager.WorkingDir, retryOnLock: false, throwOnError: false);
                            if (logExit2 == 0 && !string.IsNullOrWhiteSpace(logOut))
                                earliest = TryGetFirstRevisionFromLog(logOut);
                        }
                        catch { /* non-blocking */ }

                        if (earliest.HasValue)
                        {
                            LogInfoBox(new[]
                            {
                                $"Earliest revision of this folder: r{earliest.Value}",
                                "",
                                "Options:",
                                $"  - enter a revision >= r{earliest.Value} and retry",
                                "  - or leave the field empty (HEAD) to export the latest state"
                            });
                        }
                        else
                        {
                            LogInfoBox(new[]
                            {
                                "Options:",
                                "  - use a NEWER revision (folder was probably added later)",
                                "  - or leave the field empty (HEAD)",
                                "  - verify manually:  svn ls -r " + rev + " <folder URL>"
                            });
                        }
                    }
                    else
                    {
                        LogError($"Folder verification failed (exit {lsExit}): {lsError}");
                    }
                    LogEnd(false);
                    return;
                }

                LogDetail("Folder found - proceeding with export.");

                // STEP 3: export
                LogStep(3, 3, $"Exporting folder structure for {revDisplay} (svn export)...");
                string command = $"export -r {rev} \"{folderUrl}\" \"{targetLocalPath}\" --force";
                LogCmd(command);

                var (expOut, expErr, expExit) = await SvnRunner.RunDetailedAsync(
                    command, svnManager.WorkingDir, retryOnLock: true);
                if (expExit != 0)
                {
                    LogError($"svn export failed (exit code {expExit}): {ExtractSvnError(expErr)}");
                    LogEnd(false);
                    return;
                }

                try
                {
                    int fileCount = Directory.GetFiles(targetLocalPath, "*", SearchOption.AllDirectories).Length;
                    long size = CalculateFolderSize(targetLocalPath);
                    int dirCount = Directory.GetDirectories(targetLocalPath, "*", SearchOption.AllDirectories).Length;

                    LogSuccess($"Folder exported to: {targetLocalPath}");
                    LogInfoBox(new[]
                    {
                        $"Extracted Files:   {fileCount}",
                        $"Extracted Folders: {dirCount}",
                        $"Total Size:        {FormatFileSize(size)}"
                    });
                }
                catch
                {
                    LogSuccess($"Folder exported to: {targetLocalPath}");
                }
                StopOperationTimer();
                LogDetail($"Elapsed time: {GetElapsedTime()}");
                LogEnd(true);
            }
            catch (Exception ex)
            {
                StopOperationTimer();
                LogError($"[Extract Folder Error] {ex.Message}");
                LogEnd(false);
            }
            finally
            {
                ExitProcessing();
            }
        }

        // ========== REVERT PATH (dispatcher: revision optional) ==========
        // svn revert does NOT accept a revision (no -r option). The revision field changes the MODE:
        //   empty   -> classic revert of local changes to BASE (svn revert)
        //   filled  -> roll the FILE back to that revision's state (svn cat -r + Commit)
        public async Task RevertPathAsync(string pathFromInput)
        {
            if (string.IsNullOrWhiteSpace(pathFromInput))
            {
                LogWarning("[Revert Path] Target path cannot be empty.");
                return;
            }

            if (IsProcessing)
            {
                LogWarning("Processing busy. Cannot revert path right now.");
                return;
            }

            // Optional revision number from UpdateRevisionInput
            string revRaw = svnUI?.UpdateRevisionInput?.text?.Trim();
            string rev = revRaw?.TrimStart('r', 'R');
            bool hasRevision = !string.IsNullOrWhiteSpace(revRaw);

            if (hasRevision && (!long.TryParse(rev, out long revNum) || revNum <= 0))
            {
                ClearLogPanel();
                LogHeader("REVERT PATH - INVALID REVISION");
                LogError($"Invalid revision format: \"{revRaw}\". Enter numbers only (e.g. 150).");
                LogDetail("Tip: leave the revision field EMPTY to revert local modifications only.");
                LogEnd(false);
                return;
            }

            // === MODE 2: ROLL FILE BACK TO A REVISION (revision field filled) ===
            if (hasRevision)
            {
                // Double-click confirmation (separate key so it does not clash with Revert Commits)
                string key = $"REVERT|{pathFromInput}|r{rev}";
                float timeSinceLastClick = Time.time - _lastRevertPathClickTime;

                if (timeSinceLastClick >= DoubleClickThreshold || _pendingRevertPathKey != key)
                {
                    _lastRevertPathClickTime = Time.time;
                    _pendingRevertPathKey = key;

                    ClearLogPanel();
                    LogHeader($"CONFIRMATION REQUIRED - ROLLBACK FILE TO r{rev}");
                    LogWarning("The file will be OVERWRITTEN with its content from revision r" + rev + ".");
                    LogDetail("After Commit, the repository copy of this file will match r" + rev + ".");
                    LogDetail("Leave the revision field EMPTY to revert local modifications instead.");
                    LogDetail("Click the REVERT button ONE MORE TIME within 5 seconds to confirm.");
                    return;
                }
                _pendingRevertPathKey = null;

                string workingDir = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(workingDir))
                {
                    ClearLogPanel();
                    LogHeader("ROLLBACK TO REVISION - ERROR");
                    LogError("svnManager.WorkingDir is not set.");
                    LogEnd(false);
                    return;
                }

                string cleanRel = ResolveRelativeInsideWorkingDir(pathFromInput);
                if (cleanRel == null)
                {
                    ClearLogPanel();
                    LogHeader("ROLLBACK TO REVISION - INVALID PATH");
                    LogError("Path is outside the working copy or invalid.");
                    LogDetail($"WorkingDir: {workingDir}");
                    LogDetail($"Input:      {pathFromInput}");
                    LogEnd(false);
                    return;
                }
                if (cleanRel.Length == 0)
                {
                    ClearLogPanel();
                    LogHeader("ROLLBACK TO REVISION - INVALID PATH");
                    LogError("Path points to the working copy root - a FILE path is required.");
                    LogEnd(false);
                    return;
                }

                string absPath = Path.GetFullPath(Path.Combine(workingDir, cleanRel.Replace('/', Path.DirectorySeparatorChar)));
                if (Directory.Exists(absPath) && !File.Exists(absPath))
                {
                    ClearLogPanel();
                    LogHeader("ROLLBACK TO REVISION - FOLDERS NOT SUPPORTED");
                    LogError("Rolling back a FOLDER to a revision is not supported here.");
                    LogInfoBox(new[]
                    {
                        "For folders use:",
                        "  - EXTRACT FOLDER      (standalone snapshot of r" + rev + ")",
                        "  - UPDATE TO REVISION  (whole working copy)"
                    });
                    LogEnd(false);
                    return;
                }

                // Delegation to the proven svn cat implementation (-> overwrite -> M -> Commit)
                await RestoreSingleFileAsync(cleanRel, rev);
                return;
            }

            // === MODE 1: CLASSIC REVERT OF LOCAL CHANGES (revision field empty) ===
            if (!TryEnterProcessing()) return;

            ClearLogPanel();
            LogHeader($"REVERT PATH - {pathFromInput}");
            StartOperationTimer();

            try
            {
                string workingDir = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(workingDir))
                {
                    LogError("[Revert Path] svnManager.WorkingDir is not set.");
                    LogEnd(false);
                    return;
                }

                string cleanRel = ResolveRelativeInsideWorkingDir(pathFromInput);
                if (cleanRel == null)
                {
                    LogError("Target is OUTSIDE the working copy or invalid - cannot revert.");
                    LogDetail($"WorkingDir: {workingDir}");
                    LogDetail($"Input:      {pathFromInput}");
                    LogEnd(false);
                    return;
                }

                // Absolute path for SVN commands (independent of the process cwd);
                // cleanRel == "" (root / ".") -> revert the whole working copy
                string absPath = cleanRel.Length == 0
                    ? Path.GetFullPath(workingDir)
                    : Path.GetFullPath(Path.Combine(workingDir, cleanRel.Replace('/', Path.DirectorySeparatorChar)));

                bool isDir = Directory.Exists(absPath) && !File.Exists(absPath);
                string typeDesc = isDir ? "folder (recursive)" : "file";

                // Status pre-check - full result (no exception)
                LogStep(1, 3, $"Checking uncommitted changes on {typeDesc}...");
                var (stOut, stErr, stExit) = await SvnRunner.RunDetailedAsync(
                    $"status \"{absPath}\"", workingDir, retryOnLock: false, throwOnError: false);

                if (stExit != 0)
                    LogWarning($"Status pre-check failed (exit {stExit}): {ExtractSvnError(stErr)}");

                var statusLines = (stOut ?? "")
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();

                bool targetUnversioned = statusLines.Any(l =>
                    l.StartsWith("?") && StatusLineRefersToPath(l, absPath, cleanRel));
                int changeCount = statusLines.Count(l => l[0] != '?');

                if (targetUnversioned)
                {
                    LogWarning("Target is NOT under version control (status '?').");
                    LogDetail("SVN has never tracked this file - there is nothing to revert.");
                    LogInfoBox(new[]
                    {
                        "Options:",
                        "  1. Track it:      Add + Commit",
                        "  2. Remove it:     delete it manually (SVN ignores it)",
                        "  3. Wrong target?  Browse the ORIGINAL versioned file, not the copy"
                    });
                    StopOperationTimer();
                    LogDetail($"Elapsed time: {GetElapsedTime()}");
                    LogEnd(true);   // a no-op is NOT an error
                    return;
                }

                if (changeCount == 0)
                {
                    LogWarning("Nothing to revert - this target is already clean.");
                    LogDetail("svn revert only discards UNCOMMITTED local edits.");
                    LogDetail("It never needs a revision - it always goes back to BASE.");
                    LogInfoBox(new[]
                    {
                        "If you expected an action, pick your actual intent:",
                        "  - my local edits are gone?       they were already discarded",
                        "  - want the newest server state?  use UPDATE",
                        "  - want an OLD version of it?     RESTORE FILE / EXTRACT FILE",
                        "    (these DO need a revision - they must know which point",
                        "     in time to roll back to)"
                    });
                    StopOperationTimer();
                    LogDetail($"Elapsed time: {GetElapsedTime()}");
                    LogEnd(true);
                    return;
                }

                LogDetail($"{changeCount} uncommitted change(s) will be discarded.");

                // Revert with an absolute path - full result (stderr lands in the panel)
                LogStep(2, 3, $"Reverting {typeDesc}...");
                string depthFlag = isDir ? "--depth infinity " : "";
                string revertArgs = $"revert {depthFlag}\"{absPath}\"";
                LogCmd(revertArgs);

                var (revOut, revErr, revExit) = await SvnRunner.RunDetailedAsync(
                    revertArgs, workingDir, retryOnLock: true, throwOnError: false);

                foreach (var line in (revErr ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!string.IsNullOrWhiteSpace(line)) LogWarning(ExtractSvnError(line));

                if (revExit != 0)
                {
                    LogError($"svn revert failed (exit code {revExit}).");
                    LogEnd(false);
                    return;
                }

                bool reverted = (revOut ?? "").IndexOf("Reverted", StringComparison.OrdinalIgnoreCase) >= 0;
                if (reverted) LogSuccess($"SVN reverted: {absPath}");
                else LogWarning("SVN did not report any 'Reverted' entries.");

                LogStep(3, 3, "Refreshing working copy status...");
                await svnManager.RefreshStatus();

                StopOperationTimer();
                LogDetail($"Elapsed time: {GetElapsedTime()}");
                LogEnd(reverted);
            }
            catch (Exception ex)
            {
                StopOperationTimer();
                LogError($"[Revert Path Error] {ex.Message}");
                LogEnd(false);
            }
            finally
            {
                ExitProcessing();
            }
        }

        // ========== ERROR PARSING HELPERS ==========
        /// <summary>
        /// Extracts the actual SVN message from raw stderr - strips the SSH banner
        /// ("WARNING! You are entering a restricted access system..." etc.) that the
        /// server prepends to every connection.
        /// </summary>
        private static string ExtractSvnError(string rawError)
        {
            if (string.IsNullOrWhiteSpace(rawError)) return "";
            int idx = rawError.IndexOf("svn:", StringComparison.Ordinal);
            return (idx >= 0 ? rawError.Substring(idx) : rawError).Trim();
        }

        /// <summary>
        /// Parses the first revision from 'svn log' output (header line "rNNN | author | date").
        /// </summary>
        private static long? TryGetFirstRevisionFromLog(string logOutput)
        {
            foreach (var raw in logOutput.Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.Length > 1 && line[0] == 'r' && char.IsDigit(line[1]))
                {
                    int end = 1;
                    while (end < line.Length && char.IsDigit(line[end])) end++;
                    if (long.TryParse(line.Substring(1, end - 1), out long r))
                        return r;
                }
            }
            return null;
        }
    }
}