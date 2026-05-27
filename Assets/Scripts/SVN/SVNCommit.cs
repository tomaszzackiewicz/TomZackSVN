using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using TMPro;
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

            // usuń control chars (TAB, CR, LF itd.)
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

                        if (line.StartsWith("!"))
                        {
                            string path = NormalizeSvnPath(line.Substring(1));
                            if (!string.IsNullOrEmpty(path))
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

                var commitModule = svnManager.GetModule<SVNCommit>();
                _ = commitModule.ExecuteCommitSelected(messageFromUI);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN] Button click failed: {ex.Message}");
            }
        }

        public async void CommitAll()
        {
            if (IsProcessing) return;

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0.05f;
            }

            string message = svnUI.CommitMessageInput?.text;
            if (string.IsNullOrWhiteSpace(message))
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=red>Error:</color> Commit message is empty!", append: true);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.gameObject.SetActive(false);
                return;
            }

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>Initiating commit...</b>", append: true);
            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n", append: true);

            string root = svnManager.WorkingDir.Replace("\\", "/").TrimEnd('/');
            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();

            try
            {
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
                            if (line.Length > 8 && line.StartsWith("!"))
                            {
                                // POPRAWKA: Wymuszamy forward slashe, żeby ubić problem traktowania \t jako tabulacji
                                string path = line.Substring(8).Trim().Replace("\\", "/");

                                // Obejście błędu "peg revision" dla plików zawierających '@'
                                if (path.Contains("@") && !path.EndsWith("@")) path += "@";

                                if (!string.IsNullOrEmpty(path)) missingFiles.Add(path);
                            }
                        }
                    }
                }

                if (missingFiles.Count > 0)
                {
                    string deleteTargets = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "svn_delete_targets.txt");
                    SVNLogBridge.LogLine($"[SVN Commit Debug] First missing path: '{missingFiles[0]}'");

                    var utf8NoBom = new System.Text.UTF8Encoding(false);
                    System.IO.File.WriteAllLines(deleteTargets, missingFiles, utf8NoBom);

                    _commitCTS.Token.ThrowIfCancellationRequested();
                    await SvnRunner.RunAsync($"delete --force --targets \"{deleteTargets}\"", root, true, _commitCTS.Token);

                    if (System.IO.File.Exists(deleteTargets)) System.IO.File.Delete(deleteTargets);
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=white>Fixed {missingFiles.Count} missing entries.</color>", append: true);
                }
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.4f;

                // [3/4] Adding all new files
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[3/4]</b> Adding all new files (Recursive)...", append: true);
                await SvnRunner.RunAsync("add . --force --parents --depth infinity", root, true, _commitCTS.Token);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.6f;

                // [4/4] Commit
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[4/4]</b> Sending to server...", append: true);
                var commitTask = SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root, true, _commitCTS.Token);
                string commitResult = await commitTask;

                bool isSuccess = commitResult.Contains("Committed revision") ||
                                 !string.IsNullOrWhiteSpace(svnManager.ParseRevision(commitResult));

                if (isSuccess)
                {
                    string revision = svnManager.ParseRevision(commitResult);
                    if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 1.0f;
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=green><b>SUCCESS!</b></color> Revision: {revision}", append: true);

                    if (svnUI.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
                    if (svnUI.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";

                    var statusModule = svnManager.GetModule<SVNStatus>();
                    statusModule?.ClearCurrentData();

                    if (svnUI.TreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                }
                else
                {
                    string info = string.IsNullOrWhiteSpace(commitResult) ? "<i>Nothing to commit.</i>" : commitResult;
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

                try
                {
                    await svnManager.RefreshStatus();
                }
                catch (Exception refreshEx)
                {
                    SVNLogBridge.LogLine($"[SVN Post-Commit Refresh Error] {refreshEx.Message}");
                }
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

            var selectedItems = allElements
                .Where(e => e.IsChecked && !e.IsFolder)
                .ToList();

            if (allElements == null || allElements.Count == 0)
            {
                LogToConsole(
                    "\n<color=yellow><b>No SVN changes detected.</b></color>\n" +
                    "Working copy is already clean."
                );
                return;
            }

            if (selectedItems.Count == 0)
            {
                LogToConsole(
                    "\n<color=orange><b>Nothing selected for commit.</b></color>\n" +
                    "Please check at least one file in the tree view."
                );
                return;
            }

            selectedItems = selectedItems
                .Where(e => "MADC?!".Contains(e.Status))
                .ToList();

            if (selectedItems.Count == 0)
            {
                LogToConsole(
                    "\n<color=yellow><b>No valid files to commit.</b></color>\n" +
                    "Selected items do not contain any SVN changes."
                );
                return;
            }

            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();

            message = message.Replace("\"", "\\\"");

            try
            {
                LogToConsole("\n<b>[0/3]</b> Cleanup...");

                await SvnRunner.RunAsync(
                    "cleanup",
                    root,
                    false,
                    _commitCTS.Token);

                HashSet<string> targets = new HashSet<string>();

                int deletedCount = 0;
                int addedCount = 0;
                int modifiedCount = 0;
                int skippedCount = 0;

                var deletions = selectedItems
                    .Where(e => e.Status == "!" || e.Status == "D");

                foreach (var item in deletions)
                {
                    string path = NormalizeSvnPath(item.FullPath);

                    if (string.IsNullOrWhiteSpace(path))
                    {
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        await SvnRunner.RunAsync(
                            $"delete \"{path}\" --force",
                            root,
                            false,
                            _commitCTS.Token);

                        targets.Add(path);
                        deletedCount++;
                    }
                    catch
                    {
                        skippedCount++;
                    }
                }

                var others = selectedItems
                    .Where(e => e.Status != "!" && e.Status != "D");

                foreach (var item in others)
                {
                    string path = NormalizeSvnPath(item.FullPath);

                    if (string.IsNullOrWhiteSpace(path))
                    {
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        if (item.Status == "?" || item.Status == "A")
                        {
                            await SvnRunner.RunAsync(
                                $"add \"{path}\" --force --parents --no-ignore",
                                root,
                                false,
                                _commitCTS.Token);

                            addedCount++;
                        }
                        else if (item.Status == "M")
                        {
                            modifiedCount++;
                        }

                        targets.Add(path);
                    }
                    catch
                    {
                        skippedCount++;
                    }
                }

                LogToConsole(
                    $"\n<b>[1/3]</b> Prepared targets:\n" +
                    $"<color=green>Added:</color> {addedCount}\n" +
                    $"<color=yellow>Modified:</color> {modifiedCount}\n" +
                    $"<color=red>Deleted:</color> {deletedCount}\n" +
                    $"<color=grey>Skipped:</color> {skippedCount}"
                );

                if (targets.Count == 0)
                {
                    LogToConsole(
                        "\n<color=yellow><b>Nothing to commit.</b></color>\n" +
                        "No valid SVN targets were prepared."
                    );

                    return;
                }

                string targetsPath = Path.Combine(
                    Path.GetTempPath(),
                    "svn_commit_list.txt");

                File.WriteAllLines(targetsPath, targets);

                LogToConsole(
                    $"\n<b>[2/3]</b> Sending <color=green>{targets.Count}</color> items to server..."
                );

                string result = await SvnRunner.RunAsync(
                    $"commit --targets \"{targetsPath}\" -m \"{message}\" --non-interactive",
                    root,
                    false,
                    _commitCTS.Token);

                if (File.Exists(targetsPath))
                    File.Delete(targetsPath);

                bool isSuccess =
                    result.Contains("Committed revision") ||
                    !string.IsNullOrWhiteSpace(
                        svnManager.ParseRevision(result));

                if (isSuccess)
                {
                    string revision =
                        svnManager.ParseRevision(result);

                    SVNLogBridge.UpdateUIField(
                        svnUI.CommitConsoleContent,
                        $"\n<color=green><b>SUCCESS!</b></color>\n" +
                        $"Revision: <b>{revision}</b>\n" +
                        $"Committed items: <b>{targets.Count}</b>",
                        append: true);

                    statusModule?.ClearCurrentData();

                    if (svnUI.SvnTreeView != null)
                        svnUI.SvnTreeView.ClearView();

                    if (svnUI.SVNCommitTreeDisplay != null)
                        svnUI.SVNCommitTreeDisplay.ClearView();

                    if (svnUI.TreeDisplay != null)
                    {
                        SVNLogBridge.UpdateUIField(
                            svnUI.TreeDisplay,
                            "",
                            "TREE",
                            append: false);
                    }

                    if (svnUI.CommitTreeDisplay != null)
                    {
                        SVNLogBridge.UpdateUIField(
                            svnUI.CommitTreeDisplay,
                            "",
                            "COMMIT_TREE",
                            append: false);
                    }

                    if (svnUI.CommitMessageInput != null)
                        svnUI.CommitMessageInput.text = "";

                    await svnManager.RefreshStatus();
                }
                else
                {
                    string shortResult =
                        string.IsNullOrWhiteSpace(result)
                        ? "Nothing to commit."
                        : result.Length > 500
                            ? result.Substring(0, 500) + "..."
                            : result;

                    SVNLogBridge.UpdateUIField(
                        svnUI.CommitConsoleContent,
                        $"\n<color=yellow>Commit Result:</color>\n{shortResult}",
                        append: true);
                }
            }
            catch (OperationCanceledException)
            {
                LogToConsole(
                    "\n<color=orange><b>[ABORTED]</b></color> Commit cancelled by user.");
            }
            catch (Exception ex)
            {
                LogToConsole(
                    $"\n<color=red>Error:</color> {ex.Message}");
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