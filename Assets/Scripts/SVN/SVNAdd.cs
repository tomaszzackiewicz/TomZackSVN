using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNAdd : SVNBase
    {
        // Token pozwalający na przerwanie poprzedniej operacji przy nowym wywołaniu
        private CancellationTokenSource _activeCTS;

        public SVNAdd(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        /// <summary>
        /// Pełna synchronizacja: najpierw foldery, potem pliki.
        /// </summary>
        public async void AddAll()
        {
            CancellationToken token = PrepareNewOperation();

            try
            {
                SVNLogBridge.LogLine("<b>[Full Scan]</b> Starting project synchronization...", append: false);

                // 1. Dodaj foldery (muszą być pierwsze, żeby pliki miały "miejsce")
                await AddFoldersLogic(token);

                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);

                // 2. Dodaj pliki
                await AddFilesLogic(token);

                SVNLogBridge.LogLine("<color=green><b>[Scan Complete]</b> All items are now under version control.</color>");
                await svnManager.RefreshStatus();
            }
            catch (OperationCanceledException) { /* Ignorujemy - nowa operacja nas przerwała */ }
            catch (Exception ex) { SVNLogBridge.LogLine($"<color=red>Scan Error:</color> {ex.Message}"); }
            finally { CleanUpOperation(token); }
        }

        public async void AddAllNewFolders()
        {
            CancellationToken token = PrepareNewOperation();
            try { await AddFoldersLogic(token); }
            catch (OperationCanceledException) { }
            finally { CleanUpOperation(token); }
        }

        public async void AddAllNewFiles()
        {
            CancellationToken token = PrepareNewOperation();
            try { await AddFilesLogic(token); }
            catch (OperationCanceledException) { }
            finally { CleanUpOperation(token); }
        }

        // --- Logika Wewnętrzna ---

        private async Task AddFoldersLogic(CancellationToken token)
        {
            SVNLogBridge.LogLine("Scanning for unversioned folders...");
            string root = svnManager.WorkingDir;

            // Pobieramy status z przekazaniem tokena
            var statusDict = await GetFullStatusDictionaryAsync(root, false, token);

            var foldersToAdd = statusDict
                .Where(x => x.Value.status == "?" && Directory.Exists(Path.Combine(root, x.Key)))
                .Select(x => x.Key)
                .OrderBy(path => path.Length) // Najpierw foldery nadrzędne
                .ToArray();

            if (foldersToAdd.Length > 0)
            {
                foreach (var folderPath in foldersToAdd)
                {
                    token.ThrowIfCancellationRequested();
                    SVNLogBridge.LogLine($"Adding folder: <color=#4FC3F7>{folderPath}</color>");
                    // Depth empty, aby nie dodawać wszystkiego na raz (lepiej kontrolować to etapami)
                    await SvnRunner.RunAsync($"add \"{folderPath}\" --depth empty", root, true, token);
                }
                SVNLogBridge.LogLine($"<color=green>Added {foldersToAdd.Length} folders.</color>");
            }
        }

        public static async Task<Dictionary<string, (string status, string lockInfo)>> GetFullStatusDictionaryAsync(
    string workingDir,
    bool showIgnored,
    CancellationToken token = default) // Dodajemy ten parametr
        {
            var dict = new Dictionary<string, (string status, string lockInfo)>();

            // Przekazujemy token do RunAsync
            string output = await SvnRunner.RunAsync(showIgnored ? "status --no-ignore" : "status", workingDir, true, token);

            if (string.IsNullOrEmpty(output)) return dict;

            using (var reader = new System.IO.StringReader(output))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    // Reagujemy na przerwanie zadania wewnątrz pętli
                    token.ThrowIfCancellationRequested();

                    if (line.Length < 8) continue;

                    string status = line[0].ToString();
                    string lockInfo = line[5].ToString();
                    string path = line.Substring(8).Trim().Replace('\\', '/');

                    dict[path] = (status, lockInfo);
                }
            }
            return dict;
        }

        private async Task AddFilesLogic(CancellationToken token)
        {
            SVNLogBridge.LogLine("Searching for unversioned files...");
            string root = svnManager.WorkingDir;

            string output = await SvnRunner.RunAsync("status", root, true, token);
            List<string> filesToAdd = new List<string>();

            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    token.ThrowIfCancellationRequested();
                    if (line.Length >= 8 && line[0] == '?')
                    {
                        string path = line.Substring(8).Trim().Replace('\\', '/');
                        if (File.Exists(Path.Combine(root, path)))
                        {
                            filesToAdd.Add(path);
                        }
                    }
                }
            }

            if (filesToAdd.Count > 0)
            {
                // Grupowanie plików, aby nie odpalać svn.exe 1000 razy (max 20 plików na raz)
                int batchSize = 20;
                for (int i = 0; i < filesToAdd.Count; i += batchSize)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = filesToAdd.Skip(i).Take(batchSize).Select(f => $"\"{f}\"");
                    string args = string.Join(" ", batch);

                    await SvnRunner.RunAsync($"add {args} --force --parents", root, true, token);
                    SVNLogBridge.LogLine($"Added batch {i / batchSize + 1}...");
                }
                SVNLogBridge.LogLine($"<color=green>Successfully added {filesToAdd.Count} files.</color>");
            }
        }

        // --- Zarządzanie Stanem i Tokenami ---

        private CancellationToken PrepareNewOperation()
        {
            // Jeśli coś już działa - cancelujemy to natychmiast
            if (_activeCTS != null)
            {
                _activeCTS.Cancel();
                _activeCTS.Dispose();
            }

            _activeCTS = new CancellationTokenSource();
            IsProcessing = true;
            return _activeCTS.Token;
        }

        private void CleanUpOperation(CancellationToken token)
        {
            // Tylko jeśli to nasze zadanie dobiegło końca (nie zostało zastąpione nowym)
            if (_activeCTS != null && _activeCTS.Token == token)
            {
                IsProcessing = false;
                _activeCTS.Dispose();
                _activeCTS = null;
            }
        }
    }
}