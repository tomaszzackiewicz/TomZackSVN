using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNCommit : SVNBase
    {
        private CancellationTokenSource _commitCTS;
        private List<SVNStatusElement> _items = new List<SVNStatusElement>();
        private const double BytesConversionFactor = 1024.0;

        public SVNCommit(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void CancelOperation()
        {
            if (_commitCTS == null || !IsProcessing) return;
            _commitCTS.Cancel();
            SVNLogBridge.UpdateUIField(
                svnUI.CommitConsoleContent,
                "\n<color=orange><b>[System]</b> Operation cancelled by user.</color>",
                append: true);
        }

        private string FormatCommitSize(long bytes)
        {
            if (bytes == 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int order = 0;
            while (size >= BytesConversionFactor && order < suffixes.Length - 1)
            {
                order++;
                size /= BytesConversionFactor;
            }
            return $"{size:0.##} {suffixes[order]}";
        }

        public async void ShowWhatWillBeCommitted()
        {
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir);
            var commitables = statusDict.Where(x => "MADC?".Contains(x.Value.status)).ToList();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<b>Current changes to send:</b>");
            foreach (var item in commitables)
                sb.AppendLine($"[{item.Value.status}] {SvnRunner.NormalizeRepositoryPath(item.Key)}");

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: true);
        }

        public async void RefreshCommitList()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir);
                _items = statusDict
                    .Where(x => "MADC?".Contains(x.Value.status))
                    .Select(x => new SVNStatusElement
                    {
                        FullPath = SvnRunner.NormalizeRepositoryPath(x.Key),
                        Status = x.Value.status,
                        IsChecked = true
                    })
                    .ToList();

                RenderCommitList(_items);
            }
            finally { IsProcessing = false; }
        }

        public void RenderCommitList(List<SVNStatusElement> items)
        {
            if (items.Count == 0)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "No changes to commit.", append: false);
                return;
            }

            long totalSize = 0;
            string root = svnManager.WorkingDir.Replace("\\", "/").TrimEnd('/');
            foreach (var item in items)
            {
                if (!item.IsChecked || item.Status == "!" || item.Status == "D") continue;
                string full = Path.Combine(root, item.FullPath);
                if (File.Exists(full))
                {
                    try { totalSize += new FileInfo(full).Length; } catch { }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"<b>Files to be committed</b> (Payload: <color=blue>{FormatCommitSize(totalSize)}</color>):");

            foreach (var item in items)
            {
                string color = item.Status switch
                {
                    "M" => "yellow",
                    "A" => "green",
                    "?" => "#00E5FF",
                    "D" => "red",
                    _ => "white"
                };
                sb.AppendLine($"<color={color}>[{item.Status}]</color> {item.FullPath}");
            }

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: false);
        }

        public List<SvnTreeElement> GetSelectedFiles()
        {
            var status = svnManager.GetModule<SVNStatus>();
            if (status != null)
                return status.GetCurrentData().Where(e => e.IsChecked && !e.IsFolder).ToList();
            return new List<SvnTreeElement>();
        }

        public async void ExecuteRevertAllMissing()
        {
            await RevertAllMissing();
            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=green><b>[System]</b> Repair process finished.</color>", append: true);
        }

        private async Task RevertAllMissing()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            string root = svnManager.WorkingDir;

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[Revert]</b> Starting recovery of missing files...", append: false);
            try
            {
                string rawStatus = await SvnRunner.RunAsync("status", root);
                var filesToRevert = new List<string>();

                using (var reader = new StringReader(rawStatus))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string path = svnManager.ExtractPathFromStatusLine(line, "!");
                        if (path != null)
                        {
                            path = SvnRunner.NormalizeRepositoryPath(path); // ← NORMALIZACJA
                            if (!string.IsNullOrWhiteSpace(path))
                                filesToRevert.Add(path);
                        }
                    }
                }

                if (filesToRevert.Count == 0)
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=green>No missing files found.</color>", append: true);
                    return;
                }

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"Found {filesToRevert.Count} missing files. Restoring...", append: true);

                const int batchSize = 20;
                for (int i = 0; i < filesToRevert.Count; i += batchSize)
                {
                    var batch = filesToRevert.Skip(i).Take(batchSize).Select(p => $"\"{p}\"");
                    await SvnRunner.RunAsync($"revert {string.Join(" ", batch)}", root);
                }

                var statusModule = svnManager.GetModule<SVNStatus>();
                statusModule?.ClearCurrentData();
                if (svnUI.TreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                if (svnUI.CommitTreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                await statusModule.ExecuteRefreshWithAutoExpand();

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<color=green><b>SUCCESS!</b></color> Missing files restored.", append: true);
            }
            catch (Exception ex)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=#FFAA00>Revert Error:</color> {ex.Message}", append: true);
            }
            finally { IsProcessing = false; }
        }

        public void CommitSelected()
        {
            string message = svnUI.CommitMessageInput?.text;
            if (string.IsNullOrWhiteSpace(message))
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=#FFAA00>Error:</color> Please enter a commit message!", append: true);
                return;
            }
            _ = ExecuteCommitSelected(message);
        }

        public async void CommitAll()
        {
            if (IsProcessing) return;
            await svnManager.CancelBackgroundTasksAsync();

            string message = svnUI.CommitMessageInput?.text;
            if (string.IsNullOrWhiteSpace(message))
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=#FFAA00>Error:</color> Commit message is empty!", append: true);
                return;
            }

            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();
            var token = _commitCTS.Token;
            string root = svnManager.WorkingDir.Replace("\\", "/").TrimEnd('/');

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0.05f;
            }

            try
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>Initiating commit...</b>\n", append: false);

                // [1/4] Cleanup
                await CleanupWorkingCopy(root, token);
                UpdateProgress(0.2f);

                // [2/4] Fix missing files
                var missing = new List<string>();
                string rawStatus = await SvnRunner.RunAsync("status", root, true, token);
                if (!string.IsNullOrEmpty(rawStatus))
                {
                    using (var reader = new StringReader(rawStatus))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string path = svnManager.ExtractPathFromStatusLine(line, "!");
                            if (path != null)
                            {
                                if (path.Contains("@") && !path.EndsWith("@")) path += "@";
                                missing.Add(path);
                            }
                        }
                    }
                }
                await FixMissingFiles(root, missing, token);
                UpdateProgress(0.4f);

                // [3/4] Add new files
                var unversioned = new List<string>();
                string statusForAdd = await SvnRunner.RunAsync("status", root, true, token);
                if (!string.IsNullOrEmpty(statusForAdd))
                {
                    using (var reader = new StringReader(statusForAdd))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.StartsWith("?"))
                            {
                                string rawPath = line.Substring(8).TrimStart();
                                string cleanPath = SvnRunner.NormalizeRepositoryPath(rawPath);
                                if (!string.IsNullOrWhiteSpace(cleanPath))
                                    unversioned.Add(cleanPath);
                            }
                        }
                    }
                }
                await AddNewFiles(root, unversioned, token);
                UpdateProgress(0.6f);

                // [4/4] Commit
                string result = await SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root, true, token);

                if (result.Contains("Committed revision"))
                {
                    string rev = svnManager.ParseRevision(result);
                    UpdateProgress(1.0f);
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=green><b>SUCCESS!</b></color> Revision: {rev}", append: true);

                    SVNStatus.ClearLockCache();
                    svnManager._diskChangesDetected = true;
                    ClearCommitUI();
                    svnManager.GetModule<SVNStatus>()?.ClearCurrentData();
                }
                else
                {
                    string info = string.IsNullOrWhiteSpace(result) ? "Nothing to commit." : result;
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=yellow>Result:</color> {info}", append: true);
                    ClearCommitUI();
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<color=orange><b>[ABORTED]</b></color> User cancelled.", append: true);
            }
            catch (Exception ex)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=#FFAA00>Error:</color> {ex.Message}", append: true);
            }
            finally
            {
                IsProcessing = false;
                _commitCTS?.Dispose();
                _commitCTS = null;
                HideProgressBarAfterDelay(2.0f);
                await svnManager.RefreshStatus();
            }
        }

        public async Task ExecuteCommitSelected(string message)
        {
            if (IsProcessing) return;

            await svnManager.CancelBackgroundTasksAsync();

            string root = svnManager.WorkingDir.Replace('\\', '/').TrimEnd('/');

            var statusModule = svnManager.GetModule<SVNStatus>();
            if (statusModule == null)
            {
                LogToConsole("\n<color=#FFAA00>Error:</color> SVN Status module not found.");
                return;
            }

            var allElements = statusModule.GetCurrentData();
            var selectedItems = allElements?
                .Where(e => e.IsChecked && !e.IsFolder)
                .ToList() ?? new List<SvnTreeElement>();

            if (allElements == null || allElements.Count == 0)
            {
                LogToConsole("\n<color=yellow>No SVN changes detected.\nWorking copy is already clean.</color>");
                return;
            }

            if (selectedItems.Count == 0)
            {
                LogToConsole("\n<color=orange>Nothing selected for commit.</color>");
                return;
            }

            selectedItems = selectedItems
                .Where(e => "MADC?!".Contains(e.Status))
                .ToList();

            if (selectedItems.Count == 0)
            {
                LogToConsole("\n<color=yellow>No valid files to commit.</color>");
                return;
            }

            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();
            CancellationToken token = _commitCTS.Token;
            message = message.Replace("\"", "\\\"");

            try
            {
                LogToConsole("\n<b>Initiating commit...</b>\n");

                // [1/4] Cleanup
                await CleanupWorkingCopy(root, token);

                // [2/4] Fix missing files
                var missingPaths = selectedItems
                    .Where(e => e.Status == "!" || e.Status == "D")
                    .Select(e => e.FullPath);
                await FixMissingFiles(root, missingPaths, token);

                // [3/4] Add new files
                var newPaths = selectedItems
                    .Where(e => e.Status == "?" || e.Status == "A")
                    .Select(e => e.FullPath);
                await AddNewFiles(root, newPaths, token);

                // [4/4] Commit selected
                var allTargets = selectedItems.Select(e => e.FullPath);
                string result = await CommitTargets(root, allTargets, message, token);

                if (result.Contains("Committed revision"))
                {
                    string rev = svnManager.ParseRevision(result);
                    LogToConsole(
                        $"\n<color=green><b>SUCCESS!</b></color>" +
                        $"\nRevision: <b>{rev}</b>" +
                        $"\nCommitted items: <b>{selectedItems.Count}</b>");

                    SVNStatus.ClearLockCache();
                    svnManager._diskChangesDetected = true;
                    statusModule.ClearCurrentData();
                    ClearCommitUI();

                    if (svnUI.CommitMessageInput != null)
                        svnUI.CommitMessageInput.text = "";

                    await svnManager.RefreshStatus();
                }
                else
                {
                    string shortResult = string.IsNullOrWhiteSpace(result)
                        ? "Nothing to commit."
                        : result.Length > 500
                            ? result.Substring(0, 500) + "..."
                            : result;
                    LogToConsole($"\n<color=yellow>Result:</color> {shortResult}");
                    ClearCommitUI();
                }
            }
            catch (OperationCanceledException)
            {
                LogToConsole("\n<color=orange><b>[ABORTED]</b></color> Commit cancelled.");
            }
            catch (Exception ex)
            {
                LogToConsole($"\n<color=#FFAA00>Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _commitCTS?.Dispose();
                _commitCTS = null;
            }
        }

        private void ClearCommitUI()
        {
            if (svnUI.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
            if (svnUI.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();
            if (svnUI.TreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
            if (svnUI.CommitTreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
        }

        private void UpdateProgress(float value)
        {
            if (svnUI.OperationProgressBar != null)
                svnUI.OperationProgressBar.value = value;
        }

        private async void HideProgressBarAfterDelay(float seconds)
        {
            await Task.Delay((int)(seconds * 1000));
            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(false);
                svnUI.OperationProgressBar.value = 0f;
            }
        }

        private async Task<bool> CleanupWorkingCopy(string root, CancellationToken token)
        {
            LogToConsole("\n<b>[1/4]</b> Cleaning up database...");

            await SvnRunner.RunAsync(
                "cleanup",
                root,
                false,
                token);

            return true;
        }

        private async Task FixMissingFiles(
    string root,
    IEnumerable<string> files,
    CancellationToken token)
        {
            var deletions = files
                .Select(SvnRunner.NormalizeRepositoryPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (deletions.Count == 0)
                return;

            LogToConsole("\n<b>[2/4]</b> Fixing missing files...");

            string file = Path.Combine(
                Path.GetTempPath(),
                $"svn_delete_{Guid.NewGuid():N}.txt");

            File.WriteAllLines(file, deletions);

            try
            {
                await SvnRunner.RunAsync(
                    $"delete --force --targets \"{file}\"",
                    root,
                    false,
                    token);
            }
            finally
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
        }

        private async Task AddNewFiles(string root, IEnumerable<string> files, CancellationToken token)
        {
            var additions = files
                .Select(SvnRunner.NormalizeRepositoryPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (additions.Count == 0) return;

            LogToConsole("\n<b>[3/4]</b> Adding new files...");

            string file = Path.Combine(Path.GetTempPath(), $"svn_add_{Guid.NewGuid():N}.txt");
            File.WriteAllLines(file, additions);

            try
            {
                await SvnRunner.RunAsync($"add --force --parents --targets \"{file}\"", root, false, token);
            }
            finally
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        private async Task<string> CommitTargets(
    string root,
    IEnumerable<string> targets,
    string message,
    CancellationToken token)
        {
            var list = targets
                .Select(SvnRunner.NormalizeRepositoryPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.Count == 0)
                return "";

            string file = Path.Combine(
                Path.GetTempPath(),
                $"svn_commit_{Guid.NewGuid():N}.txt");

            File.WriteAllLines(file, list);

            try
            {
                LogToConsole("\n<b>[4/4]</b> Sending to server...");

                return await SvnRunner.RunAsync(
                    $"commit --targets \"{file}\" -m \"{message}\" --non-interactive",
                    root,
                    false,
                    token);
            }
            finally
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
        }

        private void LogToConsole(string msg)
        {
            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, msg, append: true);
        }

        public class SVNStatusElement
        {
            public string FullPath;
            public string Status;
            public bool IsChecked;
            public bool IsExpanded;
            public bool IsFolder;
            public List<SVNStatusElement> Children;
        }
    }
}