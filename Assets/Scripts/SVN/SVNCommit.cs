using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNCommit : SVNBase
    {
        private List<CommitItemData> _items = new List<CommitItemData>();

        public SVNCommit(SVNUI ui, SVNManager manager) : base(ui, manager) { }

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

            string root = svnManager.WorkingDir;
            IsProcessing = true;

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0.05f;
            }

            try
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[1/4]</b> Cleaning up database...", append: false);
                await SvnRunner.RunAsync("cleanup", root);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.25f;

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[2/4]</b> Removing missing files (Fixing '!')...", append: true);
                string rawStatus = await SvnRunner.RunAsync("status", root);

                if (!string.IsNullOrEmpty(rawStatus))
                {
                    using (var reader = new System.IO.StringReader(rawStatus))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.StartsWith("!"))
                            {
                                string pathInSvn = line.Substring(1).Trim();
                                try { await SvnRunner.RunAsync($"delete --force \"{pathInSvn}\"", root); } catch { }
                            }
                        }
                    }
                }
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.50f;

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[3/4]</b> Adding new files...", append: true);
                await SvnRunner.RunAsync("add . --force --parents", root);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.75f;

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[4/4]</b> Sending to server...", append: true);
                string commitResult = await SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root);

                bool isSuccess = commitResult.Contains("Committed revision") ||
                                 commitResult.Contains("Transmitting file data") ||
                                 !string.IsNullOrWhiteSpace(svnManager.ParseRevision(commitResult));

                if (isSuccess)
                {
                    if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 1.0f;

                    string revision = svnManager.ParseRevision(commitResult);

                    svnManager.GetModule<SVNStatus>().ClearUI();
                    await svnManager.RefreshStatus();

                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"<color=green><b>SUCCESS!</b></color>\nRevision: {revision}", append: false);
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
                }
                else
                {
                    string info = string.IsNullOrWhiteSpace(commitResult)
                        ? "<color=yellow>Info:</color> Nothing to commit (Working copy up to date)."
                        : "<color=yellow>Info:</color> Unexpected server response. Check console.";

                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, info, append: true);
                    if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"<color=red>Error:</color> {ex.Message}", append: true);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
            }
            finally
            {
                IsProcessing = false;
                await svnManager.RefreshStatus();
                HideProgressBarAfterDelay(2.0f);
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

        public async void ExecuteCommit()
        {
            if (svnUI.CommitMessageInput == null || _items == null) return;

            string message = svnUI.CommitMessageInput.text;
            if (string.IsNullOrWhiteSpace(message))
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=red>Error:</color> Commit message cannot be empty!", append: true);
                return;
            }

            var selectedItems = _items.Where(x => x.IsSelected).ToList();
            if (selectedItems.Count == 0)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=yellow>Warning:</color> No files selected for commit.", append: true);
                return;
            }

            IsProcessing = true;
            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>Starting commit process...</b>", append: false);

            try
            {
                var unversionedPaths = selectedItems
                    .Where(x => x.Status == "?")
                    .Select(x => $"\"{x.Path}\"")
                    .ToArray();

                if (unversionedPaths.Length > 0)
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"Adding {unversionedPaths.Length} new files...", append: true);
                    await SvnRunner.RunAsync($"add {string.Join(" ", unversionedPaths)}", svnManager.WorkingDir);
                }

                string[] pathsToCommit = selectedItems.Select(x => $"\"{x.Path}\"").ToArray();
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "Uploading changes to server...", append: true);

                await SvnRunner.RunAsync($"commit -m \"{message}\" {string.Join(" ", pathsToCommit)}", svnManager.WorkingDir);

                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=green><b>Commit successful!</b></color>", append: true);
                svnUI.CommitMessageInput.text = "";
                RefreshCommitList();
            }
            catch (Exception ex)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"<color=red>Commit failed:</color> {ex.Message}", append: true);
            }
            finally
            {
                IsProcessing = false;
                await svnManager.RefreshStatus();
            }
        }
    }

    public class CommitItemData
    {
        public string Path;
        public string Status;
        public bool IsSelected;
    }
}