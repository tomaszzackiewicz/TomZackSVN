using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using SFB; // StandaloneFileBrowser dla wersji EXE

namespace SVN.Core
{
    public class SVNDiff : SVNBase
    {
        public SVNDiff(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            // Wykorzystujemy dedykowaną konsolę dla Diffa, jeśli istnieje
            var console = svnUI.DiffConsoleText != null ? svnUI.DiffConsoleText : svnUI.CommitConsoleContent;
            SVNLogBridge.UpdateUIField(console, msg, "DIFF", true);
        }

        /// <summary>
        /// Otwiera systemowe okno wyboru pliku (działa w EXE dzięki SFB)
        /// </summary>
        public void Button_BrowseDiffFilePath()
        {
            string root = svnManager.WorkingDir;

            if (string.IsNullOrEmpty(root))
            {
                LogBoth("<color=red>Error:</color> Working Directory is not set!");
                return;
            }

            var extensions = new[] { new ExtensionFilter("All Files", "*") };

            // Korzystamy z SFB zamiast EditorUtility
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File to Diff", root, extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');
                string normalizedRoot = root.Replace('\\', '/');

                // Przekształcanie na ścieżkę relatywną dla SVN
                if (selectedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    selectedPath = selectedPath.Substring(normalizedRoot.Length).TrimStart('/');
                }
                else
                {
                    LogBoth("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!");
                }

                if (svnUI.DiffTargetFileInput != null)
                {
                    svnUI.DiffTargetFileInput.text = selectedPath;
                    LogBoth($"<color=green>Diff:</color> Selected file: {selectedPath}");
                }
            }
        }

        /// <summary>
        /// Wywoływane przez przycisk "Execute Diff"
        /// </summary>
        public async void ExecuteDiff()
        {
            if (IsProcessing) return;

            string relativePath = svnUI.DiffTargetFileInput?.text.Trim();
            if (string.IsNullOrEmpty(relativePath))
            {
                LogBoth("<color=yellow>Please select or enter a file path first.</color>");
                return;
            }

            IsProcessing = true;
            try
            {
                await ShowDiff(relativePath);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>
        /// Główna logika porównywania plików
        /// </summary>
        public async Task ShowDiff(string relativePath)
        {
            if (string.IsNullOrEmpty(svnManager.WorkingDir))
            {
                LogBoth("<color=red>Error:</color> Working Directory is not set!");
                return;
            }

            // --- POBIERANIE ŚCIEŻKI DO EDYTORA ---
            string editorPath = svnManager.MergeToolPath;

            // Ratunek: Pobranie z UI lub PlayerPrefs
            if (string.IsNullOrEmpty(editorPath) && svnUI.SettingsMergeToolPathInput != null)
                editorPath = svnUI.SettingsMergeToolPathInput.text;

            if (string.IsNullOrEmpty(editorPath))
                editorPath = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");

            // Czyszczenie ścieżki
            if (!string.IsNullOrEmpty(editorPath))
                editorPath = editorPath.Trim().Replace("\"", "");

            // Weryfikacja
            if (string.IsNullOrEmpty(editorPath) || !File.Exists(editorPath))
            {
                LogBoth($"<color=red>Error:</color> Invalid Diff Tool path! (Found: '{editorPath}')");
                return;
            }

            LogBoth($"Comparing: <color=green>{relativePath}</color>...");

            try
            {
                // Wykonujemy svn diff przez SvnRunner
                string diffContent = await SvnRunner.RunAsync($"diff \"{relativePath}\"", svnManager.WorkingDir, false);

                if (string.IsNullOrWhiteSpace(diffContent))
                {
                    LogBoth("<color=white>No local changes detected.</color>");
                    return;
                }

                // Wykrywanie plików binarnych (.uasset, .png, .jpg itp.)
                bool isBinary = diffContent.Contains("Cannot display: file marked as a binary type");

                if (isBinary)
                {
                    LogBoth("<color=orange>Binary File:</color> Notepad++ cannot show visual changes for this type.");
                    string fullPath = Path.Combine(svnManager.WorkingDir, relativePath).Replace("/", "\\");

                    LogBoth("Opening folder and selecting file...");
                    // Zaznaczamy plik w Explorerze, by użytkownik mógł go sprawdzić ręcznie
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                    return;
                }

                // Logika otwierania w Notepad++ (tworzy plik .diff)
                if (editorPath.ToLower().Contains("notepad++"))
                {
                    string tempDiffPath = Path.Combine(Application.temporaryCachePath, "svn_preview.diff");
                    await File.WriteAllTextAsync(tempDiffPath, diffContent);
                    System.Diagnostics.Process.Start(editorPath, $"\"{tempDiffPath}\"");
                }
                else
                {
                    // Dla TortoiseMerge / VS Code / WinMerge - otwieramy bezpośrednio plik
                    string fullPath = Path.Combine(svnManager.WorkingDir, relativePath);
                    System.Diagnostics.Process.Start(editorPath, $"\"{fullPath}\"");
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Exception:</color> {ex.Message}");
            }
        }
    }
}