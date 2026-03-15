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

        public async Task ShowPreviewInUnity(string relativePath)
        {
            if (string.IsNullOrEmpty(svnManager.WorkingDir))
            {
                LogBoth("<color=red>Error:</color> Working Directory is not set!");
                return;
            }

            // Clear the preview field before loading new content
            if (svnUI.LogText != null)
            {
                svnUI.LogText.text = "Loading diff...";
            }

            try
            {
                string diffContent = await SvnRunner.RunAsync($"diff \"{relativePath}\"", svnManager.WorkingDir, false);

                if (string.IsNullOrWhiteSpace(diffContent))
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text = "<color=white>No local changes detected (or file is unversioned).</color>";
                    return;
                }

                // 2. Handle binary files
                bool isBinary = diffContent.Contains("Cannot display: file marked as a binary type");

                if (isBinary)
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text = "<color=#FF8080>Binary File:</color> Text preview is not available.";
                    return;
                }

                // 3. Format and display in LogText
                if (svnUI.LogText != null)
                {
                    svnUI.LogText.text = FormatDiffForUnity(diffContent);

                    // Reset scroll position to top
                    var scrollRect = svnUI.LogText.GetComponentInParent<UnityEngine.UI.ScrollRect>();
                    if (scrollRect != null)
                        scrollRect.verticalNormalizedPosition = 1f;
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Preview Error:</color> {ex.Message}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text = $"<color=red>Exception during Diff generation:</color>\n{ex.Message}";
            }
        }

        private string FormatDiffForUnity(string rawDiff)
        {
            string[] lines = rawDiff.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            int addedCount = 0;
            int removedCount = 0;
            System.Text.StringBuilder formattedBody = new System.Text.StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // Protection against TMP tags
                line = line.Replace("<", "<noparse><</noparse>").Replace(">", "<noparse>></noparse>");

                // 1. Path headers (--- / +++)
                if (line.StartsWith("---") || line.StartsWith("+++"))
                {
                    formattedBody.AppendLine($"<color=#00FFFF>{line}</color>");

                    // Add spacing after the header block
                    if (line.StartsWith("+++"))
                    {
                        formattedBody.AppendLine("");
                    }
                }
                // 2. Added lines
                else if (line.StartsWith("+"))
                {
                    addedCount++;
                    formattedBody.AppendLine($"<color=#A6FFB5>{line}</color>");
                }
                // 3. Removed lines
                else if (line.StartsWith("-"))
                {
                    removedCount++;
                    // Deep Cherry (bolded for extra presence)
                    formattedBody.AppendLine($"<color=#800000><b>{line}</b></color>");
                }
                // 4. Section metadata (@@)
                else if (line.StartsWith("@@"))
                {
                    formattedBody.AppendLine($"<color=#00E5FF>{line}</color>");
                }
                // 5. SVN Headers (Index / ===)
                else if (line.StartsWith("Index:") || line.StartsWith("==="))
                {
                    formattedBody.AppendLine($"<color=#00E5FF><b>{line}</b></color>");
                }
                // 6. Normal text
                else
                {
                    formattedBody.AppendLine($"<color=#DDDDDD>{line}</color>");
                }
            }

            // Legend with MATCHING stats colors
            // Added <b> to removed count to match the style of removed lines
            string stats = $"<color=#A6FFB5>+{addedCount}</color>  <color=#800000><b>-{removedCount}</b></color>";

            string legend = $"<b>[ PREVIEW MODE ]</b>  {stats}\n" +
                            $"<color=#00E5FF>[@@/Index] Metadata</color>  " +
                            $"<color=#50C8FF>Double-click for External Editor</color>\n" +
                            $"<color=#555555>__________________________________________________________________________</color>\n\n";

            return legend + formattedBody.ToString();
        }

        public void OpenExternalDiff(SvnTreeElement element)
        {
            _ = ShowDiff(element.FullPath);
        }

        public void ExecuteDiffForElement(SvnTreeElement element)
        {
            _ = ShowPreviewInUnity(element.FullPath);
        }
    }
}