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
        private List<CommitItemData> _items = new List<CommitItemData>();

        public SVNCommit(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void CancelOperation()
        {
            if (_commitCTS != null)
            {
                _commitCTS.Cancel();

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<color=orange><b>[System]</b> Operation cancelled by user.</color>", append: true);
            }
        }

        public async void ShowWhatWillBeCommitted()
        {
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir, false);
            var commitables = statusDict.Where(x => "MADC?".Contains(x.Value.status)).ToList();

            SVNLogBridge.LogLine("<b>Current changes to send:</b>");

            StringBuilder sb = new StringBuilder();
            foreach (var item in commitables)
            {
                sb.AppendLine($"[{item.Value.status}] {item.Key}");
            }
            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: true);
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

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>Initiating commit...</b>", append: true);
            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n", append: true);

            string root = svnManager.WorkingDir;
            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0.05f;
            }

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
                                string path = line.Substring(8).Trim();
                                if (!string.IsNullOrEmpty(path)) missingFiles.Add(path);
                            }
                        }
                    }
                }

                if (missingFiles.Count > 0)
                {
                    string deleteTargets = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "svn_delete_targets.txt");

                    UnityEngine.Debug.Log($"[SVN Commit Debug] First missing path: '{missingFiles[0]}'");

                    var utf8NoBom = new System.Text.UTF8Encoding(false);
                    System.IO.File.WriteAllLines(deleteTargets, missingFiles, utf8NoBom);

                    _commitCTS.Token.ThrowIfCancellationRequested();

                    await SvnRunner.RunAsync($"delete --force --targets \"{deleteTargets}\"", root, true, _commitCTS.Token);

                    if (System.IO.File.Exists(deleteTargets)) System.IO.File.Delete(deleteTargets);

                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=white>Fixed {missingFiles.Count} missing entries.</color>", append: true);
                }
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.4f;

                // [3/4] Adding all new files (Recursive)
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

                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
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
                await svnManager.RefreshStatus();
                HideProgressBarAfterDelay(3.0f);
            }
        }

        private async void HideProgressBarAfterDelay(float delaySeconds)
        {
            await Task.Delay((int)(delaySeconds * 1000));
            if (!IsProcessing && svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.value = 0f;
                svnUI.OperationProgressBar.gameObject.SetActive(false);
            }
        }

        public async void RefreshCommitList()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir, false);
                var commitables = statusDict
                    .Where(x => "MADC?".Contains(x.Value.status)) // Added '?' to include new files
                    .Select(x => new CommitItemData
                    {
                        Path = x.Key,
                        Status = x.Value.status,
                        IsSelected = true
                    })
                    .ToList();

                _items = commitables;
                RenderCommitList(_items);
            }
            finally { IsProcessing = false; }
        }

        public void RenderCommitList(List<CommitItemData> items)
        {
            if (items.Count == 0)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "No changes to commit.", append: false);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<b>Files to be committed:</b>");

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
                sb.AppendLine($"<color={color}>[{item.Status}]</color> {item.Path}");
            }

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: false);
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
                List<string> filesToRevert = new List<string>();

                using (var reader = new System.IO.StringReader(rawStatus))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith("!"))
                        {
                            filesToRevert.Add(line.Substring(1).Trim());
                        }
                    }
                }

                if (filesToRevert.Count == 0)
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=green>No missing files found.</color>", append: true);
                    return;
                }

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"Found {filesToRevert.Count} missing files. Restoring...", append: true);

                int batchSize = 20;
                for (int i = 0; i < filesToRevert.Count; i += batchSize)
                {
                    var batch = filesToRevert.Skip(i).Take(batchSize).Select(p => $"\"{p}\"");
                    await SvnRunner.RunAsync($"revert {string.Join(" ", batch)}", root);
                }

                if (svnUI.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
                if (svnUI.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    statusModule.ClearCurrentData();

                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);

                    if (svnUI.CommitTreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);

                    IsProcessing = false;

                    await statusModule.ExecuteRefreshWithAutoExpand();
                }

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<color=green><b>SUCCESS!</b></color> All missing files restored and UI updated.", append: true);
            }
            catch (Exception ex)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=red>Revert Error:</color> {ex.Message}", append: true);
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
                Debug.LogError($"[SVN] Button click failed: {ex.Message}");
            }
        }

        public async Task ExecuteCommitSelected(string message)
        {
            if (IsProcessing) return;

            var statusModule = svnManager.GetModule<SVNStatus>();
            var allElements = statusModule.GetCurrentData();
            string root = svnManager.WorkingDir.Replace("\\", "/").TrimEnd('/');

            var selectedItems = allElements.Where(e => e.IsChecked).ToList();
            if (selectedItems.Count == 0) return;

            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();

            try
            {
                LogToConsole("\n<b>[0/3]</b> Resolving conflicts and cleanup...");
                await SvnRunner.RunAsync("cleanup", root, true, _commitCTS.Token);

                LogToConsole("\n<b>[1/3]</b> Registering items and hierarchy...");

                HashSet<string> commitTargetsRel = new HashSet<string>();

                var deletions = selectedItems
                    .Where(e => e.Status == "!" || e.Status == "D")
                    .OrderByDescending(x => x.FullPath.Split('/').Length);

                foreach (var item in deletions)
                {
                    string relPath = item.FullPath.Replace("\\", "/").TrimStart('/');
                    await SvnRunner.RunAsync($"delete \"{relPath}\" --force", root, true, _commitCTS.Token);
                    commitTargetsRel.Add(relPath);
                }

                var others = selectedItems.Where(e => e.Status != "!" && e.Status != "D");
                foreach (var item in others)
                {
                    string relPath = item.FullPath.Replace("\\", "/").TrimStart('/');

                    if (item.Status == "?" || item.Status == "A")
                    {
                        await SvnRunner.RunAsync($"add \"{relPath}\" --force --parents --no-ignore", root, true, _commitCTS.Token);
                    }
                    commitTargetsRel.Add(relPath);

                    string parentDir = Path.GetDirectoryName(relPath).Replace("\\", "/");
                    if (!string.IsNullOrEmpty(parentDir) && parentDir != ".")
                    {
                        commitTargetsRel.Add(parentDir);
                    }
                }

                LogToConsole("\n<b>[3/3]</b> Sending to server...");

                string targetsPath = Path.Combine(Path.GetTempPath(), "svn_commit_list.txt");
                var sortedTargets = commitTargetsRel.OrderBy(x => x.Length).Distinct().ToList();
                File.WriteAllLines(targetsPath, sortedTargets, new UTF8Encoding(false));

                string commitCmd = $"commit --targets \"{targetsPath}\" -m \"{message}\" --non-interactive";
                string commitResult = await SvnRunner.RunAsync(commitCmd, root, true, _commitCTS.Token);

                if (File.Exists(targetsPath)) File.Delete(targetsPath);

                if (commitResult.Contains("Committed revision"))
                {
                    string revision = svnManager.ParseRevision(commitResult);
                    LogToConsole($"\n<color=green><b>SUCCESS!</b></color> Revision: {revision}");

                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";

                    statusModule.ClearCurrentData();
                    IsProcessing = false;

                    await Task.Delay(1000);
                    statusModule.ShowOnlyModified();
                }
                else
                {
                    LogToConsole($"\n<color=yellow>SVN Result:</color>\n{commitResult}");
                    IsProcessing = false;
                }
            }
            catch (Exception ex)
            {
                LogToConsole($"\n<color=red>Error:</color> {ex.Message}");
                IsProcessing = false;
            }
            finally
            {
                _commitCTS?.Dispose();
                _commitCTS = null;
            }
        }

        private void LogToConsole(string msg)
        {
            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, msg, append: true);
        }

        public class CommitItemData
        {
            public string Path;
            public string Status;
            public bool IsSelected;
        }
    }
}