using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using UnityEngine;
using System.IO;

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
            if (_commitCTS != null)
            {
                _commitCTS.Cancel();

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<color=orange><b>[System]</b> Operation cancelled by user.</color>", append: true);
            }
        }

        private string NormalizeSvnPath(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;
            var cleaned = new string(input.Where(c => !char.IsControl(c)).ToArray());

            return cleaned
                .Replace("\\", "/")
                .Replace("\t", "")
                .Replace("\r", "")
                .Replace("\n", "")
                .Trim();
        }

        public async void ShowWhatWillBeCommitted()
        {
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir, false);

            var commitables = statusDict
                .Where(x => "MADC?".Contains(x.Value.status))
                .ToList();

            SVNLogBridge.LogLine("<b>Current changes to send:</b>");

            StringBuilder sb = new StringBuilder();

            foreach (var item in commitables)
            {
                string safePath = NormalizeSvnPath(item.Key);
                sb.AppendLine($"[{item.Value.status}] {safePath}");
            }

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: true);
        }

        public async void RefreshCommitList()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir, false);

                var commitables = statusDict
                    .Where(x => "MADC?".Contains(x.Value.status))
                    .Select(x => new SVNStatusElement
                    {
                        FullPath = NormalizeSvnPath(x.Key),
                        Status = x.Value.status,
                        IsChecked = true
                    })
                    .ToList();

                _items = commitables;
                RenderCommitList(_items);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void RenderCommitList(List<SVNStatusElement> items)
        {
            if (items.Count == 0)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "No changes to commit.", append: false);
                return;
            }

            long totalSizeBytes = 0;
            string root = svnManager.WorkingDir.Replace("\\", "/").TrimEnd('/');

            foreach (var item in items)
            {
                if (!item.IsChecked || item.Status == "!" || item.Status == "D")
                    continue;

                string absolutePath = System.IO.Path.Combine(root, item.FullPath);
                if (System.IO.File.Exists(absolutePath))
                {
                    totalSizeBytes += new System.IO.FileInfo(absolutePath).Length;
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"<b>Files to be committed</b> (Payload size: <color=blue>{FormatCommitSize(totalSizeBytes)}</color>):");

            foreach (var item in items)
            {
                string color = item.Status switch
                {
                    "M" => "yellow",
                    "A" => "green",
                    "?" => "#888888",
                    "D" => "red",
                    _ => "white"
                };
                sb.AppendLine($"<color={color}>[{item.Status}]</color> {item.FullPath}");
            }

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: false);
        }

        public List<SvnTreeElement> GetSelectedFiles()
        {
            var statusModule = svnManager.GetModule<SVNStatus>();
            if (statusModule != null)
            {
                return statusModule.GetCurrentData()
                    .Where(e => e.IsChecked && !e.IsFolder)
                    .ToList();
            }
            return new List<SvnTreeElement>();
        }

        private string FormatCommitSize(long bytes)
        {
            if (bytes == 0) return "0 B";

            string[] sizeSuffixes = { "B", "KB", "MB", "GB", "TB" };
            double length = bytes;
            int order = 0;

            while (length >= BytesConversionFactor && order < sizeSuffixes.Length - 1)
            {
                order++;
                length /= BytesConversionFactor;
            }

            return $"{length:0.##} {sizeSuffixes[order]}";
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

            string root = NormalizeSvnPath(svnManager.WorkingDir);

            SVNLogBridge.UpdateUIField(
                svnUI.CommitConsoleContent,
                "<b>[Revert]</b> Starting recovery of missing files...",
                append: false);

            try
            {
                string rawStatus = await SvnRunner.RunAsync("status", root);

                List<string> filesToRevert = new List<string>();

                using (var reader = new StringReader(rawStatus))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = NormalizeSvnPath(line);

                        string path = svnManager.ExtractPathFromStatusLine(line, "!");
                        if (path != null)
                        {
                            filesToRevert.Add(path);
                        }
                    }
                }

                if (filesToRevert.Count == 0)
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent,
                        "<color=green>No missing files found.</color>",
                        append: true);
                    return;
                }

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent,
                    $"Found {filesToRevert.Count} missing files. Restoring...",
                    append: true);

                int batchSize = 20;

                for (int i = 0; i < filesToRevert.Count; i += batchSize)
                {
                    var batch = filesToRevert
                        .Skip(i)
                        .Take(batchSize)
                        .Select(p => $"\"{p}\"");

                    await SvnRunner.RunAsync($"revert {string.Join(" ", batch)}", root);
                }

                var statusModule = svnManager.GetModule<SVNStatus>();
                statusModule?.ClearCurrentData();

                if (svnUI.TreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);

                if (svnUI.CommitTreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);

                await statusModule.ExecuteRefreshWithAutoExpand();

                SVNLogBridge.UpdateUIField(
                    svnUI.CommitConsoleContent,
                    "\n<color=green><b>SUCCESS!</b></color> Missing files restored.",
                    append: true);
            }
            catch (Exception ex)
            {
                SVNLogBridge.UpdateUIField(
                    svnUI.CommitConsoleContent,
                    $"\n<color=red>Revert Error:</color> {ex.Message}",
                    append: true);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void CommitSelected()
        {
            try
            {
                string messageFromUI = svnUI.CommitMessageInput?.text;

                if (string.IsNullOrWhiteSpace(messageFromUI))
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent,
                        "<color=red>Error:</color> Please enter a commit message!", append: true);
                    return;
                }

                _ = ExecuteCommitSelected(messageFromUI);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN] Button click failed: {ex.Message}");
            }
        }

        public async void CommitAll()
        {
            if (IsProcessing) return;

            string message = svnUI.CommitMessageInput?.text;
            if (string.IsNullOrWhiteSpace(message))
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=red>Error:</color> Commit message is empty!", append: true);
                return;
            }

            var statusCheck = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir, false);
            bool hasAnyChanges = statusCheck.Any(x => "MADC?!".Contains(x.Value.status));
            if (!hasAnyChanges)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=yellow>No SVN changes detected.\nWorking copy is already clean.</color>", append: false);
                return;
            }

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0.05f;
            }

            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();

            string root = svnManager.WorkingDir.Replace("\\", "/").TrimEnd('/');

            try
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>Initiating commit...</b>\n", append: false);

                // [1/4] Cleanup
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[1/4]</b> Cleaning up database...", append: true);
                await SvnRunner.RunAsync("cleanup", root, true, _commitCTS.Token);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.2f;

                // [2/4] Scanning & fixing missing files
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[2/4]</b> Scanning & fixing missing files...", append: true);
                string rawStatus = await SvnRunner.RunAsync("status", root, true, _commitCTS.Token);

                List<string> missingFiles = new List<string>();
                if (!string.IsNullOrEmpty(rawStatus))
                {
                    using (var reader = new System.IO.StringReader(rawStatus))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string path = svnManager.ExtractPathFromStatusLine(line, "!");
                            if (path != null)
                            {
                                if (path.Contains("@") && !path.EndsWith("@")) path += "@";
                                missingFiles.Add(path);
                            }
                        }
                    }
                }

                if (missingFiles.Count > 0)
                {
                    string deleteTargets = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "svn_delete_targets.txt");
                    var utf8NoBom = new System.Text.UTF8Encoding(false);
                    System.IO.File.WriteAllLines(deleteTargets, missingFiles, utf8NoBom);
                    _commitCTS.Token.ThrowIfCancellationRequested();
                    await SvnRunner.RunAsync($"delete --force --targets \"{deleteTargets}\"", root, true, _commitCTS.Token);
                    if (System.IO.File.Exists(deleteTargets)) System.IO.File.Delete(deleteTargets);
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\nFixed {missingFiles.Count} missing entries.", append: true);
                }
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.4f;

                // [3/4] Adding all new files
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[3/4]</b> Adding all new files (Recursive)...", append: true);
                await SvnRunner.RunAsync("add . --force --parents --depth infinity", root, true, _commitCTS.Token);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.6f;

                // [4/4] Sending to server
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[4/4]</b> Sending to server...", append: true);
                string commitResult = await SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root, true, _commitCTS.Token);

                if (commitResult.Contains("Committed revision") ||
                    !string.IsNullOrWhiteSpace(svnManager.ParseRevision(commitResult)))
                {
                    string revision = svnManager.ParseRevision(commitResult);
                    if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 1.0f;
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=green><b>SUCCESS!</b></color> Revision: {revision}", append: true);

                    svnManager._diskChangesDetected = true;

                    if (svnUI.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
                    if (svnUI.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";

                    var statusModule = svnManager.GetModule<SVNStatus>();
                    statusModule?.ClearCurrentData();
                    if (svnUI.TreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                }
                else
                {
                    string info = string.IsNullOrWhiteSpace(commitResult) ? "Nothing to commit." : commitResult;
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=yellow>Result:</color> {info}", append: true);
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<color=orange><b>[ABORTED]</b></color> User cancelled.", append: true);
            }
            catch (Exception ex)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=red>Error:</color> {ex.Message}", append: true);
            }
            finally
            {
                IsProcessing = false;
                _commitCTS?.Dispose();
                _commitCTS = null;
                HideProgressBarAfterDelay(2.0f);

                try { await svnManager.RefreshStatus(); }
                catch (Exception refreshEx) { SVNLogBridge.LogLine($"[SVN Post-Commit Refresh Error] {refreshEx.Message}"); }
            }
        }

        public async Task ExecuteCommitSelected(string message)
        {
            if (IsProcessing) return;

            string root = NormalizeSvnPath(svnManager.WorkingDir);
            var statusModule = svnManager.GetModule<SVNStatus>();

            if (statusModule == null)
            {
                LogToConsole("\n<color=red>Error:</color> SVN Status module not found.");
                return;
            }

            var allElements = statusModule.GetCurrentData();
            var selectedItems = allElements?.Where(e => e.IsChecked && !e.IsFolder).ToList() ?? new List<SvnTreeElement>();

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

            selectedItems = selectedItems.Where(e => "MADC?!".Contains(e.Status)).ToList();
            if (selectedItems.Count == 0)
            {
                LogToConsole("\n<color=yellow>No valid files to commit.\nSelected items do not contain any SVN changes.</color>");
                return;
            }

            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();
            message = message.Replace("\"", "\\\"");

            try
            {
                LogToConsole("\n<b>Initiating commit...</b>\n");

                // [1/4] Cleanup
                LogToConsole("\n<b>[1/4]</b> Cleaning up database...");
                await SvnRunner.RunAsync("cleanup", root, false, _commitCTS.Token);

                // [2/4] Scanning & fixing missing files
                LogToConsole("\n<b>[2/4]</b> Scanning & fixing missing files...");
                var deletions = selectedItems.Where(e => e.Status == "!" || e.Status == "D");
                foreach (var item in deletions)
                {
                    string path = NormalizeSvnPath(item.FullPath);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    try
                    {
                        await SvnRunner.RunAsync($"delete \"{path}\" --force", root, false, _commitCTS.Token);
                    }
                    catch { }
                }

                // [3/4] Adding all new files
                LogToConsole("\n<b>[3/4]</b> Adding all new files (Recursive)...");
                var additions = selectedItems.Where(e => e.Status == "?" || e.Status == "A");
                foreach (var item in additions)
                {
                    string path = NormalizeSvnPath(item.FullPath);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    try
                    {
                        await SvnRunner.RunAsync($"add \"{path}\" --force --parents --no-ignore", root, false, _commitCTS.Token);
                    }
                    catch { }
                }

                // Zbierz finalną listę ścieżek do commita
                var finalTargets = new HashSet<string>(
                    selectedItems.Select(e => NormalizeSvnPath(e.FullPath))
                                 .Where(p => !string.IsNullOrWhiteSpace(p))
                );

                if (finalTargets.Count == 0)
                {
                    LogToConsole("\n<color=yellow>Result: Nothing to commit.</color>");
                    return;
                }

                string targetsPath = Path.Combine(Path.GetTempPath(), "svn_commit_list.txt");
                File.WriteAllLines(targetsPath, finalTargets);

                // [4/4] Sending to server
                LogToConsole("\n<b>[4/4]</b> Sending to server...");
                string result = await SvnRunner.RunAsync(
                    $"commit --targets \"{targetsPath}\" -m \"{message}\" --non-interactive",
                    root, false, _commitCTS.Token);

                if (File.Exists(targetsPath)) File.Delete(targetsPath);

                bool isSuccess = result.Contains("Committed revision") || !string.IsNullOrWhiteSpace(svnManager.ParseRevision(result));
                if (isSuccess)
                {
                    string revision = svnManager.ParseRevision(result);
                    LogToConsole($"\n<color=green><b>SUCCESS!</b></color>\nRevision: <b>{revision}</b>\nCommitted items: <b>{finalTargets.Count}</b>");

                    svnManager._diskChangesDetected = true;
                    statusModule?.ClearCurrentData();
                    if (svnUI.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
                    if (svnUI.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();
                    if (svnUI.TreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                    if (svnUI.CommitTreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";

                    await svnManager.RefreshStatus();
                }
                else
                {
                    string shortResult = string.IsNullOrWhiteSpace(result) ? "Nothing to commit." : (result.Length > 500 ? result.Substring(0, 500) + "..." : result);
                    LogToConsole($"\n<color=yellow>Result:</color> {shortResult}");
                }
            }
            catch (OperationCanceledException)
            {
                LogToConsole("\n<color=orange><b>[ABORTED]</b></color> Commit cancelled by user.");
            }
            catch (Exception ex)
            {
                LogToConsole($"\n<color=red>Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _commitCTS?.Dispose();
                _commitCTS = null;
            }
        }

        private async void HideProgressBarAfterDelay(float delaySeconds)
        {
            await Task.Delay((int)(delaySeconds * 1000));

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(false);
                svnUI.OperationProgressBar.value = 0f;
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