using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

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

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>Initiating commit...</b>", append: false);

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
                // KROK 1: Cleanup
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[1/4]</b> Cleaning up database...", append: true);
                await SvnRunner.RunAsync("cleanup", root, true, _commitCTS.Token);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.2f;

                // KROK 2: Batch Removal of missing files (!)
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
                    int batchSize = 50; // Optymalna liczba plików na jedną komendę
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

                // KROK 3: Recursive Add (To rozwiązuje problem "pustych folderów")
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[3/4]</b> Adding all new files (Recursive)...", append: true);
                // --force sprawia, że SVN przechodzi przez już dodane pliki bez błędu, dodając tylko te nowe (?)
                await SvnRunner.RunAsync("add . --force --parents --depth infinity", root, true, _commitCTS.Token);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.6f;

                // KROK 4: Final Commit
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[4/4]</b> Sending data to server (This may take a while)...", append: true);

                // Przy ogromnych commitach przekazujemy "." jako cel, aby SVN sam zebrał zmiany
                string commitResult = await SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root, true, _commitCTS.Token);

                bool isSuccess = commitResult.Contains("Committed revision") ||
                                 !string.IsNullOrWhiteSpace(svnManager.ParseRevision(commitResult));

                if (isSuccess)
                {
                    string revision = svnManager.ParseRevision(commitResult);
                    if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 1.0f;

                    svnManager.GetModule<SVNStatus>().ClearUI();
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=green><b>SUCCESS!</b></color> Revision: {revision}", append: true);
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
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
                await svnManager.RefreshStatus();
                HideProgressBarAfterDelay(3.0f);
            }
        }

        // public async void CommitAll()
        // {
        //     if (IsProcessing) return;

        //     string message = svnUI.CommitMessageInput?.text;
        //     if (string.IsNullOrWhiteSpace(message))
        //     {
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=red>Error:</color> Commit message is empty!", append: true);
        //         return;
        //     }

        //     // RESET UI: Czysty start
        //     SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>Initiating...</b>", append: false);

        //     string root = svnManager.WorkingDir;
        //     IsProcessing = true;
        //     _commitCTS = new CancellationTokenSource(); // Inicjalizacja tokena do przerywania

        //     if (svnUI.OperationProgressBar != null)
        //     {
        //         svnUI.OperationProgressBar.gameObject.SetActive(true);
        //         svnUI.OperationProgressBar.value = 0.05f;
        //     }

        //     try
        //     {
        //         // KROK 1: Cleanup (Z Twojej działającej metody)
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[1/4]</b> Cleaning up database...", append: false);
        //         await SvnRunner.RunAsync("cleanup", root, true, _commitCTS.Token);
        //         if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.25f;

        //         // KROK 2: Removing missing files
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[2/4]</b> Removing missing files (Fixing '!')...", append: true);
        //         string rawStatus = await SvnRunner.RunAsync("status", root, true, _commitCTS.Token);

        //         if (!string.IsNullOrEmpty(rawStatus))
        //         {
        //             using (var reader = new System.IO.StringReader(rawStatus))
        //             {
        //                 string line;
        //                 while ((line = reader.ReadLine()) != null)
        //                 {
        //                     // Sprawdzamy czy użytkownik nie kliknął Cancel w trakcie pętli
        //                     _commitCTS.Token.ThrowIfCancellationRequested();

        //                     if (line.StartsWith("!"))
        //                     {
        //                         string pathInSvn = line.Substring(1).Trim();
        //                         try
        //                         {
        //                             // Używamy tokena również tutaj
        //                             await SvnRunner.RunAsync($"delete --force \"{pathInSvn}\"", root, true, _commitCTS.Token);
        //                         }
        //                         catch { /* Ignorujemy błędy pojedynczych plików */ }
        //                     }
        //                 }
        //             }
        //         }
        //         if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.50f;

        //         // KROK 3: Adding new files
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[3/4]</b> Adding new files...", append: true);
        //         await SvnRunner.RunAsync("add . --force --parents", root, true, _commitCTS.Token);
        //         if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.75f;

        //         // KROK 4: Sending to server
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[4/4]</b> Sending to server...", append: true);
        //         string commitResult = await SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root, true, _commitCTS.Token);

        //         // Logika sukcesu z Twojej działającej metody
        //         bool isSuccess = commitResult.Contains("Committed revision") ||
        //                          commitResult.Contains("Transmitting file data") ||
        //                          !string.IsNullOrWhiteSpace(svnManager.ParseRevision(commitResult));

        //         if (isSuccess)
        //         {
        //             if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 1.0f;
        //             string revision = svnManager.ParseRevision(commitResult);

        //             svnManager.GetModule<SVNStatus>().ClearUI();
        //             SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"<color=green><b>SUCCESS!</b></color>\nRevision: {revision}", append: false);
        //             if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
        //         }
        //         else
        //         {
        //             string info = string.IsNullOrWhiteSpace(commitResult)
        //                 ? "<color=yellow>Info:</color> Nothing to commit."
        //                 : $"<color=yellow>Info:</color> {commitResult}";

        //             SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, info, append: true);
        //             if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
        //         }
        //     }
        //     catch (OperationCanceledException)
        //     {
        //         // To się wykona, gdy klikniesz przycisk Cancel
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<color=orange><b>[ABORTED]</b></color> Operation stopped by user.", append: true);
        //         if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
        //     }
        //     catch (Exception ex)
        //     {
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=red>Error:</color> {ex.Message}", append: true);
        //         if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
        //     }
        //     finally
        //     {
        //         IsProcessing = false;
        //         _commitCTS?.Dispose();
        //         _commitCTS = null;
        //         await svnManager.RefreshStatus();
        //         HideProgressBarAfterDelay(2.0f);
        //     }
        // }

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