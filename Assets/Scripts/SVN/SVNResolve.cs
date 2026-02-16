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
            var console = svnUI.MergeConsoleText != null ? svnUI.MergeConsoleText : svnUI.CommitConsoleContent;
            SVNLogBridge.UpdateUIField(console, msg, "RESOLVE", true);
        }

        private async Task<string[]> GetTargetPaths(string root)
        {
            if (svnUI.ResolveTargetFileInput != null && !string.IsNullOrEmpty(svnUI.ResolveTargetFileInput.text))
            {
                string manualPath = svnUI.ResolveTargetFileInput.text.Trim();
                return new[] { manualPath };
            }

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
            IsProcessing = true;

            try
            {
                string[] targetPaths = await GetTargetPaths(root);
                if (targetPaths.Length == 0)
                {
                    LogBoth("<color=yellow>No conflicts found.</color>");
                    return;
                }

                foreach (var path in targetPaths)
                {
                    string fullPath = Path.Combine(root, path);
                    if (File.Exists(fullPath))
                    {
                        string content = await File.ReadAllTextAsync(fullPath);
                        if (content.Contains("<<<<<<<") || content.Contains("=======") || content.Contains(">>>>>>>"))
                        {
                            LogBoth($"<color=red>Abort:</color> File {path} still has conflict markers!");
                            return;
                        }
                    }
                }

                await RunSvnResolve(root, targetPaths, "working");
                LogBoth("<color=green>Marked as resolved successfully.</color>");
                await svnManager.RefreshStatus();
            }
            catch (Exception ex) { LogBoth($"<color=red>Error:</color> {ex.Message}"); }
            finally { IsProcessing = false; }
        }

        public async void Button_ResolveTheirs() => await RunResolveStrategy("theirs-full");
        public async void Button_ResolveMine() => await RunResolveStrategy("mine-full");

        private async Task RunResolveStrategy(string strategy)
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            IsProcessing = true;

            try
            {
                string[] targetPaths = await GetTargetPaths(root);
                if (targetPaths.Length > 0)
                {
                    LogBoth($"Applying strategy <color=orange>{strategy}</color> to {targetPaths.Length} items...");
                    await RunSvnResolve(root, targetPaths, strategy);
                    LogBoth("<color=green>Resolved!</color>");
                    await svnManager.RefreshStatus();
                }
                else LogBoth("<color=yellow>Nothing to resolve.</color>");
            }
            catch (Exception ex) { LogBoth($"<color=red>Error:</color> {ex.Message}"); }
            finally { IsProcessing = false; }
        }

        private async Task RunSvnResolve(string root, string[] paths, string acceptStrategy)
        {
            string pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));
            await SvnRunner.RunAsync($"resolve --accept {acceptStrategy} {pathsArg}", root);
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

                if (svnUI.ResolveTargetFileInput != null && !string.IsNullOrEmpty(svnUI.ResolveTargetFileInput.text))
                {
                    targetFile = svnUI.ResolveTargetFileInput.text.Trim();
                }
                else
                {
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
    }
}