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
            // Logging to main log window
            SVNLogBridge.LogLine(msg);

            // Logging to commit console field specifically
            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, msg, logLabel: "RESOLVE", append: true);
        }

        public async void Button_OpenInEditor()
        {
            if (IsProcessing) return;

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
                LogBoth("<color=red>Error:</color> Merge tool path is not set! Go to Settings and Save it.");
                return;
            }

            if (!File.Exists(editorPath))
            {
                LogBoth($"<color=red>Error:</color> Editor executable not found at: <color=yellow>{editorPath}</color>");
                return;
            }

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                LogBoth("<color=red>Error:</color> Invalid Working Directory!");
                return;
            }

            try
            {
                IsProcessing = true;
                LogBoth("Searching for conflicted files (C)...");

                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                if (statusDict == null)
                {
                    LogBoth("<color=red>Error:</color> Failed to get SVN status.");
                    return;
                }

                var conflictEntry = statusDict.FirstOrDefault(x =>
                    !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"));

                if (!string.IsNullOrEmpty(conflictEntry.Key))
                {
                    string fullFilePath = Path.Combine(root, conflictEntry.Key);
                    LogBoth($"Opening editor: <color=green>{Path.GetFileName(fullFilePath)}</color>");

                    System.Diagnostics.Process.Start(editorPath, $"\"{fullFilePath}\"");

                    LogBoth("<color=yellow>Hint:</color> Resolve conflicts, save the file, and click 'Mark as Resolved'.");
                }
                else
                {
                    LogBoth("<color=yellow>No files in conflict state (C) found.</color>");
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Exception:</color> {ex.Message}");
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
            LogBoth("Starting safe resolve check...");

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                var conflictedPaths = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                    .Select(x => x.Key).ToArray();

                if (conflictedPaths.Length == 0)
                {
                    LogBoth("<color=yellow>No conflicts found to resolve.</color>");
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
                    LogBoth("<color=red><b>ABORTED:</b></color> The following files still contain conflict markers:");
                    foreach (var f in failedValidation) LogBoth($" - <color=orange>{f}</color>");
                    LogBoth("<color=yellow>Please edit them, remove <<<<<<, =======, >>>>>>> markers, save and try again.</color>");

                    if (readyToResolve.Count == 0) return;
                }

                if (readyToResolve.Count > 0)
                {
                    LogBoth($"Marking {readyToResolve.Count} validated items as resolved...");

                    string pathsJoined = "\"" + string.Join("\" \"", readyToResolve) + "\"";
                    await SvnRunner.RunAsync($"resolve --accept working {pathsJoined}", root);

                    LogBoth("<color=green><b>Success!</b></color> Validated files are now clean and ready to commit.");
                    await svnManager.RefreshStatus();
                    svnManager.PanelHandler.Button_CloseResolve();
                }
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Error during safe resolve:</color> {ex.Message}");
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
                    LogBoth($"Resolving {paths.Length} items using <color=orange>{strategy}</color>...");
                    await ResolveAsync(root, paths, useMine);
                    LogBoth("<color=green>Resolved!</color> Refreshing status...");
                    await svnManager.RefreshStatus();
                    svnManager.PanelHandler.Button_CloseResolve();
                }
                else LogBoth("<color=yellow>No conflicts found.</color>");
            }
            catch (Exception ex) { LogBoth($"<color=red>Error:</color> {ex.Message}"); }
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