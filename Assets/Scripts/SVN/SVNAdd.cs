using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNAdd : SVNBase
    {
        public SVNAdd(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void PrepareAllNewItemsForCommit()
        {
            svnUI.LogText.text = "Starting full project scan...\n";

            // 1. Add Folders first
            AddAllNewFolders();

            // Czekamy chwilê na odœwie¿enie statusu przez SVN
            await Task.Delay(500);

            // 2. Add Files next
            AddAllNewFiles();

            svnUI.LogText.text += "<color=blue>Ready to Commit!</color>\n";
        }

        /// <summary>
        /// Scans for unversioned directories and adds them to SVN with '--depth empty'
        /// </summary>
        public async void AddAllNewFolders()
        {
            if (IsProcessing) return;

            IsProcessing = true;
            svnUI.LogText.text = "Scanning for unversioned folders (respecting ignores)...\n";

            try
            {
                string root = svnManager.WorkingDir;

                // 1. Fetch status: includeIgnored MUST be false.
                // This way, SVN handles the heavy lifting of filtering out 
                // /Intermediate/, /Saved/, /Binaries/, etc., based on your svn:ignore rules.
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 2. Filter: Only unversioned (?) entries that are actually directories
                var foldersToAdd = statusDict
                    .Where(x => x.Value.status == "?" && Directory.Exists(Path.Combine(root, x.Key)))
                    .Select(x => x.Key)
                    .ToArray();

                if (foldersToAdd.Length > 0)
                {
                    foreach (var folderPath in foldersToAdd)
                    {
                        // Double check: In Unreal, we never want to accidentally add these
                        // even if svn:ignore is misconfigured.
                        svnUI.LogText.text += $"Adding folder: {folderPath}\n";
                        await SvnRunner.AddFolderOnlyAsync(root, folderPath);
                    }

                    svnUI.LogText.text += $"<color=green>Successfully added {foldersToAdd.Length} folders.</color>\n";
                    svnManager.Button_RefreshStatus();
                }
                else
                {
                    svnUI.LogText.text += "No new unversioned folders found.\n";
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>AddFolders Error:</color> {ex.Message}\n";
                Debug.LogError($"[SVNAdd] {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void AddAllNewFiles()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            svnUI.LogText.text = "Searching for unversioned files (respecting ignores)...\n";

            try
            {
                string root = svnManager.WorkingDir;

                // 1. Pobieramy status BEZ flagi --no-ignore.
                // Wywo³uj¹c 'svn status', SVN sam odfiltruje wszystko, co jest zignorowane.
                // Pliki zignorowane w ogóle nie pojawi¹ siê na liœcie z kodem '?'.
                string output = await SvnRunner.RunAsync("status", root);

                List<string> filesToAdd = new List<string>();
                string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    // Sprawdzamy tylko pliki ze statusem '?' (Unversioned)
                    // SVN nie poka¿e tu plików ignorowanych, chyba ¿e u¿ylibyœmy --no-ignore
                    if (line.Length >= 8 && line[0] == '?')
                    {
                        string path = line.Substring(8).Trim().Replace('\\', '/');
                        string fullPath = Path.Combine(root, path);

                        // Sprawdzamy czy to plik, a nie folder (foldery dodajemy inn¹ metod¹)
                        if (File.Exists(fullPath))
                        {
                            filesToAdd.Add(path);
                        }
                    }
                }

                if (filesToAdd.Count > 0)
                {
                    svnUI.LogText.text += $"Found {filesToAdd.Count} new files to add.\n";

                    // Dodajemy pliki. U¿ywamy cudzys³owów dla bezpieczeñstwa œcie¿ek ze spacjami.
                    await SvnRunner.AddAsync(root, filesToAdd.ToArray());

                    svnUI.LogText.text += $"<color=green>Successfully added {filesToAdd.Count} files.</color>\n";
                    svnManager.Button_RefreshStatus();
                }
                else
                {
                    svnUI.LogText.text += "No new unversioned files found.\n";
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Add Error:</color> {ex.Message}\n";
            }
            finally { IsProcessing = false; }
        }
    }
}