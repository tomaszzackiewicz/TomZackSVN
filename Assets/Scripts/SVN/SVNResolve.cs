using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNResolve : SVNBase
    {
        public SVNResolve(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, msg, logLabel: "RESOLVE", append: true);
        }

        /// <summary>
        /// Kluczowa metoda decydująca o zakresie operacji.
        /// Jeśli InputField ma treść -> zwraca tylko ten plik.
        /// Jeśli InputField jest pusty -> zwraca wszystkie pliki w stanie konfliktu (C).
        /// </summary>
        private async Task<string[]> GetTargetPaths(string root)
        {
            if (svnUI.ResolveTargetFileInput != null && !string.IsNullOrEmpty(svnUI.ResolveTargetFileInput.text))
            {
                string manualPath = svnUI.ResolveTargetFileInput.text.Trim();
                LogBoth($"Targeting manual path: <color=orange>{manualPath}</color>");
                return new[] { manualPath };
            }

            LogBoth("No manual path selected. Searching for all conflicts (C)...");
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
            if (statusDict == null) return Array.Empty<string>();

            return statusDict
                .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                .Select(x => x.Key)
                .ToArray();
        }

        public async void Button_MarkAsResolved()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root)) return;

            IsProcessing = true;
            try
            {
                string[] targetPaths = await GetTargetPaths(root);

                if (targetPaths.Length == 0)
                {
                    LogBoth("<color=yellow>No conflicts found to resolve.</color>");
                    return;
                }

                List<string> readyToResolve = new List<string>();
                List<string> failedValidation = new List<string>();

                foreach (var relativePath in targetPaths)
                {
                    string fullPath = Path.Combine(root, relativePath);
                    if (File.Exists(fullPath))
                    {
                        string content = await File.ReadAllTextAsync(fullPath);
                        // Walidacja czy użytkownik usunął znaczniki z pliku tekstowego
                        if (content.Contains("<<<<<<< .mine") || content.Contains(">>>>>>> .r") || content.Contains("======="))
                        {
                            failedValidation.Add(relativePath);
                            continue;
                        }
                    }
                    readyToResolve.Add(relativePath);
                }

                if (failedValidation.Count > 0)
                {
                    LogBoth("<color=red><b>ABORTED:</b></color> Conflict markers still exist in:");
                    foreach (var f in failedValidation) LogBoth($" - <color=orange>{f}</color>");
                    if (readyToResolve.Count == 0) return;
                }

                if (readyToResolve.Count > 0)
                {
                    LogBoth($"Marking {readyToResolve.Count} items as resolved...");
                    string pathsJoined = "\"" + string.Join("\" \"", readyToResolve) + "\"";
                    await SvnRunner.RunAsync($"resolve --accept working {pathsJoined}", root);

                    LogBoth("<color=green><b>Success!</b></color> Items are now clean.");
                    await svnManager.RefreshStatus();
                    svnManager.PanelHandler.Button_CloseResolve();
                }
            }
            catch (Exception ex) { LogBoth($"<color=red>Error:</color> {ex.Message}"); }
            finally { IsProcessing = false; }
        }

        public async void Button_ResolveTheirs() => await RunResolveStrategy(false);
        public async void Button_ResolveMine() => await RunResolveStrategy(true);

        private async Task RunResolveStrategy(bool useMine)
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root)) return;

            IsProcessing = true;
            try
            {
                string[] targetPaths = await GetTargetPaths(root);
                string strategy = useMine ? "mine-full" : "theirs-full";

                if (targetPaths.Length > 0)
                {
                    LogBoth($"Resolving {targetPaths.Length} item(s) using <color=orange>{strategy}</color>...");
                    await ResolveAsync(root, targetPaths, useMine);

                    LogBoth("<color=green>Resolved!</color> Refreshing status...");
                    await svnManager.RefreshStatus();
                    svnManager.PanelHandler.Button_CloseResolve();
                }
                else LogBoth("<color=yellow>No items found to resolve.</color>");
            }
            catch (Exception ex) { LogBoth($"<color=red>Error:</color> {ex.Message}"); }
            finally { IsProcessing = false; }
        }

        public async void Button_OpenInEditor()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            string editorPath = svnManager.MergeToolPath;

            if (string.IsNullOrEmpty(editorPath))
            {
                editorPath = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
                if (string.IsNullOrEmpty(editorPath))
                {
                    LogBoth("<color=red>Error:</color> Merge tool path is not set!");
                    return;
                }
            }

            try
            {
                IsProcessing = true;
                string targetFile = "";

                // Najpierw patrzymy na InputField
                if (svnUI.ResolveTargetFileInput != null && !string.IsNullOrEmpty(svnUI.ResolveTargetFileInput.text))
                {
                    targetFile = svnUI.ResolveTargetFileInput.text.Trim();
                }
                else
                {
                    // Jak pusto, to pierwszy z brzegu konflikt (jak dawniej)
                    var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                    var conflict = statusDict?.FirstOrDefault(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"));
                    if (conflict.HasValue && !string.IsNullOrEmpty(conflict.Value.Key))
                        targetFile = conflict.Value.Key;
                }

                if (!string.IsNullOrEmpty(targetFile))
                {
                    string fullPath = Path.Combine(root, targetFile);
                    LogBoth($"Opening editor: <color=green>{targetFile}</color>");
                    System.Diagnostics.Process.Start(editorPath, $"\"{fullPath}\"");
                }
                else LogBoth("<color=yellow>No conflicted file found to open.</color>");
            }
            catch (Exception ex) { LogBoth($"<color=red>Exception:</color> {ex.Message}"); }
            finally { IsProcessing = false; }
        }

        public static async Task<string> ResolveAsync(string workingDir, string[] paths, bool useMine)
        {
            if (paths == null || paths.Length == 0) return "No paths to resolve.";
            string pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));
            string strategy = useMine ? "mine-full" : "theirs-full";
            return await SvnRunner.RunAsync($"resolve --accept {strategy} {pathsArg}", workingDir);
        }
    }
}