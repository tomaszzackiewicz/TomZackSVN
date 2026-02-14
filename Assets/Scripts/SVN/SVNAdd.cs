using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNAdd : SVNBase
    {
        public SVNAdd(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void AddAll()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            try
            {
                svnUI.LogText.text = "<b>[Full Scan]</b> Starting project synchronization...\n";

                await AddFoldersLogic();

                await Task.Delay(300);

                await AddFilesLogic();

                svnUI.LogText.text += "<color=green><b>[Scan Complete]</b> All items are now under version control.</color>\n";
                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Scan Error:</color> {ex.Message}\n";
            }
            finally { IsProcessing = false; }
        }

        public async void AddAllNewFolders()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try { await AddFoldersLogic(); }
            finally { IsProcessing = false; }
        }

        public async void AddAllNewFiles()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try { await AddFilesLogic(); }
            finally { IsProcessing = false; }
        }

        private async Task AddFoldersLogic()
        {
            svnUI.LogText.text += "Scanning for unversioned folders...\n";
            string root = svnManager.WorkingDir;

            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
            var foldersToAdd = statusDict
                .Where(x => x.Value.status == "?" && Directory.Exists(Path.Combine(root, x.Key)))
                .Select(x => x.Key)
                .ToArray();

            if (foldersToAdd.Length > 0)
            {
                foreach (var folderPath in foldersToAdd)
                {
                    svnUI.LogText.text += $"Adding folder: {folderPath}\n";
                    await SvnRunner.AddFolderOnlyAsync(root, folderPath);
                }
                svnUI.LogText.text += $"<color=green>Added {foldersToAdd.Length} folders.</color>\n";
            }
            else
            {
                svnUI.LogText.text += "No new folders found.\n";
            }
        }

        private async Task AddFilesLogic()
        {
            svnUI.LogText.text += "Searching for unversioned files...\n";
            string root = svnManager.WorkingDir;

            string output = await SvnRunner.RunAsync("status", root);
            List<string> filesToAdd = new List<string>();
            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.Length >= 8 && line[0] == '?')
                {
                    string path = line.Substring(8).Trim().Replace('\\', '/');
                    if (File.Exists(Path.Combine(root, path)))
                    {
                        filesToAdd.Add(path);
                    }
                }
            }

            if (filesToAdd.Count > 0)
            {
                await SvnRunner.AddAsync(root, filesToAdd.ToArray());
                svnUI.LogText.text += $"<color=green>Successfully added {filesToAdd.Count} files.</color>\n";
            }
            else
            {
                svnUI.LogText.text += "No new files found.\n";
            }
        }

        public async Task<int> GetUnversionedCountAsync()
        {
            try
            {
                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;

                string output = await SvnRunner.RunAsync("status", root);
                string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                return lines.Count(line => line.Length >= 1 && line[0] == '?');
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}