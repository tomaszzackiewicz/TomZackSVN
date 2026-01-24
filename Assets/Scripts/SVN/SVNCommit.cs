using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

            // Loguj to do g³ównego okna logów
            svnUI.LogText.text += "<b>Current changes to send:</b>\n";
            foreach (var item in commitables)
            {
                svnUI.LogText.text += $"[{item.Value.status}] {item.Key}\n";
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
                svnUI.LogText.text += "<color=red>Error:</color> Commit message cannot be empty!\n";
                return;
            }

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root)) return;

            IsProcessing = true;
            svnUI.LogText.text = "<b>Analyzing changes before commit...</b>\n";

            try
            {
                // 1. Get current status to identify new and modified files
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 2. Handle unversioned files (status '?')
                var filesToAdd = statusDict
                    .Where(x => x.Value.status == "?")
                    .Select(x => x.Key)
                    .ToArray();

                if (filesToAdd.Length > 0)
                {
                    svnUI.LogText.text += $"Scheduling {filesToAdd.Length} unversioned files for addition...\n";
                    await SvnRunner.AddAsync(root, filesToAdd);
                }

                // 3. Check if there are any changes to send (Modified, Added, Deleted, etc.)
                bool hasChanges = statusDict.Any(x =>
                    !string.IsNullOrEmpty(x.Value.status) && "MADRC?".Contains(x.Value.status));

                if (!hasChanges)
                {
                    svnUI.LogText.text += "<color=yellow>Nothing to commit. Working copy is clean.</color>\n";
                }
                else
                {
                    svnUI.LogText.text += "Sending changes to server...\n";

                    // Perform the commit on the current directory
                    string commitResult = await SvnRunner.CommitAsync(root, new string[] { "." }, message);

                    // 4. Parse and display revision info
                    string revision = svnManager.ParseRevision(commitResult);

                    if (!string.IsNullOrEmpty(revision))
                    {
                        svnUI.LogText.text += $"<color=green><b>Success!</b></color> Committed revision: <b>{revision}</b>\n";
                    }
                    else
                    {
                        svnUI.LogText.text += $"<color=green>Success!</color>\n{commitResult}\n";
                    }

                    // Clear message input on success
                    if (svnUI.CommitMessageInput != null)
                        svnUI.CommitMessageInput.text = "";
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Commit Failed:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;

                // 5. Final UI Refresh
                // Updates branch info (revision number) and rebuilds the tree view
                svnManager.UpdateBranchInfo();

                // Assuming your manager has this method to trigger the Status module's refresh
                svnManager.Button_RefreshStatus();
            }
        }

        public async void RefreshCommitList()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            try
            {
                // 1. Pobieramy status (bez --no-ignore, bo ignorowanych nie commitujemy)
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir, false);

                // 2. Filtrujemy tylko to, co nadaje siê do commitu (M, A, D, C)
                // Pomijamy '?' (unversioned) - u¿ytkownik musi je najpierw dodaæ przyciskiem Add New
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

                // 3. Renderowanie listy w UI (wywo³anie metody w SVNUI)
                svnUI.RenderCommitList(_items);
            }
            finally { IsProcessing = false; }
        }

        public async void ExecuteCommit()
        {
            string message = svnUI.CommitMessageInput.text;

            if (string.IsNullOrWhiteSpace(message))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Commit message cannot be empty!\n";
                return;
            }

            // Pobieramy œcie¿ki tylko zaznaczonych plików
            string[] selectedPaths = _items
                .Where(x => x.IsSelected)
                .Select(x => x.Path)
                .ToArray();

            if (selectedPaths.Length == 0) return;

            IsProcessing = true;
            svnUI.LogText.text = "Committing changes...\n";

            try
            {
                // Wywo³anie komendy: svn commit -m "msg" path1 path2 ...
                string result = await SvnRunner.RunAsync($"commit -m \"{message}\" {string.Join(" ", selectedPaths)}", svnManager.WorkingDir);

                svnUI.LogText.text += $"<color=green>Commit successful!</color>\n{result}\n";
                svnUI.CommitMessageInput.text = ""; // Czyœcimy pole po sukcesie
                RefreshCommitList();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Commit failed:</color> {ex.Message}\n";
            }
            finally { IsProcessing = false; }
        }
    }

    public class CommitItemData
    {
        public string Path;
        public string Status;
        public bool IsSelected;
    }
}