using System;
using System.IO;
using System.Linq;
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

            Action<string> LogBoth = (msg) => {
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
                    LogBoth($"Opening editor: <color=cyan>{Path.GetFileName(fullFilePath)}</color>\n");

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
            LogBoth("Checking for conflicts to mark as resolved...\n");

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                var conflictedPaths = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                    .Select(x => x.Key).ToArray();

                if (conflictedPaths.Length > 0)
                {
                    LogBoth($"Marking {conflictedPaths.Length} items as resolved...\n");

                    string pathsJoined = "\"" + string.Join("\" \"", conflictedPaths) + "\"";
                    await SvnRunner.RunAsync($"resolved {pathsJoined}", root);

                    LogBoth("<color=green><b>Success!</b></color> Metadata cleaned. You can now commit.\n");
                    await svnManager.RefreshStatus();
                }
                else
                {
                    LogBoth("<color=yellow>No conflicts found to resolve.</color>\n");
                }
            }
            catch (Exception ex) { LogBoth($"<color=red>Error:</color> {ex.Message}\n"); }
            finally { IsProcessing = false; }
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
                    await SvnRunner.ResolveAsync(root, paths, useMine);
                    LogBoth("<color=green>Resolved!</color> Refreshing status...\n");
                    await svnManager.RefreshStatus();
                }
                else LogBoth("<color=yellow>No conflicts found.</color>\n");
            }
            catch (Exception ex) { LogBoth($"<color=red>Error:</color> {ex.Message}\n"); }
            finally { IsProcessing = false; }
        }
    }
}