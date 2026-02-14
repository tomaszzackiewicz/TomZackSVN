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
            if (svnUI.LogText != null) svnUI.LogText.text += msg;
            if (svnUI.CommitConsoleContent != null) svnUI.CommitConsoleContent.text += msg;
        }

        public async void Button_OpenInEditor()
        {
            if (IsProcessing) return;

            Action<string> LogBoth = (msg) =>
            {
                if (svnUI.LogText != null) svnUI.LogText.text += msg;
                if (svnUI.CommitConsoleContent != null) svnUI.CommitConsoleContent.text += msg;
            };

            string editorPath = svnManager.MergeToolPath;

            if (string.IsNullOrEmpty(editorPath) && svnUI.SettingsMergeToolPathInput != null)
            {
                editorPath = svnUI.SettingsMergeToolPathInput.text;
            }

            if (string.IsNullOrEmpty(editorPath))
            {
                editorPath = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
                svnManager.MergeToolPath = editorPath;
            }

            if (string.IsNullOrEmpty(editorPath))
            {
                LogBoth("<color=red>Error:</color> Merge tool path is not set! Go to Settings and Save it.\n");
                return;
            }

            if (!File.Exists(editorPath))
            {
                LogBoth($"<color=red>Error:</color> Editor executable not found at: <color=yellow>{editorPath}</color>\n");
                return;
            }

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                LogBoth("<color=red>Error:</color> Invalid Working Directory!\n");
                return;
            }

            try
            {
                IsProcessing = true;
                LogBoth($"[{DateTime.Now:HH:mm:ss}] Searching for conflicted files (C)...\n");

                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                if (statusDict == null)
                {
                    LogBoth("<color=red>Error:</color> Failed to get SVN status.\n");
                    return;
                }

                var conflictEntry = statusDict.FirstOrDefault(x =>
                    !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"));

                if (!string.IsNullOrEmpty(conflictEntry.Key))
                {
                    string fullFilePath = Path.Combine(root, conflictEntry.Key);
                    LogBoth($"Opening editor: <color=green>{Path.GetFileName(fullFilePath)}</color>\n");

                    System.Diagnostics.Process.Start(editorPath, $"\"{fullFilePath}\"");

                    LogBoth("<color=yellow>Hint:</color> Resolve conflicts, save the file, and click 'Mark as Resolved'.\n");
                }
                else
                {
                    LogBoth("<color=yellow>No files in conflict state (C) found.</color>\n");
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Exception:</color> {ex.Message}\n");
                Debug.LogError($"[SVN Resolve] {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void Button_MarkAsResolved()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root)) return;

            IsProcessing = true;
            LogBoth("Starting safe resolve check...\n");

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                var conflictedPaths = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                    .Select(x => x.Key).ToArray();

                if (conflictedPaths.Length == 0)
                {
                    LogBoth("<color=yellow>No conflicts found to resolve.</color>\n");
                    return;
                }

                List<string> readyToResolve = new List<string>();
                List<string> failedValidation = new List<string>();

                foreach (var relativePath in conflictedPaths)
                {
                    string fullPath = Path.Combine(root, relativePath);
                    if (File.Exists(fullPath))
                    {
                        string content = await File.ReadAllTextAsync(fullPath);

                        if (content.Contains("<<<<<<< .mine") ||
                            content.Contains(">>>>>>> .r") ||
                            content.Contains("======="))
                        {
                            failedValidation.Add(relativePath);
                        }
                        else
                        {
                            readyToResolve.Add(relativePath);
                        }
                    }
                }

                if (failedValidation.Count > 0)
                {
                    LogBoth("<color=red><b>ABORTED:</b></color> The following files still contain conflict markers:\n");
                    foreach (var f in failedValidation) LogBoth($" - <color=orange>{f}</color>\n");
                    LogBoth("<color=yellow>Please edit them, remove <<<<<<, =======, >>>>>>> markers, save and try again.</color>\n");

                    if (readyToResolve.Count == 0) return;
                }

                if (readyToResolve.Count > 0)
                {
                    LogBoth($"Marking {readyToResolve.Count} validated items as resolved...\n");

                    string pathsJoined = "\"" + string.Join("\" \"", readyToResolve) + "\"";
                    await SvnRunner.RunAsync($"resolve --accept working {pathsJoined}", root);

                    LogBoth("<color=green><b>Success!</b></color> Validated files are now clean and ready to commit.\n");
                    await svnManager.RefreshStatus();
                    svnManager.PanelHandler.Button_CloseResolve();
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Error during safe resolve:</color> {ex.Message}\n");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void Button_ResolveTheirs() => await RunResolveStrategy(false);
        public async void Button_ResolveMine() => await RunResolveStrategy(true);

        private async System.Threading.Tasks.Task RunResolveStrategy(bool useMine)
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            string strategy = useMine ? "mine-full" : "theirs-full";

            IsProcessing = true;
            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                var paths = statusDict.Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                                      .Select(x => x.Key).ToArray();

                if (paths.Length > 0)
                {
                    LogBoth($"Resolving {paths.Length} items using <color=orange>{strategy}</color>...\n");
                    await ResolveAsync(root, paths, useMine);
                    LogBoth("<color=green>Resolved!</color> Refreshing status...\n");
                    await svnManager.RefreshStatus();
                    svnManager.PanelHandler.Button_CloseResolve();
                }
                else LogBoth("<color=yellow>No conflicts found.</color>\n");
            }
            catch (Exception ex) { LogBoth($"<color=red>Error:</color> {ex.Message}\n"); }
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