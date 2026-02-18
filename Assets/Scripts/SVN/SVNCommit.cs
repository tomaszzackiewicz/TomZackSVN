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

            // RESET UI: Czysty start
            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>Initiating...</b>", append: false);

            string root = svnManager.WorkingDir;
            IsProcessing = true;
            _commitCTS = new CancellationTokenSource(); // Inicjalizacja tokena do przerywania

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0.05f;
            }

            try
            {
                // KROK 1: Cleanup (Z Twojej działającej metody)
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[1/4]</b> Cleaning up database...", append: false);
                await SvnRunner.RunAsync("cleanup", root, true, _commitCTS.Token);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.25f;

                // KROK 2: Removing missing files
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[2/4]</b> Removing missing files (Fixing '!')...", append: true);
                string rawStatus = await SvnRunner.RunAsync("status", root, true, _commitCTS.Token);

                if (!string.IsNullOrEmpty(rawStatus))
                {
                    using (var reader = new System.IO.StringReader(rawStatus))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            // Sprawdzamy czy użytkownik nie kliknął Cancel w trakcie pętli
                            _commitCTS.Token.ThrowIfCancellationRequested();

                            if (line.StartsWith("!"))
                            {
                                string pathInSvn = line.Substring(1).Trim();
                                try
                                {
                                    // Używamy tokena również tutaj
                                    await SvnRunner.RunAsync($"delete --force \"{pathInSvn}\"", root, true, _commitCTS.Token);
                                }
                                catch { /* Ignorujemy błędy pojedynczych plików */ }
                            }
                        }
                    }
                }
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.50f;

                // KROK 3: Adding new files
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[3/4]</b> Adding new files...", append: true);
                await SvnRunner.RunAsync("add . --force --parents", root, true, _commitCTS.Token);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.75f;

                // KROK 4: Sending to server
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[4/4]</b> Sending to server...", append: true);
                string commitResult = await SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root, true, _commitCTS.Token);

                // Logika sukcesu z Twojej działającej metody
                bool isSuccess = commitResult.Contains("Committed revision") ||
                                 commitResult.Contains("Transmitting file data") ||
                                 !string.IsNullOrWhiteSpace(svnManager.ParseRevision(commitResult));

                if (isSuccess)
                {
                    if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 1.0f;
                    string revision = svnManager.ParseRevision(commitResult);

                    svnManager.GetModule<SVNStatus>().ClearUI();
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"<color=green><b>SUCCESS!</b></color>\nRevision: {revision}", append: false);
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
                }
                else
                {
                    string info = string.IsNullOrWhiteSpace(commitResult)
                        ? "<color=yellow>Info:</color> Nothing to commit."
                        : $"<color=yellow>Info:</color> {commitResult}";

                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, info, append: true);
                    if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
                }
            }
            catch (OperationCanceledException)
            {
                // To się wykona, gdy klikniesz przycisk Cancel
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<color=orange><b>[ABORTED]</b></color> Operation stopped by user.", append: true);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
            }
            catch (Exception ex)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=red>Error:</color> {ex.Message}", append: true);
                if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
            }
            finally
            {
                IsProcessing = false;
                _commitCTS?.Dispose();
                _commitCTS = null;
                await svnManager.RefreshStatus();
                HideProgressBarAfterDelay(2.0f);
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

        //     string root = svnManager.WorkingDir;
        //     IsProcessing = true;

        //     if (svnUI.OperationProgressBar != null)
        //     {
        //         svnUI.OperationProgressBar.gameObject.SetActive(true);
        //         svnUI.OperationProgressBar.value = 0.05f;
        //     }

        //     try
        //     {
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[1/4]</b> Cleaning up database...", append: false);
        //         await SvnRunner.RunAsync("cleanup", root);
        //         if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.25f;

        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[2/4]</b> Removing missing files (Fixing '!')...", append: true);
        //         string rawStatus = await SvnRunner.RunAsync("status", root);

        //         if (!string.IsNullOrEmpty(rawStatus))
        //         {
        //             using (var reader = new System.IO.StringReader(rawStatus))
        //             {
        //                 string line;
        //                 while ((line = reader.ReadLine()) != null)
        //                 {
        //                     if (line.StartsWith("!"))
        //                     {
        //                         string pathInSvn = line.Substring(1).Trim();
        //                         try { await SvnRunner.RunAsync($"delete --force \"{pathInSvn}\"", root); } catch { }
        //                     }
        //                 }
        //             }
        //         }
        //         if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.50f;

        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[3/4]</b> Adding new files...", append: true);
        //         await SvnRunner.RunAsync("add . --force --parents", root);
        //         if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0.75f;

        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[4/4]</b> Sending to server...", append: true);
        //         string commitResult = await SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root);

        //         bool isSuccess = commitResult.Contains("Committed revision") ||
        //                          commitResult.Contains("Transmitting file data") ||
        //                          !string.IsNullOrWhiteSpace(svnManager.ParseRevision(commitResult));

        //         if (isSuccess)
        //         {
        //             if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 1.0f;

        //             string revision = svnManager.ParseRevision(commitResult);

        //             svnManager.GetModule<SVNStatus>().ClearUI();
        //             await svnManager.RefreshStatus();

        //             SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"<color=green><b>SUCCESS!</b></color>\nRevision: {revision}", append: false);
        //             if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
        //         }
        //         else
        //         {
        //             string info = string.IsNullOrWhiteSpace(commitResult)
        //                 ? "<color=yellow>Info:</color> Nothing to commit (Working copy up to date)."
        //                 : "<color=yellow>Info:</color> Unexpected server response. Check console.";

        //             SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, info, append: true);
        //             if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
        //         }
        //     }
        //     catch (Exception ex)
        //     {
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"<color=red>Error:</color> {ex.Message}", append: true);
        //         if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = 0f;
        //     }
        //     finally
        //     {
        //         IsProcessing = false;
        //         await svnManager.RefreshStatus();
        //         HideProgressBarAfterDelay(2.0f);
        //     }
        // }



        // public async void CommitAll()
        // {
        //     if (IsProcessing) return;

        //     string message = svnUI.CommitMessageInput?.text;
        //     if (string.IsNullOrWhiteSpace(message))
        //     {
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<color=red>Error:</color> Commit message is empty!", append: true);
        //         return;
        //     }

        //     // RESET UI: Czyścimy wszystko na start
        //     SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>Initiating...</b>", append: false);

        //     string root = svnManager.WorkingDir;
        //     IsProcessing = true;
        //     _commitCTS = new CancellationTokenSource();

        //     try
        //     {
        //         // KROK 0: Próba Cleanup (Wyciszamy błąd E720032)
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "<b>[0/3]</b> Checking workspace health...", append: false);
        //         try
        //         {
        //             // Korzystamy z Twojej statycznej metody z SVNClean
        //             await SVNClean.CleanupAsync(root);
        //         }
        //         catch (Exception ex)
        //         {
        //             // Jeśli cleanup nie może usunąć pliku tmp, wypisujemy tylko ostrzeżenie i idziemy dalej
        //             SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<color=yellow>Note:</color> Cleanup skipped (some files busy), trying to proceed...", append: true);
        //             await Task.Delay(500); // Dajemy systemowi chwilę na odblokowanie
        //         }

        //         // KROK 1: Sync deleted
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[1/3]</b> Synchronizing deleted files...", append: true);
        //         string rawStatus = await SvnRunner.RunAsync("status", root, true, _commitCTS.Token);

        //         List<string> missingFiles = new List<string>();
        //         if (!string.IsNullOrEmpty(rawStatus))
        //         {
        //             using (var reader = new System.IO.StringReader(rawStatus))
        //             {
        //                 string line;
        //                 while ((line = reader.ReadLine()) != null)
        //                 {
        //                     if (line.StartsWith("!"))
        //                     {
        //                         string path = line.Substring(1).Trim();
        //                         if (!path.Contains(".svn")) missingFiles.Add($"\"{path}\"");
        //                     }
        //                 }
        //             }
        //             if (missingFiles.Count > 0)
        //             {
        //                 try { await SvnRunner.RunAsync($"delete --force {string.Join(" ", missingFiles)}", root, true, _commitCTS.Token); }
        //                 catch { /* Ignorujemy błędy blokady */ }
        //             }
        //         }

        //         // KROK 2: Add new
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[2/3]</b> Adding new files...", append: true);
        //         try { await SvnRunner.RunAsync("add . --force --parents", root, true, _commitCTS.Token); }
        //         catch { /* Ignorujemy błędy blokady przy add */ }

        //         // KROK 3: Commit z wewnętrznym Retry
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<b>[3/3]</b> Sending to server...", append: true);

        //         string commitResult = "";
        //         bool committed = false;
        //         int retry = 0;

        //         while (!committed && retry < 3)
        //         {
        //             try
        //             {
        //                 await Task.Delay(1000 * (retry + 1));
        //                 commitResult = await SvnRunner.RunAsync($"commit -m \"{message}\" --non-interactive .", root, true, _commitCTS.Token);
        //                 committed = true;
        //             }
        //             catch (Exception ex) when (ex.Message.Contains("E720032") || ex.Message.Contains("access"))
        //             {
        //                 retry++;
        //                 SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=yellow>Retry {retry}/3: Files still busy, waiting...</color>", append: true);
        //                 // Próba cichego odblokowania przez cleanup
        //                 try { await SVNClean.CleanupAsync(root); } catch { }
        //             }
        //         }

        //         if (committed && (commitResult.Contains("Committed revision") || !string.IsNullOrWhiteSpace(svnManager.ParseRevision(commitResult))))
        //         {
        //             string revision = svnManager.ParseRevision(commitResult);
        //             SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n\n<color=green><b>SUCCESS!</b></color> Revision: {revision}", append: true);
        //             if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
        //         }
        //         else
        //         {
        //             SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=yellow>Commit Response:</color>\n{commitResult}", append: true);
        //         }
        //     }
        //     catch (OperationCanceledException)
        //     {
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, "\n<color=orange><b>[ABORTED]</b></color> Operation stopped by user.", append: true);
        //     }
        //     catch (Exception ex)
        //     {
        //         SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, $"\n<color=red>Critical Error:</color> {ex.Message}", append: true);
        //     }
        //     finally
        //     {
        //         IsProcessing = false;
        //         _commitCTS?.Dispose();
        //         _commitCTS = null;
        //         await Task.Delay(500);
        //         await svnManager.RefreshStatus();
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