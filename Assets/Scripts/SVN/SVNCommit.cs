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

        private string NormalizeSvnPath(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var cleaned = new string(input.Where(c => !char.IsControl(c)).ToArray());
            return cleaned.Replace("\\", "/").Replace("\t", "").Replace("\r", "").Replace("\n", "").Trim();
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
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir, false);
            var commitables = statusDict.Where(x => "MADC?".Contains(x.Value.status)).ToList();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<b>Current changes to send:</b>");
            foreach (var item in commitables)
                sb.AppendLine($"[{item.Value.status}] {NormalizeSvnPath(item.Key)}");

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: true);
        }

        public async void RefreshCommitList()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir, false);
                _items = statusDict
                    .Where(x => "MADC?".Contains(x.Value.status))
                    .Select(x => new SVNStatusElement
                    {
                        FullPath = NormalizeSvnPath(x.Key),
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
                        if (path != null) filesToRevert.Add(path);
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
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=red>Revert Error:</color> {ex.Message}", append: true);
            }
            finally { IsProcessing = false; }
        }

        public void CommitSelected()
        {
            string message = svnUI.CommitMessageInput?.text;
            if (string.IsNullOrWhiteSpace(message))
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=red>Error:</color> Please enter a commit message!", append: true);
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
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=red>Error:</color> Commit message is empty!", append: true);
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

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[1/4]</b> Cleaning up database...", append: true);
                await SvnRunner.RunAsync("cleanup", root, true, token);
                UpdateProgress(0.2f);

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[2/4]</b> Scanning & fixing missing files...", append: true);
                string rawStatus = await SvnRunner.RunAsync("status", root, true, token);
                var missing = new List<string>();
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

                if (missing.Count > 0)
                {
                    string targetsFile = Path.Combine(Path.GetTempPath(), "svn_delete_targets.txt");
                    File.WriteAllLines(targetsFile, missing, new UTF8Encoding(false));
                    try
                    {
                        await SvnRunner.RunAsync($"delete --force --targets \"{targetsFile}\"", root, true, token);
                    }
                    finally
                    {
                        if (File.Exists(targetsFile)) File.Delete(targetsFile);
                    }
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\nFixed {missing.Count} missing entries.", append: true);
                }
                UpdateProgress(0.4f);

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[3/4]</b> Adding new files...", append: true);
                string statusForAdd = await SvnRunner.RunAsync("status", root, true, token);
                var unversioned = new List<string>();
                if (!string.IsNullOrEmpty(statusForAdd))
                {
                    using (var reader = new StringReader(statusForAdd))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.StartsWith("?"))
                            {
                                string path = line.Substring(8).Trim();
                                unversioned.Add(path);
                            }
                        }
                    }
                }
                if (unversioned.Count > 0)
                {
                    string addTargetsFile = Path.Combine(Path.GetTempPath(), "svn_add_targets.txt");
                    File.WriteAllLines(addTargetsFile, unversioned);
                    try
                    {
                        await SvnRunner.RunAsync($"add --force --parents --targets \"{addTargetsFile}\"", root, true, token);
                    }
                    finally
                    {
                        if (File.Exists(addTargetsFile)) File.Delete(addTargetsFile);
                    }
                }
                UpdateProgress(0.6f);

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[4/4]</b> Sending to server...", append: true);
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
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=red>Error:</color> {ex.Message}", append: true);
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
                LogToConsole("\n<color=yellow>No valid files to commit.</color>");
                return;
            }

            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();
            var token = _commitCTS.Token;
            message = message.Replace("\"", "\\\"");
            string targetsFile = null;

            try
            {
                LogToConsole("\n<b>Initiating commit...</b>\n");

                LogToConsole("\n<b>[1/4]</b> Cleaning up database...");
                await SvnRunner.RunAsync("cleanup", root, false, token);

                LogToConsole("\n<b>[2/4]</b> Fixing missing files...");
                var deletions = selectedItems
                    .Where(e => e.Status == "!" || e.Status == "D")
                    .Select(e => NormalizeSvnPath(e.FullPath))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                if (deletions.Count > 0)
                {
                    string deleteTargetsFile = Path.Combine(Path.GetTempPath(), "svn_delete_targets.txt");
                    File.WriteAllLines(deleteTargetsFile, deletions);
                    try
                    {
                        await SvnRunner.RunAsync($"delete --force --targets \"{deleteTargetsFile}\"", root, false, token);
                    }
                    finally
                    {
                        if (File.Exists(deleteTargetsFile)) File.Delete(deleteTargetsFile);
                    }
                }

                LogToConsole("\n<b>[3/4]</b> Adding new files...");
                var additions = selectedItems
                    .Where(e => e.Status == "?" || e.Status == "A")
                    .Select(e => NormalizeSvnPath(e.FullPath))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                if (additions.Count > 0)
                {
                    string addTargetsFile = Path.Combine(Path.GetTempPath(), "svn_add_targets.txt");
                    File.WriteAllLines(addTargetsFile, additions);
                    try
                    {
                        await SvnRunner.RunAsync($"add --force --parents --targets \"{addTargetsFile}\"", root, false, token);
                    }
                    finally
                    {
                        if (File.Exists(addTargetsFile)) File.Delete(addTargetsFile);
                    }
                }

                var targets = new HashSet<string>(
                    selectedItems.Select(e => NormalizeSvnPath(e.FullPath)).Where(p => !string.IsNullOrWhiteSpace(p))
                );
                if (targets.Count == 0)
                {
                    LogToConsole("\n<color=yellow>Nothing to commit.</color>");
                    return;
                }

                targetsFile = Path.Combine(Path.GetTempPath(), "svn_commit_list.txt");
                File.WriteAllLines(targetsFile, targets);

                LogToConsole("\n<b>[4/4]</b> Sending to server...");
                string result = await SvnRunner.RunAsync(
                    $"commit --targets \"{targetsFile}\" -m \"{message}\" --non-interactive",
                    root, false, token);

                if (result.Contains("Committed revision"))
                {
                    string rev = svnManager.ParseRevision(result);
                    LogToConsole($"\n<color=green><b>SUCCESS!</b></color>\nRevision: <b>{rev}</b>\nCommitted items: <b>{targets.Count}</b>");

                    SVNStatus.ClearLockCache();
                    svnManager._diskChangesDetected = true;
                    statusModule?.ClearCurrentData();
                    ClearCommitUI();
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";

                    await svnManager.RefreshStatus();
                }
                else
                {
                    string shortResult = string.IsNullOrWhiteSpace(result)
                        ? "Nothing to commit."
                        : (result.Length > 500 ? result.Substring(0, 500) + "..." : result);
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
                LogToConsole($"\n<color=red>Error:</color> {ex.Message}");
            }
            finally
            {
                if (targetsFile != null && File.Exists(targetsFile))
                {
                    try { File.Delete(targetsFile); } catch { }
                }
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