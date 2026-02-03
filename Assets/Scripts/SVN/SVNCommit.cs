using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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

            svnUI.LogText.text += "<b>Current changes to send:</b>\n";
            foreach (var item in commitables)
            {
                svnUI.CommitConsoleContent.text += $"[{item.Value.status}] {item.Key}\n";
            }
        }

        /// <summary>
        /// Analyzes changes, adds unversioned files, and performs the commit.
        /// </summary>
        public async void CommitAll()
        {
            if (IsProcessing) return;

            string message = svnUI.CommitMessageInput?.text;
            if (string.IsNullOrWhiteSpace(message))
            {
                svnUI.CommitConsoleContent.text += "<color=red>Error:</color> Commit message is empty!\n";
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
                // STEP 1: Cleanup
                svnUI.CommitConsoleContent.text = "<b>[1/4]</b> Cleaning up database...\n";
                await SvnRunner.RunAsync("cleanup", root);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.25f;

                // STEP 2: Handle Missing Files (!)
                svnUI.CommitConsoleContent.text += "<b>[2/4]</b> Removing missing files (Fixing '!')...\n";
                string rawStatus = await SvnRunner.RunAsync("status", root);

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
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.50f;

                // STEP 3: Add New Files (?)
                svnUI.CommitConsoleContent.text += "<b>[3/4]</b> Adding new files...\n";
                await SvnRunner.RunAsync("add . --force --parents", root);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.75f;

                // STEP 4: Commit
                svnUI.CommitConsoleContent.text += "<b>[4/4]</b> Sending to server...\n";
                string commitResult = await SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root);

                if (commitResult.Contains("Committed revision"))
                {
                    if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 1.0f;

                    string revision = svnManager.ParseRevision(commitResult);
                    await SvnRunner.RunAsync("update --depth empty", root);

                    svnManager.SVNStatus.ClearUI();
                    svnManager.RefreshStatus();

                    svnUI.CommitConsoleContent.text = $"<color=green><b>SUCCESS!</b></color> Revision: {revision}";
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
                }
                else
                {
                    svnUI.CommitConsoleContent.text += "<color=yellow>Info:</color> The server did not accept the changes. Check Console.";
                    if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
                }
            }
            catch (System.Exception ex)
            {
                svnUI.CommitConsoleContent.text += $"\n<color=red>Error:</color> {ex.Message}";
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
            }
            finally
            {
                IsProcessing = false;
                svnManager.RefreshStatus();
                HideProgressBarAfterDelay(2.0f);
            }
        }

        private async void HideProgressBarAfterDelay(float delaySeconds)
        {
            await System.Threading.Tasks.Task.Delay((int)(delaySeconds * 1000));
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
                    .Where(x => "MADC".Contains(x.Value.status))
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
            if (svnUI.LogText == null) return;

            if (items.Count == 0)
            {
                svnUI.CommitConsoleContent.text = "No changes to commit.";
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("<b>Files to be committed:</b>");

            foreach (var item in items)
            {
                string color = item.Status == "M" ? "yellow" : (item.Status == "A" ? "green" : "white");
                sb.AppendLine($"<color={color}>[{item.Status}]</color> {item.Path}");
            }

            svnUI.CommitConsoleContent.text = sb.ToString();
        }

        public async void ExecuteCommit()
        {
            if (svnUI.CommitMessageInput == null || _items == null) return;

            string message = svnUI.CommitMessageInput.text;

            if (string.IsNullOrWhiteSpace(message))
            {
                svnUI.CommitConsoleContent.text += "<color=red>Error:</color> Commit message cannot be empty!\n";
                return;
            }

            var selectedItems = _items.Where(x => x.IsSelected).ToList();

            if (selectedItems.Count == 0)
            {
                svnUI.CommitConsoleContent.text += "<color=yellow>Warning:</color> No files selected for commit.\n";
                return;
            }

            IsProcessing = true;
            svnUI.CommitConsoleContent.text = "<b>Starting commit process...</b>\n";

            try
            {
                var unversionedPaths = selectedItems
                    .Where(x => x.Status == "?")
                    .Select(x => $"\"{x.Path}\"")
                    .ToArray();

                if (unversionedPaths.Length > 0)
                {
                    svnUI.CommitConsoleContent.text += $"Adding {unversionedPaths.Length} new files to version control...\n";
                    string addArgs = $"add {string.Join(" ", unversionedPaths)}";
                    await SvnRunner.RunAsync(addArgs, svnManager.WorkingDir);
                }

                string[] pathsToCommit = selectedItems
                    .Select(x => $"\"{x.Path}\"")
                    .ToArray();

                string commitArgs = $"commit -m \"{message}\" {string.Join(" ", pathsToCommit)}";

                svnUI.CommitConsoleContent.text += "Uploading changes to server...\n";
                string result = await SvnRunner.RunAsync(commitArgs, svnManager.WorkingDir);

                svnUI.CommitConsoleContent.text += $"<color=green><b>Commit successful!</b></color>\n";

                svnUI.CommitMessageInput.text = "";

                RefreshCommitList();
            }
            catch (Exception ex)
            {
                svnUI.CommitConsoleContent.text += $"<color=red>Commit failed:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;

                svnManager.RefreshStatus();
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