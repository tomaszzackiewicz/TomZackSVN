using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SVN.Core
{
    public class SVNResolve : SVNBase
    {
        public SVNResolve(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void Button_OpenInEditor()
        {
            if (IsProcessing) return;

            // 1. Get the editor path from your Settings InputField
            string editorPath = svnUI.SettingsMergeToolPathInput.text;

            if (string.IsNullOrEmpty(editorPath) || !File.Exists(editorPath))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Provide a valid editor path (.exe) in Settings!\n";
                return;
            }

            // 2. Determine the working directory
            if (string.IsNullOrEmpty(svnManager.WorkingDir) || !Directory.Exists(svnManager.WorkingDir))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Invalid Working Directory!\n";
                return;
            }

            try
            {
                // 3. Get the current status (returns status and size tuples)
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir, false);

                // FIX: Checking the .status field in the Value tuple
                // Using Any() before FirstOrDefault() to safely handle missing results
                var conflictEntry = statusDict.FirstOrDefault(x =>
                    !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"));

                if (!string.IsNullOrEmpty(conflictEntry.Key))
                {
                    // Build the full file path
                    string fullFilePath = Path.Combine(svnManager.WorkingDir, conflictEntry.Key);

                    svnUI.LogText.text += $"Opening editor for: <color=cyan>{conflictEntry.Key}</color>...\n";

                    // 4. Start the external process
                    System.Diagnostics.Process.Start(editorPath, $"\"{fullFilePath}\"");

                    svnUI.LogText.text += "<color=yellow>Instructions:</color> Fix the conflict in the editor, save the file, and click <b>Mark as Resolved</b>.\n";
                }
                else
                {
                    svnUI.LogText.text += "<color=yellow>No files found in conflict state (C).</color>\n";
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Error opening editor:</color> {ex.Message}\n";
            }
        }

        public async void Button_MarkAsResolved()
        {
            if (IsProcessing) return;

            // 1. Get the path directly from the Manager
            string root = svnManager.WorkingDir;

            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning("[SVN] MarkAsResolved: Working directory is empty.");
                return;
            }

            if (svnUI == null)
            {
                Debug.LogError("[SVN] MarkAsResolved: svnUI reference is missing!");
                return;
            }

            IsProcessing = true;

            try
            {
                if (svnUI.LogText != null)
                    svnUI.LogText.text = "Checking for conflicts to mark as resolved...\n";

                // 2. Get current statuses
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 3. Look for files with status 'C' (Conflict)
                var conflictedPaths = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                    .Select(x => x.Key)
                    .ToArray();

                if (conflictedPaths.Length > 0)
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += $"Marking {conflictedPaths.Length} items as resolved...\n";

                    // 4. Build arguments for the 'svn resolved' command
                    // Use quotes for each path to handle spaces
                    string pathsJoined = "\"" + string.Join("\" \"", conflictedPaths) + "\"";
                    string args = $"resolved {pathsJoined}";

                    // 5. Call SvnRunner
                    await SvnRunner.RunAsync(args, root);

                    if (svnUI.LogText != null)
                    {
                        svnUI.LogText.text += "<color=green><b>Success!</b></color> Conflicts marked as resolved.\n";
                        svnUI.LogText.text += "Local metadata cleaned. You can now commit.\n";
                    }

                    // 6. Refresh the view (this will hide the conflict panel and refresh the tree)
                    svnManager.RefreshStatus();
                }
                else
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += "<color=yellow>No conflicts found to mark as resolved.</color>\n";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] MarkAsResolved Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text += $"<color=red>Resolved Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        // --- CONFLICT RESOLUTION ---
        // Accepts the server version (overwrites your changes)
        public async void Button_ResolveTheirs()
        {
            if (IsProcessing) return;

            // 1. Get the path directly from the manager (eliminates Input error)
            string root = svnManager.WorkingDir;

            // Basic path and UI reference check
            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning("[SVN] ResolveTheirs: Working directory is empty.");
                return;
            }

            if (svnUI == null)
            {
                Debug.LogError("[SVN] ResolveTheirs: svnUI reference is missing!");
                return;
            }

            IsProcessing = true;

            if (svnUI.LogText != null)
                svnUI.LogText.text = "Searching for conflicts to resolve using <color=orange>THEIRS</color>...\n";

            try
            {
                // 2. Get statuses from the repository
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 3. Filter files in conflict state (status 'C')
                var conflictedPaths = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                    .Select(x => x.Key)
                    .ToArray();

                if (conflictedPaths.Length > 0)
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += $"Resolving {conflictedPaths.Length} items using <color=orange>theirs-full</color>...\n";

                    // 4. Call SVN Resolve command with 'theirs-full' strategy (false)
                    await SvnRunner.ResolveAsync(root, conflictedPaths, false);

                    if (svnUI.LogText != null)
                    {
                        svnUI.LogText.text += "<color=green><b>Conflicts Resolved!</b></color>\n";
                        svnUI.LogText.text += "<color=yellow>Remember:</color> You MUST <b>Commit</b> these files now to finish the process.\n";
                    }

                    // 5. Automatic file tree refresh
                    svnManager.RefreshStatus();
                }
                else
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += "<color=yellow>No conflicts found to resolve.</color>\n";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Resolve Theirs Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text += $"<color=red>Resolve Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void Button_ResolveMine()
        {
            if (IsProcessing) return;

            // 1. Get the path directly from the manager (eliminates Input error)
            string root = svnManager.WorkingDir;

            // Basic path and UI check
            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning("[SVN] ResolveMine: Working directory is empty.");
                return;
            }

            if (svnUI == null)
            {
                Debug.LogError("[SVN] ResolveMine: svnUI reference is missing!");
                return;
            }

            IsProcessing = true;

            if (svnUI.LogText != null)
                svnUI.LogText.text = "Searching for conflicts to resolve using <color=cyan>MINE</color>...\n";

            try
            {
                // 2. Get statuses
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 3. Look for files with status 'C' (Conflict)
                var conflictedPaths = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                    .Select(x => x.Key)
                    .ToArray();

                if (conflictedPaths.Length > 0)
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += $"Resolving {conflictedPaths.Length} conflicts using strategy: <color=cyan>mine-full</color>...\n";

                    // 4. Call SvnRunner.ResolveAsync (mine-full)
                    await SvnRunner.ResolveAsync(root, conflictedPaths, true);

                    if (svnUI.LogText != null)
                    {
                        svnUI.LogText.text += $"<color=green><b>Success!</b></color> Resolved {conflictedPaths.Length} conflicts.\n";
                        svnUI.LogText.text += "<color=#AAAAAA>Your local changes preserved.</color>\n";
                    }

                    // 5. Refresh the manager view
                    svnManager.RefreshStatus();
                }
                else
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += "<color=yellow>No conflicts found to resolve.</color>\n";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Resolve Mine Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text += $"<color=red>Resolve Error (Mine):</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }
}