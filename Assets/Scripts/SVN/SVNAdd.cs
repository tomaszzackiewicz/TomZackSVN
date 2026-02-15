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
                SVNLogBridge.LogLine("<b>[Full Scan]</b> Starting project synchronization...", append: false);

                await AddFoldersLogic();
                await Task.Delay(300);
                await AddFilesLogic();

                SVNLogBridge.LogLine("<color=green><b>[Scan Complete]</b> All items are now under version control.</color>");
                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Scan Error:</color> {ex.Message}");
            }
            finally { IsProcessing = false; }
        }

        public async void AddAllNewFolders()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try
            {
                SVNLogBridge.LogLine("<b>[Folder Sync]</b> Searching for unversioned directories...", append: false);
                await AddFoldersLogic();
            }
            finally { IsProcessing = false; }
        }

        public async void AddAllNewFiles()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try
            {
                SVNLogBridge.LogLine("<b>[File Sync]</b> Searching for unversioned files...", append: false);
                await AddFilesLogic();
            }
            finally { IsProcessing = false; }
        }

        private async Task AddFoldersLogic()
        {
            SVNLogBridge.LogLine("Scanning for unversioned folders...");
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
                    SVNLogBridge.LogLine($"Adding folder: <color=#4FC3F7>{folderPath}</color>");
                    await AddFolderOnlyAsync(root, folderPath);
                }
                SVNLogBridge.LogLine($"<color=green>Added {foldersToAdd.Length} folders.</color>");
            }
            else
            {
                SVNLogBridge.LogLine("No new folders found.");
            }
        }

        private async Task AddFilesLogic()
        {
            SVNLogBridge.LogLine("Searching for unversioned files...");
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
                await AddAsync(root, filesToAdd.ToArray());
                SVNLogBridge.LogLine($"<color=green>Successfully added {filesToAdd.Count} files.</color>");
            }
            else
            {
                SVNLogBridge.LogLine("No new files found.");
            }
        }

        // --- Static Helpers ---

        public static async Task<string> AddFolderOnlyAsync(string workingDir, string path)
        {
            string cmd = $"add \"{path}\" --depth empty";
            return await SvnRunner.RunAsync(cmd, workingDir);
        }

        public static async Task<string> AddAsync(string workingDir, string[] files)
        {
            if (files == null || files.Length == 0) return "";

            string fileArgs = string.Join(" ", files.Select(f => $"\"{f}\""));
            return await SvnRunner.RunAsync($"add {fileArgs} --force --parents", workingDir);
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
            catch { return 0; }
        }
    }
}