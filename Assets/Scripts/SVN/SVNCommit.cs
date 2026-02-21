using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using TMPro;

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
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[1/4]</b> Cleaning up database...", append: true);
                await SvnRunner.RunAsync("cleanup", root, true, _commitCTS.Token);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.2f;

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
                            if (line.StartsWith("!"))
                            {
                                string path = line.Substring(1).Trim();
                                if (!string.IsNullOrEmpty(path)) missingFiles.Add($"\"{path}\"");
                            }
                        }
                    }
                }

                if (missingFiles.Count > 0)
                {
                    int batchSize = 50;
                    for (int i = 0; i < missingFiles.Count; i += batchSize)
                    {
                        _commitCTS.Token.ThrowIfCancellationRequested();
                        var batch = missingFiles.Skip(i).Take(batchSize);
                        string batchArgs = $"delete --force {string.Join(" ", batch)}";
                        await SvnRunner.RunAsync(batchArgs, root, true, _commitCTS.Token);

                        float progress = 0.2f + ((float)i / missingFiles.Count * 0.2f);
                        if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = progress;
                    }
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=white>Fixed {missingFiles.Count} missing entries.</color>", append: true);
                }
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.4f;

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[3/4]</b> Adding all new files (Recursive)...", append: true);

                await SvnRunner.RunAsync("add . --force --parents --depth infinity", root, true, _commitCTS.Token);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.6f;

                var commitTask = SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root, true, _commitCTS.Token);

                string commitResult = await commitTask;

                bool isSuccess = commitResult.Contains("Committed revision") ||
                                 !string.IsNullOrWhiteSpace(svnManager.ParseRevision(commitResult));

                if (isSuccess)
                {
                    string revision = svnManager.ParseRevision(commitResult);
                    if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 1.0f;

                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n", append: true);

                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=green><b>SUCCESS!</b></color> Revision: {revision}", append: true);

                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n", append: true);

                    if (svnUI.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
                    if (svnUI.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();

                    var statusModule = svnManager.GetModule<SVNStatus>();
                    statusModule.ClearCurrentData();

                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";

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
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"Restored {Math.Min(i + batchSize, filesToRevert.Count)}/{filesToRevert.Count}...", append: true);
                }

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=green><b>Success:</b> All missing files restored.</color>", append: true);
            }
            catch (Exception ex)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"<color=red>Revert Error:</color> {ex.Message}", append: true);
            }
            finally
            {
                IsProcessing = false;
                await svnManager.RefreshStatus();
            }
        }

        public class CommitItemData
        {
            public string Path;
            public string Status;
            public bool IsSelected;
        }
    }
}